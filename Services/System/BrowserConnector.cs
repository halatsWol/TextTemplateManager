using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TextTemplateManager.Services.System;

// ---- Wire DTOs (serialized as camelCase JSON) ----
public sealed record PasteModeDto(string Id, string Label);
public sealed record ConnectorNodeDto(string Id, string Name, string Type, string? DefaultMode, string? Source, List<ConnectorNodeDto>? Children);
public sealed record TemplateContentDto(string Id, string Name, string Mode, string ContentType, string Content);
public sealed record CreatedTemplateDto(string Id, string Name);

/// <summary>Data the connector serves. The app implements this over its live templates; a test
/// harness can implement it with fixtures. Read methods run on the connector's background threads
/// (implementations read an immutable snapshot); CreateTemplate marshals to the UI thread itself.</summary>
public interface IConnectorDataSource
{
    string AppVersion { get; }
    IReadOnlyList<PasteModeDto> PasteModes();
    IReadOnlyList<ConnectorNodeDto> Tree();
    TemplateContentDto? Template(string id, string? mode);   // null when the id is unknown
    CreatedTemplateDto CreateTemplate(string content, string? name);   // adds a template to the local area
}

/// <summary>
/// A loopback (127.0.0.1) HTTP/1.1 server that browser extensions call to list templates and fetch
/// rendered content. Built on TcpListener (not HttpListener) so it needs no URL-ACL reservation and
/// runs under a non-admin per-user install. Security: loopback-only bind, a required token header,
/// and CORS limited to extension origins.
/// </summary>
public sealed class BrowserConnector : IDisposable
{
    public const int ProtocolVersion = 2;   // v2 adds POST /template (create)
    public const string TokenHeader = "x-ttm-token";

    private readonly IConnectorDataSource _data;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Loopback JSON consumed by JSON.parse — relaxed escaping keeps HTML content readable.
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public BrowserConnector(IConnectorDataSource data) => _data = data;

    public bool IsRunning => _listener != null;

    /// <summary>Binds loopback:port and begins serving. Throws (SocketException) if the port is taken.</summary>
    public void Start(int port, string token)
    {
        Stop();
        _token = token ?? "";
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        // Run the accept loop on the thread pool, never the caller's (UI) thread. Otherwise the
        // awaits capture the UI context and request handling runs on the UI thread — where
        // CreateTemplate (which marshals a create back to the UI thread and waits for it) deadlocks.
        var listener = _listener;
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, ct));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                // No token here on purpose: Stop() unblocks the accept by closing the listener,
                // which surfaces as ObjectDisposedException below — cleaner than the
                // OperationCanceledException a cancellation token throws on every disable.
                client = await listener.AcceptTcpClientAsync();
            }
            catch { break; }   // listener stopped / disposed on shutdown
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var req = await ReadRequestAsync(stream, ct);
                if (req == null) return;
                var res = Route(req);
                await WriteResponseAsync(stream, res, ct);
            }
            catch { /* best effort — drop the connection */ }
        }
    }

    // ---- Request parsing ----
    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body);

    private static readonly byte[] HeaderEnd = { 0x0D, 0x0A, 0x0D, 0x0A };   // \r\n\r\n

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        int headEnd = -1;

        // Read raw bytes until the header block ends. Headers are ASCII, but a POST body (below) is
        // UTF-8, so the two can't be ASCII-decoded together.
        while (headEnd < 0)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            headEnd = IndexOf(ms.GetBuffer(), (int)ms.Length, HeaderEnd);
            if (ms.Length > 64 * 1024) break;   // guard against oversized headers
        }
        if (headEnd < 0) return null;

        byte[] all = ms.GetBuffer();
        int total = (int)ms.Length;

        var lines = Encoding.ASCII.GetString(all, 0, headEnd).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length < 2) return null;

        string method = start[0].ToUpperInvariant();
        string rawUrl = start[1];
        string path = rawUrl;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = rawUrl.IndexOf('?');
        if (q >= 0)
        {
            path = rawUrl.Substring(0, q);
            foreach (var pair in rawUrl.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                query[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v);
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
        }

        // Body: read up to Content-Length bytes (capped), decoded as UTF-8. Some may already be buffered.
        string body = "";
        if (headers.TryGetValue("Content-Length", out var clStr) &&
            int.TryParse(clStr, out int contentLength) && contentLength > 0)
        {
            contentLength = Math.Min(contentLength, 1024 * 1024);   // cap at 1 MB
            int bodyStart = headEnd + HeaderEnd.Length;
            using var bodyMs = new MemoryStream();
            int have = total - bodyStart;
            if (have > 0) bodyMs.Write(all, bodyStart, Math.Min(have, contentLength));
            while (bodyMs.Length < contentLength)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) break;
                bodyMs.Write(buf, 0, Math.Min(n, contentLength - (int)bodyMs.Length));
            }
            body = Encoding.UTF8.GetString(bodyMs.GetBuffer(), 0, (int)bodyMs.Length);
        }

        string cleanPath = path.TrimEnd('/');
        return new Request(method, cleanPath.Length == 0 ? "/" : cleanPath, query, headers, body);
    }

    // First index of `pattern` within the first `len` bytes of `b`, or -1.
    private static int IndexOf(byte[] b, int len, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= len; i++)
        {
            int j = 0;
            while (j < pattern.Length && b[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    // ---- Routing + security ----
    private sealed record Response(int Status, string Body, string Origin);

    private static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("safari-web-extension://", StringComparison.OrdinalIgnoreCase);

    private Response Route(Request req)
    {
        req.Headers.TryGetValue("Origin", out string? origin);
        origin ??= "";

        // CORS is echoed back only to extension origins; a website can neither read responses nor
        // (without the token) get a useful one.
        string corsOrigin = IsExtensionOrigin(origin) ? origin : "";

        if (req.Method == "OPTIONS") return new Response(204, "", corsOrigin);   // preflight

        // Reject a cross-origin request from a non-extension origin outright.
        if (origin.Length > 0 && corsOrigin.Length == 0)
            return new Response(403, "{\"error\":\"forbidden origin\"}", "");

        // Token gate.
        req.Headers.TryGetValue(TokenHeader, out string? token);
        if (string.IsNullOrEmpty(_token) || token != _token)
            return new Response(401, "{\"error\":\"unauthorized\"}", corsOrigin);

        try
        {
            switch (req.Path)
            {
                case "/ping":
                    return Ok(new { app = "TextTemplateManager", version = _data.AppVersion, protocol = ProtocolVersion }, corsOrigin);
                case "/pastemodes":
                    return Ok(_data.PasteModes(), corsOrigin);
                case "/tree":
                    return Ok(_data.Tree(), corsOrigin);
                case "/template" when req.Method == "POST":
                    return CreateTemplate(req, corsOrigin);
                case "/template":
                    if (!req.Query.TryGetValue("id", out string? id) || string.IsNullOrWhiteSpace(id))
                        return new Response(400, "{\"error\":\"missing id\"}", corsOrigin);
                    req.Query.TryGetValue("mode", out string? mode);
                    var tpl = _data.Template(id, mode);
                    return tpl == null
                        ? new Response(404, "{\"error\":\"not found\"}", corsOrigin)
                        : Ok(tpl, corsOrigin);
                default:
                    return new Response(404, "{\"error\":\"unknown endpoint\"}", corsOrigin);
            }
        }
        catch
        {
            return new Response(500, "{\"error\":\"internal\"}", corsOrigin);
        }
    }

    // POST /template — body {"content": "...", "name"?: "..."}. Adds a template to the local area
    // (name defaults to "New Template", incremented on collision) and returns {"id","name"}.
    private Response CreateTemplate(Request req, string corsOrigin)
    {
        string? content = null, name = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("content", out var c)) content = c.GetString();
                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
            }
        }
        catch { return new Response(400, "{\"error\":\"invalid json\"}", corsOrigin); }

        if (string.IsNullOrEmpty(content))
            return new Response(400, "{\"error\":\"missing content\"}", corsOrigin);

        return Ok(_data.CreateTemplate(content, name), corsOrigin);
    }

    private static Response Ok(object payload, string origin) =>
        new(200, JsonSerializer.Serialize(payload, Json), origin);

    private static async Task WriteResponseAsync(NetworkStream stream, Response res, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(res.Body);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(Reason(res.Status)).Append("\r\n");
        head.Append("Content-Type: application/json; charset=utf-8\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (res.Origin.Length > 0)
        {
            head.Append("Access-Control-Allow-Origin: ").Append(res.Origin).Append("\r\n");
            head.Append("Vary: Origin\r\n");
            head.Append("Access-Control-Allow-Headers: ").Append(TokenHeader).Append(", Content-Type\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            head.Append("Access-Control-Max-Age: 600\r\n");
        }
        head.Append("Connection: close\r\n\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", _ => "Error",
    };
}
