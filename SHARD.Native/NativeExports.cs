using System.Runtime.InteropServices;
using System.Text;

namespace SHARD.Native;

/// <summary>
/// The actual C ABI surface, published as native exports when this project is
/// <c>dotnet publish</c>'d with <c>PublishAot=true</c>. Every function here does nothing but
/// marshal UTF-8 C strings to/from managed <see cref="string"/> and call straight into
/// <see cref="RecoveryApi"/> — all real logic lives there, where it's testable without pointers.
///
/// Every function that returns data returns a heap-allocated, null-terminated UTF-8 buffer
/// containing a JSON <see cref="ApiEnvelope"/> (<c>{"ok":true,"data":...}</c> or
/// <c>{"ok":false,"error":"..."}</c>) — callers must pass it to <c>shard_free_string</c> when
/// done. No exception ever crosses an <see cref="UnmanagedCallersOnlyAttribute"/> boundary
/// (undefined behavior / process-crashing) — <see cref="RecoveryApi"/> already catches
/// everything and encodes failures in the envelope, but the marshalling code here is wrapped
/// too as a last-resort safety net.
/// </summary>
public static unsafe class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "shard_open")]
    public static byte* ShardOpen(byte* pathUtf8) =>
        Guard(() => RecoveryApi.Open(RequireString(pathUtf8, nameof(pathUtf8))));

    [UnmanagedCallersOnly(EntryPoint = "shard_close")]
    public static void ShardClose(long handle)
    {
        try { RecoveryApi.Close(handle); } catch { /* close is best-effort */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "shard_get_header")]
    public static byte* ShardGetHeader(long handle) =>
        Guard(() => RecoveryApi.GetHeader(handle));

    [UnmanagedCallersOnly(EntryPoint = "shard_get_schema")]
    public static byte* ShardGetSchema(long handle) =>
        Guard(() => RecoveryApi.GetSchema(handle));

    [UnmanagedCallersOnly(EntryPoint = "shard_get_pages")]
    public static byte* ShardGetPages(long handle) =>
        Guard(() => RecoveryApi.GetPages(handle));

    [UnmanagedCallersOnly(EntryPoint = "shard_get_rows")]
    public static byte* ShardGetRows(long handle, byte* tableNameUtf8) =>
        Guard(() => RecoveryApi.GetRows(handle, RequireString(tableNameUtf8, nameof(tableNameUtf8))));

    [UnmanagedCallersOnly(EntryPoint = "shard_get_deleted_rows")]
    public static byte* ShardGetDeletedRows(long handle, byte* tableNameUtf8) =>
        Guard(() => RecoveryApi.GetDeletedRows(handle, RequireString(tableNameUtf8, nameof(tableNameUtf8))));

    [UnmanagedCallersOnly(EntryPoint = "shard_carve")]
    public static byte* ShardCarve(long handle, byte* modeUtf8, byte* tableFilterJsonUtf8) =>
        Guard(() => RecoveryApi.Carve(handle, RequireString(modeUtf8, nameof(modeUtf8)), ToString(tableFilterJsonUtf8)));

    [UnmanagedCallersOnly(EntryPoint = "shard_recover_to_file")]
    public static byte* ShardRecoverToFile(long handle, byte* outputPathUtf8, byte* optionsJsonUtf8) =>
        Guard(() => RecoveryApi.RecoverToFile(handle, RequireString(outputPathUtf8, nameof(outputPathUtf8)), ToString(optionsJsonUtf8)));

    [UnmanagedCallersOnly(EntryPoint = "shard_free_string")]
    public static void ShardFreeString(byte* ptr)
    {
        if (ptr != null) NativeMemory.Free(ptr);
    }

    // ── Marshalling helpers ──────────────────────────────────────────────────

    private static byte* Guard(Func<string> body)
    {
        string json;
        try { json = body(); }
        catch (Exception ex) { json = $$"""{"ok":false,"error":{{JsonEscape(ex.Message)}}}"""; }
        return StringToUtf8Ptr(json);
    }

    private static string JsonEscape(string s) => System.Text.Json.JsonSerializer.Serialize(s, NativeJsonContext.Default.String);

    private static string? ToString(byte* ptr) => ptr == null ? null : Marshal.PtrToStringUTF8((nint)ptr);

    private static string RequireString(byte* ptr, string paramName) =>
        ToString(ptr) ?? throw new ArgumentNullException(paramName);

    private static byte* StringToUtf8Ptr(string s)
    {
        int byteCount = Encoding.UTF8.GetByteCount(s);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)(byteCount + 1));
        Encoding.UTF8.GetBytes(s, new Span<byte>(buffer, byteCount));
        buffer[byteCount] = 0; // null terminator
        return buffer;
    }
}
