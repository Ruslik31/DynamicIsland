using System.Windows.Threading;
using DynamicIsland.Discord;

namespace DynamicIsland;

/// <summary>
/// Состояние Discord для острова: мост к плагину BetterDiscord и то, что из него выведено
/// (звонок, участники, кто говорит, сколько идёт разговор). Все события приходят в потоке окна.
/// </summary>
sealed class DiscordService : IDisposable
{
    static readonly VoiceState NoCall = new(null, [], false, false);

    readonly Dispatcher _dispatcher;
    readonly DiscordBridgeServer _bridge = new();
    readonly HashSet<string> _speaking = [];

    public DiscordService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _bridge.Connected += hello => Post(() => OnLinked(hello));
        _bridge.Disconnected += () => Post(OnUnlinked);
        _bridge.SelfChanged += user => Post(() => Me = user);
        _bridge.VoiceChanged += state => Post(() => OnVoice(state));
        _bridge.SpeakingChanged += (id, on) => Post(() => OnSpeaking(id, on));
        _bridge.MessageReceived += message => Post(() => MessageReceived?.Invoke(message));
    }

    public bool Linked { get; private set; }

    public string PluginVersion { get; private set; } = "";

    public string DiscordVersion { get; private set; } = "";

    public bool Started { get; private set; }

    public DiscordUser? Me { get; private set; }

    public VoiceState Voice { get; private set; } = NoCall;

    public DateTime JoinedAt { get; private set; }

    public bool InCall => Voice.Channel is not null;

    public bool IsSpeaking(string userId) => _speaking.Contains(userId);

    /// <summary>Мост поднят, плагин подключён или нет.</summary>
    public event Action? LinkChanged;

    /// <summary>Изменился канал, состав участников или чьё-то состояние (микрофон, наушники, трансляция).</summary>
    public event Action? VoiceChanged;

    public event Action<string, bool>? SpeakingChanged;

    public event Action<VoiceMember, bool>? MemberMoved;

    public event Action<ChatMessage>? MessageReceived;

    public void Start()
    {
        try
        {
            _bridge.Start();
            Started = true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>
    /// Демо без Discord: выдуманный звонок с разными состояниями участников для отладки внешнего вида и скриншотов.
    /// Мост не запускается, поэтому кнопки в таком звонке ничего не делают.
    /// </summary>
    public void StartDemo(bool withMessage)
    {
        const string Defaults = "https://cdn.discordapp.com/embed/avatars/";
        VoiceMember Member(string id, string name, int avatar, bool self = false, bool mute = false, bool deaf = false, bool stream = false) =>
            new(id, name, $"{Defaults}{avatar}.png", self, mute, deaf, false, false, stream, false, false, 100, false);

        Started = Linked = true;
        PluginVersion = "демо";
        Me = new DiscordUser("1", "Вы", $"{Defaults}0.png");
        JoinedAt = DateTime.Now.AddMinutes(-12).AddSeconds(-34);
        Voice = new VoiceState(
            new VoiceChannel("10", "Общий голосовой", "20", "Демо-сервер", 2),
            [
                Member("2", "Маша", 1, mute: true),
                Member("3", "Дима", 2, deaf: true),
                Member("4", "Лена", 3, stream: true),
                Member("5", "Артём", 4),
                Member("1", "Вы", 0, self: true),
            ],
            false, false);
        _speaking.Add("4");
        LinkChanged?.Invoke();
        VoiceChanged?.Invoke();

        if (!withMessage) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            MessageReceived?.Invoke(new ChatMessage("1", "dm", false, "30", null, "", null, true,
                new MessageAuthor("2", "Маша", $"{Defaults}1.png", false), "Привет! Идёшь в звонок? Мы уже все собрались",
                [], 0, 0, null, false, null));
        };
        timer.Start();
    }

    public void ToggleMute() => _bridge.ToggleMute();

    public void ToggleDeafen() => _bridge.ToggleDeafen();

    public void Leave() => _bridge.Leave();

    public void SetUserVolume(string userId, int percent) => _bridge.SetUserVolume(userId, Math.Clamp(percent, 0, 200));

    public void Refresh() => _bridge.RequestSnapshot();

    void Post(Action action) => _dispatcher.BeginInvoke(() =>
    {
        try { action(); }
        catch (Exception ex) { App.Log(ex); }
    });

    void OnLinked(HelloInfo hello)
    {
        Linked = true;
        PluginVersion = hello.Plugin;
        DiscordVersion = hello.Discord ?? "";
        LinkChanged?.Invoke();
    }

    void OnUnlinked()
    {
        Linked = false;
        _speaking.Clear();
        bool wasInCall = InCall;
        Voice = NoCall;
        Me = null;
        LinkChanged?.Invoke();
        if (wasInCall) VoiceChanged?.Invoke();
    }

    void OnVoice(VoiceState state)
    {
        VoiceState was = Voice;
        bool sameChannel = was.Channel is { } oldChannel && state.Channel is { } newChannel && oldChannel.Id == newChannel.Id;
        if (state.Channel is not null && !sameChannel) JoinedAt = DateTime.Now;

        _speaking.Clear();
        foreach (VoiceMember member in state.Members)
        {
            if (member.Speaking) _speaking.Add(member.Id);
        }
        Voice = state;

        if (sameChannel)
        {
            HashSet<string> before = [.. was.Members.Select(member => member.Id)];
            HashSet<string> now = [.. state.Members.Select(member => member.Id)];
            foreach (VoiceMember joined in state.Members.Where(member => !member.Self && !before.Contains(member.Id)))
                MemberMoved?.Invoke(joined, true);
            foreach (VoiceMember left in was.Members.Where(member => !member.Self && !now.Contains(member.Id)))
                MemberMoved?.Invoke(left, false);
        }
        VoiceChanged?.Invoke();
    }

    void OnSpeaking(string userId, bool on)
    {
        if (on ? !_speaking.Add(userId) : !_speaking.Remove(userId)) return;
        SpeakingChanged?.Invoke(userId, on);
    }

    public void Dispose() => _bridge.Dispose();
}
