using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Dvmp.Agent;

/// <summary>
/// Minimal loopback-only HTTP/1.1 server. One request per connection, JSON in and out.
/// Every request must carry the bearer token from the discovery file and a loopback Host
/// header (blocks DNS-rebinding from a browser). No endpoint discloses the token.
/// </summary>
internal sealed class AgentServer
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 32 * 1024 * 1024;

    private TcpListener listener;
    private Thread acceptThread;
    private volatile bool running;

    public int Port { get; private set; }
    public string Token { get; private set; }

    public void Start()
    {
        byte[] secret = new byte[32];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
        Token = BitConverter.ToString(secret).Replace("-", string.Empty).ToLowerInvariant();

        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "dvmp-agent-http" };
        acceptThread.Start();
    }

    public void Stop()
    {
        running = false;
        try { listener?.Stop(); } catch { }
    }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { if (!running) return; continue; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 30_000;
            NetworkStream stream = client.GetStream();
            int status;
            string body;
            try
            {
                Request request = Read(stream);
                Authorize(request);
                (status, body) = Endpoints.Handle(request.Path, request.Body);
            }
            catch (HttpError e)
            {
                status = e.Status;
                body = Json.Error(e.Message);
            }
            catch (Exception e)
            {
                status = 500;
                body = Json.Error(e.ToString());
            }
            try { Write(stream, status, body); } catch { }
        }
    }

    private void Authorize(Request request)
    {
        if (request.Method != "POST") throw new HttpError(405, "only POST is supported");

        string host = request.Header("host");
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
            throw new HttpError(403, "bad host header");

        string auth = request.Header("authorization");
        if (auth == null || !auth.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !FixedTimeEquals(auth.Substring(7), Token))
            throw new HttpError(401, "bad or missing token");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static Request Read(Stream stream)
    {
        // Read until the end of headers.
        MemoryStream head = new();
        uint last4 = 0;
        const uint crlfcrlf = 0x0D0A0D0A;
        while (last4 != crlfcrlf)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new HttpError(400, "connection closed in headers");
            head.WriteByte((byte)b);
            if (head.Length > MaxHeaderBytes) throw new HttpError(431, "headers too large");
            last4 = (last4 << 8) | (byte)b;
        }

        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) throw new HttpError(400, "bad request line");

        Request request = new() { Method = requestLine[0], Path = requestLine[1] };
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            request.Headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] = lines[i].Substring(colon + 1).Trim();
        }

        int length = 0;
        string lengthHeader = request.Header("content-length");
        if (lengthHeader != null && (!int.TryParse(lengthHeader, out length) || length < 0))
            throw new HttpError(400, "bad content-length");
        if (length > MaxBodyBytes) throw new HttpError(413, "body too large");

        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(body, read, length - read);
            if (n <= 0) throw new HttpError(400, "connection closed in body");
            read += n;
        }
        request.Body = Encoding.UTF8.GetString(body);
        return request;
    }

    private static void Write(Stream stream, int status, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body ?? "{}");
        string head = $"HTTP/1.1 {status} {(status < 400 ? "OK" : "Error")}\r\n" +
                      "Content-Type: application/json; charset=utf-8\r\n" +
                      $"Content-Length: {payload.Length}\r\n" +
                      "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private sealed class Request
    {
        public string Method;
        public string Path;
        public string Body = string.Empty;
        public readonly Dictionary<string, string> Headers = new();
        public string Header(string name) => Headers.TryGetValue(name, out string value) ? value : null;
    }
}

internal sealed class HttpError : Exception
{
    public HttpError(int status, string message) : base(message) => Status = status;
    public int Status { get; }
}
