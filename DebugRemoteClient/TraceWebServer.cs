using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Networking.Serialization;

namespace Multiplayer.DebugClient;

internal sealed class TraceEvent
{
    public long Sequence { get; set; }
    public string Timestamp { get; set; } = "";
    public string Direction { get; set; } = "";
    public string PacketType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Detail { get; set; } = "";
    public string RawHex { get; set; } = "";
    public int PeerId { get; set; }
    public byte Channel { get; set; }
    public string Delivery { get; set; } = "";
}

internal sealed class TraceWebServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly string webRoot;
    private readonly ConcurrentDictionary<Guid, BlockingCollection<string>> subscribers = [];
    private readonly ConcurrentDictionary<long, TraceEvent> eventDetails = [];
    private readonly ConcurrentDictionary<long, string> eventSummaries = [];
    private readonly ConcurrentQueue<long> eventOrder = [];
    // Deliberately lives at the capture boundary, not in the browser: suppressed packet types
    // neither enter SSE nor consume one of the bounded full-detail slots.
    private readonly ConcurrentDictionary<string, byte> suppressedPacketTypes = new(StringComparer.Ordinal);
    // Counts deliberately continue while a type is muted. That makes the checkbox menu an
    // accurate "what is noisy?" view rather than hiding the evidence once muted.
    private readonly ConcurrentDictionary<string, long> packetCounts = new(StringComparer.Ordinal);
    private CancellationTokenSource cancellation;
    private long sequence;
    private const int MaxStoredEvents = 10000;
    private static readonly JsonSerializerSettings browserJsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public string Url { get; }

    public TraceWebServer(int port)
    {
        Url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Url);
        webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebUI");
        foreach (ProtocolManifestPacket packet in ProtocolManifestProvider.Current.Packets.Where(packet => packet.SuppressByDefault))
            suppressedPacketTypes.TryAdd(ShortTypeName(packet.TypeName), 0);
    }

    public void Start()
    {
        if (!Directory.Exists(webRoot))
            throw new DirectoryNotFoundException($"Trace UI files are missing from {webRoot}");

        cancellation = new CancellationTokenSource();
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Publish(TraceEvent traceEvent)
    {
        packetCounts.AddOrUpdate(traceEvent.PacketType, 1, (_, count) => count + 1);
        if (suppressedPacketTypes.ContainsKey(traceEvent.PacketType)) return;
        traceEvent.Sequence = Interlocked.Increment(ref sequence);
        eventDetails[traceEvent.Sequence] = traceEvent;
        eventOrder.Enqueue(traceEvent.Sequence);
        while (eventOrder.Count > MaxStoredEvents && eventOrder.TryDequeue(out long expired))
        {
            eventDetails.TryRemove(expired, out _);
            eventSummaries.TryRemove(expired, out _);
        }

        // Keep the live SSE stream small. Full decoded JSON and raw hex are fetched only when
        // the operator expands a row in the trace viewer.
        string payload = JsonConvert.SerializeObject(new
        {
            traceEvent.Sequence,
            traceEvent.Timestamp,
            traceEvent.Direction,
            traceEvent.PacketType,
            traceEvent.Status,
            traceEvent.Summary,
            DetailPreview = Preview(traceEvent.Detail, 8192),
            RawPreview = Preview(traceEvent.RawHex, 4096),
            traceEvent.PeerId,
            traceEvent.Channel,
            traceEvent.Delivery
        }, Formatting.None, browserJsonSettings);
        eventSummaries[traceEvent.Sequence] = payload;
        foreach (BlockingCollection<string> subscriber in subscribers.Values)
            subscriber.TryAdd(payload);
    }

    private static string Preview(string value, int maximumLength) => string.IsNullOrEmpty(value) || value.Length <= maximumLength ? value : value.Substring(0, maximumLength) + "\n… truncated; expand while the event remains buffered for the full payload.";

    private async Task AcceptLoop()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext context = await listener.GetContextAsync();
                _ = Task.Run(() => Handle(context));
            }
            catch (HttpListenerException) when (cancellation.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        if (context.Request.Url.AbsolutePath == "/events")
        {
            HandleEvents(context);
            return;
        }

        if (context.Request.Url.AbsolutePath.StartsWith("/api/event/", StringComparison.Ordinal) && long.TryParse(context.Request.Url.AbsolutePath.Substring("/api/event/".Length), out long sequence))
        {
            HandleEventDetail(context, sequence);
            return;
        }

        if (context.Request.Url.AbsolutePath.Equals("/api/suppression", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
        {
            WriteJson(context, new { packetTypes = suppressedPacketTypes.Keys.OrderBy(name => name).ToArray() });
            return;
        }

        if (context.Request.Url.AbsolutePath.Equals("/api/packet-types", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
        {
            WriteJson(context, new { packetTypes = GetPacketTypes() });
            return;
        }

        const string suppressionPrefix = "/api/suppression/";
        if (context.Request.Url.AbsolutePath.StartsWith(suppressionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string packetType = Uri.UnescapeDataString(context.Request.Url.AbsolutePath.Substring(suppressionPrefix.Length));
            if (string.IsNullOrWhiteSpace(packetType) || packetType.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }
            if (context.Request.HttpMethod == "PUT") suppressedPacketTypes[packetType] = 0;
            else if (context.Request.HttpMethod == "DELETE") suppressedPacketTypes.TryRemove(packetType, out _);
            else { context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed; context.Response.Close(); return; }
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Close();
            return;
        }

        string relativePath = context.Request.Url.AbsolutePath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath)) relativePath = "index.html";
        string fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(Path.GetFullPath(webRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        byte[] content = File.ReadAllBytes(fullPath);
        context.Response.ContentType = ContentType(Path.GetExtension(fullPath));
        context.Response.ContentLength64 = content.Length;
        context.Response.OutputStream.Write(content, 0, content.Length);
        context.Response.Close();
    }

    private void HandleEventDetail(HttpListenerContext context, long eventSequence)
    {
        if (!eventDetails.TryGetValue(eventSequence, out TraceEvent traceEvent))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        WriteJson(context, traceEvent);
    }

    private static void WriteJson(HttpListenerContext context, object value)
    {
        byte[] content = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None, browserJsonSettings));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = content.Length;
        context.Response.OutputStream.Write(content, 0, content.Length);
        context.Response.Close();
    }

    private IEnumerable<TracePacketType> GetPacketTypes()
    {
        Dictionary<string, ProtocolManifestPacket> manifestPackets = ProtocolManifestProvider.Current.Packets
            .Where(packet => !string.IsNullOrWhiteSpace(packet.TypeName))
            .GroupBy(packet => ShortTypeName(packet.TypeName), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> names = new(manifestPackets.Keys, StringComparer.Ordinal);
        names.UnionWith(packetCounts.Keys);
        names.UnionWith(suppressedPacketTypes.Keys);
        return names.OrderBy(name => name, StringComparer.Ordinal).Select(name =>
        {
            manifestPackets.TryGetValue(name, out ProtocolManifestPacket packet);
            packetCounts.TryGetValue(name, out long count);
            return new TracePacketType
            {
                Name = name,
                FullName = packet?.TypeName ?? name,
                Count = count,
                Suppressed = suppressedPacketTypes.ContainsKey(name),
                Direction = packet?.Direction ?? "Observed",
                Category = packet?.Category ?? "Runtime",
                HighFrequency = packet?.HighFrequency ?? false
            };
        });
    }

    private static string ShortTypeName(string fullName)
    {
        int separator = fullName.LastIndexOf('.');
        return separator < 0 ? fullName : fullName.Substring(separator + 1);
    }

    private void HandleEvents(HttpListenerContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.SendChunked = true;
        Guid id = Guid.NewGuid();
        BlockingCollection<string> queue = new(new ConcurrentQueue<string>());
        subscribers[id] = queue;
        try
        {
            // Browsers often open after the debug client has already received game/item-load
            // traffic. Replay the bounded trace history so those messages are still searchable.
            foreach (long eventSequence in eventOrder.ToArray())
                if (eventSummaries.TryGetValue(eventSequence, out string summary))
                    queue.TryAdd(summary);

            using StreamWriter writer = new(context.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.Write(": connected\n\n");
            foreach (string payload in queue.GetConsumingEnumerable(cancellation.Token))
                writer.Write($"data: {payload}\n\n");
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
        catch (IOException) { }
        finally
        {
            subscribers.TryRemove(id, out _);
            queue.Dispose();
            context.Response.Close();
        }
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        cancellation?.Cancel();
        foreach (BlockingCollection<string> queue in subscribers.Values) queue.CompleteAdding();
        if (listener.IsListening) listener.Stop();
        listener.Close();
        cancellation?.Dispose();
    }
}

internal sealed class TracePacketType
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public long Count { get; set; }
    public bool Suppressed { get; set; }
    public string Direction { get; set; } = "";
    public string Category { get; set; } = "";
    public bool HighFrequency { get; set; }
}
