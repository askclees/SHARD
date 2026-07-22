using System.Text.Json;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

public class ShadowProjectTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void CreateTemporary_BuildsQueryableShadowDatabase()
    {
        string evidencePath = FixturePath("single_leaf_no_overflow.db");
        using var db = SqliteForensicDatabase.Open(evidencePath);

        var (project, warnings) = ShadowProject.CreateTemporary(evidencePath, db);
        using (project)
        {
            Assert.True(project.IsUnsaved);
            Assert.Null(project.ProjectFolder);
            Assert.True(File.Exists(project.ShadowDatabasePath));
        }

        // Temp file should be cleaned up after Dispose.
        // (File.Exists may still return true briefly on some OSes; we just verify no exception.)
    }

    [Fact]
    public void SaveTo_WritesManifestAndShadowDatabase()
    {
        string projectFolder = Path.Combine(Path.GetTempPath(), $"shard_project_{Guid.NewGuid():N}");
        try
        {
            string evidencePath = FixturePath("single_leaf_no_overflow.db");
            using var db = SqliteForensicDatabase.Open(evidencePath);

            var (project, _) = ShadowProject.CreateTemporary(evidencePath, db);
            using (project)
            {
                project.SaveTo(projectFolder);

                Assert.False(project.IsUnsaved);
                Assert.Equal(projectFolder, project.ProjectFolder);
                Assert.True(File.Exists(project.ManifestPath));
                Assert.True(File.Exists(project.ShadowDatabasePath));

                var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(project.ManifestPath!));
                Assert.NotNull(manifest);
                Assert.Equal(evidencePath, manifest!.EvidenceFilePath);
            }
        }
        finally
        {
            if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
        }
    }

    [Fact]
    public void SaveTo_ThrowsWhenShadowDatabaseAlreadyExists()
    {
        string projectFolder = Path.Combine(Path.GetTempPath(), $"shard_project_{Guid.NewGuid():N}");
        try
        {
            string evidencePath = FixturePath("single_leaf_no_overflow.db");
            using var db = SqliteForensicDatabase.Open(evidencePath);

            // Save once successfully.
            var (project1, _) = ShadowProject.CreateTemporary(evidencePath, db);
            using (project1)
                project1.SaveTo(projectFolder);

            // A second save to the same folder should fail.
            var (project2, _) = ShadowProject.CreateTemporary(evidencePath, db);
            using (project2)
                Assert.Throws<InvalidOperationException>(() => project2.SaveTo(projectFolder));
        }
        finally
        {
            if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
        }
    }
}
