using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SnowyNotch.Services;

public sealed class AgentBridge : IDisposable
{
    public const int Port = 47318;
    public const string Path = "/snowynotch/claude";
    public const string CodexPath = "/snowynotch/codex";
    public static string Url => $"http://127.0.0.1:{Port}{Path}";
    public static string CodexUrl => $"http://127.0.0.1:{Port}{CodexPath}";

    private const int MaxBody = 2 * 1024 * 1024;

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public AgentBridge(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _ = AcceptLoop(_cts.Token);
            Listening = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[agents] can't listen on {Port}: {ex.Message}");
        }
    }

    public bool Listening { get; }

    /// <summary>Событие от агента: источник (Claude или Codex) и JSON.</summary>
    public event Action<AgentKind, JsonElement>? EventReceived;

    private const int MaxConnections = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _slots = new(MaxConnections);

    private static readonly byte[] Ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    private static readonly byte[] Forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            // Не больше нескольких соединений сразу: зависшие клиенты не должны копиться.
            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = Handle(client, ct);
        }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            try
            {
                // ReceiveTimeout на асинхронное чтение не действует — ограничиваем весь запрос токеном.
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                var req = await ReadRequest(stream, timeout.Token);
                var kind = Classify(req);
                await stream.WriteAsync(kind == null ? Forbidden : Ok, timeout.Token);
                if (kind == null || req.Body.Length == 0) return;

                using var doc = JsonDocument.Parse(req.Body);
                var payload = doc.RootElement.Clone();
                await _ui.BeginInvoke(() => EventReceived?.Invoke(kind.Value, payload));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[agents] bad request: {ex.Message}");
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    /// <summary>
    /// Принимаем только события от локальных программ (Claude Code, пересылка Codex), а не из браузера.
    /// Любая веб-страница может отправить POST на 127.0.0.1 (fetch no-cors, скрытая форма) и подделать
    /// «агент ждёт разрешения». Браузер при этом всегда добавляет Origin / Sec-Fetch-*, а для DNS-rebinding
    /// подставляет чужой Host — такие запросы отклоняем.
    /// </summary>
    private static AgentKind? Classify(Request req)
    {
        if (req.Method != "POST") return null;
        // sec-fetch-mode не проверяем: его ставит и fetch в Node.js, которым могут слать хуки.
        if (req.Headers.ContainsKey("origin") || req.Headers.ContainsKey("sec-fetch-site")) return null;
        if (!req.Headers.TryGetValue("host", out var host) ||
            !(host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)))
            return null;
        string path = req.Target.Split('?', 2)[0];
        return path == Path ? AgentKind.Claude : path == CodexPath ? AgentKind.Codex : null;
    }

    private sealed record Request(string Method, string Target, System.Collections.Generic.Dictionary<string, string> Headers, byte[] Body);

    private static async Task<Request> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var head = new StringBuilder();
        var received = new System.IO.MemoryStream();
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) throw new InvalidOperationException("connection closed");
            received.Write(buffer, 0, n);
            if (received.Length > 64 * 1024) throw new InvalidOperationException("headers too large");
            headerEnd = IndexOf(received.GetBuffer(), (int)received.Length, "\r\n\r\n"u8);
        }

        byte[] all = received.ToArray();
        string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = headers.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        var headerMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headerMap[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        int length = 0;
        if (headerMap.TryGetValue("Content-Length", out var cl) && !int.TryParse(cl, out length))
            throw new InvalidOperationException("bad content-length");
        if (length < 0 || length > MaxBody) throw new InvalidOperationException("body too large");

        var body = new byte[length];
        int have = Math.Min(length, all.Length - (headerEnd + 4));
        Array.Copy(all, headerEnd + 4, body, 0, have);
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, length - have), ct);
            if (n == 0) break;
            have += n;
        }
        if (have < length) throw new InvalidOperationException("body truncated");
        return new Request(requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", headerMap, body);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
    {
        for (int i = 0; i + pattern.Length <= length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* уже остановлен */ }
    }
}
