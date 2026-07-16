#if DEBUG
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json;
using System;
using System.Net;

namespace Multiplayer.DebugClient;

internal interface IRuntimeTestProcessClient
{
    RuntimeTestCapabilitiesDto GetCapabilities();
    RuntimeTestCommandAcceptedDto Enqueue(RuntimeTestCommandDto command);
    RuntimeTestRunDto GetRun(string requestId);
    void Cancel(string requestId);
}

internal sealed class RuntimeTestHttpClient : IRuntimeTestProcessClient
{
    private readonly string baseUrl;
    private readonly string token;

    public RuntimeTestHttpClient(DebugSessionInfo session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(session.FirehoseUrl)) throw new ArgumentException("Debug session has no firehose URL.", nameof(session));
        if (string.IsNullOrWhiteSpace(session.ApiToken)) throw new ArgumentException("Debug session has no API token.", nameof(session));
        baseUrl = session.FirehoseUrl.TrimEnd('/') + "/";
        token = session.ApiToken;
    }

    public RuntimeTestCapabilitiesDto GetCapabilities() => Get<RuntimeTestCapabilitiesDto>("api/runtime-tests/capabilities");

    public RuntimeTestCommandAcceptedDto Enqueue(RuntimeTestCommandDto command)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        using WebClient client = CreateClient();
        client.Headers[HttpRequestHeader.ContentType] = "application/json";
        string response = client.UploadString(baseUrl + "api/runtime-tests/commands", "POST", DebugJson.Serialize(command));
        return JsonConvert.DeserializeObject<RuntimeTestCommandAcceptedDto>(response, DebugJson.Settings);
    }

    public RuntimeTestRunDto GetRun(string requestId) =>
        Get<RuntimeTestRunDto>("api/runtime-tests/runs/" + Uri.EscapeDataString(requestId ?? string.Empty));

    public void Cancel(string requestId)
    {
        using WebClient client = CreateClient();
        client.UploadString(baseUrl + "api/runtime-tests/runs/" + Uri.EscapeDataString(requestId ?? string.Empty) + "/cancel", "POST", string.Empty);
    }

    private T Get<T>(string path)
    {
        using WebClient client = CreateClient();
        return JsonConvert.DeserializeObject<T>(client.DownloadString(baseUrl + path), DebugJson.Settings);
    }

    private WebClient CreateClient()
    {
        WebClient client = new();
        client.Headers["X-DVMP-Debug-Token"] = token;
        return client;
    }
}
#endif
