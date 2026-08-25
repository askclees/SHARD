using System.Collections.Concurrent;
using System.Text.Json;
using SHARD.Core;
using SHARD.Core.Recovery;

namespace SHARD.Native;

/// <summary>
/// The managed core behind every <c>shard_*</c> native export — ordinary string-in/string-out C#
/// methods with no unsafe/pointer code, so they're directly unit-testable without needing an AOT
/// publish or a P/Invoke caller. <c>NativeExports</c> is a thin marshalling layer on top of this.
/// A "handle" here is just a validated file path kept in a session table; each call re-opens the
/// file via <see cref="SqliteRecoveryFacade"/>'s stateless, path-based methods rather than holding
/// a long-lived <c>SqliteForensicDatabase</c> open, so there's exactly one code path (the facade's)
/// doing the actual parsing/mapping — nothing here duplicates that logic.
/// </summary>
public static class RecoveryApi
{
    private static readonly ConcurrentDictionary<long, string> Sessions = new();
    private static long _nextHandle;

    static RecoveryApi() => NativeLibraryResolver.EnsureInitialized();

    public static string Open(string path)
    {
        try
        {
            using (SqliteForensicDatabase.Open(path)) { } // validate before handing back a handle
            long handle = Interlocked.Increment(ref _nextHandle);
            Sessions[handle] = path;
            return Serialize(ApiEnvelope.Success(new OpenResult(handle)));
        }
        catch (Exception ex)
        {
            return Serialize(ApiEnvelope.Failure(ex.Message));
        }
    }

    public static void Close(long handle) => Sessions.TryRemove(handle, out _);

    public static string GetHeader(long handle) =>
        WithPath(handle, path => SqliteRecoveryFacade.GetHeader(path));

    public static string GetSchema(long handle) =>
        WithPath(handle, path => SqliteRecoveryFacade.GetSchema(path).ToList());

    public static string GetPages(long handle) =>
        WithPath(handle, path => SqliteRecoveryFacade.GetPages(path).ToList());

    public static string GetRows(long handle, string tableName) =>
        WithPath(handle, path => SqliteRecoveryFacade.GetRows(path, tableName).ToList());

    public static string GetDeletedRows(long handle, string tableName) =>
        WithPath(handle, path => SqliteRecoveryFacade.GetDeletedRows(path, tableName).ToList());

    public static string Carve(long handle, string mode, string? tableFilterJson) =>
        WithPath(handle, path =>
        {
            var filter = tableFilterJson is null
                ? null
                : JsonSerializer.Deserialize(tableFilterJson, NativeJsonContext.Default.ListString);
            return SqliteRecoveryFacade.CarveUnknownPages(path, ParseCarveMode(mode), filter).ToList();
        });

    public static string RecoverToFile(long handle, string outputPath, string? optionsJson) =>
        WithPath(handle, path =>
        {
            var input = optionsJson is null
                ? new RecoverOptionsInput()
                : JsonSerializer.Deserialize(optionsJson, NativeJsonContext.Default.RecoverOptionsInput) ?? new RecoverOptionsInput();

            var options = new RecoveryOptions(
                ProcessWal: input.ProcessWal,
                CarveMode: input.CarveMode is null ? null : ParseCarveMode(input.CarveMode),
                CarveTableFilter: input.CarveTableFilter);

            return SqliteRecoveryFacade.Recover(path, outputPath, options);
        });

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CarveMode ParseCarveMode(string mode) =>
        mode.Equals("tight", StringComparison.OrdinalIgnoreCase) ? CarveMode.Tight : CarveMode.Loose;

    private static string WithPath(long handle, Func<string, object?> body)
    {
        if (!Sessions.TryGetValue(handle, out var path))
            return Serialize(ApiEnvelope.Failure($"Unknown handle {handle} (already closed, or never opened)."));

        try
        {
            return Serialize(ApiEnvelope.Success(body(path)));
        }
        catch (Exception ex)
        {
            return Serialize(ApiEnvelope.Failure(ex.Message));
        }
    }

    private static string Serialize(ApiEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, NativeJsonContext.Default.ApiEnvelope);
}
