using System.Text.Json;
using DynamicIsland.Discord;

// Консоль для проверки связи с Discord:
//   - Нативно через RPC (\\.\pipe\discord-ipc-0), без BetterDiscord
//   - Через локальный мост WebSocket (плагин BetterDiscord / Vesktop)
//   dotnet run -- [--seconds N] [--script "mute,wait:1500,mute,snapshot"]
// Клавиши: m — mute, d — deafen, s — снимок, l — выйти из канала, v — sniff, q — выход.

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

var seconds = 0;
string? script = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
    else if (args[i] == "--script" && i + 1 < args.Length) script = args[++i];
}

var json = new JsonSerializerOptions { WriteIndented = false };
void Log(string kind, string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {kind,-8} {text}");

// 1. Нативный RPC клиент
using var rpc = new DiscordRpcClient();
rpc.Connected += u => Log("rpc:conn", $"Нативный RPC подключён! Пользователь: {u.Name} ({u.Id})");
rpc.Disconnected += () => Log("rpc:lost", "Нативный RPC отключился");
rpc.SelfChanged += u => Log("rpc:self", $"{u.Name} ({u.Id})");
rpc.VoiceChanged += v => Log("rpc:voice", v.Channel is null
    ? $"не в канале, selfMute={v.SelfMute} selfDeaf={v.SelfDeaf}"
    : $"#{v.Channel.Name} [{v.Channel.GuildName}] selfMute={v.SelfMute} selfDeaf={v.SelfDeaf}: " +
      string.Join(", ", v.Members.Select(m => $"{m.Name}{(m.Speaking ? "*" : "")}{(m.SelfMute ? "(m)" : "")}{(m.SelfDeaf ? "(d)" : "")}")));
rpc.SpeakingChanged += (id, on) => Log("rpc:spk", $"{id} {(on ? "говорит" : "замолчал")}");
rpc.MemberMoved += (m, joined) => Log("rpc:move", $"{m.Name} {(joined ? "зашёл" : "вышел")}");

// 2. Сервер WebSocket моста для плагина
using var server = new DiscordBridgeServer();
server.Connected += h => Log("ws:conn", $"плагин {h.Plugin}, discord {h.Discord}, caps: " +
    string.Join(" ", h.Caps.Select(c => $"{c.Key}={(c.Value ? "ok" : "MISSING")}")));
server.Disconnected += () => Log("ws:lost", "плагин отключился");
server.SelfChanged += u => Log("ws:self", $"{u.Name} ({u.Id})");
server.VoiceChanged += v => Log("ws:voice", v.Channel is null
    ? $"не в канале, selfMute={v.SelfMute} selfDeaf={v.SelfDeaf}"
    : $"#{v.Channel.Name} [{v.Channel.GuildName}] selfMute={v.SelfMute} selfDeaf={v.SelfDeaf}: " +
      string.Join(", ", v.Members.Select(m => $"{m.Name}{(m.Speaking ? "*" : "")}{(m.SelfMute ? "(m)" : "")}{(m.SelfDeaf ? "(d)" : "")}")));
server.SpeakingChanged += (id, on) => Log("ws:spk", $"{id} {(on ? "говорит" : "замолчал")}");
server.MessageReceived += m => Log("ws:msg", $"[{m.Reason}] {m.GuildName ?? "ЛС"}/{m.ChannelName} {m.Author.Name}: {m.Text} " +
    $"(вложений {m.Attachments.Count}, фокус окна {m.WindowFocused}) {m.DeepLink}");
server.DebugReceived += n => Log("ws:debug", n.ToJsonString(json));
server.AckReceived += n => Log("ws:ack", n.ToJsonString(json));

server.Start();
rpc.Start();

Log("probe", $"Слушаю: нативный RPC (\\\\.\\pipe\\discord-ipc-*) и WebSocket (127.0.0.1:{server.Port})");

async Task Run(string step)
{
    if (step.StartsWith("wait:")) { await Task.Delay(int.Parse(step[5..])); return; }
    Log("cmd", step);
    switch (step)
    {
        case "mute":
            if (rpc.IsConnected) rpc.ToggleMute();
            if (server.IsConnected) server.ToggleMute();
            break;
        case "deafen":
            if (rpc.IsConnected) rpc.ToggleDeafen();
            if (server.IsConnected) server.ToggleDeafen();
            break;
        case "leave":
            if (rpc.IsConnected) rpc.Leave();
            if (server.IsConnected) server.Leave();
            break;
        case "snapshot":
            if (rpc.IsConnected) rpc.RequestSnapshot();
            if (server.IsConnected) server.RequestSnapshot();
            break;
        case "sniff":
            if (server.IsConnected) server.Sniff(true);
            break;
    }
}

if (script is not null || seconds > 0)
{
    var until = DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 30);
    if (script is not null)
        foreach (var step in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await Run(step);
    var left = until - DateTime.UtcNow;
    if (left > TimeSpan.Zero) await Task.Delay(left);
    return;
}

while (true)
{
    var key = Console.ReadKey(true).KeyChar;
    if (key == 'q') break;
    await Run(key switch
    {
        'm' => "mute",
        'd' => "deafen",
        'l' => "leave",
        's' => "snapshot",
        'v' => "sniff",
        _ => ""
    });
}
