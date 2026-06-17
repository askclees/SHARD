using Microsoft.Data.Sqlite;
using SHARD.Core.Pages;

namespace SHARD.Core.Tests;

public class IndexPageParsingTests
{
    [Fact]
    public void ReadPage_IndexLeafPage_ParsesCells()
    {
        string evidencePath = Path.Combine(Path.GetTempPath(), $"shard_idx_{Guid.NewGuid():N}.db");
        try
        {
            using (var setup = new SqliteConnection($"Data Source={evidencePath}"))
            {
                setup.Open();
                using var cmd = setup.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    INSERT INTO people (name) VALUES ('Alice'), ('Bob'), ('Charlie');
                    CREATE INDEX idx_name ON people (name);
                    """;
                cmd.ExecuteNonQuery();
            }

            long indexRootPage;
            using (var verify = new SqliteConnection($"Data Source={evidencePath};Mode=ReadOnly"))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE type='index' AND name='idx_name'";
                indexRootPage = (long)cmd.ExecuteScalar()!;
            }

            using var db = SqliteForensicDatabase.Open(evidencePath);
            var page = db.ReadPage((uint)indexRootPage);

            var indexLeafPage = Assert.IsType<IndexBTreeLeafPage>(page);
            Assert.Equal(3, indexLeafPage.Cells.Count);

            // Each cell should have 2 columns: the indexed name + the rowid
            foreach (var cell in indexLeafPage.Cells)
            {
                Assert.Equal(2, cell.FieldValues.Count);
                Assert.NotNull(cell.FieldValues[0]); // name
                Assert.NotNull(cell.FieldValues[1]); // rowid
            }

            // Cells are stored in index order (alphabetical for TEXT)
            var names = indexLeafPage.Cells.Select(c => c.FieldValues[0]!.Value!.ToString()).ToList();
            Assert.Equal(["Alice", "Bob", "Charlie"], names);
        }
        finally
        {
            if (File.Exists(evidencePath)) File.Delete(evidencePath);
        }
    }
}
