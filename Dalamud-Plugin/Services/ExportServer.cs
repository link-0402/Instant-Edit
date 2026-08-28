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
/// and applies them to Penumbra as the persistent XIV Instant Edit mod.
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

        [JsonPropertyName("variantName")]
        public string? VariantName { get; set; }

        [JsonPropertyName("setupInPenumbra")]
        public bool SetupInPenumbra { get; set; }

        [JsonPropertyName("variantGroupName")]
        public string? VariantGroupName { get; set; }

        [JsonPropertyName("variantTarget")]
        public string? VariantTarget { get; set; }

        [JsonPropertyName("variantTargetId")]
        public string? VariantTargetId { get; set; }

        [JsonPropertyName("backupExisting")]
        public bool BackupExisting { get; set; }
    }

    private sealed class ReattachRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("importId")]
        public string? ImportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class MashupContributorRequest
    {
        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("materials")]
        public List<string>? Materials { get; set; }
    }

    private sealed class MashupExportRequest
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
        [JsonPropertyName("destination")]
        public string? Destination { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("contributors")]
        public List<MashupContributorRequest>? Contributors { get; set; }

        [JsonPropertyName("planFingerprint")]
        public string? PlanFingerprint { get; set; }
    }

    private sealed class MashupPlanRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }
        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }
        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
        [JsonPropertyName("contributors")]
        public List<MashupContributorRequest>? Contributors { get; set; }
    }

    private sealed class RevokeRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("importId")]
        public string? ImportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class ExportStatusRequest
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
    }

    private sealed class VariantTargetsRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class BackupRestoreRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("backupName")]
        public string? BackupName { get; set; }

    }

    private sealed record HttpRequest(string Method, string Path, byte[] Body);
    internal sealed record StagedExport(string FilePath, string DirectoryPath);

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
    private readonly SemaphoreSlim _clientGate = new(8, 8);
    private readonly HashSet<TcpClient> _clients = new();
    private readonly HashSet<Task> _clientTasks = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _disposed;

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
        CleanupStaleStagedExports();
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
                _runTask = Task.Run(() => RunAsync(listener, runCts.Token));
            }
            catch (Exception e)
            {
                _log.Error(e, $"Could not start export receiver on port {_config.ListenPort}.");
                _listener = null;
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
                return;
            }
        }

        _log.Information($"XIV Instant Edit export receiver listening on port {_config.ListenPort}.");
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
                try
                {
                    await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                Task task;
                lock (_listenerLock)
                {
                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_listener, listener))
                    {
                        client.Dispose();
                        _clientGate.Release();
                        break;
                    }
                    _clients.Add(client);
                    task = Task.Run(() => HandleTrackedClientAsync(client, cancellationToken), CancellationToken.None);
                    _clientTasks.Add(task);
                }
                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (_listenerLock)
                            _clientTasks.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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

    private async Task HandleTrackedClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(static state =>
        {
            try { ((TcpClient)state!).Close(); } catch { }
        }, client);
        try
        {
            await HandleClient(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_listenerLock)
                _clients.Remove(client);
            _clientGate.Release();
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
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

                cancellationToken.ThrowIfCancellationRequested();
                var (status, body) = await ProcessRequestAsync(request).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                WriteResponse(stream, status, body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Listener shutdown closes active sockets so blocked reads terminate promptly.
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
            return (200, Json(new
            {
                ok = true,
                running = true,
                target = "original_source_mod",
                capabilities = new[]
                {
                    "instant-edit.context-reattach.v1",
                    "instant-edit.context-revoke.v1",
                    "instant-edit.export-status.v1",
                    "instant-edit.variant-targets.v1",
                    "instant-edit.backup-restore.v1",
                },
            }));

        if (method == "POST" && path.TrimEnd('/') == "/context/reattach")
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

            ReattachRequest? reattach;
            try
            {
                reattach = JsonSerializer.Deserialize<ReattachRequest>(body, JsonOpts);
            }
            catch (Exception e)
            {
                _log.Error(e, "Failed to parse context reattach request.");
                return Error(400, "invalid_json", "request body is not valid JSON");
            }

            if (reattach is null)
                return Error(400, "malformed_request", "request must be a JSON object");

            var reattachError = ValidateReattachEnvelope(reattach);
            if (reattachError is not null)
                return Error(StatusForCode(reattachError), reattachError, "unsupported or malformed reattach envelope");

            if (!_contexts.TryReattach(
                    reattach.ContextId!,
                    reattach.ImportId!,
                    reattach.Capability!,
                    _config.ListenPort,
                    out var context,
                    out var registryCode))
                return Error(StatusForCode(registryCode), registryCode, "export context was rejected");

            return (200, Json(new { ok = true, code = registryCode, context }));
        }

        if (method == "POST" && path.TrimEnd('/') == "/context/revoke")
        {
            var parsed = DeserializeRequest<RevokeRequest>(request.Body, "context revoke", out var parseError);
            if (parseError is not null)
                return parseError.Value;
            var revoke = parsed!;
            if (!string.Equals(revoke.Schema, "instant-edit.context-revoke", StringComparison.Ordinal) ||
                revoke.Version != 1 || !IsSafeId(revoke.ContextId) || !IsSafeId(revoke.ImportId) ||
                string.IsNullOrWhiteSpace(revoke.Capability))
                return Error(400, "malformed_request", "unsupported or malformed context revoke envelope");
            if (!_contexts.TryRevoke(revoke.ContextId!, revoke.ImportId!, revoke.Capability!, out var registryCode))
                return Error(StatusForCode(registryCode), registryCode, "export context revocation was rejected");
            return (200, Json(new { ok = true, code = registryCode }));
        }

        if (method == "POST" && path.TrimEnd('/') == "/export/status")
        {
            var parsed = DeserializeRequest<ExportStatusRequest>(request.Body, "export status", out var parseError);
            if (parseError is not null)
                return parseError.Value;
            var status = parsed!;
            if (!string.Equals(status.Schema, "instant-edit.export-status", StringComparison.Ordinal) ||
                status.Version != 1 || string.IsNullOrWhiteSpace(status.PluginInstanceId) ||
                !IsSafeId(status.ContextId) || !IsSafeId(status.ExportId) ||
                string.IsNullOrWhiteSpace(status.Capability))
                return Error(400, "malformed_request", "unsupported or malformed export status envelope");
            if (!_contexts.TryGetExportStatus(
                    status.PluginInstanceId!,
                    status.ContextId!,
                    status.ExportId!,
                    status.Capability!,
                    out var completion,
                    out var registryCode) || completion is null)
                return Error(StatusForCode(registryCode), registryCode, "export receipt was not found");
            if (!completion.IsCompleted)
                return (202, Json(new { ok = true, code = "export_pending", complete = false }));
            return ResultResponse(await completion.ConfigureAwait(false));
        }

        if (method == "POST" && path.TrimEnd('/') == "/variant-targets")
        {
            var parsed = DeserializeRequest<VariantTargetsRequest>(request.Body, "variant targets", out var parseError);
            if (parseError is not null)
                return parseError.Value;
            var targetsRequest = parsed!;
            if (!string.Equals(targetsRequest.Schema, "instant-edit.variant-targets", StringComparison.Ordinal) ||
                targetsRequest.Version != 1 || string.IsNullOrWhiteSpace(targetsRequest.PluginInstanceId) ||
                !IsSafeId(targetsRequest.ContextId) || string.IsNullOrWhiteSpace(targetsRequest.Capability))
                return Error(400, "malformed_request", "unsupported or malformed variant-targets envelope");
            if (!_contexts.TryAuthorizeOperation(
                    targetsRequest.PluginInstanceId!, targetsRequest.ContextId!, targetsRequest.Capability!,
                    out var target, out var registryCode) || target is null)
                return Error(StatusForCode(registryCode), registryCode, "export context was rejected");

            var result = await _penumbra.GetVariantTargetsAsync(
                target.SourceModDirectory, target.TargetFilePath, target.SourceModRootPath,
                target.TargetRelativePath, target.GamePath).ConfigureAwait(false);
            if (!result.Success)
                return Error(400, result.Code, result.Message);
            return (200, Json(new
            {
                ok = true,
                groups = result.Groups.Select(group => new
                {
                    id = group.Id,
                    name = group.Name,
                    options = group.Options.Select(option => new
                    {
                        id = option.Id,
                        name = option.Name,
                        modelPath = option.ModelPath,
                    }),
                }),
            }));
        }

        if (method == "POST" && path.TrimEnd('/') == "/backup/restore")
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

            BackupRestoreRequest? restore;
            try
            {
                restore = JsonSerializer.Deserialize<BackupRestoreRequest>(body, JsonOpts);
            }
            catch (Exception e)
            {
                _log.Error(e, "Failed to parse backup restore request.");
                return Error(400, "invalid_json", "request body is not valid JSON");
            }
            if (restore is null)
                return Error(400, "malformed_request", "request must be a JSON object");
            var restoreError = ValidateBackupRestoreEnvelope(restore);
            if (restoreError is not null)
                return Error(StatusForCode(restoreError), restoreError, "unsupported or malformed backup restore envelope");
            if (!_contexts.TryAuthorizeOperation(
                    restore.PluginInstanceId!, restore.ContextId!, restore.Capability!,
                    out var target, out var registryCode) || target is null)
                return Error(StatusForCode(registryCode), registryCode, "export context was rejected");

            var result = await _penumbra.RestoreSourceBackupAsync(
                target.SourceModDirectory,
                target.TargetFilePath,
                target.SourceModRootPath,
                target.TargetRelativePath,
                target.GamePath,
                restore.BackupName!).ConfigureAwait(false);
            return ResultResponse(new ExportReceipt(
                result.Success,
                result.Code,
                result.Message,
                result.WarningList,
                result.TargetFilePath));
        }

        if (method == "POST" && path.TrimEnd('/') == "/mashup/plan")
        {
            var parsed = DeserializeRequest<MashupPlanRequest>(request.Body, "mashup plan", out var parseError);
            if (parseError is not null)
                return parseError.Value;
            var requestPlan = parsed!;
            var envelopeError = ValidateMashupPlanEnvelope(requestPlan);
            if (envelopeError is not null)
                return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed mashup plan envelope");
            if (!_contexts.TryAuthorizeOperation(
                    requestPlan.PluginInstanceId!, requestPlan.ContextId!, requestPlan.Capability!,
                    out var activeContext, out var activeCode) || activeContext is null)
                return Error(StatusForCode(activeCode), activeCode, "active export context was rejected");

            var contributors = new List<MashupContributor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contributor in requestPlan.Contributors!)
            {
                if (!seen.Add(contributor.ContextId!))
                    return Error(400, "duplicate_context", "a mashup context was supplied more than once");
                if (!_contexts.TryAuthorizeOperation(
                        requestPlan.PluginInstanceId!, contributor.ContextId!, contributor.Capability!,
                        out var contributorContext, out var contributorCode) || contributorContext is null)
                    return Error(StatusForCode(contributorCode), contributorCode, "a contributor context was rejected");
                contributors.Add(new MashupContributor(contributorContext, contributor.Materials!));
            }

            var plan = PenumbraService.BuildMashupPlan(activeContext, contributors);
            if (!plan.Success)
                return Error(StatusForCode(plan.Code), plan.Code, plan.Message);
            return (200, Json(new
            {
                ok = true,
                code = plan.Code,
                message = plan.Message,
                planFingerprint = plan.Fingerprint,
                assignments = plan.Assignments.Select(item => new
                {
                    contextId = item.ContextId,
                    modelMaterial = item.ModelMaterial,
                    alias = item.Alias,
                    gamePath = item.GamePath,
                    slot = item.Slot,
                }),
            }));
        }

        if (method == "POST" && path.TrimEnd('/') == "/mashup/export")
        {
            var parsed = DeserializeRequest<MashupExportRequest>(request.Body, "mashup export", out var parseError);
            if (parseError is not null)
                return parseError.Value;
            var mashup = parsed!;
            var envelopeError = ValidateMashupEnvelope(mashup);
            if (envelopeError is not null)
                return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed mashup envelope");

            if (!_contexts.TryAuthorizeOperation(
                    mashup.PluginInstanceId!, mashup.ContextId!, mashup.Capability!,
                    out var activeContext, out var activeCode) || activeContext is null)
                return Error(StatusForCode(activeCode), activeCode, "active export context was rejected");

            var contributors = new List<MashupContributor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contributor in mashup.Contributors!)
            {
                if (!seen.Add(contributor.ContextId!))
                    return Error(400, "duplicate_context", "a mashup context was supplied more than once");
                if (!_contexts.TryAuthorizeOperation(
                        mashup.PluginInstanceId!, contributor.ContextId!, contributor.Capability!,
                        out var contributorContext, out var contributorCode) || contributorContext is null)
                    return Error(StatusForCode(contributorCode), contributorCode, "a contributor context was rejected");
                contributors.Add(new MashupContributor(contributorContext, contributor.Materials!));
            }

            var plan = PenumbraService.BuildMashupPlan(activeContext, contributors);
            if (!plan.Success)
                return Error(StatusForCode(plan.Code), plan.Code, plan.Message);
            if (!string.Equals(plan.Fingerprint, mashup.PlanFingerprint, StringComparison.OrdinalIgnoreCase))
                return Error(409, "mashup_plan_mismatch", "The mashup material plan changed; retry the export.");

            var fingerprintSource = JsonSerializer.Serialize(new
            {
                mashup.Destination,
                mashup.Name,
                mashup.PlanFingerprint,
                contributors = mashup.Contributors!.Select(item => new
                    {
                        item.ContextId,
                        materials = item.Materials,
                    }),
            }, JsonOpts);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
            if (!_contexts.TryBeginExport(
                    mashup.PluginInstanceId!, mashup.ContextId!, mashup.ExportId!, mashup.Capability!,
                    mashup.FilePath!, mashup.Size, mashup.Sha256!, out var reservation, out var registryCode,
                    fingerprint))
                return Error(StatusForCode(registryCode), registryCode, "mashup export was rejected");
            if (reservation is null)
                return Error(500, "internal_error", "mashup reservation was not created");
            if (!reservation.IsOwner)
                return ResultResponse(await reservation.Completion.ConfigureAwait(false));

            ExportReceipt receipt;
            StagedExport? staged = null;
            try
            {
                var stageResult = await StageExportFileAsync(
                    mashup.FilePath!, mashup.Size, mashup.Sha256!).ConfigureAwait(false);
                staged = stageResult.Export;
                if (stageResult.Error is not null)
                    receipt = new ExportReceipt(false, stageResult.Error.Value.Code, stageResult.Error.Value.Message);
                else
                {
                    var result = await _penumbra.ApplyMashupAsync(
                        activeContext,
                        contributors,
                        plan,
                        staged!.FilePath,
                        mashup.ExportId!,
                        mashup.Destination!,
                        mashup.Name!).ConfigureAwait(false);
                    if (result.Success && result.PathRemap is { } pathRemap)
                        _contexts.RemapModPaths(
                            pathRemap.ModDirectory,
                            pathRemap.ModRoot,
                            pathRemap.RelativePaths);
                    receipt = new ExportReceipt(
                        result.Success,
                        result.Code,
                        result.Message,
                        result.WarningList,
                        result.TargetFilePath,
                        result.DestinationName);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Mashup processing failed.");
                receipt = new ExportReceipt(false, "internal_error", "mashup processing failed");
            }
            finally
            {
                CleanupStagedExport(staged);
            }

            _contexts.CompleteExport(mashup.ContextId!, mashup.ExportId!, receipt);
            return ResultResponse(receipt);
        }

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
            StagedExport? staged = null;
            try
            {
                var stageResult = await StageExportFileAsync(
                    export.FilePath!,
                    export.Size,
                    export.Sha256!).ConfigureAwait(false);
                staged = stageResult.Export;
                if (stageResult.Error is not null)
                {
                    receipt = new ExportReceipt(
                        false,
                        stageResult.Error.Value.Code,
                        stageResult.Error.Value.Message);
                }
                else
                {
                    var target = reservation.Context;
                    receipt = await ApplyExport(
                        target,
                        staged!.FilePath,
                        export.VariantName,
                        export.VariantGroupName,
                        export.VariantTarget,
                        export.VariantTargetId,
                        export.SetupInPenumbra,
                        export.BackupExisting).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Export processing failed.");
                receipt = new ExportReceipt(false, "internal_error", "export processing failed");
            }
            finally
            {
                CleanupStagedExport(staged);
            }

            _contexts.CompleteExport(export.ContextId!, export.ExportId!, receipt);
            _log.Information($"Export {receipt.Code}: {receipt.Message}");
            return ResultResponse(receipt);
        }

        return (404, Json(new { ok = false, error = "not found" }));

        async Task<ExportReceipt> ApplyExport(
            InstantEditImportContext target,
            string filePath,
            string? variantName,
            string? variantGroupName,
            string? variantTarget,
            string? variantTargetId,
            bool setupVariantInPenumbra,
            bool backupExisting)
        {
            if (string.IsNullOrWhiteSpace(target.TargetFilePath) ||
                string.IsNullOrWhiteSpace(target.SourceModDirectory))
                return new ExportReceipt(false, "missing_source_target", "the import has no original Penumbra mod destination");

            var result = await _penumbra.ApplySourceExportAsync(
                target.SourceModDirectory,
                target.TargetFilePath,
                target.SourceModRootPath,
                target.TargetRelativePath,
                target.GamePath,
                filePath,
                variantName,
                variantGroupName,
                variantTarget,
                variantTargetId,
                setupVariantInPenumbra,
                backupExisting).ConfigureAwait(false);
            return new ExportReceipt(
                result.Success,
                result.Code,
                result.Message,
                result.WarningList,
                result.TargetFilePath);
        }
    }

    private T? DeserializeRequest<T>(
        byte[] bodyBytes,
        string operation,
        out (int Status, string Body)? error)
        where T : class
    {
        error = null;
        string body;
        try
        {
            body = new UTF8Encoding(false, true).GetString(bodyBytes);
        }
        catch (DecoderFallbackException)
        {
            error = Error(400, "invalid_utf8", "request body is not valid UTF-8");
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOpts);
            if (value is null)
                error = Error(400, "malformed_request", "request must be a JSON object");
            return value;
        }
        catch (Exception e)
        {
            _log.Error(e, $"Failed to parse {operation} request.");
            error = Error(400, "invalid_json", "request body is not valid JSON");
            return null;
        }
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
        if (request.VariantName is not null && !IsSafeVariantName(request.VariantName))
            return "invalid_variant_name";
        if (request.SetupInPenumbra && request.VariantName is null)
            if (!string.Equals(request.VariantTarget, "option", StringComparison.Ordinal))
                return "penumbra_setup_requires_variant";
        if (request.SetupInPenumbra && !string.Equals(request.VariantTarget, "option", StringComparison.Ordinal) &&
            !PenumbraService.IsSafeVariantGroupName(request.VariantGroupName))
            return "penumbra_setup_requires_group_name";
        if (request.SetupInPenumbra && request.VariantTarget is not ("new_group" or "group" or "option"))
            return "invalid_variant_target";
        if (request.SetupInPenumbra && request.VariantTarget is not "new_group" &&
            string.IsNullOrWhiteSpace(request.VariantTargetId))
            return "missing_variant_target";
        return null;
    }

    private static string? ValidateMashupEnvelope(MashupExportRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.mashup-export", StringComparison.Ordinal))
            return request.Version == 2 ? "unsupported_schema" : "unsupported_version";
        if (request.Version != 2)
            return "unsupported_version";
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
            !IsSafeId(request.ExportId) || string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.FilePath) || string.IsNullOrWhiteSpace(request.Sha256) ||
            string.IsNullOrWhiteSpace(request.PlanFingerprint) ||
            request.Destination is not ("active_mod" or "new_mod") ||
            !PenumbraService.IsSafeVariantGroupName(request.Name))
            return "missing_field";
        if (request.Size <= 0 || request.Size > MaxExportBytes || request.Sha256.Length != 64 ||
            request.Sha256.Any(c => !Uri.IsHexDigit(c)) || request.PlanFingerprint.Length != 64 ||
            request.PlanFingerprint.Any(c => !Uri.IsHexDigit(c)))
            return "invalid_export_file";
        if (request.Contributors is not { Count: >= 2 and <= 16 })
            return "invalid_contributors";
        foreach (var contributor in request.Contributors)
        {
            if (!IsSafeId(contributor.ContextId) || string.IsNullOrWhiteSpace(contributor.Capability) ||
                contributor.Materials is not { Count: >= 1 and <= 256 } ||
                contributor.Materials.Any(material => string.IsNullOrWhiteSpace(material) || material.Length > 512))
                return "invalid_contributors";
        }
        return null;
    }

    private static string? ValidateMashupPlanEnvelope(MashupPlanRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.mashup-plan", StringComparison.Ordinal))
            return request.Version == 1 ? "unsupported_schema" : "unsupported_version";
        if (request.Version != 1)
            return "unsupported_version";
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            request.Contributors is not { Count: >= 2 and <= 16 })
            return "invalid_contributors";
        foreach (var contributor in request.Contributors)
        {
            if (!IsSafeId(contributor.ContextId) || string.IsNullOrWhiteSpace(contributor.Capability) ||
                contributor.Materials is not { Count: >= 1 and <= 256 } ||
                contributor.Materials.Any(material => string.IsNullOrWhiteSpace(material) || material.Length > 512))
                return "invalid_contributors";
        }
        return null;
    }

    private static string? ValidateBackupRestoreEnvelope(BackupRestoreRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.backup-restore", StringComparison.Ordinal))
            return request.Version == 1 ? "unsupported_schema" : "unsupported_version";
        if (request.Version != 1)
            return "unsupported_version";
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.BackupName))
            return "missing_field";
        if (request.BackupName.Length > 512 ||
            request.BackupName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            request.BackupName.Contains('/') || request.BackupName.Contains('\\'))
            return "invalid_backup_name";
        return null;
    }

    private static string? ValidateReattachEnvelope(ReattachRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.reattach", StringComparison.Ordinal))
            return request.Version == 1 ? "unsupported_schema" : "unsupported_version";
        if (request.Version != 1)
            return "unsupported_version";
        if (!IsSafeId(request.ContextId) || !IsSafeId(request.ImportId) ||
            string.IsNullOrWhiteSpace(request.Capability))
            return "missing_field";
        return null;
    }

    private static bool IsSafeVariantName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value is "." or ".." ||
            value.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            return false;
        return value.All(c => !char.IsControl(c));
    }

    private static bool IsSafeId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static async Task<(StagedExport? Export, (string Code, string Message)? Error)> StageExportFileAsync(
        string filePath,
        long expectedSize,
        string expectedSha256)
    {
        string fullPath;
        StagedExport? staged = null;
        var retainStaged = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > 4096 || filePath.Contains('\0') ||
                !Path.IsPathRooted(filePath) || filePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(filePath), ".mdl", StringComparison.OrdinalIgnoreCase) ||
                filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
                return (null, ("unsafe_file_path", "filePath must be a local absolute .mdl path"));

            fullPath = Path.GetFullPath(filePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!File.Exists(fullPath))
                return (null, ("file_not_found", "export file was not found"));
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                (parent is not null && HasReparsePointInPath(parent)))
                return (null, ("unsafe_file_path", "filePath must not be a reparse point"));

            var info = new FileInfo(fullPath);
            if (info.Length != expectedSize)
                return (null, ("size_mismatch", "export file size did not match the request"));
            if (info.Length > MaxExportBytes)
                return (null, ("invalid_size", "export file is too large"));

            var stagingRoot = Path.Combine(Path.GetTempPath(), "InstantEdit", "plugin-exports");
            Directory.CreateDirectory(stagingRoot);
            if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0 ||
                HasReparsePointInPath(Path.GetDirectoryName(stagingRoot)!))
                return (null, ("internal_error", "plugin export staging directory is unsafe"));
            var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            staged = new StagedExport(Path.Combine(stagingDirectory, "model.mdl"), stagingDirectory);

            await using var source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            await using var destination = new FileStream(
                staged.FilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;
                copied += read;
                if (copied > MaxExportBytes)
                {
                    return (null, ("invalid_size", "export file is too large"));
                }
                sha.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            await destination.FlushAsync().ConfigureAwait(false);

            if (copied != expectedSize)
            {
                return (null, ("size_mismatch", "export file size changed during staging"));
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (null, ("hash_mismatch", "export file hash did not match the request"));
            }
            retainStaged = true;
            return (staged, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, ("unsafe_file_path", "export file is not readable"));
        }
        catch (IOException)
        {
            return (null, ("file_not_readable", "export file could not be staged"));
        }
        catch (ArgumentException)
        {
            return (null, ("unsafe_file_path", "filePath is invalid"));
        }
        finally
        {
            if (!retainStaged)
                CleanupStagedExport(staged);
        }
    }

    internal static void CleanupStagedExport(StagedExport? staged)
    {
        if (staged is null)
            return;
        try
        {
            if (Directory.Exists(staged.DirectoryPath))
                Directory.Delete(staged.DirectoryPath, true);
        }
        catch
        {
            // Crash/stale staging cleanup is best-effort; it must never change
            // an already recorded export result.
        }
    }

    private static void CleanupStaleStagedExports()
    {
        var root = Path.Combine(Path.GetTempPath(), "InstantEdit", "plugin-exports");
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                HasReparsePointInPath(Path.GetDirectoryName(root)!))
                return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var info = new DirectoryInfo(directory);
                if (!Guid.TryParseExact(info.Name, "N", out _) ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LastWriteTimeUtc >= cutoff)
                    continue;
                info.Delete(true);
            }
        }
        catch
        {
            // Cleanup is best-effort and never prevents the listener starting.
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
            ? (200, Json(new
            {
                ok = true,
                code = receipt.Code,
                message = receipt.Message,
                warnings = receipt.Warnings ?? Array.Empty<string>(),
                targetFilePath = receipt.TargetFilePath,
                destinationName = receipt.DestinationName,
            }))
            : Error(StatusForCode(receipt.Code), receipt.Code, receipt.Message);

    private static int StatusForCode(string code)
        => code switch
        {
            "stale_context" => 410,
            "invalid_capability" or "plugin_instance_mismatch" => 401,
            "duplicate_export_id" => 409,
            "mashup_plan_mismatch" => 409,
            "export_not_found" => 404,
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
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            409 => "Conflict",
            410 => "Gone",
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
        lock (_listenerLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _cts.Cancel();
        StopListener();
        _cts.Dispose();
        if (_ownsContexts)
            _contexts.Dispose();
    }

    private void StopListener()
    {
        CancellationTokenSource? runCts;
        TcpListener? listener;
        Task[] tasks;
        lock (_listenerLock)
        {
            runCts = _runCts;
            _runCts = null;
            listener = _listener;
            _listener = null;
            tasks = _clientTasks.Append(_runTask).Where(task => task is not null).Cast<Task>().ToArray();
            _runTask = null;
            runCts?.Cancel();
            listener?.Stop();
            foreach (var client in _clients.ToArray())
            {
                try { client.Close(); } catch { }
            }
        }

        try
        {
            Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            _log.Debug($"Export receiver shutdown completed with active work: {e.Message}");
        }
        runCts?.Dispose();
    }
}
