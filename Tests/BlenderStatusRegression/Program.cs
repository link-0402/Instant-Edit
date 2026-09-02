using System.Net;
using System.Net.Http;
using InstantEdit.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"[PASS] {message}");
}

static async Task<BlenderStatus> ProbeAsync(
    Func<HttpResponseMessage>? responseFactory = null,
    Exception? exception = null,
    CancellationToken cancellationToken = default)
{
    using var http = new HttpClient(new StubHandler(responseFactory, exception));
    using var contexts = new ExportContextRegistry("blender-status-regression");
    using var client = new BlenderClient(null!, contexts, http);
    return await client.GetStatusAsync(42424, cancellationToken).ConfigureAwait(false);
}

var pluginVersion = BlenderClient.CurrentPluginVersion;
Require(
    BlenderClient.NormalizeVersion(pluginVersion) == pluginVersion,
    "the plugin release version is normalized to major.minor.patch");
Require(
    BlenderClient.VersionMismatchMessage(pluginVersion) ==
        $"Version mismatch. Verify Blender addon version is in sync with Plugin version {pluginVersion}.",
    "the version mismatch message uses the exact requested wording");

var matching = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent($"{{\"addonVersion\":\"{pluginVersion}\"}}"),
});
Require(matching.Reachable && matching.AddonVersion == pluginVersion &&
        matching.Classify(pluginVersion) == BlenderConnectionState.Online,
    "matching add-on and plugin versions produce the online state");

var mismatched = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{\"addonVersion\":\"1.1.3\"}"),
});
Require(mismatched.Reachable && mismatched.Classify(pluginVersion) == BlenderConnectionState.VersionMismatch,
    "a different add-on version produces a reachable mismatch state");

var missingVersion = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{\"ok\":true,\"ready\":true}"),
});
Require(missingVersion.Reachable && missingVersion.AddonVersion is null &&
        missingVersion.Classify(pluginVersion) == BlenderConnectionState.VersionMismatch,
    "a missing add-on version produces a mismatch rather than offline state");

var malformedVersion = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{\"addonVersion\":123}"),
});
Require(malformedVersion.Reachable && malformedVersion.AddonVersion is null &&
        malformedVersion.Classify(pluginVersion) == BlenderConnectionState.VersionMismatch,
    "a malformed add-on version produces a mismatch rather than offline state");

var malformedResponse = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{"),
});
Require(malformedResponse.Reachable && malformedResponse.Classify(pluginVersion) == BlenderConnectionState.VersionMismatch,
    "a malformed status document remains reachable but mismatched");

var nonSuccess = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
Require(!nonSuccess.Reachable && nonSuccess.Classify(pluginVersion) == BlenderConnectionState.Offline,
    "a non-success status response produces offline state");

var failedRequest = await ProbeAsync(exception: new HttpRequestException("connection refused"));
Require(!failedRequest.Reachable && failedRequest.Classify(pluginVersion) == BlenderConnectionState.Offline,
    "a failed status request produces offline state");

var canceledRequest = await ProbeAsync(cancellationToken: new CancellationToken(true));
Require(!canceledRequest.Reachable && canceledRequest.Classify(pluginVersion) == BlenderConnectionState.Offline,
    "a canceled status request produces offline state");

Console.WriteLine("All Blender status regressions passed.");

sealed class StubHandler(
    Func<HttpResponseMessage>? responseFactory,
    Exception? exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (exception is not null)
            return Task.FromException<HttpResponseMessage>(exception);
        return Task.FromResult(responseFactory?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK));
    }
}
