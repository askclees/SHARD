using System.Text;
using SHARD.Core.BlobViewers;

namespace SHARD.Core.Tests;

/// <summary>
/// Exercises the real python3 subprocess protocol (not a mock), so these are skipped
/// wholesale if python3 isn't on PATH — the same tradeoff CorpusTests makes for its fixtures.
/// </summary>
public class BlobViewerTests
{
    private static readonly bool Python3Available = IsPython3Available();

    private static bool IsPython3Available()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a plugin folder whose script: `check` exits 0 iff stdin starts with 0xCAFE;
    /// `decode` reads either stdin or a file path arg (per <paramref name="fileMode"/>) and
    /// echoes it back framed as the given <paramref name="kind"/>.
    /// </summary>
    private static string WriteTestPlugin(string root, string name, bool fileMode = false, string kind = "text", string? extension = null)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        string extraJson = extension is null ? "" : $",\"extension\":\"{extension}\"";
        string readData = fileMode
            ? "data = open(sys.argv[2], 'rb').read()"
            : "data = sys.stdin.buffer.read()";

        File.WriteAllText(Path.Combine(dir, "viewer.py"), $$"""
            import sys

            def check():
                sample = sys.stdin.buffer.read()
                sys.exit(0 if sample.startswith(b'\xca\xfe') else 1)

            def decode():
                {{readData}}
                sys.stdout.buffer.write(b'{"kind":"{{kind}}"{{extraJson}}}\n')
                sys.stdout.buffer.write(data)

            if sys.argv[1] == 'check':
                check()
            elif sys.argv[1] == 'decode':
                decode()
            else:
                sys.exit(1)
            """);

        File.WriteAllText(Path.Combine(dir, "viewer.json"), $$"""
            {
              "name": "{{name}}",
              "script": "viewer.py",
              "check":  { "args": ["check"],  "input_mode": "stdin" },
              "decode": { "args": ["decode"], "input_mode": "{{(fileMode ? "file" : "stdin")}}" }
            }
            """);

        return dir;
    }

    [Fact]
    public void Load_DiscoversValidPlugin_SkipsFoldersWithoutManifest()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            WriteTestPlugin(root, "good-plugin");
            Directory.CreateDirectory(Path.Combine(root, "no-manifest-here"));

            var registry = BlobViewerRegistry.Load(root);

            Assert.Single(registry.Plugins);
            Assert.Equal("good-plugin", registry.Plugins[0].Manifest.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_SkipsMalformedManifest_AndMissingScript()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string malformed = Path.Combine(root, "malformed");
            Directory.CreateDirectory(malformed);
            File.WriteAllText(Path.Combine(malformed, "viewer.json"), "{ not valid json ");

            string missingScript = Path.Combine(root, "missing-script");
            Directory.CreateDirectory(missingScript);
            File.WriteAllText(Path.Combine(missingScript, "viewer.json"),
                """{ "name": "x", "script": "nope.py", "check": {}, "decode": {} }""");

            var registry = BlobViewerRegistry.Load(root);

            Assert.Empty(registry.Plugins);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_CombinesMultipleRoots()
    {
        string root1 = Directory.CreateTempSubdirectory().FullName;
        string root2 = Directory.CreateTempSubdirectory().FullName;
        try
        {
            WriteTestPlugin(root1, "plugin-a");
            WriteTestPlugin(root2, "plugin-b");

            var registry = BlobViewerRegistry.Load(root1, root2);

            Assert.Equal(2, registry.Plugins.Count);
        }
        finally
        {
            Directory.Delete(root1, recursive: true);
            Directory.Delete(root2, recursive: true);
        }
    }

    [Fact]
    public void CanHandle_ReturnsTrueForMatchingSample_FalseOtherwise()
    {
        if (!Python3Available) return;

        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dir = WriteTestPlugin(root, "sniffer");
            var plugin = new BlobViewerPlugin(LoadManifest(dir), dir);

            Assert.True(plugin.CanHandle([0xCA, 0xFE, 0x01, 0x02]));
            Assert.False(plugin.CanHandle([0x00, 0x01, 0x02]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Regression test for a real deadlock: RunPhase used to start its stdout/stderr reads as
    /// plain awaited Tasks (CopyToAsync/ReadToEndAsync), then block on them with
    /// GetAwaiter().GetResult(). On a thread with an installed SynchronizationContext — e.g.
    /// Avalonia's UI dispatcher, which is exactly the thread BlobViewerWindow calls
    /// CanHandle/Decode from — those Tasks' continuations try to resume on that same captured
    /// context, which the blocking wait is itself occupying, hanging forever. A plain xunit
    /// test thread has no SynchronizationContext, so this bug didn't reproduce without
    /// installing a stand-in one that (like a blocked UI thread) never pumps its queue.
    /// </summary>
    [Fact]
    public void CanHandle_DoesNotDeadlock_UnderABlockedSynchronizationContext()
    {
        if (!Python3Available) return;

        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dir = WriteTestPlugin(root, "ctx-plugin");
            var plugin = new BlobViewerPlugin(LoadManifest(dir), dir);

            bool? result = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new NeverPumpsSynchronizationContext());
                result = plugin.CanHandle([0xCA, 0xFE, 0x01, 0x02]);
            });
            thread.IsBackground = true;
            thread.Start();

            bool joined = thread.Join(TimeSpan.FromSeconds(10));

            Assert.True(joined, "CanHandle deadlocked under a captured SynchronizationContext that never pumps.");
            Assert.True(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Simulates a blocked UI thread: any continuation posted to it just sits forever, unrun.</summary>
    private sealed class NeverPumpsSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* never invoked, by design */ }
    }

    [Fact]
    public void Decode_StdinMode_RoundTripsPayloadAndKind()
    {
        if (!Python3Available) return;

        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dir = WriteTestPlugin(root, "stdin-plugin", fileMode: false, kind: "image");
            var plugin = new BlobViewerPlugin(LoadManifest(dir), dir);

            byte[] input = [0xCA, 0xFE, 0xDE, 0xAD, 0xBE, 0xEF];
            var result = plugin.Decode(input);

            Assert.NotNull(result);
            Assert.Equal(BlobViewerKind.Image, result!.Kind);
            Assert.Equal(input, result.Payload);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Decode_FileMode_RoundTripsPayload()
    {
        if (!Python3Available) return;

        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dir = WriteTestPlugin(root, "file-plugin", fileMode: true, kind: "binary", extension: ".heic");
            var plugin = new BlobViewerPlugin(LoadManifest(dir), dir);

            byte[] input = Encoding.UTF8.GetBytes("hello from a temp file");
            var result = plugin.Decode(input);

            Assert.NotNull(result);
            Assert.Equal(BlobViewerKind.Binary, result!.Kind);
            Assert.Equal(".heic", result.SuggestedExtension);
            Assert.Equal(input, result.Payload);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindHandler_PicksFirstMatchingPlugin_NullWhenNoneMatch()
    {
        if (!Python3Available) return;

        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            WriteTestPlugin(root, "matcher");
            var registry = BlobViewerRegistry.Load(root);

            var match = registry.FindHandler([0xCA, 0xFE, 0x00]);
            Assert.NotNull(match);
            Assert.Equal("matcher", match!.Manifest.Name);

            var noMatch = registry.FindHandler([0x11, 0x22, 0x33]);
            Assert.Null(noMatch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BlobViewerManifest LoadManifest(string dir) =>
        System.Text.Json.JsonSerializer.Deserialize<BlobViewerManifest>(
            File.ReadAllText(Path.Combine(dir, "viewer.json")),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })!;
}
