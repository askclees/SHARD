using System.Text.Json;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

public class ShadowProjectTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void Create_WritesManifestAndShadowDatabase()
    {
        string projectFolder = Path.Combine(Path.GetTempPath(), $"shard_project_{Guid.NewGuid():N}");
        try
        {
            string evidencePath = FixturePath("single_leaf_no_overflow.db");
            using var db = SqliteForensicDatabase.Open(evidencePath);

            var (project, _) = ShadowProject.Create(projectFolder, evidencePath, db);

            Assert.True(File.Exists(project.ManifestPath));
            Assert.True(File.Exists(project.ShadowDatabasePath));

            var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(project.ManifestPath));
            Assert.NotNull(manifest);
            Assert.Equal(evidencePath, manifest!.EvidenceFilePath);
        }
        finally
        {
            if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
        }
    }

    [Fact]
    public void Create_ThrowsWhenShadowDatabaseAlreadyExists()
    {
        string projectFolder = Path.Combine(Path.GetTempPath(), $"shard_project_{Guid.NewGuid():N}");
        try
        {
            string evidencePath = FixturePath("single_leaf_no_overflow.db");
            using var db = SqliteForensicDatabase.Open(evidencePath);

            ShadowProject.Create(projectFolder, evidencePath, db);

            Assert.Throws<InvalidOperationException>(() =>
                ShadowProject.Create(projectFolder, evidencePath, db));
        }
        finally
        {
            if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
        }
    }
}
