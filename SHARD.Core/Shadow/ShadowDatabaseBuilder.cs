using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Integrity;
using SHARD.Core.Journal;
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
    /// <summary>Recovered tables only: the 1-based WAL frame a recovered record was read from, or
    /// NULL when it came from the database file (or a rollback journal).</summary>
    public  const string WalFrameColumn       = "_wal_frame";

    public const string RecoveryMethodDeletedCell        = "deleted_cell";
    public const string RecoveryMethodCarving            = "carving";
    public const string RecoveryMethodFreeblock          = "freeblock";
    public const string RecoveryMethodManual             = "manual";
    public const string RecoveryMethodWalFrame           = "wal_frame";
    public const string RecoveryMethodWalPreviousVersion = "wal_previous_version";
    /// <summary>A record live in the database file whose page a committed WAL transaction rewrote
    /// without it: deleted by the WAL, though not yet checkpointed into the file.</summary>
    public const string RecoveryMethodDbFileDeletedInWal     = "db_file_deleted_in_wal";
    /// <summary>The database file's version of a record a committed WAL transaction has since changed.</summary>
    public const string RecoveryMethodDbFilePreviousVersion  = "db_file_previous_version";
    /// <summary>A record found in a rollback-journal page image that no longer exists in the database.</summary>
    public const string RecoveryMethodJournalPage            = "journal_page";
    /// <summary>A rollback-journal page image's version of a record whose live payload has since changed.</summary>
    public const string RecoveryMethodJournalPreviousVersion = "journal_previous_version";
    /// <summary>Attributed to a live table purely by content-matching an unattributed page's bytes against its <see cref="RecordStructure"/> — see <see cref="OrphanPageCarver"/>.</summary>
    public const string RecoveryMethodOrphanCarving       = "orphan_carving";

    /// <summary>Prefix for tables SHARD itself creates in the shadow database (as opposed to mirrored evidence tables), so consumers can filter them out of table listings.</summary>
    public const string InternalTablePrefix  = "_shard_";
    public const string RecoveredTablePrefix = InternalTablePrefix + "recovered_";
    /// <summary>Prefix for shadow tables that hold records read from a dropped table's still-valid root page.</summary>
    public const string DeletedTablePrefix   = InternalTablePrefix + "deleted_";
    private const string OverflowTableName = InternalTablePrefix + "overflow_pages";
    private const string PagesTableName    = InternalTablePrefix + "pages";
    /// <summary>Hashes of the evidence files this shadow database was built from, one row per file.</summary>
    public  const string EvidenceTableName = InternalTablePrefix + "evidence";
    /// <summary>Key/value facts about how this shadow database was built.</summary>
    public  const string InfoTableName     = InternalTablePrefix + "info";

    /// <summary><see cref="InfoTableName"/> key: what the live tables were built from — one of
    /// <see cref="LiveSourceDatabaseFile"/> or <see cref="LiveSourceCommittedWal"/>.</summary>
    public const string InfoKeyLiveTablesSource = "live_tables_source";
    public const string LiveSourceDatabaseFile  = "database_file";
    public const string LiveSourceCommittedWal  = "database_file_with_committed_wal";

    /// <summary>
    /// Builds the shadow database. Returns a list of warning strings for any user tables that
    /// were silently skipped (e.g. unparseable SQL, empty schema). Empty list means all tables
    /// were processed. <paramref name="reportProgress"/>, if given, is called with coarse-grained
    /// stage text (e.g. per-table during the row-insert pass) — useful for showing the user a
    /// large evidence file's shadow build hasn't hung. <paramref name="flags"/> controls which
    /// in-tree recovery types run per table (null means <see cref="RecoveryFlags.Default"/> —
    /// everything on); live rows are always inserted regardless. <paramref name="evidenceHashes"/>,
    /// if given, is written to <see cref="EvidenceTableName"/> so the output carries a record of
    /// exactly which files it was built from.
    /// <paramref name="layoutDatabase"/>, if given, is where the page layout (<c>_shard_pages</c>:
    /// each page's type and owning table) is taken from instead of <paramref name="database"/>.
    /// Used when <paramref name="database"/> is the committed state with a WAL applied: rows and
    /// schema come from that, while the page layout still describes the database file itself,
    /// which is what page-level views browse.
    /// </summary>
    public static IReadOnlyList<string> Create(
        string shadowDbPath, SqliteForensicDatabase database,
        RecoveryFlags? flags = null, Action<string>? reportProgress = null,
        IReadOnlyList<EvidenceFileHash>? evidenceHashes = null,
        SqliteForensicDatabase? layoutDatabase = null)
    {
        var layout = layoutDatabase ?? database;
        flags ??= RecoveryFlags.Default;
        var warnings = new List<string>();

        using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
        connection.Open();

        CreateOverflowTable(connection);
        CreatePagesTable(connection);
        reportProgress?.Invoke("Building shadow database — indexing pages…");
        PopulatePagesBaseline(connection, layout);
        TagLayoutPages(connection, layout);

        var masterRows = database.ReadSqliteMaster().ToList();
        int totalTables = masterRows.Count(r =>
            r.ObjectType == SqliteMasterObjectType.Table && r.Sql is not null && r.RootPage is not null &&
            !r.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) &&
            !r.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase));
        int tableIndex = 0;

        foreach (var row in masterRows)
        {
            if (row.ObjectType != SqliteMasterObjectType.Table) continue;
            if (row.Sql is null || row.RootPage is null)
            {
                warnings.Add($"Skipped table '{row.Name}': missing SQL or root page in sqlite_master.");
                continue;
            }
            if (row.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;

            tableIndex++;
            reportProgress?.Invoke($"Building shadow database — table {tableIndex}/{totalTables}: {row.Name}");

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
                InsertDeletedRows(connection, tableSchema, row.RootPage.Value, database, flags);
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

        TagFreelistPages(connection, layout);

        WriteInfo(connection, InfoKeyLiveTablesSource,
            database.IncludesWal ? LiveSourceCommittedWal : LiveSourceDatabaseFile);

        if (evidenceHashes is not null)
            WriteEvidenceHashes(connection, evidenceHashes);

        return warnings;
    }

    /// <summary>Sets <paramref name="key"/> in <see cref="InfoTableName"/>, creating the table if needed.</summary>
    public static void WriteInfo(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {QuoteIdentifier(InfoTableName)} (key TEXT PRIMARY KEY, value TEXT);
            INSERT OR REPLACE INTO {QuoteIdentifier(InfoTableName)} (key, value) VALUES (@key, @value);
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>The value of <paramref name="key"/> in <see cref="InfoTableName"/>, or null if it
    /// (or the table — a shadow built before it existed) isn't there.</summary>
    public static string? ReadInfo(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {QuoteIdentifier(InfoTableName)} WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);
        try { return command.ExecuteScalar() as string; }
        catch (SqliteException) { return null; }
    }

    /// <summary>
    /// Tags every page of <paramref name="layout"/>'s B-trees with its owning object: sqlite_master,
    /// then each table (including sqlite_* internal tables), then each index.
    /// </summary>
    private static void TagLayoutPages(SqliteConnection connection, SqliteForensicDatabase layout)
    {
        TagTablePages(connection, "sqlite_master", layout.GetTreePageNumbers(1));
        var masterRows = layout.ReadSqliteMaster().ToList();
        foreach (var type in new[] { SqliteMasterObjectType.Table, SqliteMasterObjectType.Index })
        {
            foreach (var row in masterRows)
            {
                if (row.ObjectType != type || row.RootPage is null || row.Name is null) continue;
                if (type == SqliteMasterObjectType.Table && row.Sql is not null &&
                    row.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;
                TagTablePages(connection, row.Name, layout.GetTreePageNumbers(row.RootPage.Value));
            }
        }
    }

    /// <summary>
    /// Replaces the contents of <see cref="EvidenceTableName"/> with <paramref name="hashes"/>.
    /// Written once when the shadow database is built: it records the files as they were then,
    /// so re-verification results are reported elsewhere rather than overwriting it.
    /// </summary>
    public static void WriteEvidenceHashes(SqliteConnection connection, IReadOnlyList<EvidenceFileHash> hashes)
    {
        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {QuoteIdentifier(EvidenceTableName)} (
                  role TEXT NOT NULL,
                  path TEXT NOT NULL,
                  size_bytes INTEGER NOT NULL,
                  last_write_utc TEXT NOT NULL,
                  md5 TEXT NOT NULL,
                  sha1 TEXT NOT NULL,
                  sha256 TEXT NOT NULL,
                  hashed_utc TEXT NOT NULL
                );
                DELETE FROM {QuoteIdentifier(EvidenceTableName)};
                """;
            create.ExecuteNonQuery();
        }

        foreach (var h in hashes)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {QuoteIdentifier(EvidenceTableName)}
                  (role, path, size_bytes, last_write_utc, md5, sha1, sha256, hashed_utc)
                VALUES (@role, @path, @size, @lastWrite, @md5, @sha1, @sha256, @hashed)
                """;
            insert.Parameters.AddWithValue("@role", h.Role);
            insert.Parameters.AddWithValue("@path", h.Path);
            insert.Parameters.AddWithValue("@size", h.Length);
            insert.Parameters.AddWithValue("@lastWrite", h.LastWriteUtc.ToString("O"));
            insert.Parameters.AddWithValue("@md5", h.Md5);
            insert.Parameters.AddWithValue("@sha1", h.Sha1);
            insert.Parameters.AddWithValue("@sha256", h.Sha256);
            insert.Parameters.AddWithValue("@hashed", h.HashedUtc.ToString("O"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
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
            if (c.DefaultValueSql is not null) parts.Add($"DEFAULT {c.DefaultValueSql}");
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
        columnSql.Add($"{QuoteIdentifier(WalFrameColumn)}       INTEGER");

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

        var defaultCache = new Dictionary<string, object?>();
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var column = schema.Columns[i];
            object value = column.IsRowIdAlias
                ? cell.RowId.Value
                : i < cell.FieldValues.Count
                    ? (object?)cell.FieldValues[i]?.Value ?? DBNull.Value
                    : EvaluateColumnDefault(connection, null, column, defaultCache) ?? DBNull.Value;
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

        var defaultCache = new Dictionary<string, object?>();
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
                    : i < row.FieldValues.Count
                        ? (object?)row.FieldValues[i]?.Value ?? DBNull.Value
                        : EvaluateColumnDefault(connection, transaction, column, defaultCache) ?? DBNull.Value;
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

    private static void InsertDeletedRows(
        SqliteConnection connection, TableSchema schema, uint rootPage, SqliteForensicDatabase database, RecoveryFlags flags)
    {
        using var transaction = connection.BeginTransaction();
        string sql = BuildRecoveredInsertSql(schema);

        bool needsRecordStructure = flags.InTreeCarving || flags.FreeblockCarving;
        var recordStructure = needsRecordStructure ? RecordStructure.FromSchema(schema) : null;

        foreach (uint pageNum in database.GetTreePageNumbers(rootPage))
        {
            if (database.ReadPage(pageNum) is not TableBTreeLeafPage tlp) continue;
            int? walFrame = database.WalFrameNumberFor(pageNum); // the page's committed WAL image, if any

            if (flags.DeletedCells)
                foreach (var cell in tlp.DeletedCells)
                    InsertRecoveredCellInTransaction(connection, transaction, sql, schema, cell, pageNum, RecoveryMethodDeletedCell, walFrame);

            if (flags.InTreeCarving)
            {
                tlp.CarveDeletedCells(recordStructure!);
                foreach (var cell in tlp.CarvedCells)
                    InsertRecoveredCellInTransaction(connection, transaction, sql, schema, cell, pageNum, RecoveryMethodCarving, walFrame);
            }

            if (flags.FreeblockCarving)
            {
                tlp.CarveFreeblockCells(recordStructure!);
                foreach (var cell in tlp.FreeblockCells)
                    InsertRecoveredCellInTransaction(connection, transaction, sql, schema, cell, pageNum, RecoveryMethodFreeblock, walFrame);
            }
        }

        transaction.Commit();
    }

    /// <summary>INSERT into a table's recovered table, with parameters @p0..@pN for its columns
    /// followed by @p_page, @p_offset, @p_overflow, @p_method and @p_wal_frame.</summary>
    private static string BuildRecoveredInsertSql(TableSchema schema)
    {
        var cols = schema.Columns.Select(c => QuoteIdentifier(c.Name)).ToList();
        var phs  = schema.Columns.Select((_, i) => $"@p{i}").ToList();
        cols.Add(QuoteIdentifier(PageNumberColumn));     phs.Add("@p_page");
        cols.Add(QuoteIdentifier(CellOffsetColumn));     phs.Add("@p_offset");
        cols.Add(QuoteIdentifier(OverflowPageColumn));   phs.Add("@p_overflow");
        cols.Add(QuoteIdentifier(RecoveryMethodColumn)); phs.Add("@p_method");
        cols.Add(QuoteIdentifier(WalFrameColumn));       phs.Add("@p_wal_frame");
        return $"INSERT INTO {QuoteIdentifier(RecoveredTablePrefix + schema.TableName)} " +
               $"({string.Join(", ", cols)}) VALUES ({string.Join(", ", phs)})";
    }

    /// <summary>Inserts <paramref name="cell"/> into the recovered table and returns its shadow rowid.</summary>
    private static long InsertRecoveredCellInTransaction(
        SqliteConnection connection, SqliteTransaction transaction,
        string sql, TableSchema schema, BTreeLeafCell cell, uint pageNum, string recoveryMethod, int? walFrame = null)
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
        command.Parameters.AddWithValue("@p_wal_frame", walFrame is { } f ? f : DBNull.Value);
        command.ExecuteNonQuery();

        using var last = connection.CreateCommand();
        last.Transaction = transaction;
        last.CommandText = "SELECT last_insert_rowid()";
        return (long)last.ExecuteScalar()!;
    }

    /// <summary>
    /// Resolves the value to use for a column whose record physically has no entry for it —
    /// i.e. a row written before a later <c>ALTER TABLE ... ADD COLUMN</c> migration, per SQLite's
    /// schema-evolution semantics. Evaluates the column's DEFAULT expression via SQLite itself
    /// (so literals, keywords like CURRENT_TIMESTAMP, and expressions all resolve correctly),
    /// caching the result per column name for the lifetime of one table's insert pass. Returns
    /// null if the column has no DEFAULT, in which case the caller falls back to DBNull.Value —
    /// matching SQLite's own behavior for a defaultless column.
    /// </summary>
    private static object? EvaluateColumnDefault(
        SqliteConnection connection, SqliteTransaction? transaction, ColumnDefinition column, Dictionary<string, object?> cache)
    {
        if (column.DefaultValueSql is not { } defaultSql) return null;
        if (cache.TryGetValue(column.Name, out var cached)) return cached;

        object? result;
        using (var command = connection.CreateCommand())
        {
            if (transaction is not null) command.Transaction = transaction;
            command.CommandText = $"SELECT {defaultSql}";
            try { result = command.ExecuteScalar(); }
            catch { result = null; }
        }

        cache[column.Name] = result;
        return result;
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
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        // A recovered table from a shadow built before _wal_frame existed
        string table = RecoveredTablePrefix + schema.TableName;
        bool hasWalFrame;
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"SELECT 1 FROM pragma_table_info({QuoteLiteral(table)}) WHERE name = {QuoteLiteral(WalFrameColumn)}";
            hasWalFrame = info.ExecuteScalar() is not null;
        }
        if (!hasWalFrame)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {QuoteIdentifier(WalFrameColumn)} INTEGER";
            alter.ExecuteNonQuery();
        }
    }

    internal static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// Walks every WAL frame oldest-to-newest and inserts into the shadow database any deleted or
    /// superseded records that do not already appear in the live or recovered tables, each tagged
    /// with its frame number. That includes committed frames a later committed frame replaced — a
    /// row inserted and deleted within the WAL survives only there — as well as frames SQLite
    /// ignores (old generation, uncommitted, bad checksum, or after the point it stops reading).
    /// Live cells that are still live are skipped. With <paramref name="flags"/> (default: all on),
    /// each frame's deleted cells, unallocated space and freeblocks are searched too.
    /// Returns the number of records inserted.
    /// </summary>
    public static int InsertWalDeletedRows(
        SqliteConnection connection,
        SqliteForensicDatabase database,
        WalFile wal,
        RecoveryFlags? flags = null)
    {
        var pageImages = wal.Frames
            .Select((f, i) => new PageImage(f.Header.PageNumber, f.Page, i + 1));

        // Rows on each page's latest committed frame are live as far as SQLite is concerned, even
        // though they aren't in the database file yet — without this, an ignored frame that also
        // carries such a row (every page image carries all of its rows) would report it as deleted.
        var committedPages = wal.CommittedFrames
            .GroupBy(f => f.Header.PageNumber)
            .Select(g => g.Last())
            .Select(f => (f.Header.PageNumber, f.Page));

        return InsertHistoricalPageRows(connection, database, pageImages,
            RecoveryMethodWalFrame, RecoveryMethodWalPreviousVersion, committedPages,
            flags ?? RecoveryFlags.Default);
    }

    /// <summary>
    /// Recovers records from the database file's own version of every page the WAL's committed
    /// frames replace. <paramref name="committed"/> is the database as SQLite sees it (the file with
    /// the WAL applied; see <see cref="SqliteForensicDatabase.OpenCommittedState"/>), and is what
    /// the shadow's live tables were built from, so a row live in the file but no longer live
    /// there — deleted by a committed WAL transaction not yet checkpointed — would otherwise be
    /// lost. Such rows are inserted as <see cref="RecoveryMethodDbFileDeletedInWal"/>, and the
    /// file's version of a row the WAL changed as <see cref="RecoveryMethodDbFilePreviousVersion"/>.
    /// Rows are compared per table by rowid, so a row that merely moved to another page isn't
    /// reported. With <paramref name="flags"/> (default: all on), those file pages' deleted cells,
    /// unallocated space and freeblocks are searched too — in-tree recovery only reads the
    /// committed version of each page. Returns the number of records inserted.
    /// </summary>
    public static int InsertSupersededFilePageRows(
        SqliteConnection connection,
        SqliteForensicDatabase committed,
        SqliteForensicDatabase file,
        RecoveryFlags? flags = null)
    {
        var pageImages = committed.WalPageNumbers
            .Where(p => p >= 1 && p <= file.PageCount)
            .OrderBy(p => p)
            .Select(p => new PageImage(p, file.ReadPage(p), null))
            .ToList();
        return InsertHistoricalPageRows(connection, committed, pageImages,
            RecoveryMethodDbFileDeletedInWal, RecoveryMethodDbFilePreviousVersion,
            deletedDataFlags: flags ?? RecoveryFlags.Default);
    }

    /// <summary>
    /// Inserts records from a rollback journal's page images that are no longer in the database
    /// (<see cref="RecoveryMethodJournalPage"/>) or whose live payload has since changed
    /// (<see cref="RecoveryMethodJournalPreviousVersion"/>). Each journal record holds a page's
    /// content from before the journalled transaction modified it, so it can hold rows that
    /// transaction deleted or updated. Every record is used, not only those SQLite would play back:
    /// a stale record (checksum from an earlier transaction's nonce) or one after playback stopped
    /// is still a genuine earlier image of that page, and schema correlation rejects anything that
    /// doesn't fit. For a hot journal (crashed mid-transaction) the database file itself may hold
    /// the uncommitted changes, in which case the journal's rows are the last committed state.
    /// Returns the number of records inserted.
    /// </summary>
    public static int InsertJournalDeletedRows(
        SqliteConnection connection,
        SqliteForensicDatabase database,
        RollbackJournalFile journal)
    {
        var pageImages = journal.Segments
            .SelectMany(s => s.PageRecords)
            .Where(r => r.Status != JournalRecordStatus.InvalidPageNumber)
            .Select(r => new PageImage(r.PageNumber, r.Page, null));
        // Journal images' deleted data isn't searched yet: without a journal provenance column
        // it would be indistinguishable from the database file's own.
        return InsertHistoricalPageRows(connection, database, pageImages,
            RecoveryMethodJournalPage, RecoveryMethodJournalPreviousVersion);
    }

    /// <summary>One image of a database page to mine: from the database file, a rollback journal, or
    /// a WAL frame (<paramref name="WalFrame"/>, 1-based; null otherwise).</summary>
    private readonly record struct PageImage(uint PageNumber, SqlitePage Page, int? WalFrame);

    /// <summary>
    /// Shared by WAL and rollback-journal recovery: walks earlier images of database pages
    /// oldest-to-newest, correlates each table-leaf image to a table, and inserts records that are
    /// no longer live (<paramref name="deletedMethod"/>) or whose live payload differs
    /// (<paramref name="previousVersionMethod"/>). Skips records already in the recovered table
    /// under either method, so running it again over the same source (e.g. re-opening a saved
    /// project) doesn't duplicate rows.
    /// <para>
    /// When <paramref name="deletedDataFlags"/> is given, each image's deleted data is searched as
    /// well — deleted cells, carved unallocated space and freeblocks, gated by the same flags as
    /// in-tree recovery — and inserted under those techniques' own methods. Such records often have
    /// no usable rowid (a freeblock overwrites it), so they're de-duplicated by value: a copy of a
    /// live row, of a record already recovered, or of one seen in an earlier image is skipped, as
    /// is a record with no non-NULL values at all.
    /// </para>
    /// </summary>
    private static int InsertHistoricalPageRows(
        SqliteConnection connection,
        SqliteForensicDatabase database,
        IEnumerable<PageImage> pageImages,
        string deletedMethod,
        string previousVersionMethod,
        IEnumerable<(uint PageNumber, SqlitePage Page)>? alsoLivePages = null,
        RecoveryFlags? deletedDataFlags = null)
    {
        var leafImages = pageImages
            .Where(p => p.Page is TableBTreeLeafPage)
            .ToList();
        if (leafImages.Count == 0) return 0;

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

        // Newer live versions of pages the database file is behind on (a WAL's committed frames):
        // their rows count as live too, with their values taking precedence as the newer version.
        foreach (var (pageNum, page) in alsoLivePages ?? [])
        {
            if (page is not TableBTreeLeafPage livePage) continue;
            if (!pageTableMap.TryGetValue(pageNum, out var tableName)) continue;
            if (!liveRowIds.TryGetValue(tableName, out var ids)) continue;
            foreach (var cell in livePage.Cells)
            {
                ids.Add(cell.RowId.Value);
                livePayloads[tableName][cell.RowId.Value] = cell.FieldValues;
            }
        }

        // Build per-table: already-recovered rowid sets from the shadow DB, plus the subset this
        // source has already contributed (by either of its methods) on an earlier run
        var recoveredRowIds   = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var previouslyAdded   = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, (schema, _)) in allSchemas)
        {
            recoveredRowIds[tableName] = GetRecoveredRowIdsFromShadow(connection, schema);
            previouslyAdded[tableName] = GetRecoveredRowIdsFromShadow(connection, schema, deletedMethod, previousVersionMethod);
        }

        // Ensure recovered tables exist for every schema before opening a transaction
        foreach (var (_, (schema, _)) in allSchemas)
            EnsureRecoveredTableExists(connection, schema);

        // Value-based de-duplication for deleted data (see remarks): values of live rows, and of
        // every record already recovered, per table.
        // Rowid-less recovered copies (keyed by values) are kept as their shadow rowids, so a complete
        // copy of the same record found later replaces them rather than sitting beside them.
        var liveValueKeys      = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var recoveredValueKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var partialCopies      = new Dictionary<string, Dictionary<string, List<long>>>(StringComparer.OrdinalIgnoreCase);
        var recoveredVersions  = new Dictionary<string, HashSet<(long RowId, string Key)>>(StringComparer.OrdinalIgnoreCase);
        var recordStructures   = new Dictionary<string, RecordStructure>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, (schema, _)) in allSchemas)
        {
            liveValueKeys[tableName] = livePayloads[tableName].Values.Select(v => ValueKey(schema, v)).ToHashSet();
            (recoveredValueKeys[tableName], partialCopies[tableName], recoveredVersions[tableName]) = GetRecoveredValueKeysFromShadow(connection, schema);
            if (deletedDataFlags is not null)
                recordStructures[tableName] = RecordStructure.FromSchema(schema);
        }

        var insertSqlPerTable = allSchemas.ToDictionary(
            e => e.Key, e => BuildRecoveredInsertSql(e.Value.Schema), StringComparer.OrdinalIgnoreCase);

        int inserted = 0;

        using var transaction = connection.BeginTransaction();

        foreach (var (pageNum, page, walFrame) in leafImages)
        {
            var imagePage = (TableBTreeLeafPage)page;
            var schema = CorrelatePageImageToSchema(
                imagePage, pageNum, database, pageTableMap, allSchemas,
                liveRowIds, recoveredRowIds, freelistPages);
            // An image with no live cells can't be matched by rowid; for deleted data, fall back to
            // the table that owns the page now.
            if (schema is null && deletedDataFlags is not null && imagePage.Cells.Count == 0 &&
                pageTableMap.TryGetValue(pageNum, out var ownerName) && allSchemas.TryGetValue(ownerName, out var owner))
                schema = owner.Schema;
            if (schema is null) continue;

            string tableName = schema.TableName;
            var knownLive      = liveRowIds.GetValueOrDefault(tableName)    ?? [];
            var knownRecovered = recoveredRowIds.GetValueOrDefault(tableName) ?? [];
            var knownPayloads  = livePayloads.GetValueOrDefault(tableName)  ?? [];

            // Rowids this source has already inserted (oldest version wins)
            var addedSet = previouslyAdded[tableName];

            string insertSql = insertSqlPerTable[tableName];

            foreach (var cell in imagePage.Cells)
            {
                long rowId = cell.RowId.Value;
                if (addedSet.Contains(rowId)) continue;

                if (!knownLive.Contains(rowId) && !knownRecovered.Contains(rowId))
                {
                    // Stage 2: record deleted before the current DB version
                    ReplacePartialCopies(connection, transaction, schema, partialCopies[tableName], ValueKey(schema, cell.FieldValues));
                    InsertRecoveredCellInTransaction(connection, transaction, insertSql, schema, cell, pageNum, deletedMethod, walFrame);
                    addedSet.Add(rowId);
                    recoveredValueKeys[tableName].Add(ValueKey(schema, cell.FieldValues));
                    inserted++;
                }
                else if (knownLive.Contains(rowId) && knownPayloads.TryGetValue(rowId, out var livePayload))
                {
                    // Stage 3: record still exists but payload has changed — keep the older version,
                    // unless another pass already recovered this same version (e.g. from the file
                    // as well as from a WAL frame that still held it)
                    if (FieldsDiffer(cell.FieldValues, livePayload) &&
                        !recoveredVersions[tableName].Contains((rowId, ValueKey(schema, cell.FieldValues))))
                    {
                        ReplacePartialCopies(connection, transaction, schema, partialCopies[tableName], ValueKey(schema, cell.FieldValues));
                        InsertRecoveredCellInTransaction(connection, transaction, insertSql, schema, cell, pageNum, previousVersionMethod, walFrame);
                        recoveredVersions[tableName].Add((rowId, ValueKey(schema, cell.FieldValues)));
                        addedSet.Add(rowId);
                        recoveredValueKeys[tableName].Add(ValueKey(schema, cell.FieldValues));
                        inserted++;
                    }
                }
            }

            if (deletedDataFlags is null) continue;

            // Deleted data inside the image: the same three techniques as in-tree recovery
            var candidates = new List<(BTreeLeafCell Cell, string Method)>();
            if (deletedDataFlags.DeletedCells)
                candidates.AddRange(imagePage.DeletedCells.Select(c => (c, RecoveryMethodDeletedCell)));
            if (deletedDataFlags.InTreeCarving)
            {
                imagePage.CarveDeletedCells(recordStructures[tableName]);
                candidates.AddRange(imagePage.CarvedCells.Select(c => (c, RecoveryMethodCarving)));
            }
            if (deletedDataFlags.FreeblockCarving)
            {
                imagePage.CarveFreeblockCells(recordStructures[tableName]);
                candidates.AddRange(imagePage.FreeblockCells.Select(c => (c, RecoveryMethodFreeblock)));
            }

            var liveKeys = liveValueKeys[tableName];
            var seenKeys = recoveredValueKeys[tableName];
            foreach (var (cell, method) in candidates)
            {
                if (cell.FieldValues.All(v => v?.Value is null)) continue;   // nothing left to recover
                string key = ValueKey(schema, cell.FieldValues);
                long rowId = cell.RowId.Value;

                // A copy of a live row: by rowid and values when the rowid survives, otherwise by values
                if (rowId > 0 && knownPayloads.TryGetValue(rowId, out var live) && !FieldsDiffer(cell.FieldValues, live)) continue;
                if (rowId <= 0 && liveKeys.Contains(key)) continue;
                // Already recovered, from this image or an earlier one, or by in-tree recovery —
                // unless this copy kept its rowid and the earlier ones didn't
                bool replacesPartial = rowId > 0 && partialCopies[tableName].ContainsKey(key);
                if (!seenKeys.Add(key) && !replacesPartial) continue;
                if (replacesPartial)
                    ReplacePartialCopies(connection, transaction, schema, partialCopies[tableName], key);

                long shadowRowId = InsertRecoveredCellInTransaction(connection, transaction, insertSql, schema, cell, pageNum, method, walFrame);
                if (rowId <= 0)
                {
                    if (!partialCopies[tableName].TryGetValue(key, out var ids))
                        partialCopies[tableName][key] = ids = [];
                    ids.Add(shadowRowId);
                }
                inserted++;
            }
        }

        transaction.Commit();
        return inserted;
    }

    /// <summary>
    /// A record's values as one comparable string, skipping the rowid-alias column (its value is the
    /// rowid, which deleted data often no longer has). Values are compared as text, so a value that
    /// round-trips through a shadow column's type affinity (e.g. 5 stored in a TEXT column as '5')
    /// still matches.
    /// </summary>
    internal static string ValueKey(TableSchema schema, IReadOnlyList<SqliteValue?> fieldValues) =>
        ValueKey(schema, i => i < fieldValues.Count ? fieldValues[i]?.Value : null);

    internal static string ValueKey(TableSchema schema, Func<int, object?> valueAt)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].IsRowIdAlias) continue;
            object? v = valueAt(i);
            sb.Append(v switch
            {
                null or DBNull => "\u0001",
                byte[] bytes   => "x" + Convert.ToHexString(bytes),
                _              => "v" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
            });
            sb.Append('\u001f');
        }
        return sb.ToString();
    }

    /// <summary>
    /// <see cref="ValueKey(TableSchema, IReadOnlyList{SqliteValue?})"/> of every row already in the
    /// table's recovered table, plus — for rows recovered without a rowid (no rowid-alias column, or
    /// its value NULL or not positive) — their shadow rowids by key, so a complete copy can replace them.
    /// </summary>
    private static (HashSet<string> Keys, Dictionary<string, List<long>> Partials, HashSet<(long RowId, string Key)> Versions)
        GetRecoveredValueKeysFromShadow(SqliteConnection connection, TableSchema schema)
    {
        var keys = new HashSet<string>();
        var partials = new Dictionary<string, List<long>>();
        var versions = new HashSet<(long RowId, string Key)>();
        int aliasIndex = schema.Columns.FindIndex(c => c.IsRowIdAlias);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", schema.Columns.Select(c => QuoteIdentifier(c.Name)))}, rowid " +
                              $"FROM {QuoteIdentifier(RecoveredTablePrefix + schema.TableName)}";
            using var reader = cmd.ExecuteReader();
            int shadowRowIdOrdinal = schema.Columns.Count;
            while (reader.Read())
            {
                string key = ValueKey(schema, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
                keys.Add(key);
                if (aliasIndex >= 0 && !reader.IsDBNull(aliasIndex) && reader.GetValue(aliasIndex) is long id && id > 0)
                {
                    versions.Add((id, key));
                    continue;
                }
                if (!partials.TryGetValue(key, out var ids)) partials[key] = ids = [];
                ids.Add(reader.GetInt64(shadowRowIdOrdinal));
            }
        }
        catch (SqliteException) { /* table may not exist yet */ }
        return (keys, partials, versions);
    }

    /// <summary>Deletes recovered rows that are rowid-less copies of the record with <paramref name="key"/>,
    /// about to be replaced by a complete copy.</summary>
    private static void ReplacePartialCopies(SqliteConnection connection, SqliteTransaction transaction,
        TableSchema schema, Dictionary<string, List<long>> partials, string key)
    {
        if (!partials.Remove(key, out var ids)) return;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"DELETE FROM {QuoteIdentifier(RecoveredTablePrefix + schema.TableName)} " +
                          $"WHERE rowid IN ({string.Join(", ", ids)})";
        cmd.ExecuteNonQuery();
    }

    private static TableSchema? CorrelatePageImageToSchema(
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
            .Where(e => framePage.Cells.All(c => CellMatchesSchema(c, e.Schema)))
            .Select(e => e.Schema)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool CellMatchesSchema(BTreeLeafCell cell, TableSchema schema)
    {
        int expected = schema.Columns.Count(c => !c.IsRowIdAlias);
        return cell.FieldValues.Count == expected;
    }

    private static bool FieldsDiffer(List<SqliteValue?> frameFields, List<SqliteValue?> liveFields)
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

    /// <summary>Rowids already in the table's recovered table — restricted to rows recovered by one
    /// of <paramref name="methods"/> when any are given.</summary>
    private static HashSet<long> GetRecoveredRowIdsFromShadow(SqliteConnection connection, TableSchema schema, params string[] methods)
    {
        var ids = new HashSet<long>();
        var rowIdCol = schema.Columns.FirstOrDefault(c => c.IsRowIdAlias);
        if (rowIdCol is null) return ids; // No rowid alias — can't retrieve rowids

        string recoveredTable = RecoveredTablePrefix + schema.TableName;
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {QuoteIdentifier(rowIdCol.Name)} FROM {QuoteIdentifier(recoveredTable)} WHERE {QuoteIdentifier(rowIdCol.Name)} IS NOT NULL";
            if (methods.Length > 0)
            {
                var placeholders = methods.Select((_, i) => $"@m{i}").ToList();
                cmd.CommandText += $" AND {QuoteIdentifier(RecoveryMethodColumn)} IN ({string.Join(", ", placeholders)})";
                for (int i = 0; i < methods.Length; i++)
                    cmd.Parameters.AddWithValue(placeholders[i], methods[i]);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        catch { /* table may not exist for this schema */ }

        return ids;
    }

    internal static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
