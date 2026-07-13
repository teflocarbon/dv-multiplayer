using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplayer.Debugging.Protocol;

public sealed class DebugHttpServer : IDisposable
{
    private readonly DebugEventStore store;
    private readonly Func<DebugSessionInfo> session;
    private readonly Func<IEnumerable<DebugEntityDto>> entities;
    private readonly Func<string, string, DebugEntityDto> entity;
    private readonly Func<DebugRuntimeSettingsDto> settings;
    private readonly Func<IEnumerable<DebugSessionInfo>> sessions;
    private readonly Func<IEnumerable<ReplicationOperationSummaryDto>> replicationOperations;
    private readonly Func<string, ReplicationOperationDto> replicationOperation;
    private readonly Action<DebugRuntimeSettingsDto> updateSettings;
    private readonly Action<string> mark;
    private readonly Func<string, string, string> startCapture;
    private readonly Func<string> stopCapture;
    private readonly ConcurrentDictionary<Guid, BlockingCollection<DebugEvent>> subscribers = new();
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
        Func<string, ReplicationOperationDto> replicationOperation = null)
    {
        this.store = store;
        this.session = session;
        this.entities = entities ?? (() => Array.Empty<DebugEntityDto>());
        this.entity = entity ?? ((_, _) => null);
        this.settings = settings ?? (() => new DebugRuntimeSettingsDto());
        this.sessions = sessions ?? (() => new[] { session() });
        this.replicationOperations = replicationOperations ?? (() => Array.Empty<ReplicationOperationSummaryDto>());
        this.replicationOperation = replicationOperation ?? (_ => null);
        this.updateSettings = updateSettings ?? (_ => { });
        this.mark = mark ?? (_ => { });
        this.startCapture = startCapture ?? ((_, _) => string.Empty);
        this.stopCapture = stopCapture ?? (() => string.Empty);
    }

    public void Start()
    {
        if (listener != null) return;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
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
                _ = Task.Run(() => HandleClient(client));
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
        string reason = status switch { 200 => "OK", 204 => "No Content", 403 => "Forbidden", 404 => "Not Found", _ => "Internal Server Error" };
        string lengthHeader = length >= 0 ? $"Content-Length: {length}\r\n" : string.Empty;
        byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\n{lengthHeader}{extra}\r\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void ServeAsset(NetworkStream output, string path)
    {
        string file = path == "/" ? "index.html" : path.TrimStart('/');
        if (file is not ("index.html" or "app.css" or "app.js")) { WriteEmpty(output, 404); return; }
        string resource = "Multiplayer.Debugging.Protocol.WebUI." + file;
        using Stream stream = typeof(DebugHttpServer).Assembly.GetManifestResourceStream(resource);
        if (stream == null) { WriteEmpty(output, 404); return; }
        using MemoryStream buffer = new(); stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
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
