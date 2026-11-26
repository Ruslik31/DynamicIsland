using System.Windows.Threading;
using DynamicIsland.Discord;

namespace DynamicIsland;

/// <summary>
/// Состояние Discord для острова: гибридный контроллер, поддерживающий как нативный
/// Discord RPC (\\.\pipe\discord-ipc-0), не требующий модификаций клиента, так и
/// классический локальный WebSocket-мост для BetterDiscord / Vesktop.
/// Все события передаются в поток интерфейса (Dispatcher).
/// </summary>
sealed class DiscordService : IDisposable
{
    static readonly VoiceState NoCall = new(null, [], false, false);

    readonly Dispatcher _dispatcher;
    readonly DiscordBridgeServer _bridge = new();
    readonly DiscordRpcClient _rpc = new();
    readonly HashSet<string> _speaking = [];

    VoiceState _bridgeVoice = NoCall;
    VoiceState _rpcVoice = NoCall;

    public DiscordService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        // Нативный RPC канал
        _rpc.Connected += user => Post(() => OnRpcLinked(user));
        _rpc.Disconnected += () => Post(OnRpcUnlinked);
        _rpc.SelfChanged += user => Post(() => { if (_rpc.IsConnected) Me = user; });
        _rpc.VoiceChanged += state => Post(() => OnRpcVoice(state));
        _rpc.SpeakingChanged += (id, on) => Post(() => { if (_rpc.IsConnected) OnSpeaking(id, on); });
        _rpc.MemberMoved += (member, joined) => Post(() => { if (_rpc.IsConnected) MemberMoved?.Invoke(member, joined); });

        // Мост BetterDiscord / Vesktop (для плагинов и сообщений)
        _bridge.Connected += hello => Post(() => OnBridgeLinked(hello));
        _bridge.Disconnected += () => Post(OnBridgeUnlinked);
        _bridge.SelfChanged += user => Post(() => { if (!_rpc.IsConnected) Me = user; });
        _bridge.VoiceChanged += state => Post(() => OnBridgeVoice(state));
        _bridge.SpeakingChanged += (id, on) => Post(() => { if (!_rpc.IsConnected) OnSpeaking(id, on); });
        _bridge.MessageReceived += message => Post(() => MessageReceived?.Invoke(message));
    }

    public bool Linked => _rpc.IsConnected || _bridge.IsConnected;

    public bool IsRpcConnected => _rpc.IsConnected;

    public bool IsBridgeConnected => _bridge.IsConnected;

    public string ConnectionMode => (_rpc.IsConnected && _bridge.IsConnected)
        ? "нативно + плагин"
        : _rpc.IsConnected
            ? "нативно (RPC)"
            : _bridge.IsConnected
                ? $"плагин v{PluginVersion}"
                : "отключён";

    public string PluginVersion { get; private set; } = "";

    public string DiscordVersion { get; private set; } = "";

    public bool Started { get; private set; }

    public DiscordUser? Me { get; private set; }

    public VoiceState Voice { get; private set; } = NoCall;

    public DateTime JoinedAt { get; private set; }

    public bool InCall => Voice.Channel is not null;

    public bool IsSpeaking(string userId) => _speaking.Contains(userId);

    /// <summary>Связь с Discord (RPC или плагин) установлена или потеряна.</summary>
    public event Action? LinkChanged;

    /// <summary>Изменился канал, состав участников или чьё-то состояние (микрофон, наушники, трансляция).</summary>
    public event Action? VoiceChanged;

    public event Action<string, bool>? SpeakingChanged;

    public event Action<VoiceMember, bool>? MemberMoved;

    public event Action<ChatMessage>? MessageReceived;

    public void Stop()
    {
        Started = false;
        try { _rpc.Stop(); } catch { }
        try { _bridge.Stop(); } catch { }
        _rpcVoice = NoCall;
        _bridgeVoice = NoCall;
        Voice = NoCall;
        _speaking.Clear();
        Me = null;
        LinkChanged?.Invoke();
        VoiceChanged?.Invoke();
    }

    public void Start()
    {
        try
        {
            _bridge.Start();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        try
        {
            _rpc.Start();
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

        Started = true;
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

    public void ToggleMute()
    {
        if (_rpc.IsConnected) _rpc.ToggleMute();
        if (_bridge.IsConnected) _bridge.ToggleMute();
    }

    public void ToggleDeafen()
    {
        if (_rpc.IsConnected) _rpc.ToggleDeafen();
        if (_bridge.IsConnected) _bridge.ToggleDeafen();
    }

    public void Leave()
    {
        if (_rpc.IsConnected) _rpc.Leave();
        if (_bridge.IsConnected) _bridge.Leave();
    }

    public void SetUserVolume(string userId, int percent)
    {
        int clamped = Math.Clamp(percent, 0, 200);
        if (_rpc.IsConnected) _rpc.SetUserVolume(userId, clamped);
        if (_bridge.IsConnected) _bridge.SetUserVolume(userId, clamped);
    }

    public void Refresh()
    {
        if (_rpc.IsConnected) _rpc.RequestSnapshot();
        if (_bridge.IsConnected) _bridge.RequestSnapshot();
    }

    /// <summary>URL иконки сервера для карточки Rich Presence (только нативный RPC знает её).</summary>
    public string? GuildIconUrl(string? guildId) => _rpc.GuildIconUrl(guildId);

    void Post(Action action) => _dispatcher.BeginInvoke(() =>
    {
        try { action(); }
        catch (Exception ex) { App.Log(ex); }
    });

    // ---------- обработка нативного RPC ----------

    void OnRpcLinked(DiscordUser user)
    {
        Me = user;
        DiscordVersion = "Native";
        LinkChanged?.Invoke();
    }

    void OnRpcUnlinked()
    {
        _rpcVoice = NoCall;
        if (!_bridge.IsConnected)
        {
            _speaking.Clear();
            bool wasInCall = InCall;
            Voice = NoCall;
            Me = null;
            LinkChanged?.Invoke();
            if (wasInCall) VoiceChanged?.Invoke();
        }
        else
        {
            ApplyEffectiveVoice();
            LinkChanged?.Invoke();
        }
    }

    void OnRpcVoice(VoiceState state)
    {
        _rpcVoice = state;
        ApplyEffectiveVoice();
    }

    // ---------- обработка плагина BetterDiscord ----------

    void OnBridgeLinked(HelloInfo hello)
    {
        PluginVersion = hello.Plugin;
        if (string.IsNullOrEmpty(DiscordVersion) || DiscordVersion == "Native")
        {
            DiscordVersion = hello.Discord ?? DiscordVersion;
        }
        LinkChanged?.Invoke();
    }

    void OnBridgeUnlinked()
    {
        _bridgeVoice = NoCall;
        PluginVersion = "";
        if (!_rpc.IsConnected)
        {
            _speaking.Clear();
            bool wasInCall = InCall;
            Voice = NoCall;
            Me = null;
            LinkChanged?.Invoke();
            if (wasInCall) VoiceChanged?.Invoke();
        }
        else
        {
            ApplyEffectiveVoice();
            LinkChanged?.Invoke();
        }
    }

    void OnBridgeVoice(VoiceState state)
    {
        _bridgeVoice = state;
        if (!_rpc.IsConnected || (_rpc.IsConnected && !_rpc.InCall && state.Channel is not null))
        {
            ApplyEffectiveVoice();
        }
    }

    void ApplyEffectiveVoice()
    {
        // Приоритет голоса: если нативный RPC в звонке, берём его; если нет, пробуем мост
        VoiceState target = (_rpc.IsConnected && _rpcVoice.Channel is not null)
            ? _rpcVoice
            : (_bridge.IsConnected && _bridgeVoice.Channel is not null)
                ? _bridgeVoice
                : (_rpc.IsConnected ? _rpcVoice : _bridgeVoice);

        VoiceState was = Voice;
        bool sameChannel = was.Channel is { } oldChannel && target.Channel is { } newChannel && oldChannel.Id == newChannel.Id;
        if (target.Channel is not null && !sameChannel) JoinedAt = DateTime.Now;

        _speaking.Clear();
        foreach (VoiceMember member in target.Members)
        {
            if (member.Speaking) _speaking.Add(member.Id);
        }
        Voice = target;

        if (sameChannel)
        {
            HashSet<string> before = [.. was.Members.Select(member => member.Id)];
            HashSet<string> now = [.. target.Members.Select(member => member.Id)];
            foreach (VoiceMember joined in target.Members.Where(member => !member.Self && !before.Contains(member.Id)))
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

    public void Dispose()
    {
        _bridge.Dispose();
        _rpc.Dispose();
    }
}
