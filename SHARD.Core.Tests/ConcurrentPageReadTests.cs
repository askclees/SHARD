using Xunit;

namespace SHARD.Core.Tests;

/// <summary>
/// The GUI reads pages on its own thread while loading and counting run on background threads,
/// so reading pages from several threads at once must give each the right page's bytes.
/// </summary>
public class ConcurrentPageReadTests
{
    [Fact]
    public void ReadPage_FromManyThreadsAtOnce_ReturnsEachPagesOwnBytes()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "interior_no_overflow.db");
        using var db = SqliteForensicDatabase.Open(path);
        Assert.True(db.PageCount > 1);

        var expected = Enumerable.Range(1, (int)db.PageCount)
            .ToDictionary(p => (uint)p, p => db.ReadPage((uint)p).Data);

        // Many readers each walking the pages in a different order, so reads interleave.
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, reader =>
        {
            for (int round = 0; round < 50; round++)
            {
                for (uint i = 0; i < db.PageCount; i++)
                {
                    uint page = (uint)((i * 7 + reader + round) % db.PageCount) + 1;
                    Assert.Equal(expected[page], db.ReadPage(page).Data);
                }
            }
        });
    }
}
