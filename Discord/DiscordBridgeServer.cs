using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Discord;

/// <summary>
/// Локальный WebSocket-сервер, к которому подключается плагин DiscordIslandBridge внутри Discord.
/// Слушает только 127.0.0.1, принимает только запросы со страниц Discord (Origin) и только с общим
/// секретом из %APPDATA%\DiscordIsland\bridge.json. Без сторонних пакетов.
/// </summary>
public sealed class DiscordBridgeServer : IDisposable
{
    public const int PreferredPort = 47615;
    private const int MaxMessage = 2_000_000;

    private static readonly string[] AllowedOrigins =
    {
        "https://discord.com", "https://canary.discord.com", "https://ptb.discord.com",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _configPath;
    private readonly string _token;
    private HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Connection? _active;

    private sealed class Connection
    {
        public required WebSocket Socket { get; init; }
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    public int Port { get; private set; }
    public bool IsConnected { get { lock (_gate) return _active is not null; } }

    public event Action<HelloInfo>? Connected;
    public event Action? Disconnected;
    public event Action<DiscordUser>? SelfChanged;
    public event Action<VoiceState>? VoiceChanged;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<JsonNode>? DebugReceived;
    public event Action<JsonNode>? AckReceived;

    public DiscordBridgeServer(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscordIsland");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "bridge.json");
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public void Start()
    {
        Port = Listen(PreferredPort) ? PreferredPort : ListenOnFreePort();
        var cfg = JsonSerializer.Serialize(new { port = Port, token = _token, pid = Environment.ProcessId });
        File.WriteAllText(_configPath, cfg, new UTF8Encoding(false));
        _ = Task.Run(AcceptLoop);
    }

    private bool Listen(int port)
    {
        // после неудачного Start() HttpListener уничтожается, поэтому на каждую попытку новый
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException) { return false; }
        _listener = listener;
        return true;
    }

    private int ListenOnFreePort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            if (Listen(port)) return port;
        }
        throw new InvalidOperationException("Не удалось занять локальный порт для моста Discord.");
    }

    // ---------- команды плагину ----------

    public bool ToggleMute() => Command("toggleMute");
    public bool ToggleDeafen() => Command("toggleDeaf");
    public bool Leave() => Command("leave");
    public bool SetMute(bool value) => Command("setMute", ("value", value));
    public bool SetDeafen(bool value) => Command("setDeaf", ("value", value));
    public bool SetUserVolume(string userId, int percent) => Command("setVolume", ("userId", userId), ("volume", percent));
    public bool RequestSnapshot() => Command("snapshot");
    public bool Sniff(bool on, string? pattern = null) => Command("sniff", ("on", on), ("pattern", pattern));

    private bool Command(string name, params (string Key, object? Value)[] args)
    {
        var obj = new JsonObject { ["c"] = name, ["id"] = Guid.NewGuid().ToString("N")[..8] };
        foreach (var (key, value) in args) obj[key] = JsonValue.Create(value);
        return Send(obj.ToJsonString());
    }

    private bool Send(string json)
    {
        Connection? c;
        lock (_gate) c = _active;
        if (c is null || c.Socket.State != WebSocketState.Open) return false;
        _ = SendAsync(c, json);
        return true;
    }

    private async Task SendAsync(Connection c, string json)
    {
        try
        {
            await c.SendLock.WaitAsync(_cts.Token);
            try
            {
                await c.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token);
            }
            finally { c.SendLock.Release(); }
        }
        catch { /* соединение закрылось, Serve это заметит */ }
    }

    // ---------- приём ----------

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Accept(ctx));
        }
    }

    private async Task Accept(HttpListenerContext ctx)
    {
        try
        {
            var origin = ctx.Request.Headers["Origin"];
            if (!ctx.Request.IsWebSocketRequest || origin is null || !AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            await Serve(wsCtx.WebSocket);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* уже закрыт */ }
        }
    }

    private async Task Serve(WebSocket ws)
    {
        var conn = new Connection { Socket = ws };
        var authed = false;
        try
        {
            var buffer = new byte[16 * 1024];
            using var hello = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var helloLinked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, hello.Token);
            var message = new MemoryStream();

            while (ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, authed ? _cts.Token : helloLinked.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > MaxMessage) return;
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

                if (!authed)
                {
                    if (!TryHello(text, conn, out var info)) return;
                    authed = true;
                    Connected?.Invoke(info);
                    continue;
                }
                Handle(text);
            }
        }
        catch { /* сокет закрыт или таймаут приветствия */ }
        finally
        {
            var wasActive = false;
            lock (_gate)
            {
                if (ReferenceEquals(_active, conn)) { _active = null; wasActive = true; }
            }
            try { ws.Abort(); } catch { /* уже закрыт */ }
            ws.Dispose();
            if (wasActive) Disconnected?.Invoke();
        }
    }

    private bool TryHello(string text, Connection conn, out HelloInfo info)
    {
        info = default!;
        try
        {
            var node = JsonNode.Parse(text);
            if (node?["t"]?.GetValue<string>() != "hello") return false;
            var given = node["token"]?.GetValue<string>() ?? "";
            if (given.Length != _token.Length ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(_token)))
                return false;

            Connection? old;
            lock (_gate) { old = _active; _active = conn; }
            try { old?.Socket.Abort(); } catch { /* уже закрыт */ }

            var caps = node["caps"]?.Deserialize<Dictionary<string, bool>>(Json) ?? new();
            info = new HelloInfo(node["plugin"]?.GetValue<string>() ?? "?", node["discord"]?.GetValue<string>(), caps);
            _ = SendAsync(conn, "{\"t\":\"welcome\"}");
            return true;
        }
        catch { return false; }
    }

    private void Handle(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); } catch { return; }
        if (node is null) return;
        try
        {
            switch (node["t"]?.GetValue<string>())
            {
                case "self": SelfChanged?.Invoke(node["user"].Deserialize<DiscordUser>(Json)!); break;
                case "voice": VoiceChanged?.Invoke(node.Deserialize<VoiceState>(Json)!); break;
                case "speaking": SpeakingChanged?.Invoke(node["userId"]!.GetValue<string>(), node["speaking"]!.GetValue<bool>()); break;
                case "message": MessageReceived?.Invoke(node.Deserialize<ChatMessage>(Json)!); break;
                case "dbg": DebugReceived?.Invoke(node); break;
                case "ack": AckReceived?.Invoke(node); break;
            }
        }
        catch (Exception e)
        {
            DebugReceived?.Invoke(new JsonObject { ["error"] = "parse: " + e.Message });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { /* уже закрыт */ }
        lock (_gate) { try { _active?.Socket.Abort(); } catch { /* уже закрыт */ } _active = null; }
        try { File.Delete(_configPath); } catch { /* файл мог быть удалён */ }
    }
}
