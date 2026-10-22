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
