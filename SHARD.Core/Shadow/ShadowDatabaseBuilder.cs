using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.WAL;

namespace SHARD.Core.Shadow;

/// <summary>
/// Builds a "shadow" SQLite database that mirrors an evidence file's table
/// structure and row data, reconstructed from parsed <see cref="TableSchema"/>
/// and decoded <see cref="TableRow"/>s rather than re-executing the evidence
/// file's original SQL or reading it with a SQLite library. Each row carries
/// forensic provenance (page number, cell offset, first overflow page), and
/// overflow chains are recorded in a side table so fragments can be displayed.
/// </summary>
public static class ShadowDatabaseBuilder
{
    private const string PageNumberColumn    = "_page_number";
    private const string CellOffsetColumn   = "_cell_offset";
    private const string OverflowPageColumn = "_overflow_page";
    public  const string RecoveryMethodColumn = "_recovery_method";

    public const string RecoveryMethodDeletedCell        = "deleted_cell";
    public const string RecoveryMethodCarving            = "carving";
    public const string RecoveryMethodFreeblock          = "freeblock";
    public const string RecoveryMethodManual             = "manual";
    public const string RecoveryMethodWalFrame           = "wal_frame";
    public const string RecoveryMethodWalPreviousVersion = "wal_previous_version";
    /// <summary>Attributed to a live table purely by content-matching an unattributed page's bytes against its <see cref="RecordStructure"/> — see <see cref="OrphanPageCarver"/>.</summary>
    public const string RecoveryMethodOrphanCarving       = "orphan_carving";

    /// <summary>Prefix for tables SHARD itself creates in the shadow database (as opposed to mirrored evidence tables), so consumers can filter them out of table listings.</summary>
    public const string InternalTablePrefix  = "_shard_";
    public const string RecoveredTablePrefix = InternalTablePrefix + "recovered_";
    /// <summary>Prefix for shadow tables that hold records read from a dropped table's still-valid root page.</summary>
    public const string DeletedTablePrefix   = InternalTablePrefix + "deleted_";
    private const string OverflowTableName = InternalTablePrefix + "overflow_pages";
    private const string PagesTableName    = InternalTablePrefix + "pages";

    /// <summary>
    /// Builds the shadow database. Returns a list of warning strings for any user tables that
    /// were silently skipped (e.g. unparseable SQL, empty schema). Empty list means all tables
    /// were processed.
    /// </summary>
    public static IReadOnlyList<string> Create(string shadowDbPath, SqliteForensicDatabase database)
    {
        var warnings = new List<string>();

        using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
        connection.Open();

        CreateOverflowTable(connection);
        CreatePagesTable(connection);
        PopulatePagesBaseline(connection, database);
        TagTablePages(connection, "sqlite_master", database.GetTreePageNumbers(1));

        foreach (var row in database.ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Table) continue;
            if (row.Sql is null || row.RootPage is null)
            {
                warnings.Add($"Skipped table '{row.Name}': missing SQL or root page in sqlite_master.");
                continue;
            }
            if (row.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                TagTablePages(connection, row.Name, database.GetTreePageNumbers(row.RootPage.Value));
                continue;
            }
            if (row.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;

            var tableSchema = CreateTableParser.ExtractTableSchema(row.Sql);
            if (tableSchema is null)
            {
                warnings.Add($"Skipped table '{row.Name}': CREATE TABLE SQL could not be parsed.\nSQL: {row.Sql}");
                continue;
            }
            if (tableSchema.Columns.Count == 0)
            {
                warnings.Add($"Skipped table '{row.Name}': no columns were parsed from the schema.\nSQL: {row.Sql}");
                continue;
            }

            string createTableSql = BuildCreateTableSql(tableSchema);
            try
            {
                using (var createCommand = connection.CreateCommand())
                {
                    createCommand.CommandText = createTableSql;
                    createCommand.ExecuteNonQuery();
                }

                using (var createRecoveredCommand = connection.CreateCommand())
                {
                    createRecoveredCommand.CommandText = BuildCreateRecoveredTableSql(tableSchema);
                    createRecoveredCommand.ExecuteNonQuery();
                }

                InsertRows(connection, tableSchema, database.ReadTableRows(row.RootPage.Value));
                InsertDeletedRows(connection, tableSchema, row.RootPage.Value, database);
                TagTablePages(connection, tableSchema.TableName, database.GetTreePageNumbers(row.RootPage.Value));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to build shadow table '{tableSchema.TableName}'.\n" +
                    $"Original CREATE TABLE SQL:\n{row.Sql}\n\n" +
                    $"Generated SQL:\n{createTableSql}\n\n" +
                    $"Error: {ex.Message}", ex);
            }
        }

        foreach (var row in database.ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Index) continue;
            if (row.RootPage is null) continue;

            TagTablePages(connection, row.Name, database.GetTreePageNumbers(row.RootPage.Value));
        }

        TagFreelistPages(connection, database);

        return warnings;
    }

    public static string BuildCreateTableSql(TableSchema schema)
    {
        var columnSql = schema.Columns.Select(c =>
        {
            var parts = new List<string> { QuoteIdentifier(c.Name) };
            if (!string.IsNullOrEmpty(c.DeclaredType)) parts.Add(c.DeclaredType);
            if (c.IsRowIdAlias) parts.Add("PRIMARY KEY");
            if (c.IsNotNull) parts.Add("NOT NULL");
            if (c.IsUnique) parts.Add("UNIQUE");
            return string.Join(' ', parts);
        }).ToList();

        // Provenance columns must come before any table constraint — SQLite's grammar
        // doesn't allow a column-def to follow a table-constraint in the same list.
        columnSql.Add($"{QuoteIdentifier(PageNumberColumn)} INTEGER NOT NULL");
        columnSql.Add($"{QuoteIdentifier(CellOffsetColumn)} INTEGER NOT NULL");
        columnSql.Add($"{QuoteIdentifier(OverflowPageColumn)} INTEGER NOT NULL DEFAULT 0");

        var compositeKey = schema.Columns.Where(c => c.IsPrimaryKey && !c.IsRowIdAlias).Select(c => c.Name).ToList();
        if (compositeKey.Count > 0)
            columnSql.Add($"PRIMARY KEY ({string.Join(", ", compositeKey.Select(QuoteIdentifier))})");

        return $"CREATE TABLE {QuoteIdentifier(schema.TableName)} (\n  {string.Join(",\n  ", columnSql)}\n)";
    }

    /// <summary>
    /// Same columns as <see cref="BuildCreateTableSql"/> but without any constraints
    /// (no PRIMARY KEY, UNIQUE, NOT NULL), so recovered records with unknown or
    /// duplicate keys can always be inserted.
    /// </summary>
    public static string BuildCreateRecoveredTableSql(TableSchema schema)
    {
        var columnSql = schema.Columns.Select(c =>
        {
            var parts = new List<string> { QuoteIdentifier(c.Name) };
            if (!string.IsNullOrEmpty(c.DeclaredType)) parts.Add(c.DeclaredType);
            return string.Join(' ', parts);
        }).ToList();

        columnSql.Add($"{QuoteIdentifier(PageNumberColumn)}     INTEGER NOT NULL");
        columnSql.Add($"{QuoteIdentifier(CellOffsetColumn)}    INTEGER NOT NULL");
        columnSql.Add($"{QuoteIdentifier(OverflowPageColumn)}  INTEGER NOT NULL DEFAULT 0");
        columnSql.Add($"{QuoteIdentifier(RecoveryMethodColumn)} TEXT NOT NULL");

        string tableName = RecoveredTablePrefix + schema.TableName;
        return $"CREATE TABLE {QuoteIdentifier(tableName)} (\n  {string.Join(",\n  ", columnSql)}\n)";
    }

    /// <summary>
    /// Inserts a B-tree leaf cell from a WAL frame into the live shadow table using
    /// <c>INSERT OR IGNORE</c>, so records already present from the baseline database
    /// are not duplicated.
    /// </summary>
    public static void InsertWalRecord(
        SqliteConnection connection,
        TableSchema schema,
        BTreeLeafCell cell,
        uint pageNumber,
        int cellOffset)
    {
        var columnNames  = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
        var placeholders = schema.Columns.Select((_, i) => $"@p{i}").ToList();
        columnNames.Add(QuoteIdentifier(PageNumberColumn));   placeholders.Add("@p_page");
        columnNames.Add(QuoteIdentifier(CellOffsetColumn));   placeholders.Add("@p_offset");
        columnNames.Add(QuoteIdentifier(OverflowPageColumn)); placeholders.Add("@p_overflow");

        string sql = $"INSERT OR IGNORE INTO {QuoteIdentifier(schema.TableName)} " +
                     $"({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            object value = schema.Columns[i].IsRowIdAlias
                ? cell.RowId.Value
                : (object?)(i < cell.FieldValues.Count ? cell.FieldValues[i]?.Value : null) ?? DBNull.Value;
            command.Parameters.AddWithValue($"@p{i}", value);
        }

        command.Parameters.AddWithValue("@p_page",    (long)pageNumber);
        command.Parameters.AddWithValue("@p_offset",  cellOffset);
        command.Parameters.AddWithValue("@p_overflow", (long)cell.OverflowPage);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts a recovered (deleted) B-tree leaf cell into the
    /// <c>_shard_recovered_{tableName}</c> table of the already-open shadow connection.
    /// Creates the table first if it does not yet exist (e.g. for sqlite_* tables that
    /// are skipped during initial shadow DB construction).
    /// </summary>
    public static void InsertRecoveredRecord(
        SqliteConnection connection,
        TableSchema schema,
        BTreeLeafCell cell,
        uint pageNumber,
        int cellOffset,
        string recoveryMethod = RecoveryMethodManual)
    {
        EnsureRecoveredTableExists(connection, schema);
        string recoveredTable = RecoveredTablePrefix + schema.TableName;

        var columnNames  = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
        var placeholders = schema.Columns.Select((_, i) => $"@p{i}").ToList();
        columnNames.Add(QuoteIdentifier(PageNumberColumn));     placeholders.Add("@p_page");
        columnNames.Add(QuoteIdentifier(CellOffsetColumn));     placeholders.Add("@p_offset");
        columnNames.Add(QuoteIdentifier(OverflowPageColumn));   placeholders.Add("@p_overflow");
        columnNames.Add(QuoteIdentifier(RecoveryMethodColumn)); placeholders.Add("@p_method");

        string sql = $"INSERT INTO {QuoteIdentifier(recoveredTable)} " +
                     $"({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            object value = schema.Columns[i].IsRowIdAlias
                ? cell.RowId.Value
                : (object?)(i < cell.FieldValues.Count ? cell.FieldValues[i]?.Value : null) ?? DBNull.Value;
            command.Parameters.AddWithValue($"@p{i}", value);
        }

        command.Parameters.AddWithValue("@p_page",    (long)pageNumber);
        command.Parameters.AddWithValue("@p_offset",  cellOffset);
        command.Parameters.AddWithValue("@p_overflow", (long)cell.OverflowPage);
        command.Parameters.AddWithValue("@p_method",  recoveryMethod);
        command.ExecuteNonQuery();
    }

    private static void CreateOverflowTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {QuoteIdentifier(OverflowTableName)} (
                table_name TEXT NOT NULL,
                row_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                page_number INTEGER NOT NULL,
                next_page_number INTEGER NOT NULL,
                payload_length INTEGER NOT NULL,
                PRIMARY KEY (table_name, row_id, sequence)
            )
            """;
        command.ExecuteNonQuery();
    }

    private static void CreatePagesTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {QuoteIdentifier(PagesTableName)} (
                page_number INTEGER PRIMARY KEY,
                page_type TEXT NOT NULL,
                table_name TEXT
            )
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Records which table's B-tree a set of pages belongs to (root, interior, and leaf pages).</summary>
    public static void TagTablePages(SqliteConnection connection, string tableName, IEnumerable<uint> pageNumbers)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {QuoteIdentifier(PagesTableName)} SET table_name = @table WHERE page_number = @page
            """;
        var tableParam = command.CreateParameter();
        tableParam.ParameterName = "@table";
        tableParam.Value = tableName;
        command.Parameters.Add(tableParam);
        var pageParam = command.CreateParameter();
        pageParam.ParameterName = "@page";
        command.Parameters.Add(pageParam);

        foreach (uint pageNumber in pageNumbers)
        {
            pageParam.Value = pageNumber;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void PopulatePagesBaseline(SqliteConnection connection, SqliteForensicDatabase database)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {QuoteIdentifier(PagesTableName)} (page_number, page_type)
            VALUES (@page, @type)
            """;
        var pageParam = command.CreateParameter();
        pageParam.ParameterName = "@page";
        command.Parameters.Add(pageParam);
        var typeParam = command.CreateParameter();
        typeParam.ParameterName = "@type";
        command.Parameters.Add(typeParam);

        foreach (var page in database.ReadAllPages())
        {
            pageParam.Value = page.PageNumber;
            typeParam.Value = page.PageType.ToString();
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void TagFreelistPages(SqliteConnection connection, SqliteForensicDatabase database)
    {
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {QuoteIdentifier(PagesTableName)} SET page_type = @type WHERE page_number = @page
                """;
            var pageParam = command.CreateParameter();
            pageParam.ParameterName = "@page";
            command.Parameters.Add(pageParam);
            var typeParam = command.CreateParameter();
            typeParam.ParameterName = "@type";
            command.Parameters.Add(typeParam);

            foreach (var trunk in database.ReadFreelistChain())
            {
                pageParam.Value = trunk.PageNumber;
                typeParam.Value = nameof(PageType.FreelistTrunk);
                command.ExecuteNonQuery();

                foreach (uint leafPageNumber in trunk.LeafPageNumbers)
                {
                    pageParam.Value = leafPageNumber;
                    typeParam.Value = nameof(PageType.FreelistLeaf);
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch (NotImplementedException)
        {
            // Freelist chain walking isn't implemented yet; pages remain classified by
            // their type-byte baseline (Unknown) until ReadFreelistChain is filled in.
        }
    }

    private static void InsertRows(SqliteConnection connection, TableSchema schema, IEnumerable<TableRow> rows)
    {
        using var transaction = connection.BeginTransaction();

        string insertSql = BuildInsertSql(schema);
        string overflowSql = $"""
            INSERT INTO {QuoteIdentifier(OverflowTableName)}
                (table_name, row_id, sequence, page_number, next_page_number, payload_length)
            VALUES (@table, @row_id, @sequence, @page, @next, @length)
            """;
        string pageTypeUpdateSql = $"""
            UPDATE {QuoteIdentifier(PagesTableName)} SET page_type = @type, table_name = @table WHERE page_number = @page
            """;

        foreach (var row in rows)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = insertSql;

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var column = schema.Columns[i];
                object value = column.IsRowIdAlias
                    ? row.RowId
                    : (object?)(i < row.FieldValues.Count ? row.FieldValues[i]?.Value : null) ?? DBNull.Value;
                insertCommand.Parameters.AddWithValue($"@p{i}", value);
            }

            insertCommand.Parameters.AddWithValue("@p_page", row.PageNumber);
            insertCommand.Parameters.AddWithValue("@p_offset", row.CellOffset);
            insertCommand.Parameters.AddWithValue("@p_overflow",
                row.OverflowFragments.Count > 0 ? row.OverflowFragments[0].PageNumber : 0);

            insertCommand.ExecuteNonQuery();

            foreach (var fragment in row.OverflowFragments)
            {
                using var overflowCommand = connection.CreateCommand();
                overflowCommand.Transaction = transaction;
                overflowCommand.CommandText = overflowSql;
                overflowCommand.Parameters.AddWithValue("@table", schema.TableName);
                overflowCommand.Parameters.AddWithValue("@row_id", row.RowId);
                overflowCommand.Parameters.AddWithValue("@sequence", fragment.Sequence);
                overflowCommand.Parameters.AddWithValue("@page", fragment.PageNumber);
                overflowCommand.Parameters.AddWithValue("@next", fragment.NextPageNumber);
                overflowCommand.Parameters.AddWithValue("@length", fragment.PayloadLength);
                overflowCommand.ExecuteNonQuery();

                using var pageTypeCommand = connection.CreateCommand();
                pageTypeCommand.Transaction = transaction;
                pageTypeCommand.CommandText = pageTypeUpdateSql;
                pageTypeCommand.Parameters.AddWithValue("@type", nameof(PageType.Overflow));
                pageTypeCommand.Parameters.AddWithValue("@table", schema.TableName);
                pageTypeCommand.Parameters.AddWithValue("@page", fragment.PageNumber);
                pageTypeCommand.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static void InsertDeletedRows(SqliteConnection connection, TableSchema schema, uint rootPage, SqliteForensicDatabase database)
    {
        using var transaction = connection.BeginTransaction();
        string recoveredTable = RecoveredTablePrefix + schema.TableName;

        var columnNames  = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
        var placeholders = schema.Columns.Select((_, i) => $"@p{i}").ToList();
        columnNames.Add(QuoteIdentifier(PageNumberColumn));     placeholders.Add("@p_page");
        columnNames.Add(QuoteIdentifier(CellOffsetColumn));     placeholders.Add("@p_offset");
        columnNames.Add(QuoteIdentifier(OverflowPageColumn));   placeholders.Add("@p_overflow");
        columnNames.Add(QuoteIdentifier(RecoveryMethodColumn)); placeholders.Add("@p_method");

        string sql = $"INSERT INTO {QuoteIdentifier(recoveredTable)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";

        var recordStructure = RecordStructure.FromSchema(schema);

        foreach (uint pageNum in database.GetTreePageNumbers(rootPage))
        {
            if (database.ReadPage(pageNum) is not TableBTreeLeafPage tlp) continue;

            foreach (var cell in tlp.DeletedCells)
                InsertRecoveredCellInTransaction(connection, transaction, sql, schema, cell, pageNum, RecoveryMethodDeletedCell);

            tlp.CarveDeletedCells(recordStructure);
            foreach (var cell in tlp.CarvedCells)
                InsertRecoveredCellInTransaction(connection, transaction, sql, schema, cell, pageNum, RecoveryMethodCarving);

            tlp.CarveFreeblockCells(recordStructure);
            foreach (var cell in tlp.FreeblockCells)
                InsertRecoveredCellInTransaction(connection, transaction, sql, schema, cell, pageNum, RecoveryMethodFreeblock);
        }

        transaction.Commit();
    }

    private static void InsertRecoveredCellInTransaction(
        SqliteConnection connection, SqliteTransaction transaction,
        string sql, TableSchema schema, BTreeLeafCell cell, uint pageNum, string recoveryMethod)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            object value = schema.Columns[i].IsRowIdAlias
                ? cell.RowId.Value
                : (object?)(i < cell.FieldValues.Count ? cell.FieldValues[i]?.Value : null) ?? DBNull.Value;
            command.Parameters.AddWithValue($"@p{i}", value);
        }

        command.Parameters.AddWithValue("@p_page",    (long)pageNum);
        command.Parameters.AddWithValue("@p_offset",  cell.PageOffset);
        command.Parameters.AddWithValue("@p_overflow", (long)cell.OverflowPage);
        command.Parameters.AddWithValue("@p_method",  recoveryMethod);
        command.ExecuteNonQuery();
    }

    private static string BuildInsertSql(TableSchema schema)
    {
        var columnNames = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
        columnNames.Add(QuoteIdentifier(PageNumberColumn));
        columnNames.Add(QuoteIdentifier(CellOffsetColumn));
        columnNames.Add(QuoteIdentifier(OverflowPageColumn));

        var placeholders = schema.Columns.Select((_, i) => $"@p{i}").ToList();
        placeholders.Add("@p_page");
        placeholders.Add("@p_offset");
        placeholders.Add("@p_overflow");

        return $"INSERT INTO {QuoteIdentifier(schema.TableName)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";
    }

    /// <summary>
    /// Tags the given page numbers in <c>_shard_pages</c> with <c>"{tableName} (deleted)"</c>
    /// so they appear correctly labelled in the Pages list.
    /// </summary>
    public static void TagDeletedTablePages(SqliteConnection connection, string tableName, IEnumerable<uint> pageNumbers)
    {
        TagTablePages(connection, $"{tableName} (deleted)", pageNumbers);
    }

    /// <summary>
    /// Tags the given page numbers in <c>_shard_pages</c> with <c>"{tableName} (carved)"</c>.
    /// Distinct from <see cref="TagDeletedTablePages"/>'s "(deleted)" suffix: this label means the
    /// page's table attribution was inferred purely by matching record-shaped byte content against a
    /// live table's <see cref="RecordStructure"/> (see <see cref="OrphanPageCarver"/>) — not confirmed
    /// by any b-tree pointer, deleted-cell-pointer, or sqlite_master evidence.
    /// </summary>
    public static void TagCarvedTablePages(SqliteConnection connection, string tableName, IEnumerable<uint> pageNumbers)
    {
        TagTablePages(connection, $"{tableName} (carved)", pageNumbers);
    }

    /// <summary>
    /// Persists the results of an <see cref="OrphanPageCarver.Carve"/> run into
    /// <c>_shard_recovered_{tableName}</c>, tagged <see cref="RecoveryMethodOrphanCarving"/>, and
    /// labels each carved page in <c>_shard_pages</c> via <see cref="TagCarvedTablePages"/> (joining
    /// table names if a single page's carved cells matched more than one table). Never called
    /// automatically from <see cref="Create"/> — this is an explicit, on-demand step.
    /// </summary>
    public static void PersistCarvedOrphanRecords(SqliteConnection connection, IReadOnlyList<CarvedOrphanRecord> results)
    {
        if (results.Count == 0) return;

        using (var transaction = connection.BeginTransaction())
        {
            var insertSqlByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var schema in results.Select(r => r.Schema).DistinctBy(s => s.TableName, StringComparer.OrdinalIgnoreCase))
            {
                EnsureRecoveredTableExists(connection, schema);
                var columnNames  = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
                var placeholders = schema.Columns.Select((_, i) => $"@p{i}").ToList();
                columnNames.Add(QuoteIdentifier(PageNumberColumn));     placeholders.Add("@p_page");
                columnNames.Add(QuoteIdentifier(CellOffsetColumn));     placeholders.Add("@p_offset");
                columnNames.Add(QuoteIdentifier(OverflowPageColumn));   placeholders.Add("@p_overflow");
                columnNames.Add(QuoteIdentifier(RecoveryMethodColumn)); placeholders.Add("@p_method");
                string recoveredTable = RecoveredTablePrefix + schema.TableName;
                insertSqlByTable[schema.TableName] =
                    $"INSERT INTO {QuoteIdentifier(recoveredTable)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";
            }

            foreach (var result in results)
                InsertRecoveredCellInTransaction(
                    connection, transaction, insertSqlByTable[result.Schema.TableName],
                    result.Schema, result.Cell, result.PageNumber, RecoveryMethodOrphanCarving);

            transaction.Commit();
        }

        foreach (var pageGroup in results.GroupBy(r => r.PageNumber))
        {
            string label = string.Join(", ", pageGroup.Select(r => r.Schema.TableName).Distinct(StringComparer.OrdinalIgnoreCase));
            TagCarvedTablePages(connection, label, new[] { pageGroup.Key });
        }
    }

    /// <summary>
    /// Creates a <c>_shard_deleted_{tableName}</c> table in the shadow database and
    /// populates it with rows read directly from a dropped table's still-valid root page.
    /// Uses <c>CREATE TABLE IF NOT EXISTS</c> so it is safe to call more than once.
    /// </summary>
    public static void CreateAndPopulateDeletedTable(
        SqliteConnection connection, TableSchema schema, IEnumerable<TableRow> rows)
    {
        EnsureDeletedTableExists(connection, schema);

        string shadowTable   = DeletedTablePrefix + schema.TableName;
        string insertSql     = BuildDeletedTableInsertSql(schema, shadowTable);

        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = insertSql;

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                object value = col.IsRowIdAlias
                    ? row.RowId
                    : (object?)(i < row.FieldValues.Count ? row.FieldValues[i]?.Value : null) ?? DBNull.Value;
                cmd.Parameters.AddWithValue($"@p{i}", value);
            }

            cmd.Parameters.AddWithValue("@p_page",    (long)row.PageNumber);
            cmd.Parameters.AddWithValue("@p_offset",  (long)row.CellOffset);
            cmd.Parameters.AddWithValue("@p_overflow",
                (long)(row.OverflowFragments.Count > 0 ? row.OverflowFragments[0].PageNumber : 0));
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Creates (if absent) and populates <c>_shard_deleted_{tableName}</c> with B-tree leaf
    /// cells carved directly from a freed page's raw bytes, where no live cell list exists.
    /// </summary>
    public static void AppendCarvedCellsToDeletedTable(
        SqliteConnection connection, TableSchema schema,
        IEnumerable<BTreeLeafCell> cells, uint pageNumber)
    {
        EnsureDeletedTableExists(connection, schema);

        string shadowTable = DeletedTablePrefix + schema.TableName;
        string insertSql   = BuildDeletedTableInsertSql(schema, shadowTable);

        using var transaction = connection.BeginTransaction();
        foreach (var cell in cells)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = insertSql;

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                object value = col.IsRowIdAlias
                    ? cell.RowId.Value
                    : (object?)(i < cell.FieldValues.Count ? cell.FieldValues[i]?.Value : null) ?? DBNull.Value;
                cmd.Parameters.AddWithValue($"@p{i}", value);
            }

            cmd.Parameters.AddWithValue("@p_page",    (long)pageNumber);
            cmd.Parameters.AddWithValue("@p_offset",  cell.PageOffset);
            cmd.Parameters.AddWithValue("@p_overflow", 0L);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void EnsureDeletedTableExists(SqliteConnection connection, TableSchema schema)
    {
        string shadowTable = DeletedTablePrefix + schema.TableName;

        var columnSql = schema.Columns.Select(c =>
        {
            var parts = new List<string> { QuoteIdentifier(c.Name) };
            if (!string.IsNullOrEmpty(c.DeclaredType)) parts.Add(c.DeclaredType);
            return string.Join(' ', parts);
        }).ToList();
        columnSql.Add($"{QuoteIdentifier(PageNumberColumn)}    INTEGER NOT NULL");
        columnSql.Add($"{QuoteIdentifier(CellOffsetColumn)}   INTEGER NOT NULL");
        columnSql.Add($"{QuoteIdentifier(OverflowPageColumn)} INTEGER NOT NULL DEFAULT 0");

        using var create = connection.CreateCommand();
        create.CommandText = $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(shadowTable)} (\n  {string.Join(",\n  ", columnSql)}\n)";
        create.ExecuteNonQuery();
    }

    private static string BuildDeletedTableInsertSql(TableSchema schema, string shadowTable)
    {
        var colNames     = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
        var placeholders = schema.Columns.Select((_, i) => $"@p{i}").ToList();
        colNames.Add(QuoteIdentifier(PageNumberColumn));    placeholders.Add("@p_page");
        colNames.Add(QuoteIdentifier(CellOffsetColumn));   placeholders.Add("@p_offset");
        colNames.Add(QuoteIdentifier(OverflowPageColumn)); placeholders.Add("@p_overflow");
        return $"INSERT INTO {QuoteIdentifier(shadowTable)} ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", placeholders)})";
    }

    /// <summary>
    /// Creates the <c>_shard_recovered_{tableName}</c> table if it does not already exist.
    /// Used to lazily create recovered tables for sqlite_* tables that are skipped during
    /// initial shadow DB construction.
    /// </summary>
    private static void EnsureRecoveredTableExists(SqliteConnection connection, TableSchema schema)
    {
        string sql = BuildCreateRecoveredTableSql(schema)
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Walks all WAL frames oldest-to-newest and inserts into the shadow database
    /// any deleted or superseded records that do not already appear in the live or
    /// recovered tables. Returns the number of records inserted.
    /// </summary>
    public static int InsertWalDeletedRows(
        SqliteConnection connection,
        SqliteForensicDatabase database,
        WalFile wal)
    {
        if (wal.Frames.Count == 0) return 0;

        var pageTableMap = database.BuildPageTableMap();

        // Collect all parseable user-table schemas
        var allSchemas = new Dictionary<string, (TableSchema Schema, uint RootPage)>(StringComparer.OrdinalIgnoreCase);
        foreach (var masterRow in database.ReadSqliteMaster())
        {
            if (masterRow.ObjectType != SqliteMasterObjectType.Table) continue;
            if (masterRow.Name is null || masterRow.Sql is null || masterRow.RootPage is null) continue;
            if (masterRow.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            if (masterRow.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;
            var schema = CreateTableParser.ExtractTableSchema(masterRow.Sql);
            if (schema is null || schema.Columns.Count == 0) continue;
            allSchemas[masterRow.Name] = (schema, masterRow.RootPage.Value);
        }
        if (allSchemas.Count == 0) return 0;

        // Pre-compute freelist page numbers for correlation
        var freelistPages = new HashSet<uint>();
        try
        {
            foreach (var trunk in database.ReadFreelistChain())
            {
                freelistPages.Add(trunk.PageNumber);
                foreach (uint leaf in trunk.LeafPageNumbers)
                    freelistPages.Add(leaf);
            }
        }
        catch { }

        // Build per-table: live rowid sets and current field-value snapshots
        var liveRowIds   = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var livePayloads = new Dictionary<string, Dictionary<long, List<SqliteValue?>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, (schema, rootPage)) in allSchemas)
        {
            var ids      = new HashSet<long>();
            var payloads = new Dictionary<long, List<SqliteValue?>>();
            foreach (var row in database.ReadTableRows(rootPage))
            {
                ids.Add(row.RowId);
                payloads[row.RowId] = row.FieldValues;
            }
            liveRowIds[tableName]   = ids;
            livePayloads[tableName] = payloads;
        }

        // Build per-table: already-recovered rowid sets from the shadow DB
        var recoveredRowIds = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, (schema, _)) in allSchemas)
            recoveredRowIds[tableName] = GetRecoveredRowIdsFromShadow(connection, schema);

        // Ensure recovered tables exist for every schema before opening a transaction
        foreach (var (_, (schema, _)) in allSchemas)
            EnsureRecoveredTableExists(connection, schema);

        // Pre-build INSERT SQL per table (recovery method is a parameter, not in SQL)
        var insertSqlPerTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, (schema, _)) in allSchemas)
        {
            string recoveredTable = RecoveredTablePrefix + tableName;
            var cols = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
            var phs  = schema.Columns.Select((_, i) => $"@p{i}").ToList();
            cols.Add(QuoteIdentifier(PageNumberColumn));     phs.Add("@p_page");
            cols.Add(QuoteIdentifier(CellOffsetColumn));     phs.Add("@p_offset");
            cols.Add(QuoteIdentifier(OverflowPageColumn));   phs.Add("@p_overflow");
            cols.Add(QuoteIdentifier(RecoveryMethodColumn)); phs.Add("@p_method");
            insertSqlPerTable[tableName] =
                $"INSERT INTO {QuoteIdentifier(recoveredTable)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", phs)})";
        }

        // Track which WAL rowids have already been inserted per table (oldest version wins)
        var walAdded = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        int inserted = 0;

        using var transaction = connection.BeginTransaction();

        foreach (var frame in wal.Frames)
        {
            // Skip current-generation frames — those are live data handled by SyncWalFramesToShadow,
            // not historical deleted data. Only salt-mismatched (pre-checkpoint) frames hold
            // records that may have been deleted or overwritten.
            if (frame.Header.IsCurrent) continue;
            if (frame.Page is not TableBTreeLeafPage framePage) continue;
            uint pageNum = frame.Header.PageNumber;

            var schema = CorrelateWalFrameToSchema(
                framePage, pageNum, database, pageTableMap, allSchemas,
                liveRowIds, recoveredRowIds, freelistPages);
            if (schema is null) continue;

            string tableName = schema.TableName;
            var knownLive      = liveRowIds.GetValueOrDefault(tableName)    ?? [];
            var knownRecovered = recoveredRowIds.GetValueOrDefault(tableName) ?? [];
            var knownPayloads  = livePayloads.GetValueOrDefault(tableName)  ?? [];

            if (!walAdded.TryGetValue(tableName, out var addedSet))
            {
                addedSet = new HashSet<long>();
                walAdded[tableName] = addedSet;
            }

            string insertSql = insertSqlPerTable[tableName];

            foreach (var cell in framePage.Cells)
            {
                long rowId = cell.RowId.Value;
                if (addedSet.Contains(rowId)) continue;

                if (!knownLive.Contains(rowId) && !knownRecovered.Contains(rowId))
                {
                    // Stage 2: record deleted before the current DB version
                    InsertRecoveredCellInTransaction(connection, transaction, insertSql, schema, cell, pageNum, RecoveryMethodWalFrame);
                    addedSet.Add(rowId);
                    inserted++;
                }
                else if (knownLive.Contains(rowId) && knownPayloads.TryGetValue(rowId, out var livePayload))
                {
                    // Stage 3: record still exists but payload has changed — keep the older version
                    if (WalFieldsDiffer(cell.FieldValues, livePayload))
                    {
                        InsertRecoveredCellInTransaction(connection, transaction, insertSql, schema, cell, pageNum, RecoveryMethodWalPreviousVersion);
                        addedSet.Add(rowId);
                        inserted++;
                    }
                }
            }
        }

        transaction.Commit();
        return inserted;
    }

    private static TableSchema? CorrelateWalFrameToSchema(
        TableBTreeLeafPage framePage,
        uint pageNum,
        SqliteForensicDatabase database,
        Dictionary<uint, string> pageTableMap,
        Dictionary<string, (TableSchema Schema, uint RootPage)> allSchemas,
        Dictionary<string, HashSet<long>> liveRowIds,
        Dictionary<string, HashSet<long>> recoveredRowIds,
        HashSet<uint> freelistPages)
    {
        var frameRowIds = framePage.Cells.Select(c => c.RowId.Value).ToHashSet();
        if (frameRowIds.Count == 0) return null;

        if (pageNum <= database.PageCount)
        {
            SqlitePage currentPage;
            try { currentPage = database.ReadPage(pageNum); }
            catch { return null; }

            if (currentPage is TableBTreeLeafPage currentLeaf)
            {
                if (!pageTableMap.TryGetValue(pageNum, out var tableName)) return null;
                if (!allSchemas.TryGetValue(tableName, out var entry)) return null;

                // Validate correlation: at least one frame rowid must appear in live or recovered records
                var knownIds = new HashSet<long>(currentLeaf.Cells.Select(c => c.RowId.Value));
                knownIds.UnionWith(liveRowIds.GetValueOrDefault(tableName) ?? []);
                knownIds.UnionWith(recoveredRowIds.GetValueOrDefault(tableName) ?? []);
                return frameRowIds.Any(id => knownIds.Contains(id)) ? entry.Schema : null;
            }

            if (freelistPages.Contains(pageNum))
            {
                // Page freed — match against all tables' known rowids; accept if exactly one table matches
                TableSchema? matched = null;
                foreach (var (tableName, (schema, _)) in allSchemas)
                {
                    var live      = liveRowIds.GetValueOrDefault(tableName)      ?? [];
                    var recovered = recoveredRowIds.GetValueOrDefault(tableName) ?? [];
                    if (frameRowIds.Any(id => live.Contains(id) || recovered.Contains(id)))
                    {
                        if (matched is not null) return null; // Ambiguous
                        matched = schema;
                    }
                }
                return matched;
            }

            // Interior, index, overflow, or other non-leaf type → skip
            return null;
        }

        // Page number beyond current DB page count (e.g., database was VACUUMed)
        // Accept only if exactly one known schema matches every cell's column count
        var candidates = allSchemas.Values
            .Where(e => framePage.Cells.All(c => WalCellMatchesSchema(c, e.Schema)))
            .Select(e => e.Schema)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool WalCellMatchesSchema(BTreeLeafCell cell, TableSchema schema)
    {
        int expected = schema.Columns.Count(c => !c.IsRowIdAlias);
        return cell.FieldValues.Count == expected;
    }

    private static bool WalFieldsDiffer(List<SqliteValue?> frameFields, List<SqliteValue?> liveFields)
    {
        if (frameFields.Count != liveFields.Count) return true;
        for (int i = 0; i < frameFields.Count; i++)
        {
            var fv = frameFields[i];
            var lv = liveFields[i];
            if (fv is null && lv is null) continue;
            if (fv is null || lv is null) return true;
            if (!fv.Equals(lv)) return true;
        }
        return false;
    }

    private static HashSet<long> GetRecoveredRowIdsFromShadow(SqliteConnection connection, TableSchema schema)
    {
        var ids = new HashSet<long>();
        var rowIdCol = schema.Columns.FirstOrDefault(c => c.IsRowIdAlias);
        if (rowIdCol is null) return ids; // No rowid alias — can't retrieve rowids

        string recoveredTable = RecoveredTablePrefix + schema.TableName;
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {QuoteIdentifier(rowIdCol.Name)} FROM {QuoteIdentifier(recoveredTable)} WHERE {QuoteIdentifier(rowIdCol.Name)} IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        catch { /* table may not exist for this schema */ }

        return ids;
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
