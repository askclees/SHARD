using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SHARD.Core.BlobViewers;

/// <summary>
/// A discovered blob-viewer plugin: a parsed <see cref="BlobViewerManifest"/> plus the folder
/// it lives in. Runs the manifest's <c>check</c>/<c>decode</c> phases as a <c>python3</c>
/// subprocess per the manifest's protocol (see <see cref="BlobViewerManifest"/> doc comments).
/// </summary>
public sealed class BlobViewerPlugin
{
    /// <summary>How many leading bytes of a blob are sampled for <see cref="CanHandle"/>.</summary>
    public const int CheckSampleBytes = 4096;

    private static readonly JsonSerializerOptions HeaderJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BlobViewerManifest Manifest { get; }
    public string FolderPath { get; }

    private string ScriptPath => Path.Combine(FolderPath, Manifest.Script);

    public BlobViewerPlugin(BlobViewerManifest manifest, string folderPath)
    {
        Manifest = manifest;
        FolderPath = folderPath;
    }

    /// <summary>
    /// Runs the manifest's <c>check</c> phase against a small leading sample of the blob.
    /// Returns false (never throws) on any failure — a broken or misbehaving plugin should
    /// just be skipped, not block the search for one that works.
    /// </summary>
    public bool CanHandle(byte[] sample)
    {
        try
        {
            var output = RunPhase(Manifest.Check, sample);
            return output.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the manifest's <c>decode</c> phase against the full blob. Returns null (never
    /// throws) if the process fails, or its stdout doesn't match the header-line-then-payload
    /// protocol.
    /// </summary>
    public BlobViewerResult? Decode(byte[] data)
    {
        try
        {
            var output = RunPhase(Manifest.Decode, data);
            if (output.ExitCode != 0) return null;

            int headerEnd = Array.IndexOf(output.Stdout, (byte)'\n');
            if (headerEnd < 0) return null;

            string headerJson = Encoding.UTF8.GetString(output.Stdout, 0, headerEnd);
            var header = JsonSerializer.Deserialize<BlobViewerOutputHeader>(headerJson, HeaderJsonOptions);
            if (header is null) return null;
            if (!Enum.TryParse<BlobViewerKind>(header.Kind, ignoreCase: true, out var kind)) return null;

            byte[] payload = output.Stdout[(headerEnd + 1)..];
            return new BlobViewerResult(kind, payload, header.Extension);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct PhaseOutput(int ExitCode, byte[] Stdout, string Stderr);

    private PhaseOutput RunPhase(BlobViewerPhase phase, byte[] inputBytes)
    {
        string? tempFile = null;
        try
        {
            var psi = new ProcessStartInfo("python3")
            {
                WorkingDirectory       = FolderPath,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add(ScriptPath);
            foreach (var arg in phase.Args) psi.ArgumentList.Add(arg);

            byte[]? stdinBytes = null;
            if (phase.InputMode == BlobViewerInputMode.Stdin)
            {
                stdinBytes = inputBytes;
            }
            else
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"shard_blob_{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(tempFile, inputBytes);
                psi.ArgumentList.Add(tempFile);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the 'python3' process.");

            // Stdout/stderr are drained on background tasks *before* writing stdin, so a
            // script producing output before we finish writing input can't deadlock on a
            // full pipe buffer in either direction. Task.Run (not a bare async Task) matters
            // here: this method is called synchronously from callers that may be on a UI
            // thread with its own SynchronizationContext (e.g. Avalonia's dispatcher) — an
            // awaited CopyToAsync/ReadToEndAsync would try to resume its continuation on that
            // same captured context, which the blocking GetAwaiter().GetResult() below is
            // itself occupying, deadlocking forever. Task.Run's delegate does its blocking
            // I/O entirely on a thread-pool thread, so completing it never needs to marshal
            // back onto anything the caller's thread might be blocking.
            var stdoutTask = Task.Run(() =>
            {
                using var buffer = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(buffer);
                return buffer.ToArray();
            });
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

            if (stdinBytes is not null)
                process.StandardInput.BaseStream.Write(stdinBytes, 0, stdinBytes.Length);
            process.StandardInput.Close();

            byte[] stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit();

            return new PhaseOutput(process.ExitCode, stdout, stderr);
        }
        finally
        {
            if (tempFile is not null)
            {
                try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
