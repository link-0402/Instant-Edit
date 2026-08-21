using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary> Talks to the HTTP listener hosted by the Yet Another Addon in Blender. </summary>
public sealed class BlenderClient : IDisposable
{
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
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary> Ping Blender's addon server. </summary>
    public bool IsReachable(int port)
        => IsReachableAsync(port).GetAwaiter().GetResult();

    /// <summary>
    /// Ping Blender's addon server without blocking the caller's thread.
    /// A stopped addon is a normal condition, so connection and timeout failures
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
        string modName)
        => SendImportAsync(port, filePath, gamePath, objectIndex, name, callbackPort, modName)
            .GetAwaiter().GetResult();

    /// <summary> Tell Blender to import the given mdl without blocking the caller's thread. </summary>
    public async Task SendImportAsync(
        int port,
        string filePath,
        string gamePath,
        int objectIndex,
        string name,
        CancellationToken cancellationToken = default)
        => await SendImportAsync(
            port,
            filePath,
            gamePath,
            objectIndex,
            name,
            _callbackPort,
            _modName,
            cancellationToken).ConfigureAwait(false);

    /// <summary> Send an import with the callback port and target mod name used by Quick Export. </summary>
    public async Task SendImportAsync(
        int port,
        string filePath,
        string gamePath,
        int objectIndex,
        string name,
        int callbackPort,
        string modName,
        CancellationToken cancellationToken = default)
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
            // values remain solely for older addon versions which still read them.
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

    public void Dispose()
        => _http.Dispose();
}
