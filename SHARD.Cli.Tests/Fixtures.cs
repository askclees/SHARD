using Microsoft.Data.Sqlite;

namespace SHARD.Cli.Tests;

internal static class Fixtures
{
    /// <summary>
    /// A table whose INTEGER PRIMARY KEY (rowid alias) is NOT the first column — the exact
    /// shape that exposed the RowToDict field-shift bug (every field after the alias silently
    /// reported the wrong column's value).
    /// </summary>
    public static string CreateRowidAliasNotFirstColumnDb()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_cli_test_{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE items (name TEXT, id INTEGER PRIMARY KEY, price REAL, note TEXT);
                INSERT INTO items (name, id, price, note) VALUES ('widget', 1, 9.99, 'first');
                INSERT INTO items (name, id, price, note) VALUES ('gadget', 2, 19.99, 'second');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools(); // release the file lock before the CLI subprocess opens it
        return path;
    }

    /// <summary>
    /// A minimal, otherwise-valid 4096-byte-page-size SQLite header with page 1's own type byte
    /// (offset 100) set to an unrecognised value — a valid-header, corrupted-schema-page file,
    /// which used to crash ReadSqliteMaster() with a raw NotImplementedException.
    /// </summary>
    public static string CreateCorruptPage1Db()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_cli_test_{Guid.NewGuid():N}.db");
        var data = new byte[4096];

        "SQLite format 3\0"u8.CopyTo(data);
        WriteBigEndianUInt16(data, 16, 4096); // page size
        data[18] = 1;  // file format write version
        data[19] = 1;  // file format read version
        data[21] = 64; // max embedded payload fraction
        data[22] = 32; // min embedded payload fraction
        data[23] = 32; // leaf payload fraction
        WriteBigEndianUInt32(data, 24, 1); // file change counter
        WriteBigEndianUInt32(data, 28, 1); // database size in pages
        WriteBigEndianUInt32(data, 40, 1); // schema cookie
        WriteBigEndianUInt32(data, 44, 4); // schema format
        WriteBigEndianUInt32(data, 56, 1); // text encoding (UTF-8)
        WriteBigEndianUInt32(data, 96, 3_046_001); // sqlite version number

        data[100] = 0x99; // page 1's own type byte: not a recognised page type at all

        File.WriteAllBytes(path, data);
        return path;
    }

    private static void WriteBigEndianUInt16(byte[] data, int offset, ushort value)
    {
        data[offset]     = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteBigEndianUInt32(byte[] data, int offset, uint value)
    {
        data[offset]     = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
