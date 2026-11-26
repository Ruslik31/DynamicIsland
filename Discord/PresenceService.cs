using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DynamicIsland.Discord;

/// <summary>
/// Две карточки Rich Presence в профиле Discord: музыка и голосовой канал.
/// Discord показывает одну активность на приложение, поэтому каждая карточка —
/// отдельное IPC-подключение со своим Application ID (PresenceClient).
/// </summary>
sealed class PresenceService : IDisposable
{
    // Создайте два приложения на https://discord.com/developers/applications (New Application)
    // и загрузите картинки в Rich Presence → Art Assets:
    // «music» для музыкальной карточки, «voice» для карточки звонка (PNG, 512×512).
    // Имя приложения — это заголовок карточки («Discord Island Music», «Discord Island Voice»).
    // ID приложений хранятся XOR-пакованными байтами (ключ ниже), не открытым текстом.
    static readonly byte[] MusicPacked = [117, 92, 68, 86, 90, 71, 82, 112, 67, 91, 85, 91, 84, 114, 90, 67, 81, 90, 68];
    static readonly byte[] VoicePacked = [117, 92, 68, 86, 90, 71, 83, 121, 65, 84, 81, 87, 81, 113, 80, 75, 84, 90, 68];
    const string PackKey = "DiscordIsland";

    static string Unpack(byte[] packed)
    {
        char[] chars = new char[packed.Length];
        for (int i = 0; i < packed.Length; i++) chars[i] = (char)(packed[i] ^ PackKey[i % PackKey.Length]);
        return new string(chars);
    }

    static string ResolveClientId(string builtin, string configured) =>
        ulong.TryParse(builtin, out ulong id) && id > 1000 ? builtin : configured;

    readonly PresenceClient _music;
    readonly PresenceClient _voice;
    readonly MediaService _media;
    readonly DiscordService _discord;
    readonly DispatcherTimer _voiceDelay = new() { Interval = TimeSpan.FromSeconds(1.5) };

    string _musicKey = "", _voiceKey = "";

    public PresenceService(MediaService media, DiscordService discord)
    {
        _media = media;
        _discord = discord;
        _music = new PresenceClient(ResolveClientId(Unpack(MusicPacked), Settings.RpcMusicClientId), "music");
        _voice = new PresenceClient(ResolveClientId(Unpack(VoicePacked), Settings.RpcVoiceClientId), "voice");

        _media.Changed += UpdateMusic;
        _discord.VoiceChanged += UpdateVoice;
        _discord.SpeakingChanged += (_, _) => UpdateVoice();
        _voiceDelay.Tick += (_, _) =>
        {
            _voiceDelay.Stop();
            SendVoice();
        };
    }

    public void Start()
    {
        _music.Start();
        _voice.Start();
        UpdateMusic();
        UpdateVoice();
    }

    /// <summary>Перечитать настройки и переслать обе карточки (после переключателей в меню).</summary>
    public void Refresh()
    {
        _musicKey = _voiceKey = "";
        UpdateMusic();
        UpdateVoice();
    }

    // ---------- музыка ----------

    void UpdateMusic()
    {
        if (!Settings.RpcMusic || !_media.HasTrack)
        {
            if (_musicKey.Length > 0)
            {
                _musicKey = "";
                _music.SetActivity(null);
            }
            return;
        }

        TimeSpan duration = _media.Duration;
        bool timed = _media.IsPlaying && _media.Seekable && duration.TotalSeconds >= 1;
        // Позиция входит в ключ корзинами по 10 секунд: прогресс-бар поправляется, но без спама.
        string key = $"{_media.Title}|{_media.Artist}|{_media.IsPlaying}|{(long)duration.TotalSeconds}|{(timed ? (long)(_media.Position.TotalSeconds / 10) : -1)}";
        if (key == _musicKey) return;
        _musicKey = key;

        string title = _media.Name;
        string artist = _media.Artist;
        var activity = new JsonObject
        {
            ["details"] = title,
            ["state"] = artist.Length > 0 ? artist : SourceApp.Name(_media.Source) is { Length: > 0 } app ? app : null,
            ["assets"] = new JsonObject
            {
                ["large_image"] = "music",
                ["large_text"] = SourceApp.Name(_media.Source),
            },
            ["instance"] = false,
        };

        if (timed)
        {
            DateTimeOffset start = DateTimeOffset.UtcNow - _media.Position;
            activity["timestamps"] = new JsonObject
            {
                ["start"] = start.ToUnixTimeSeconds(),
                ["end"] = start.Add(duration).ToUnixTimeSeconds(),
            };
        }

        _music.SetActivity(activity);
    }

    // ---------- звонок ----------

    void UpdateVoice()
    {
        VoiceChannel? channel = Settings.RpcVoice ? _discord.Voice.Channel : null;
        if (channel is null)
        {
            _voiceDelay.Stop();
            if (_voiceKey.Length > 0)
            {
                _voiceKey = "";
                _voice.SetActivity(null);
            }
            return;
        }

        VoiceMember? speaker = _discord.Voice.Members.FirstOrDefault(m => m.Speaking && !string.IsNullOrEmpty(m.Avatar));
        string? icon = _discord.GuildIconUrl(channel.GuildId);
        string key = $"{channel.Id}|{channel.Name}|{_discord.Voice.Members.Count}|{speaker?.Avatar}|{icon}";
        if (key == _voiceKey) return;
        _voiceKey = key;
        _voiceDelay.Stop();
        _voiceDelay.Start();
    }

    void SendVoice()
    {
        VoiceChannel? channel = Settings.RpcVoice ? _discord.Voice.Channel : null;
        if (channel is null) return;

        int count = _discord.Voice.Members.Count;
        VoiceMember? speaker = _discord.Voice.Members.FirstOrDefault(m => m.Speaking && !string.IsNullOrEmpty(m.Avatar));
        string? guild = channel.GuildName;
        string? icon = _discord.GuildIconUrl(channel.GuildId);

        var activity = new JsonObject
        {
            ["details"] = $"🔊 {channel.Name}",
            ["state"] = guild is { Length: > 0 }
                ? $"{count} {People(count)} · {guild}"
                : $"{count} {People(count)}",
            ["timestamps"] = new JsonObject
            {
                ["start"] = new DateTimeOffset(_discord.JoinedAt.ToUniversalTime()).ToUnixTimeSeconds(),
            },
            ["assets"] = new JsonObject
            {
                ["large_image"] = icon ?? "voice",
                ["large_text"] = guild ?? "Голосовой канал",
            },
            ["instance"] = false,
        };

        if (speaker is not null)
            (activity["assets"] as JsonObject)!["small_image"] = speaker.Avatar;

        _voice.SetActivity(activity);
    }

    static string People(int count) => (count % 100 / 10) == 1
        ? "участников"
        : (count % 10) switch
        {
            1 => "участник",
            2 or 3 or 4 => "участника",
            _ => "участников",
        };

    public void Dispose()
    {
        _voiceDelay.Stop();
        _music.Dispose();
        _voice.Dispose();
    }
}
