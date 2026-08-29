using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary> Talks to the HTTP listener hosted by XIV Instant Edit in Blender. </summary>
public sealed class BlenderClient : IDisposable
{
    public const string ImportOptionsCapability = "instant-edit.import-options.v1";
    public const string MaterialPreviewCapability = "instant-edit.material-preview.v1";
    public const string CacheHandoffCapability = "instant-edit.cache-handoff.v1";

    private readonly HttpClient _http;
    private readonly IPluginLog _log;
    private readonly ExportContextRegistry _contexts;

    public BlenderClient(IPluginLog log, ExportContextRegistry contexts)
    {
        _log         = log;
        _contexts    = contexts;
        _http        = new HttpClient
        {
            // Import handoff can include a bounded material-preview bundle. The
            // caller supplies short cancellation tokens for status probes, while
            // an actual local handoff is allowed enough time to copy large files.
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>
    /// Ping Blender's add-on server without blocking the caller's thread.
    /// A stopped add-on is a normal condition, so connection and timeout failures
    /// are reported as false rather than escaping to the UI thread.
    /// </summary>
    public async Task<bool> IsReachableAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            return false;

        try
        {
            using var resp = await _http.GetAsync(
                $"http://127.0.0.1:{port}/status",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Returns whether the connected add-on advertises import options support.</summary>
    public async Task<bool> SupportsImportOptionsAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, ImportOptionsCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns whether the connected add-on can consume material preview bundles.</summary>
    public async Task<bool> SupportsMaterialPreviewAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, MaterialPreviewCapability, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SupportsCacheHandoffAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, CacheHandoffCapability, cancellationToken).ConfigureAwait(false);

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
        ResourceDependencyManifest? resourceManifest = null)
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
            resourceManifest);

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
                objectIndex = context.ObjectIndex,
                displayName = name,
                callbackPort = context.CallbackPort,
                managedDestination = context.TargetFolder,
                targetFilePath = context.TargetFilePath,
                sourceModDirectory = context.SourceModDirectory,
                sourceModName = context.SourceModName,
                sourceModRootPath = context.SourceModRootPath,
                targetRelativePath = context.TargetRelativePath,
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
        => _http.Dispose();
}
