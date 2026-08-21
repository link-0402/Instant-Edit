using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary>
/// Minimal HTTP server that receives export results from Blender's Quick Export button
/// and applies them to Penumbra as the persistent Instant Edit mod.
/// </summary>
public sealed class ExportServer : IDisposable
{
    private sealed class ExportRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("exportId")]
        public string? ExportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }

    private sealed record HttpRequest(string Method, string Path, byte[] Body);

    private readonly Configuration    _config;
    private readonly PenumbraService  _penumbra;
    private readonly ExportContextRegistry _contexts;
    private readonly bool _ownsContexts;
    private readonly IPluginLog       _log;
    private readonly CancellationTokenSource _cts = new();
    private const int MaxRequestBytes = 256 * 1024;
    private const int MaxHeaderBytes = 64 * 1024;
    private const long MaxExportBytes = 512L * 1024 * 1024;

    private readonly object _listenerLock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;

    public ExportServer(
        Configuration config,
        PenumbraService penumbra,
        ExportContextRegistry contexts,
        IPluginLog log)
    {
        _config   = config;
        _penumbra = penumbra;
        _contexts = contexts;
        _ownsContexts = false;
        _log      = log;
    }

    /// <summary> Compatibility constructor for hosts that do not provide a shared registry. </summary>
    public ExportServer(Configuration config, PenumbraService penumbra, IPluginLog log)
        : this(config, penumbra, new ExportContextRegistry(Guid.NewGuid().ToString("N")), log)
        => _ownsContexts = true;

    public void Start()
    {
        lock (_listenerLock)
        {
            if (_listener is not null || _cts.IsCancellationRequested)
                return;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _config.ListenPort);
                listener.Start();
                _listener = listener;
                var runCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _runCts = runCts;
                _ = Task.Run(() => RunAsync(listener, runCts.Token));
            }
            catch (Exception e)
            {
                _log.Error(e, $"Could not start export receiver on port {_config.ListenPort}.");
                _listener = null;
                _runCts?.Dispose();
                _runCts = null;
                return;
            }
        }

        _log.Information($"Instant Edit export receiver listening on port {_config.ListenPort}.");
    }

    /// <summary> Stop and start the listener using the current configuration. </summary>
    public void Restart()
    {
        StopListener();
        Start();
    }

    private async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClient(client));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                _log.Error(e, "Export receiver accept failed.");
                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 10_000;
            stream.WriteTimeout = 10_000;
            try
            {
                var (request, error) = ReadRequest(stream);
                if (error is not null || request is null)
                {
                    WriteResponse(stream, 400, Json(new { ok = false, error = error ?? "bad request" }));
                    return;
                }

                var (status, body) = await ProcessRequestAsync(request).ConfigureAwait(false);
                WriteResponse(stream, status, body);
            }
            catch (Exception e)
            {
                _log.Debug($"Export receiver client error: {e.Message}");
                try
                {
                    WriteResponse(stream, 500, Json(new { ok = false, error = "internal server error" }));
                }
                catch
                {
                    // The peer may already have disconnected.
                }
            }
        }
    }

    private async Task<(int Status, string Body)> ProcessRequestAsync(HttpRequest request)
    {
        var method = request.Method;
        var path   = request.Path;

        if (method == "GET" && path.TrimEnd('/') == "/status")
            return (200, Json(new { ok = true, running = true, mod = _config.ModName }));

        if (method == "POST" && path.TrimEnd('/') == "/export")
        {
            string body;
            try
            {
                body = new UTF8Encoding(false, true).GetString(request.Body);
            }
            catch (DecoderFallbackException)
            {
                return Error(400, "invalid_utf8", "request body is not valid UTF-8");
            }

            ExportRequest? export;
            try
            {
                export = JsonSerializer.Deserialize<ExportRequest>(body, JsonOpts);
            }
            catch (Exception e)
            {
                _log.Error(e, "Failed to parse export request.");
                return Error(400, "invalid_json", "request body is not valid JSON");
            }

            if (export is null)
                return Error(400, "malformed_request", "request must be a JSON object");

            var envelopeError = ValidateEnvelope(export);
            if (envelopeError is not null)
                return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed export envelope");

            if (!_contexts.TryBeginExport(
                    export.PluginInstanceId!,
                    export.ContextId!,
                    export.ExportId!,
                    export.Capability!,
                    export.FilePath!,
                    export.Size,
                    export.Sha256!,
                    out var reservation,
                    out var registryCode))
                return Error(StatusForCode(registryCode), registryCode, "export context was rejected");

            if (reservation is null)
                return Error(500, "internal_error", "export reservation was not created");

            if (!reservation.IsOwner)
            {
                var duplicate = await reservation.Completion.ConfigureAwait(false);
                return ResultResponse(duplicate);
            }

            ExportReceipt receipt;
            try
            {
                var fileError = await VerifyExportFileAsync(export.FilePath!, export.Size, export.Sha256!).ConfigureAwait(false);
                if (fileError is not null)
                {
                    receipt = new ExportReceipt(false, fileError.Value.Code, fileError.Value.Message);
                }
                else
                {
                    var target = reservation.Context;
                    var result = await _penumbra.ApplyExportAsync(
                        target.ModName,
                        target.GamePath,
                        export.FilePath!,
                        target.ObjectIndex,
                        target.ActorIdentity).ConfigureAwait(false);
                    receipt = new ExportReceipt(
                        result.Success,
                        result.Success ? "export_applied" : "apply_failed",
                        result.Message);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Export processing failed.");
                receipt = new ExportReceipt(false, "internal_error", "export processing failed");
            }

            _contexts.CompleteExport(export.ContextId!, export.ExportId!, receipt);
            _log.Information($"Export {receipt.Code}: {receipt.Message}");
            return ResultResponse(receipt);
        }

        return (404, Json(new { ok = false, error = "not found" }));
    }

    private static string? ValidateEnvelope(ExportRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.export", StringComparison.Ordinal))
            return request.Version == 1 ? "unsupported_schema" : "unsupported_version";
        if (request.Version != 1)
            return "unsupported_version";
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.ExportId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.FilePath) ||
            string.IsNullOrWhiteSpace(request.Sha256))
            return "missing_field";
        if (request.Size <= 0 || request.Size > MaxExportBytes)
            return "invalid_size";
        if (request.Sha256.Length != 64 || request.Sha256.Any(c => !Uri.IsHexDigit(c)))
            return "invalid_sha256";
        return null;
    }

    private static async Task<(string Code, string Message)?> VerifyExportFileAsync(
        string filePath,
        long expectedSize,
        string expectedSha256)
    {
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > 4096 || filePath.Contains('\0') ||
                !Path.IsPathRooted(filePath) || filePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(filePath), ".mdl", StringComparison.OrdinalIgnoreCase) ||
                filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
                return ("unsafe_file_path", "filePath must be a local absolute .mdl path");

            fullPath = Path.GetFullPath(filePath);
            var parent = Path.GetDirectoryName(fullPath);
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                (parent is not null && HasReparsePointInPath(parent)))
                return ("unsafe_file_path", "filePath must not be a reparse point");

            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return ("file_not_found", "export file was not found");
            if (info.Length != expectedSize)
                return ("size_mismatch", "export file size did not match the request");
            if (info.Length > MaxExportBytes)
                return ("invalid_size", "export file is too large");

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var actual = Convert.ToHexString(await sha.ComputeHashAsync(stream).ConfigureAwait(false));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? null
                : ("hash_mismatch", "export file hash did not match the request");
        }
        catch (UnauthorizedAccessException)
        {
            return ("unsafe_file_path", "export file is not readable");
        }
        catch (IOException)
        {
            return ("file_not_readable", "export file could not be read");
        }
        catch (ArgumentException)
        {
            return ("unsafe_file_path", "filePath is invalid");
        }
    }

    private static bool HasReparsePointInPath(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            current = current.Parent;
        }

        return false;
    }

    private static (int Status, string Body) Error(int status, string code, string message)
        => (status, Json(new { ok = false, code, error = message }));

    private static (int Status, string Body) ResultResponse(ExportReceipt receipt)
        => receipt.Success
            ? (200, Json(new { ok = true, code = receipt.Code, message = receipt.Message }))
            : Error(StatusForCode(receipt.Code), receipt.Code, receipt.Message);

    private static int StatusForCode(string code)
        => code switch
        {
            "stale_context" => 410,
            "invalid_capability" or "plugin_instance_mismatch" => 401,
            "duplicate_export_id" => 409,
            "server_stopped" or "internal_error" => 500,
            _ => 400,
        };

    private static (HttpRequest? Request, string? Error) ReadRequest(NetworkStream stream)
    {
        var bytes = new List<byte>(4096);
        var buffer = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0 && bytes.Count <= MaxHeaderBytes)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = FindHeaderEnd(bytes);
        }

        if (headerEnd < 0)
            return (null, "request headers are missing or too large");

        var header = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
        var lines = header.Split("\r\n");
        var requestLine = lines.Length > 0
            ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        if (requestLine.Length < 2)
            return (null, "invalid request line");

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Content-Length:".Length..].Trim();
            if (!int.TryParse(value, out contentLength) || contentLength < 0)
                return (null, "invalid content length");
            if (contentLength > MaxRequestBytes)
                return (null, "request body is too large");
        }

        var bodyStart = headerEnd + 4;
        var requiredBytes = bodyStart + contentLength;
        if (requiredBytes > MaxHeaderBytes + MaxRequestBytes)
            return (null, "request is too large");

        while (bytes.Count < requiredBytes)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, requiredBytes - bytes.Count));
            if (read == 0)
                return (null, "request body ended early");
            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return (new HttpRequest(
            requestLine[0],
            requestLine[1],
            bytes.GetRange(bodyStart, contentLength).ToArray()), null);
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (var i = 3; i < bytes.Count; i++)
        {
            if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' &&
                bytes[i - 1] == '\r' && bytes[i] == '\n')
                return i - 3;
        }

        return -1;
    }

    private static void WriteResponse(NetworkStream stream, int status, string body)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            500 => "Internal Server Error",
            _   => "Not Found",
        };

        var response = $"HTTP/1.1 {status} {reason}\r\n" +
                       "Content-Type: application/json\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                       "Connection: close\r\n\r\n" +
                       body;

        var bytes = Encoding.UTF8.GetBytes(response);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static string Json(object value)
        => JsonSerializer.Serialize(value);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public void Dispose()
    {
        _cts.Cancel();
        StopListener();
        _cts.Dispose();
        if (_ownsContexts)
            _contexts.Dispose();
    }

    private void StopListener()
    {
        lock (_listenerLock)
        {
            _runCts?.Cancel();
            _runCts = null;
            _listener?.Stop();
            _listener = null;
        }
    }
}
