using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services;

public enum BlenderConnectionState
{
    Offline,
    Online,
    VersionMismatch,
}

public sealed record BlenderStatus(bool Reachable, string? AddonVersion)
{
    public BlenderConnectionState Classify(string expectedPluginVersion)
    {
        if (!Reachable)
            return BlenderConnectionState.Offline;

        var expected = BlenderClient.NormalizeVersion(expectedPluginVersion);
        var actual = BlenderClient.NormalizeVersion(AddonVersion);
        return !string.IsNullOrEmpty(expected) && string.Equals(actual, expected, StringComparison.Ordinal)
            ? BlenderConnectionState.Online
            : BlenderConnectionState.VersionMismatch;
    }
}

/// <summary> Talks to the HTTP listener hosted by XIV Instant Edit in Blender. </summary>
public sealed class BlenderClient : IDisposable
{
    public const string ImportOptionsCapability = "instant-edit.import-options.v1";
    public const string MaterialPreviewCapability = "instant-edit.material-preview.v1";
    public const string CacheHandoffCapability = "instant-edit.cache-handoff.v1";
    public const string VanillaContextCapability = "instant-edit.vanilla-context.v1";

    private readonly HttpClient _http;
    private readonly IPluginLog _log;
    private readonly ExportContextRegistry _contexts;
    private readonly bool _disposeHttp;

    public BlenderClient(IPluginLog log, ExportContextRegistry contexts)
        : this(log, contexts, new HttpClient
        {
            // Import handoff can include a bounded material-preview bundle. The
            // caller supplies short cancellation tokens for status probes, while
            // an actual local handoff is allowed enough time to copy large files.
            Timeout = TimeSpan.FromMinutes(2),
        }, true)
    {
    }

    internal BlenderClient(IPluginLog log, ExportContextRegistry contexts, HttpClient http)
        : this(log, contexts, http, false)
    {
    }

    private BlenderClient(IPluginLog log, ExportContextRegistry contexts, HttpClient http, bool disposeHttp)
    {
        _log         = log;
        _contexts    = contexts;
        _http        = http;
        _disposeHttp = disposeHttp;
    }

    public static string CurrentPluginVersion
    {
        get
        {
            var version = typeof(Plugin).Assembly.GetName().Version;
            return version is null || version.Build < 0
                ? "unknown"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || !Version.TryParse(version.Trim(), out var parsed) || parsed.Build < 0)
            return string.Empty;
        return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
    }

    public static string VersionMismatchMessage(string pluginVersion)
        => $"Version mismatch. Verify Blender addon version is in sync with Plugin version {pluginVersion}.";

    /// <summary>Reads the Blender add-on status and its declared release version.</summary>
    public async Task<BlenderStatus> GetStatusAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            return new BlenderStatus(false, null);

        try
        {
            using var resp = await _http.GetAsync(
                $"http://127.0.0.1:{port}/status",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new BlenderStatus(false, null);

            var responseBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("addonVersion", out var addonVersion) ||
                    addonVersion.ValueKind != JsonValueKind.String)
                    return new BlenderStatus(true, null);

                var version = addonVersion.GetString();
                return new BlenderStatus(true, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
            }
            catch (JsonException)
            {
                return new BlenderStatus(true, null);
            }
        }
        catch (OperationCanceledException)
        {
            return new BlenderStatus(false, null);
        }
        catch (HttpRequestException)
        {
            return new BlenderStatus(false, null);
        }
        catch (InvalidOperationException)
        {
            return new BlenderStatus(false, null);
        }
    }

    /// <summary>
    /// Ping Blender's add-on server without blocking the caller's thread.
    /// A stopped add-on is a normal condition, so connection and timeout failures
    /// are reported as false rather than escaping to the UI thread.
    /// </summary>
    public async Task<bool> IsReachableAsync(int port, CancellationToken cancellationToken = default)
        => (await GetStatusAsync(port, cancellationToken).ConfigureAwait(false)).Reachable;

    /// <summary>Returns whether the connected add-on advertises import options support.</summary>
    public async Task<bool> SupportsImportOptionsAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, ImportOptionsCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns whether the connected add-on can consume material preview bundles.</summary>
    public async Task<bool> SupportsMaterialPreviewAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, MaterialPreviewCapability, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SupportsCacheHandoffAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, CacheHandoffCapability, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SupportsVanillaContextAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, VanillaContextCapability, cancellationToken).ConfigureAwait(false);

    private async Task<bool> SupportsCapabilityAsync(
        int port,
        string capability,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            return false;

        try
        {
            using var resp = await _http.GetAsync(
                $"http://127.0.0.1:{port}/status",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("capabilities", out var capabilities) &&
                   capabilities.ValueKind == JsonValueKind.Array &&
                   capabilities.EnumerateArray().Any(item =>
                       item.ValueKind == JsonValueKind.String &&
                       string.Equals(item.GetString(), capability, StringComparison.Ordinal));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Send an import whose Quick Export authority is the resolved file inside
    /// the original Penumbra mod. The physical path is retained in the shared
    /// registry, so Blender can display it but cannot substitute another target.
    /// </summary>
    public async Task<bool> SendSourceImportAsync(
        int port,
        string importFilePath,
        string gamePath,
        int objectIndex,
        string name,
        int callbackPort,
        string targetFilePath,
        string sourceModDirectory,
        string sourceModName,
        CancellationToken cancellationToken = default,
        BlenderImportOptions? importOptions = null,
        string? previewManifestPath = null,
        string? sourceModRootPath = null,
        string? targetRelativePath = null,
        ResourceDependencyManifest? resourceManifest = null,
        Guid? targetCollectionId = null,
        string? targetCollectionName = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (callbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(callbackPort));
        if (string.IsNullOrWhiteSpace(sourceModDirectory))
            throw new ArgumentException("A source Penumbra mod directory is required.", nameof(sourceModDirectory));
        if (string.IsNullOrWhiteSpace(targetFilePath))
            throw new ArgumentException("An original model path is required.", nameof(targetFilePath));

        var context = _contexts.CreateContext(
            gamePath,
            objectIndex,
            sourceModDirectory,
            targetFilePath,
            sourceModName,
            callbackPort,
            sourceModRootPath,
            targetRelativePath,
            resourceManifest,
            targetCollectionId,
            targetCollectionName);

        return await SendImportAsync(
            port, importFilePath, name, context, cancellationToken,
            importOptions, previewManifestPath).ConfigureAwait(false);
    }

    public async Task<bool> SendGameImportAsync(
        int port,
        string importFilePath,
        string gamePath,
        string resolvedGamePath,
        int objectIndex,
        string name,
        int callbackPort,
        Guid? targetCollectionId = null,
        string? targetCollectionName = null,
        CancellationToken cancellationToken = default,
        BlenderImportOptions? importOptions = null,
        string? previewManifestPath = null,
        ResourceDependencyManifest? resourceManifest = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (callbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(callbackPort));

        var context = _contexts.CreateGameContext(
            gamePath,
            resolvedGamePath,
            objectIndex,
            callbackPort,
            targetCollectionId,
            targetCollectionName,
            resourceManifest);

        return await SendImportAsync(
            port, importFilePath, name, context, cancellationToken,
            importOptions, previewManifestPath).ConfigureAwait(false);
    }

    private async Task<bool> SendImportAsync(
        int port,
        string importFilePath,
        string name,
        InstantEditImportContext context,
        CancellationToken cancellationToken,
        BlenderImportOptions? importOptions,
        string? previewManifestPath)
    {

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                schema = context.Schema,
                version = context.Version,
                pluginInstanceId = context.PluginInstanceId,
                contextId = context.ContextId,
                importId = context.ImportId,
                capability = context.Capability,
                filePath = importFilePath,
                sourceGamePath = context.GamePath,
                sourceKind = context.SourceKind,
                resolvedGamePath = context.ResolvedGamePath,
                destinationState = context.DestinationState,
                objectIndex = context.ObjectIndex,
                displayName = name,
                callbackPort = context.CallbackPort,
                managedDestination = context.TargetFolder,
                targetFilePath = context.TargetFilePath,
                sourceModDirectory = context.SourceModDirectory,
                sourceModName = context.SourceModName,
                sourceModRootPath = context.SourceModRootPath,
                targetRelativePath = context.TargetRelativePath,
                targetCollectionId = context.TargetCollectionId,
                targetCollectionName = context.TargetCollectionName,
                resourceManifestVersion = context.ResourceManifestVersion,
                resourceManifestStatus = context.ResourceManifestStatus,
                previewManifestPath,
                importOptions = importOptions ?? BlenderImportOptions.Generated,
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(
                $"http://127.0.0.1:{port}/import",
                content,
                cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var responseBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var response = JsonDocument.Parse(responseBody);
            return response.RootElement.ValueKind == JsonValueKind.Object &&
                   response.RootElement.TryGetProperty("cached", out var cached) &&
                   cached.ValueKind == JsonValueKind.True;
        }
        catch
        {
            _contexts.RemoveContext(context.ContextId);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposeHttp)
            _http.Dispose();
    }
}
