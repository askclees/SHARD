using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;

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
    private const string PageNumberColumn = "_page_number";
    private const string CellOffsetColumn = "_cell_offset";
    private const string OverflowPageColumn = "_overflow_page";

    /// <summary>Prefix for tables SHARD itself creates in the shadow database (as opposed to mirrored evidence tables), so consumers can filter them out of table listings.</summary>
    public const string InternalTablePrefix = "_shard_";
    private const string OverflowTableName = InternalTablePrefix + "overflow_pages";
    private const string PagesTableName = InternalTablePrefix + "pages";

    public static void Create(string shadowDbPath, SqliteForensicDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
        connection.Open();

        CreateOverflowTable(connection);
        CreatePagesTable(connection);
        PopulatePagesBaseline(connection, database);
        TagTablePages(connection, "sqlite_master", database.GetTreePageNumbers(1));

        foreach (var row in database.ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Table) continue;
            if (row.Sql is null || row.RootPage is null) continue;
            if (row.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                TagTablePages(connection, row.Name, database.GetTreePageNumbers(row.RootPage.Value));
                continue;
            }
            if (row.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;

            var tableSchema = CreateTableParser.ExtractTableSchema(row.Sql);
            if (tableSchema is null || tableSchema.Columns.Count == 0) continue;

            string createTableSql = BuildCreateTableSql(tableSchema);
            try
            {
                using (var createCommand = connection.CreateCommand())
                {
                    createCommand.CommandText = createTableSql;
                    createCommand.ExecuteNonQuery();
                }

                InsertRows(connection, tableSchema, database.ReadTableRows(row.RootPage.Value));
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
    private static void TagTablePages(SqliteConnection connection, string tableName, IEnumerable<uint> pageNumbers)
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

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
