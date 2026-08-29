using System.Text.Json.Serialization;

namespace InstantEdit.Models;

public sealed record SourceResourceLocator
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("gamePath")]
    public required string GamePath { get; init; }

    [JsonPropertyName("sourceModDirectory")]
    public string? SourceModDirectory { get; init; }

    [JsonPropertyName("sourceModRootPath")]
    public string? SourceModRootPath { get; init; }

    [JsonPropertyName("sourceRelativePath")]
    public string? SourceRelativePath { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

public sealed record TextureDependency
{
    [JsonPropertyName("storedGamePath")]
    public required string StoredGamePath { get; init; }

    [JsonPropertyName("effectiveGamePath")]
    public required string EffectiveGamePath { get; init; }

    [JsonPropertyName("flags")]
    public required ushort Flags { get; init; }

    [JsonPropertyName("resource")]
    public required SourceResourceLocator Resource { get; init; }
}

public sealed record MaterialDependency
{
    [JsonPropertyName("modelMaterial")]
    public required string ModelMaterial { get; init; }

    [JsonPropertyName("gamePath")]
    public required string GamePath { get; init; }

    [JsonPropertyName("resource")]
    public required SourceResourceLocator Resource { get; init; }

    [JsonPropertyName("textures")]
    public required IReadOnlyList<TextureDependency> Textures { get; init; }
}

public sealed record ResourceDependencyManifest
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("materials")]
    public required IReadOnlyList<MaterialDependency> Materials { get; init; }
}

/// <summary> Versioned, plugin-owned context sent to the Blender add-on. </summary>
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

    [JsonPropertyName("sourceGamePath")]
    public required string GamePath { get; init; }

    [JsonPropertyName("objectIndex")]
    public required ushort ObjectIndex { get; init; }

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

    /// <summary>
    /// Stable model path relative to the registered Penumbra mod root. This,
    /// together with <see cref="SourceModDirectory"/>, is the durable export
    /// destination; <see cref="TargetFilePath"/> is only the import-time
    /// absolute-path snapshot.
    /// </summary>
    [JsonPropertyName("targetRelativePath")]
    public string? TargetRelativePath { get; init; }

    [JsonPropertyName("callbackPort")]
    public required int CallbackPort { get; init; }

    [JsonPropertyName("resourceManifestVersion")]
    public int ResourceManifestVersion => ResourceManifest?.Version ?? 0;

    [JsonPropertyName("resourceManifestStatus")]
    public string ResourceManifestStatus { get; init; } = "capture_failed";

    [JsonIgnore]
    public ResourceDependencyManifest? ResourceManifest { get; init; }
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

    [JsonPropertyName("targetRelativePath")]
    public string? TargetRelativePath { get; init; }

    [JsonPropertyName("callbackPort")]
    public required int CallbackPort { get; init; }

    [JsonPropertyName("resourceManifest")]
    public ResourceDependencyManifest? ResourceManifest { get; init; }

    [JsonPropertyName("resourceManifestStatus")]
    public string ResourceManifestStatus { get; init; } = "capture_failed";

    public static PersistedExportContext FromContext(InstantEditImportContext context)
        => new()
        {
            ContextId = context.ContextId,
            ImportId = context.ImportId,
            Capability = context.Capability,
            GamePath = context.GamePath,
            ObjectIndex = context.ObjectIndex,
            TargetFilePath = context.TargetFilePath,
            TargetFolder = context.TargetFolder,
            SourceModDirectory = context.SourceModDirectory,
            SourceModName = context.SourceModName,
            SourceModRootPath = context.SourceModRootPath,
            TargetRelativePath = context.TargetRelativePath,
            CallbackPort = context.CallbackPort,
            ResourceManifest = context.ResourceManifest,
            ResourceManifestStatus = context.ResourceManifestStatus,
        };
}

/// <summary> A completed export operation retained for idempotent retries. </summary>
public sealed record ExportReceipt(
    bool Success,
    string Code,
    string Message,
    IReadOnlyList<string>? Warnings = null,
    string? TargetFilePath = null,
    string? DestinationName = null);
