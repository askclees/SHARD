using System.Text.Json.Serialization;
using SHARD.Core.Recovery;

namespace SHARD.Native;

/// <summary>The one JSON shape every exported function returns: success-with-data, or an error message. Never both.</summary>
public sealed class ApiEnvelope
{
    public bool Ok { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }

    public static ApiEnvelope Success(object? data) => new() { Ok = true, Data = data };
    public static ApiEnvelope Failure(string error) => new() { Ok = false, Error = error };
}

/// <summary>Just the open-handle result — kept separate from <see cref="ApiEnvelope"/>'s Data payloads for a stable, minimal shape.</summary>
public sealed record OpenResult(long Handle);

/// <summary>Wire shape for <c>shard_recover_to_file</c>'s options argument — CarveMode is a plain string ("loose"/"tight") rather than the C# enum, to sidestep enum-JSON-converter/source-gen edge cases across the native boundary.</summary>
public sealed record RecoverOptionsInput(bool ProcessWal = true, string? CarveMode = null, List<string>? CarveTableFilter = null);

/// <summary>
/// Source-generated (reflection-free) JSON (de)serialization context — required for Native AOT
/// compatibility (<c>SHARD.Native.csproj</c> publishes with <c>PublishAot=true</c>), since
/// System.Text.Json's default reflection-based serializer isn't trim/AOT-safe. Every type an
/// <see cref="ApiEnvelope.Data"/> can actually hold at runtime must be registered here, plus the
/// primitive types boxed inside each row's <c>Fields</c> dictionary (STJ resolves an
/// <c>object</c>-typed value's concrete serializer from its runtime type against this list).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(ApiEnvelope))]
[JsonSerializable(typeof(OpenResult))]
[JsonSerializable(typeof(RecoverOptionsInput))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(RecoveryResult))]
[JsonSerializable(typeof(DatabaseHeaderInfo))]
[JsonSerializable(typeof(List<SchemaEntryInfo>))]
[JsonSerializable(typeof(List<PageInfo>))]
[JsonSerializable(typeof(List<RowInfo>))]
[JsonSerializable(typeof(List<CarvedRowInfo>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte[]))]
public partial class NativeJsonContext : JsonSerializerContext
{
}
