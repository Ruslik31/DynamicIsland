using System.Text.Json;
using DynamicIsland.Discord;

// Консоль для проверки связи с плагином DiscordIslandBridge.
//   dotnet run -- [--seconds N] [--script "mute,wait:1500,mute,snapshot"]
// Клавиши: m — mute, d — deafen, s — снимок, v — отладка событий, q — выход.

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

using var server = new DiscordBridgeServer();
server.Connected += h => Log("connect", $"plugin {h.Plugin}, discord {h.Discord}, caps: " +
    string.Join(" ", h.Caps.Select(c => $"{c.Key}={(c.Value ? "ok" : "MISSING")}")));
server.Disconnected += () => Log("lost", "плагин отключился");
server.SelfChanged += u => Log("self", $"{u.Name} ({u.Id})");
server.VoiceChanged += v => Log("voice", v.Channel is null
    ? $"не в канале, selfMute={v.SelfMute} selfDeaf={v.SelfDeaf}"
    : $"#{v.Channel.Name} [{v.Channel.GuildName}] selfMute={v.SelfMute} selfDeaf={v.SelfDeaf}: " +
      string.Join(", ", v.Members.Select(m => $"{m.Name}{(m.Speaking ? "*" : "")}{(m.SelfMute ? "(m)" : "")}{(m.SelfDeaf ? "(d)" : "")}")));
server.SpeakingChanged += (id, on) => Log("speaking", $"{id} {(on ? "говорит" : "замолчал")}");
server.MessageReceived += m => Log("message", $"[{m.Reason}] {m.GuildName ?? "ЛС"}/{m.ChannelName} {m.Author.Name}: {m.Text} " +
    $"(вложений {m.Attachments.Count}, фокус окна {m.WindowFocused}) {m.DeepLink}");
server.DebugReceived += n => Log("debug", n.ToJsonString(json));
server.AckReceived += n => Log("ack", n.ToJsonString(json));

server.Start();
Log("listen", $"127.0.0.1:{server.Port}, ждём плагин");

async Task Run(string step)
{
    if (step.StartsWith("wait:")) { await Task.Delay(int.Parse(step[5..])); return; }
    Log("cmd", step);
    switch (step)
    {
        case "mute": server.ToggleMute(); break;
        case "deafen": server.ToggleDeafen(); break;
        case "snapshot": server.RequestSnapshot(); break;
        case "sniff": server.Sniff(true); break;
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
    await Run(key switch { 'm' => "mute", 'd' => "deafen", 's' => "snapshot", 'v' => "sniff", _ => "" });
}
