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
    private readonly int _callbackPort;
    private readonly string _modName;
    private readonly ExportContextRegistry? _contexts;
    private readonly Func<int, ActorIdentity?>? _actorIdentityProvider;

    public BlenderClient(
        IPluginLog log,
        int callbackPort = 42428,
        string modName = "InstantEdit",
        ExportContextRegistry? contexts = null,
        Func<int, ActorIdentity?>? actorIdentityProvider = null)
    {
        _log         = log;
        _callbackPort = callbackPort is >= 1 and <= 65535 ? callbackPort : 42428;
        _modName     = string.IsNullOrWhiteSpace(modName) ? "InstantEdit" : modName;
        _contexts    = contexts;
        _actorIdentityProvider = actorIdentityProvider;
        _http        = new HttpClient
        {
            // Import handoff can include a bounded material-preview bundle. The
            // caller supplies short cancellation tokens for status probes, while
            // an actual local handoff is allowed enough time to copy large files.
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary> Ping Blender's add-on server. </summary>
    public bool IsReachable(int port)
        => IsReachableAsync(port).GetAwaiter().GetResult();

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

    /// <summary> Tell Blender to import the given mdl into the active scene. </summary>
    public void SendImport(int port, string filePath, string gamePath, int objectIndex, string name)
        => SendImport(port, filePath, gamePath, objectIndex, name, _callbackPort, _modName);

    /// <summary> Compatibility overload that includes the export callback contract. </summary>
    public void SendImport(
        int port,
        string filePath,
        string gamePath,
        int objectIndex,
        string name,
        int callbackPort,
        string modName,
        BlenderImportOptions? importOptions = null)
        => SendImportAsync(port, filePath, gamePath, objectIndex, name, callbackPort, modName,
                importOptions: importOptions)
            .GetAwaiter().GetResult();

    /// <summary> Tell Blender to import the given mdl without blocking the caller's thread. </summary>
    public async Task SendImportAsync(
        int port,
        string filePath,
        string gamePath,
        int objectIndex,
        string name,
        CancellationToken cancellationToken = default,
        BlenderImportOptions? importOptions = null)
        => await SendImportAsync(
            port,
            filePath,
            gamePath,
            objectIndex,
            name,
            _callbackPort,
            _modName,
            cancellationToken,
            importOptions).ConfigureAwait(false);

    /// <summary> Send an import with the callback port and target mod name used by Quick Export. </summary>
    public async Task SendImportAsync(
        int port,
        string filePath,
        string gamePath,
        int objectIndex,
        string name,
        int callbackPort,
        string modName,
        CancellationToken cancellationToken = default,
        BlenderImportOptions? importOptions = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (callbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(callbackPort));
        if (string.IsNullOrWhiteSpace(modName))
            throw new ArgumentException("A mod name is required.", nameof(modName));

        InstantEditImportContext? context = null;
        if (_contexts is not null)
        {
            var actorIdentity = _actorIdentityProvider?.Invoke(objectIndex);
            context = _contexts.CreateContext(
                gamePath,
                objectIndex,
                modName,
                callbackPort,
                actorIdentity);
        }

        try
        {
            // The nested context is authoritative for v1. The legacy top-level
            // values remain solely for older add-on versions which still read them.
            var payload = JsonSerializer.Serialize(new
            {
                schema      = "instant-edit.import",
                version     = 1,
                command     = "import",
                context,
                filePath,
                gamePath,
                objectIndex,
                name,
                callbackPort,
                modName,
                importOptions = importOptions ?? BlenderImportOptions.Generated,
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp    = await _http.PostAsync(
                $"http://127.0.0.1:{port}/import",
                content,
                cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        catch
        {
            if (context is not null)
                _contexts?.RemoveContext(context.ContextId);
            throw;
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
        ActorIdentity? redrawActorIdentity = null,
        ResourceDependencyManifest? resourceManifest = null,
        bool resourceManifestCaptureAttempted = false)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (callbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(callbackPort));
        if (string.IsNullOrWhiteSpace(sourceModDirectory))
            throw new ArgumentException("A source Penumbra mod directory is required.", nameof(sourceModDirectory));
        if (string.IsNullOrWhiteSpace(targetFilePath))
            throw new ArgumentException("An original model path is required.", nameof(targetFilePath));

        InstantEditImportContext? context = null;
        if (_contexts is not null)
        {
            context = _contexts.CreateContext(
                gamePath,
                objectIndex,
                sourceModDirectory,
                targetFilePath,
                sourceModName,
                callbackPort,
                redrawActorIdentity,
                sourceModRootPath,
                targetRelativePath,
                resourceManifest,
                resourceManifestCaptureAttempted);
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                schema = "instant-edit.import",
                version = 1,
                command = "import",
                context,
                filePath = importFilePath,
                gamePath,
                objectIndex,
                name,
                callbackPort,
                modName = sourceModDirectory,
                targetFilePath,
                targetFolder = Path.GetDirectoryName(targetFilePath),
                sourceModDirectory,
                sourceModName,
                sourceModRootPath,
                targetRelativePath,
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
            if (context is not null)
                _contexts?.RemoveContext(context.ContextId);
            throw;
        }
    }

    public void Dispose()
        => _http.Dispose();
}
