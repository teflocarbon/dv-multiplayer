using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplayer.Debugging.Protocol;

public sealed class DebugHttpServer : IDisposable
{
    private const uint HandleFlagInherit = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle,
        uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr handle,
        out uint flags);

    private readonly DebugEventStore store;
    private readonly Func<DebugSessionInfo> session;
    private readonly Func<IEnumerable<DebugEntityDto>> entities;
    private readonly Func<string, string, DebugEntityDto> entity;
    private readonly Func<DebugRuntimeSettingsDto> settings;
    private readonly Func<IEnumerable<DebugSessionInfo>> sessions;
    private readonly Func<IEnumerable<ReplicationOperationSummaryDto>> replicationOperations;
    private readonly Func<string, ReplicationOperationDto> replicationOperation;
    private readonly Func<DebugEventQueryDto, IEnumerable<DebugEvent>> historicalEvents;
    private readonly int requestedPort;
    private readonly Action<DebugRuntimeSettingsDto> updateSettings;
    private readonly Action<string> mark;
    private readonly Func<string, string, string> startCapture;
    private readonly Func<string> stopCapture;
#if DEBUG
    private Func<RuntimeTestCapabilitiesDto> runtimeTestCapabilities;
    private Func<IEnumerable<RuntimeTestRunSummaryDto>> runtimeTestRuns;
    private Func<RuntimeTestCommandDto, RuntimeTestCommandAcceptedDto> enqueueRuntimeTest;
    private Func<string, RuntimeTestRunDto> runtimeTestRun;
    private Func<string, bool> cancelRuntimeTest;
    private Func<RuntimeEnvironmentStatusDto> runtimeEnvironmentStatus;
    private Func<RuntimeEnvironmentStartRequestDto, RuntimeEnvironmentStatusDto> startRuntimeEnvironment;
    private Func<RuntimeEnvironmentStatusDto> stopRuntimeEnvironment;
    private Func<RuntimeEnvironmentConfigurationDto> runtimeEnvironmentConfiguration;
    private Action<RuntimeEnvironmentStartRequestDto> updateRuntimeEnvironmentConfiguration;
    private Action shutdownDashboard;
#endif
    private readonly ConcurrentDictionary<Guid, BlockingCollection<DebugEvent>> subscribers = new();
    private readonly ConcurrentDictionary<Guid, TcpClient> clients = new();
    private CancellationTokenSource cancellation;
    private TcpListener listener;

    public int Port { get; private set; }
    public string Url => Port == 0 ? string.Empty : $"http://127.0.0.1:{Port}/";

    public DebugHttpServer(DebugEventStore store, Func<DebugSessionInfo> session,
        Func<IEnumerable<DebugEntityDto>> entities = null, Func<string, string, DebugEntityDto> entity = null,
        Func<DebugRuntimeSettingsDto> settings = null, Action<DebugRuntimeSettingsDto> updateSettings = null,
        Action<string> mark = null, Func<string, string, string> startCapture = null,
        Func<string> stopCapture = null, Func<IEnumerable<DebugSessionInfo>> sessions = null,
        Func<IEnumerable<ReplicationOperationSummaryDto>> replicationOperations = null,
        Func<string, ReplicationOperationDto> replicationOperation = null,
        Func<DebugEventQueryDto, IEnumerable<DebugEvent>> historicalEvents = null,
        int requestedPort = 0)
    {
        this.store = store;
        this.session = session;
        this.entities = entities ?? (() => Array.Empty<DebugEntityDto>());
        this.entity = entity ?? ((_, _) => null);
        this.settings = settings ?? (() => new DebugRuntimeSettingsDto());
        this.sessions = sessions ?? (() => new[] { session() });
        this.replicationOperations = replicationOperations ?? (() => Array.Empty<ReplicationOperationSummaryDto>());
        this.replicationOperation = replicationOperation ?? (_ => null);
        this.historicalEvents = historicalEvents ?? (_ => Array.Empty<DebugEvent>());
        this.requestedPort = requestedPort;
        this.updateSettings = updateSettings ?? (_ => { });
        this.mark = mark ?? (_ => { });
        this.startCapture = startCapture ?? ((_, _) => string.Empty);
        this.stopCapture = stopCapture ?? (() => string.Empty);
    }

#if DEBUG
    public void ConfigureRuntimeTests(Func<RuntimeTestCapabilitiesDto> capabilities,
        Func<IEnumerable<RuntimeTestRunSummaryDto>> listRuns,
        Func<RuntimeTestCommandDto, RuntimeTestCommandAcceptedDto> enqueue,
        Func<string, RuntimeTestRunDto> getRun, Func<string, bool> cancel)
    {
        runtimeTestCapabilities = capabilities;
        runtimeTestRuns = listRuns;
        enqueueRuntimeTest = enqueue;
        runtimeTestRun = getRun;
        cancelRuntimeTest = cancel;
    }

    public void ConfigureRuntimeEnvironment(Func<RuntimeEnvironmentStatusDto> status,
        Func<RuntimeEnvironmentStartRequestDto, RuntimeEnvironmentStatusDto> start,
        Func<RuntimeEnvironmentStatusDto> stop,
        Func<RuntimeEnvironmentConfigurationDto> configuration = null,
        Action<RuntimeEnvironmentStartRequestDto> updateConfiguration = null)
    {
        runtimeEnvironmentStatus = status;
        startRuntimeEnvironment = start;
        stopRuntimeEnvironment = stop;
        runtimeEnvironmentConfiguration = configuration;
        updateRuntimeEnvironmentConfiguration = updateConfiguration;
    }

    public void ConfigureDashboardControl(Action shutdown) => shutdownDashboard = shutdown;
#endif

    public void Start()
    {
        if (listener != null) return;
        TcpListener candidate = new(IPAddress.Loopback, requestedPort);
        candidate.Start();
        if (!SetHandleInformation(candidate.Server.Handle, HandleFlagInherit, 0))
        {
            int error = Marshal.GetLastWin32Error();
            candidate.Stop();
            throw new Win32Exception(error,
                "Could not mark the dashboard listener socket as non-inheritable.");
        }
        if (!GetHandleInformation(candidate.Server.Handle, out uint handleFlags))
        {
            int error = Marshal.GetLastWin32Error();
            candidate.Stop();
            throw new Win32Exception(error,
                "Could not verify dashboard listener socket inheritance flags.");
        }
        if ((handleFlags & HandleFlagInherit) != 0)
        {
            candidate.Stop();
            throw new InvalidOperationException(
                "Dashboard listener socket is still inheritable after initialization.");
        }
        listener = candidate;
        Port = ((IPEndPoint)candidate.LocalEndpoint).Port;
        cancellation = new CancellationTokenSource();
        store.Published += OnPublished;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                Guid clientId = Guid.NewGuid();
                clients[clientId] = client;
                _ = Task.Run(() =>
                {
                    try { HandleClient(client); }
                    finally { clients.TryRemove(clientId, out _); }
                });
            }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private void OnPublished(DebugEvent item)
    {
        foreach (BlockingCollection<DebugEvent> queue in subscribers.Values) queue.TryAdd(item);
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                HttpRequest request = HttpRequest.Read(stream);
                if (request == null) return;
                Handle(request, stream);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception exception)
            {
                try { WriteText(client.GetStream(), 500, exception.Message, "text/plain; charset=utf-8"); } catch { }
            }
        }
    }

    private void Handle(HttpRequest request, NetworkStream stream)
    {
        string path = request.Path;
        if (path == "/events") { HandleEvents(stream); return; }
        if (path == "/api/session") { WriteJson(stream, 200, session()); return; }
        if (path == "/api/sessions") { WriteJson(stream, 200, sessions().Where(item => item != null)); return; }
        if (path == "/api/entities") { WriteJson(stream, 200, entities()); return; }
        if (path == "/api/replication") { WriteJson(stream, 200, replicationOperations()); return; }
        if (path.StartsWith("/api/replication/", StringComparison.Ordinal))
        {
            ReplicationOperationDto result = replicationOperation(Uri.UnescapeDataString(path.Substring("/api/replication/".Length)));
            if (result == null) WriteEmpty(stream, 404); else WriteJson(stream, 200, result);
            return;
        }
        if (path.StartsWith("/api/entities/", StringComparison.Ordinal))
        {
            string[] parts = path.Substring("/api/entities/".Length).Split(new[] { '/' }, 2);
            DebugEntityDto result = parts.Length == 2 ? entity(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1])) : null;
            if (result == null) WriteEmpty(stream, 404); else WriteJson(stream, 200, result);
            return;
        }
        if (path.StartsWith("/api/event/", StringComparison.Ordinal) && long.TryParse(path.Substring("/api/event/".Length), out long sequence))
        {
            if (store.TryGet(sequence, out DebugEvent result)) WriteJson(stream, 200, result); else WriteEmpty(stream, 404);
            return;
        }
        if (path == "/api/settings" && request.Method == "GET") { WriteJson(stream, 200, settings()); return; }
#if DEBUG
        if (path == "/api/automation" && request.Method == "GET")
        {
            DebugSessionInfo current = session();
            WriteJson(stream, 200, new DashboardAutomationInfoDto
            {
                BuildNumber = !string.IsNullOrWhiteSpace(current?.BuildNumber)
                    ? current.BuildNumber
                    : Assembly.GetEntryAssembly()?.GetCustomAttribute<
                        AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                      "development",
                ProcessId = current?.ProcessId ?? 0,
                SessionId = current?.SessionId ?? string.Empty,
                RuntimeTestsAvailable = runtimeTestCapabilities != null,
                RuntimeEnvironmentAvailable = runtimeEnvironmentStatus != null,
                Endpoints = new[] { "sessions", "environment", "runtime-tests", "captures", "events" }
            });
            return;
        }
        if (path == "/api/events/query" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            DebugEventQueryDto query = ReadJson<DebugEventQueryDto>(request) ?? new DebugEventQueryDto();
            IEnumerable<DebugEvent> matches = store.Snapshot()
                .Concat(historicalEvents(query) ?? Array.Empty<DebugEvent>())
                .GroupBy(item => !string.IsNullOrWhiteSpace(item.EventKey)
                    ? item.EventKey
                    : $"{item.SessionId}:{item.SourceSequence}", StringComparer.Ordinal)
                .Select(group => group.First());
            if (query.SinceUtc.HasValue) matches = matches.Where(item => item.TimestampUtc >= query.SinceUtc.Value);
            if (!string.IsNullOrWhiteSpace(query.SessionId)) matches = matches.Where(item => string.Equals(item.SessionId, query.SessionId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.Category)) matches = matches.Where(item => string.Equals(item.Category, query.Category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.EventName)) matches = matches.Where(item => string.Equals(item.EventName, query.EventName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.EntityType)) matches = matches.Where(item => string.Equals(item.EntityType, query.EntityType, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.EntityId)) matches = matches.Where(item => string.Equals(item.EntityId, query.EntityId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.CorrelationId)) matches = matches.Where(item => string.Equals(item.CorrelationId, query.CorrelationId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.TestRunId)) matches = matches.Where(item => string.Equals(item.TestRunId, query.TestRunId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.TestCaseId)) matches = matches.Where(item => string.Equals(item.TestCaseId, query.TestCaseId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.TestPhaseId)) matches = matches.Where(item => string.Equals(item.TestPhaseId, query.TestPhaseId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.TestStepId)) matches = matches.Where(item => string.Equals(item.TestStepId, query.TestStepId, StringComparison.Ordinal));
            if (query.MinimumSeverity.HasValue) matches = matches.Where(item => item.Severity >= query.MinimumSeverity.Value);
            int limit = Math.Max(1, Math.Min(5000, query.Limit));
            WriteJson(stream, 200, matches.OrderByDescending(item => item.TimestampUtc).Take(limit).OrderBy(item => item.TimestampUtc).ToArray());
            return;
        }
        if (path == "/api/dashboard/shutdown" && request.Method == "POST")
        {
            if (shutdownDashboard == null) { WriteEmpty(stream, 404); return; }
            WriteEmpty(stream, 202);
            _ = Task.Run(() => { Thread.Sleep(50); shutdownDashboard(); });
            return;
        }
        if (path == "/api/runtime-tests/capabilities" && request.Method == "GET")
        {
            if (!Authorized(request, stream)) return;
            if (runtimeTestCapabilities == null) { WriteEmpty(stream, 404); return; }
            WriteJson(stream, 200, runtimeTestCapabilities()); return;
        }
        if (path == "/api/runtime-tests/commands" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            if (enqueueRuntimeTest == null) { WriteEmpty(stream, 404); return; }
            RuntimeTestCommandAcceptedDto accepted = enqueueRuntimeTest(ReadJson<RuntimeTestCommandDto>(request));
            WriteJson(stream, accepted?.Accepted == true ? 202 : 400, accepted); return;
        }
        if (path == "/api/runtime-tests/runs" && request.Method == "GET")
        {
            if (!Authorized(request, stream)) return;
            if (runtimeTestRuns == null) { WriteEmpty(stream, 404); return; }
            WriteJson(stream, 200, runtimeTestRuns()); return;
        }
        if (path.StartsWith("/api/runtime-tests/runs/", StringComparison.Ordinal))
        {
            if (!Authorized(request, stream)) return;
            string suffix = path.Substring("/api/runtime-tests/runs/".Length);
            bool cancel = suffix.EndsWith("/cancel", StringComparison.Ordinal);
            string requestId = Uri.UnescapeDataString(cancel ? suffix.Substring(0, suffix.Length - "/cancel".Length) : suffix);
            if (cancel && request.Method == "POST")
            {
                if (cancelRuntimeTest == null || !cancelRuntimeTest(requestId)) WriteEmpty(stream, 404); else WriteEmpty(stream, 204);
                return;
            }
            if (!cancel && request.Method == "GET")
            {
                RuntimeTestRunDto run = runtimeTestRun?.Invoke(requestId);
                if (run == null) WriteEmpty(stream, 404); else WriteJson(stream, 200, run);
                return;
            }
        }
        if (path == "/api/runtime-environment/status" && request.Method == "GET")
        {
            if (!Authorized(request, stream)) return;
            if (runtimeEnvironmentStatus == null) { WriteEmpty(stream, 404); return; }
            WriteJson(stream, 200, runtimeEnvironmentStatus()); return;
        }
        if (path == "/api/runtime-environment/config" && request.Method == "GET")
        {
            if (!Authorized(request, stream)) return;
            if (runtimeEnvironmentConfiguration == null) { WriteEmpty(stream, 404); return; }
            WriteJson(stream, 200, runtimeEnvironmentConfiguration()); return;
        }
        if (path == "/api/runtime-environment/config" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            if (updateRuntimeEnvironmentConfiguration == null) { WriteEmpty(stream, 404); return; }
            updateRuntimeEnvironmentConfiguration(ReadJson<RuntimeEnvironmentStartRequestDto>(request));
            WriteEmpty(stream, 204); return;
        }
        if (path == "/api/runtime-environment/start" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            if (startRuntimeEnvironment == null) { WriteEmpty(stream, 404); return; }
            WriteJson(stream, 202, startRuntimeEnvironment(ReadJson<RuntimeEnvironmentStartRequestDto>(request))); return;
        }
        if (path == "/api/runtime-environment/stop" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            if (stopRuntimeEnvironment == null) { WriteEmpty(stream, 404); return; }
            WriteJson(stream, 200, stopRuntimeEnvironment()); return;
        }
#endif
        if (path == "/api/settings" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            updateSettings(ReadJson<DebugRuntimeSettingsDto>(request)); WriteEmpty(stream, 204); return;
        }
        if (path == "/api/mark" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            JObject body = ReadJson<JObject>(request); mark((string)body?["text"] ?? string.Empty); WriteEmpty(stream, 204); return;
        }
        if (path == "/api/capture/start" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            JObject body = ReadJson<JObject>(request); WriteJson(stream, 200, new { path = startCapture((string)body?["name"], (string)body?["captureId"]) }); return;
        }
        if (path == "/api/capture/stop" && request.Method == "POST")
        {
            if (!Authorized(request, stream)) return;
            WriteJson(stream, 200, new { path = stopCapture() }); return;
        }
        ServeAsset(stream, path);
    }

    private bool Authorized(HttpRequest request, NetworkStream stream)
    {
        if (request.Headers.TryGetValue("X-DVMP-Debug-Token", out string token) &&
            string.Equals(token, session()?.ApiToken, StringComparison.Ordinal)) return true;
        WriteEmpty(stream, 403); return false;
    }

    private void HandleEvents(NetworkStream stream)
    {
        WriteHeaders(stream, 200, "text/event-stream; charset=utf-8", -1, "Cache-Control: no-cache\r\nConnection: keep-alive\r\n");
        Guid id = Guid.NewGuid();
        BlockingCollection<DebugEvent> queue = new(new ConcurrentQueue<DebugEvent>(), 2048);
        subscribers[id] = queue;
        try
        {
            foreach (DebugEvent item in store.Snapshot()) queue.TryAdd(item);
            byte[] connected = Encoding.UTF8.GetBytes(": connected\n\n");
            stream.Write(connected, 0, connected.Length);
            foreach (DebugEvent item in queue.GetConsumingEnumerable(cancellation.Token))
            {
                byte[] bytes = Encoding.UTF8.GetBytes($"data: {DebugJson.Serialize(item)}\n\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally { subscribers.TryRemove(id, out _); queue.Dispose(); }
    }

    private static T ReadJson<T>(HttpRequest request) => JsonConvert.DeserializeObject<T>(request.Body ?? string.Empty, DebugJson.Settings);
    private static void WriteJson(NetworkStream stream, int status, object value) => WriteText(stream, status, DebugJson.Serialize(value), "application/json; charset=utf-8");
    private static void WriteText(NetworkStream stream, int status, string value, string contentType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteHeaders(stream, status, contentType, bytes.Length, "Connection: close\r\n");
        stream.Write(bytes, 0, bytes.Length);
    }
    private static void WriteEmpty(NetworkStream stream, int status) => WriteHeaders(stream, status, "text/plain; charset=utf-8", 0, "Connection: close\r\n");
    private static void WriteHeaders(NetworkStream stream, int status, string contentType, long length, string extra)
    {
        string reason = status switch { 200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Internal Server Error" };
        string lengthHeader = length >= 0 ? $"Content-Length: {length}\r\n" : string.Empty;
        byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\n{lengthHeader}{extra}\r\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void ServeAsset(NetworkStream output, string path)
    {
        string file = path == "/" ? "index.html" : path.TrimStart('/');
        bool allowed = file is "index.html" or "app.css" or "app.js";
#if DEBUG
        allowed |= file is "runtime-tests.css" or "runtime-tests.js" or "runtime-environment.css" or "runtime-environment.js";
#endif
        if (!allowed) { WriteEmpty(output, 404); return; }
        string resource = "Multiplayer.Debugging.Protocol.WebUI." + file;
        using Stream stream = typeof(DebugHttpServer).Assembly.GetManifestResourceStream(resource);
        if (stream == null) { WriteEmpty(output, 404); return; }
        using MemoryStream buffer = new(); stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
#if DEBUG
        if (file == "index.html")
        {
            string html = Encoding.UTF8.GetString(bytes)
                .Replace("</head>", "<link rel=\"stylesheet\" href=\"/runtime-tests.css\"><link rel=\"stylesheet\" href=\"/runtime-environment.css\"></head>")
                .Replace("</body>", "<script src=\"/runtime-tests.js\"></script><script src=\"/runtime-environment.js\"></script></body>");
            bytes = Encoding.UTF8.GetBytes(html);
        }
#endif
        string contentType = file.EndsWith(".html") ? "text/html; charset=utf-8" : file.EndsWith(".css") ? "text/css; charset=utf-8" : "application/javascript; charset=utf-8";
        WriteHeaders(output, 200, contentType, bytes.Length, "Connection: close\r\n");
        output.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        store.Published -= OnPublished;
        cancellation?.Cancel();
        foreach (BlockingCollection<DebugEvent> queue in subscribers.Values) queue.CompleteAdding();
        try { listener?.Stop(); } catch { }
        foreach (TcpClient client in clients.Values)
            try { client.Close(); } catch { }
        clients.Clear();
        listener = null;
        cancellation?.Dispose();
        cancellation = null;
        Port = 0;
    }

    private sealed class HttpRequest
    {
        public string Method { get; private set; }
        public string Path { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; private set; }

        public static HttpRequest Read(NetworkStream stream)
        {
            string header = ReadHeader(stream);
            if (string.IsNullOrEmpty(header)) return null;
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            HttpRequest request = new() { Method = first[0].ToUpperInvariant(), Path = first[1].Split('?')[0] };
            for (int index = 1; index < lines.Length; index++)
            {
                int separator = lines[index].IndexOf(':');
                if (separator > 0) request.Headers[lines[index].Substring(0, separator).Trim()] = lines[index].Substring(separator + 1).Trim();
            }
            int length = request.Headers.TryGetValue("Content-Length", out string raw) && int.TryParse(raw, out int parsed) ? parsed : 0;
            if (length > 1024 * 1024) throw new InvalidDataException("Debug API request body is too large.");
            byte[] body = new byte[length];
            int read = 0;
            while (read < length) { int count = stream.Read(body, read, length - read); if (count <= 0) break; read += count; }
            request.Body = Encoding.UTF8.GetString(body, 0, read);
            return request;
        }

        private static string ReadHeader(NetworkStream stream)
        {
            using MemoryStream bytes = new();
            int matched = 0;
            byte[] terminator = { 13, 10, 13, 10 };
            while (bytes.Length < 64 * 1024)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                bytes.WriteByte((byte)value);
                matched = value == terminator[matched] ? matched + 1 : value == terminator[0] ? 1 : 0;
                if (matched == terminator.Length) break;
            }
            return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r', '\n');
        }
    }
}
