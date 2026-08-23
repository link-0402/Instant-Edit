using System.Text.Json.Serialization;

namespace InstantEdit.Models;

/// <summary> The actor identity captured when an import is started. </summary>
public sealed record ActorIdentity
{
    [JsonPropertyName("objectIndex")]
    public required ushort ObjectIndex { get; init; }

    /// <summary> Native address at capture time; it is only an in-process identity token. </summary>
    [JsonPropertyName("address")]
    public required long Address { get; init; }
}

/// <summary> Versioned, plugin-owned context sent to the Blender addon. </summary>
public sealed record InstantEditImportContext
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "instant-edit.context";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("pluginInstanceId")]
    public required string PluginInstanceId { get; init; }

    [JsonPropertyName("contextId")]
    public required string ContextId { get; init; }

    [JsonPropertyName("importId")]
    public required string ImportId { get; init; }

    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("gamePath")]
    public required string GamePath { get; init; }

    [JsonPropertyName("objectIndex")]
    public required ushort ObjectIndex { get; init; }

    [JsonPropertyName("actorIdentity")]
    public ActorIdentity? ActorIdentity { get; init; }

    [JsonPropertyName("modName")]
    public required string ModName { get; init; }

    /// <summary>Original resolved model file inside the source Penumbra mod.</summary>
    [JsonPropertyName("targetFilePath")]
    public required string TargetFilePath { get; init; }

    /// <summary>Physical directory displayed in Blender as the Quick Export target.</summary>
    [JsonPropertyName("managedDestination")]
    public required string TargetFolder { get; init; }

    /// <summary>Penumbra's directory key, used by ReloadMod.</summary>
    [JsonPropertyName("sourceModDirectory")]
    public required string SourceModDirectory { get; init; }

    [JsonPropertyName("sourceModName")]
    public required string SourceModName { get; init; }

    /// <summary>Physical root directory of the source Penumbra mod.</summary>
    [JsonPropertyName("sourceModRootPath")]
    public string? SourceModRootPath { get; init; }

    [JsonPropertyName("callbackPort")]
    public required int CallbackPort { get; init; }
}

/// <summary>
/// Durable portion of an import context. Runtime-only actor identity and
/// in-flight export reservations are intentionally excluded.
/// </summary>
public sealed record PersistedExportContext
{
    [JsonPropertyName("contextId")]
    public required string ContextId { get; init; }

    [JsonPropertyName("importId")]
    public required string ImportId { get; init; }

    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("gamePath")]
    public required string GamePath { get; init; }

    [JsonPropertyName("objectIndex")]
    public required ushort ObjectIndex { get; init; }

    [JsonPropertyName("modName")]
    public required string ModName { get; init; }

    [JsonPropertyName("targetFilePath")]
    public required string TargetFilePath { get; init; }

    [JsonPropertyName("managedDestination")]
    public required string TargetFolder { get; init; }

    [JsonPropertyName("sourceModDirectory")]
    public required string SourceModDirectory { get; init; }

    [JsonPropertyName("sourceModName")]
    public required string SourceModName { get; init; }

    [JsonPropertyName("sourceModRootPath")]
    public string? SourceModRootPath { get; init; }

    [JsonPropertyName("callbackPort")]
    public required int CallbackPort { get; init; }

    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    public static PersistedExportContext FromContext(InstantEditImportContext context, DateTimeOffset expiresAt)
        => new()
        {
            ContextId = context.ContextId,
            ImportId = context.ImportId,
            Capability = context.Capability,
            GamePath = context.GamePath,
            ObjectIndex = context.ObjectIndex,
            ModName = context.ModName,
            TargetFilePath = context.TargetFilePath,
            TargetFolder = context.TargetFolder,
            SourceModDirectory = context.SourceModDirectory,
            SourceModName = context.SourceModName,
            SourceModRootPath = context.SourceModRootPath,
            CallbackPort = context.CallbackPort,
            ExpiresAt = expiresAt,
        };
}

/// <summary> A completed export operation retained for idempotent retries. </summary>
public sealed record ExportReceipt(bool Success, string Code, string Message);
