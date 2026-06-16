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
    private const string OverflowTableName = "_shard_overflow_pages";

    public static void Create(string shadowDbPath, SqliteForensicDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
        connection.Open();

        CreateOverflowTable(connection);

        foreach (var row in database.ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Table) continue;
            if (row.Sql is null || row.RootPage is null) continue;
            if (row.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static void InsertRows(SqliteConnection connection, TableSchema schema, IEnumerable<TableRow> rows)
    {
        using var transaction = connection.BeginTransaction();

        string insertSql = BuildInsertSql(schema);
        string overflowSql = $"""
            INSERT INTO {QuoteIdentifier(OverflowTableName)}
                (table_name, row_id, sequence, page_number, next_page_number, payload_length)
            VALUES (@table, @row_id, @sequence, @page, @next, @length)
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
