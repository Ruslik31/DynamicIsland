using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Discord;

/// <summary>
/// Нативный клиент локального Discord RPC (IPC через Named Pipe \\.\pipe\discord-ipc-0..9).
/// Не требует BetterDiscord, Vencord или других модификаций клиента Discord.
/// Работает с официальным клиентом Discord (включая будущую версию на Rust / Project Phoenix).
/// Использует публичный идентификатор Streamkit Overlay для доступа к событиям голосового канала.
/// </summary>
public sealed class DiscordRpcClient : IDisposable
{
    private const string StreamkitClientId = "207646673902501888";
    private static readonly string[] RequiredScopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];
    private const string TokenBrokerUrl = "https://streamkit.discord.com/overlay/token";

    private static readonly VoiceState EmptyVoiceState = new(null, [], false, false);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _tokenFilePath;
    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = [];
    private readonly Dictionary<string, string> _guildNames = [];
    private readonly Dictionary<string, VoiceMember> _channelMembers = [];
    private readonly HashSet<string> _speakingUsers = [];

    private NamedPipeClientStream? _pipe;
    private bool _disposed;
    private bool _selfMute;
    private bool _selfDeaf;
    private string? _currentChannelId;
    private string? _currentChannelName;
    private string? _currentGuildId;
    private string? _currentGuildName;
    private int _currentChannelType = 2;

    public DiscordRpcClient(string? customTokenDir = null)
    {
        string dir = customTokenDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscordIsland");
        Directory.CreateDirectory(dir);
        _tokenFilePath = Path.Combine(dir, "rpc_token.json");
    }

    public bool IsConnected { get; private set; }
    public DiscordUser? Me { get; private set; }
    public VoiceState Voice { get; private set; } = EmptyVoiceState;
    public bool InCall => Voice.Channel is not null;

    public event Action<DiscordUser>? Connected;
    public event Action? Disconnected;
    public event Action<DiscordUser>? SelfChanged;
    public event Action<VoiceState>? VoiceChanged;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<VoiceMember, bool>? MemberMoved;
    public event Action<string>? RawMessageReceived;

    public void Start()
    {
        if (_disposed) return;
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        _ = Task.Run(WorkerLoopAsync);
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        CleanupSession();
    }

    // ---------- фоновый жизненный цикл соединения ----------

    private async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = await ConnectToAnyPipeAsync(_cts.Token).ConfigureAwait(false);
                if (pipe is null)
                {
                    await Task.Delay(2500, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                _pipe = pipe;
                await RunSessionAsync(pipe, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(ex);
            }
            finally
            {
                CleanupSession();
                if (pipe is not null)
                {
                    try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { /* пусто */ }
                }
            }

            if (!_cts.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectToAnyPipeAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            string pipeName = $"discord-ipc-{i}";
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(250);
                await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
            }
        }
        return null;
    }

    private async Task RunSessionAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        // Отправка рукопожатия (opcode 0 = HANDSHAKE)
        var handshake = new
        {
            v = 1,
            client_id = StreamkitClientId
        };
        await SendFrameAsync(0, JsonSerializer.Serialize(handshake), ct).ConfigureAwait(false);

        // Читаем входящие кадры в цикле
        await ReadLoopAsync(pipe, ct).ConfigureAwait(false);
    }

    private void CleanupSession()
    {
        IsConnected = false;
        _pipe = null;
        _channelMembers.Clear();
        _speakingUsers.Clear();
        _currentChannelId = null;
        _currentChannelName = null;
        _currentGuildId = null;
        _currentGuildName = null;
        Voice = EmptyVoiceState;

        lock (_pendingRequests)
        {
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }

        Disconnected?.Invoke();
    }

    // ---------- фрейминг Discord IPC (длина + opcode) ----------

    private async Task SendFrameAsync(int opcode, string json, CancellationToken ct)
    {
        NamedPipeClientStream? pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;

        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] buffer = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), payload.Length);
        payload.CopyTo(buffer.AsSpan(8));

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<JsonElement?> SendCommandAsync(string cmd, object? args = null, CancellationToken ct = default)
    {
        string nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingRequests)
        {
            _pendingRequests[nonce] = tcs;
        }

        var req = new JsonObject
        {
            ["cmd"] = cmd,
            ["nonce"] = nonce
        };

        if (args is not null)
        {
            req["args"] = JsonSerializer.SerializeToNode(args);
        }

        try
        {
            await SendFrameAsync(1, req.ToJsonString(), ct).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(nonce);
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        byte[] header = new byte[8];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            await ReadExactBytesAsync(pipe, header, ct).ConfigureAwait(false);
            int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

            if (length < 0 || length > 16_000_000)
            {
                throw new InvalidDataException($"Некорректная длина кадра IPC: {length}");
            }

            byte[] payload = new byte[length];
            await ReadExactBytesAsync(pipe, payload, ct).ConfigureAwait(false);
            string json = Encoding.UTF8.GetString(payload);

            _ = Task.Run(() => HandleIncomingJson(opcode, json), CancellationToken.None);
        }
    }

    private static async Task ReadExactBytesAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(readTotal, buffer.Length - readTotal), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Соединение с Discord IPC разорвано");
            readTotal += read;
        }
    }

    // ---------- обработка сообщений Discord ----------

    private void HandleIncomingJson(int opcode, string json)
    {
        try
        {
            RawMessageReceived?.Invoke(json);

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("nonce", out JsonElement nonceElem) && nonceElem.ValueKind == JsonValueKind.String)
            {
                string? nonce = nonceElem.GetString();
                if (!string.IsNullOrEmpty(nonce))
                {
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_pendingRequests)
                    {
                        _pendingRequests.TryGetValue(nonce, out tcs);
                    }
                    if (tcs is not null)
                    {
                        tcs.TrySetResult(root.Clone());
                    }
                }
            }

            string? cmd = root.TryGetProperty("cmd", out JsonElement cElem) ? cElem.GetString() : null;
            string? evt = root.TryGetProperty("evt", out JsonElement eElem) ? eElem.GetString() : null;

            if (evt == "READY")
            {
                HandleReadyEvent(root);
                return;
            }

            if (cmd == "DISPATCH")
            {
                HandleDispatchEvent(evt, root);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private void HandleReadyEvent(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("user", out JsonElement userElem))
        {
            string id = userElem.GetProperty("id").GetString() ?? "";
            string name = userElem.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
                ? gn.GetString()!
                : userElem.GetProperty("username").GetString() ?? "Discord";
            string? avatarHash = userElem.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
            Me = new DiscordUser(id, name, AvatarUrl(id, avatarHash));
            SelfChanged?.Invoke(Me);
        }

        _ = Task.Run(AuthenticateAndInitAsync);
    }

    private async Task AuthenticateAndInitAsync()
    {
        try
        {
            string? token = LoadCachedToken();
            bool authenticated = false;

            if (!string.IsNullOrEmpty(token))
            {
                authenticated = await TryAuthenticateWithTokenAsync(token).ConfigureAwait(false);
            }

            if (!authenticated)
            {
                var authResp = await SendCommandAsync("AUTHORIZE", new
                {
                    client_id = StreamkitClientId,
                    scopes = RequiredScopes
                }).ConfigureAwait(false);

                if (authResp is { } resp && resp.TryGetProperty("data", out var authData) && authData.TryGetProperty("code", out var codeElem))
                {
                    string? code = codeElem.GetString();
                    if (!string.IsNullOrEmpty(code))
                    {
                        token = await ExchangeCodeForTokenAsync(code).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(token))
                        {
                            SaveCachedToken(token);
                            authenticated = await TryAuthenticateWithTokenAsync(token).ConfigureAwait(false);
                        }
                    }
                }
            }

            if (authenticated)
            {
                IsConnected = true;
                if (Me is not null) Connected?.Invoke(Me);

                await SubscribeEventAsync("VOICE_CHANNEL_SELECT").ConfigureAwait(false);
                await SubscribeEventAsync("VOICE_SETTINGS_UPDATE").ConfigureAwait(false);

                await QueryVoiceSettingsAsync().ConfigureAwait(false);
                await QueryCurrentVoiceChannelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private async Task<bool> TryAuthenticateWithTokenAsync(string token)
    {
        var resp = await SendCommandAsync("AUTHENTICATE", new { access_token = token }).ConfigureAwait(false);
        if (resp is { } r && r.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var userElem))
        {
            string id = userElem.GetProperty("id").GetString() ?? "";
            string name = userElem.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
                ? gn.GetString()!
                : userElem.GetProperty("username").GetString() ?? "Discord";
            string? avatarHash = userElem.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
            Me = new DiscordUser(id, name, AvatarUrl(id, avatarHash));
            SelfChanged?.Invoke(Me);
            return true;
        }
        return false;
    }

    private static async Task<string?> ExchangeCodeForTokenAsync(string code)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(TokenBrokerUrl, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var tok) ? tok.GetString() : null;
        }
        catch (Exception ex)
        {
            Log(ex);
            return null;
        }
    }

    private string? LoadCachedToken()
    {
        try
        {
            if (!File.Exists(_tokenFilePath)) return null;
            string json = File.ReadAllText(_tokenFilePath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveCachedToken(string token)
    {
        try
        {
            string json = JsonSerializer.Serialize(new { token, savedAt = DateTime.UtcNow });
            File.WriteAllText(_tokenFilePath, json, Encoding.UTF8);
        }
        catch { /* пусто */ }
    }

    private async Task SubscribeEventAsync(string eventName, object? args = null)
    {
        var req = new JsonObject
        {
            ["cmd"] = "SUBSCRIBE",
            ["evt"] = eventName,
            ["nonce"] = Guid.NewGuid().ToString("N")
        };
        if (args is not null) req["args"] = JsonSerializer.SerializeToNode(args);
        await SendFrameAsync(1, req.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task UnsubscribeEventAsync(string eventName, object? args = null)
    {
        var req = new JsonObject
        {
            ["cmd"] = "UNSUBSCRIBE",
            ["evt"] = eventName,
            ["nonce"] = Guid.NewGuid().ToString("N")
        };
        if (args is not null) req["args"] = JsonSerializer.SerializeToNode(args);
        await SendFrameAsync(1, req.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
    }

    // ---------- обработка событий DISPATCH ----------

    private void HandleDispatchEvent(string? evt, JsonElement root)
    {
        if (!root.TryGetProperty("data", out JsonElement data)) return;

        switch (evt)
        {
            case "VOICE_SETTINGS_UPDATE":
                UpdateVoiceSettings(data);
                break;

            case "VOICE_CHANNEL_SELECT":
                HandleVoiceChannelSelect(data);
                break;

            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                HandleVoiceStateUpsert(data);
                break;

            case "VOICE_STATE_DELETE":
                HandleVoiceStateDelete(data);
                break;

            case "SPEAKING_START":
                if (data.TryGetProperty("user_id", out var suId))
                {
                    string id = suId.ValueKind == JsonValueKind.String ? (suId.GetString() ?? "") : suId.ToString();
                    if (!string.IsNullOrEmpty(id) && _speakingUsers.Add(id))
                    {
                        SpeakingChanged?.Invoke(id, true);
                    }
                }
                break;

            case "SPEAKING_STOP":
                if (data.TryGetProperty("user_id", out var euId))
                {
                    string id = euId.ValueKind == JsonValueKind.String ? (euId.GetString() ?? "") : euId.ToString();
                    if (!string.IsNullOrEmpty(id) && _speakingUsers.Remove(id))
                    {
                        SpeakingChanged?.Invoke(id, false);
                    }
                }
                break;
        }
    }

    private void UpdateVoiceSettings(JsonElement data)
    {
        if (data.TryGetProperty("mute", out var m)) _selfMute = m.GetBoolean();
        if (data.TryGetProperty("deaf", out var d)) _selfDeaf = d.GetBoolean();
        BroadcastVoiceState();
    }

    private async Task QueryVoiceSettingsAsync()
    {
        var resp = await SendCommandAsync("GET_VOICE_SETTINGS").ConfigureAwait(false);
        if (resp is { } r && r.TryGetProperty("data", out var data))
        {
            UpdateVoiceSettings(data);
        }
    }

    private void HandleVoiceChannelSelect(JsonElement data)
    {
        string? channelId = data.TryGetProperty("channel_id", out var ch) && ch.ValueKind == JsonValueKind.String
            ? ch.GetString()
            : null;

        string? guildId = data.TryGetProperty("guild_id", out var g) && g.ValueKind == JsonValueKind.String
            ? g.GetString()
            : null;

        if (channelId == _currentChannelId && !string.IsNullOrEmpty(channelId)) return;

        string? oldChannelId = _currentChannelId;

        if (string.IsNullOrEmpty(channelId))
        {
            _currentChannelId = null;
            _currentChannelName = null;
            _currentGuildId = null;
            _currentGuildName = null;
            _channelMembers.Clear();
            _speakingUsers.Clear();

            if (!string.IsNullOrEmpty(oldChannelId))
            {
                _ = UnsubscribeChannelEventsAsync(oldChannelId);
            }

            BroadcastVoiceState();
            return;
        }

        if (!string.IsNullOrEmpty(oldChannelId) && oldChannelId != channelId)
        {
            _ = UnsubscribeChannelEventsAsync(oldChannelId);
        }

        _currentChannelId = channelId;
        _currentGuildId = guildId;
        _ = FetchChannelDetailsAsync(channelId, guildId);
    }

    private async Task QueryCurrentVoiceChannelAsync()
    {
        var resp = await SendCommandAsync("GET_SELECTED_VOICE_CHANNEL").ConfigureAwait(false);
        if (resp is { } r && r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            ParseChannelSnapshot(data);
            if (!string.IsNullOrEmpty(_currentChannelId))
            {
                if (!string.IsNullOrEmpty(_currentGuildId))
                {
                    await EnsureGuildNameAsync(_currentGuildId).ConfigureAwait(false);
                }
                await SubscribeChannelEventsAsync(_currentChannelId).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchChannelDetailsAsync(string channelId, string? guildId)
    {
        if (!string.IsNullOrEmpty(guildId))
        {
            await EnsureGuildNameAsync(guildId).ConfigureAwait(false);
        }

        var resp = await SendCommandAsync("GET_SELECTED_VOICE_CHANNEL").ConfigureAwait(false);
        if (resp is { } r && r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            ParseChannelSnapshot(data);
        }

        await SubscribeChannelEventsAsync(channelId).ConfigureAwait(false);
    }

    private async Task EnsureGuildNameAsync(string guildId)
    {
        if (_guildNames.ContainsKey(guildId))
        {
            _currentGuildName = _guildNames[guildId];
            return;
        }

        var resp = await SendCommandAsync("GET_GUILD", new { guild_id = guildId }).ConfigureAwait(false);
        if (resp is { } r && r.TryGetProperty("data", out var data) && data.TryGetProperty("name", out var nameElem))
        {
            string gName = nameElem.GetString() ?? "";
            _guildNames[guildId] = gName;
            _currentGuildName = gName;
        }
    }

    private async Task SubscribeChannelEventsAsync(string channelId)
    {
        var args = new { channel_id = channelId };
        await SubscribeEventAsync("VOICE_STATE_CREATE", args).ConfigureAwait(false);
        await SubscribeEventAsync("VOICE_STATE_UPDATE", args).ConfigureAwait(false);
        await SubscribeEventAsync("VOICE_STATE_DELETE", args).ConfigureAwait(false);
        await SubscribeEventAsync("SPEAKING_START", args).ConfigureAwait(false);
        await SubscribeEventAsync("SPEAKING_STOP", args).ConfigureAwait(false);
    }

    private async Task UnsubscribeChannelEventsAsync(string channelId)
    {
        var args = new { channel_id = channelId };
        await UnsubscribeEventAsync("VOICE_STATE_CREATE", args).ConfigureAwait(false);
        await UnsubscribeEventAsync("VOICE_STATE_UPDATE", args).ConfigureAwait(false);
        await UnsubscribeEventAsync("VOICE_STATE_DELETE", args).ConfigureAwait(false);
        await UnsubscribeEventAsync("SPEAKING_START", args).ConfigureAwait(false);
        await UnsubscribeEventAsync("SPEAKING_STOP", args).ConfigureAwait(false);
    }

    private void ParseChannelSnapshot(JsonElement data)
    {
        _currentChannelId = data.GetProperty("id").GetString();
        _currentChannelName = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        _currentChannelType = data.TryGetProperty("type", out var t) ? t.GetInt32() : 2;

        if (data.TryGetProperty("guild_id", out var g) && g.ValueKind == JsonValueKind.String)
        {
            _currentGuildId = g.GetString();
            if (!string.IsNullOrEmpty(_currentGuildId) && _guildNames.TryGetValue(_currentGuildId, out var gn))
            {
                _currentGuildName = gn;
            }
        }

        _channelMembers.Clear();
        if (data.TryGetProperty("voice_states", out var vsList) && vsList.ValueKind == JsonValueKind.Array)
        {
            foreach (var vs in vsList.EnumerateArray())
            {
                var member = ParseVoiceMember(vs);
                if (member is not null)
                {
                    _channelMembers[member.Id] = member;
                }
            }
        }

        BroadcastVoiceState();
    }

    private void HandleVoiceStateUpsert(JsonElement data)
    {
        var member = ParseVoiceMember(data);
        if (member is null) return;

        bool isNew = !_channelMembers.ContainsKey(member.Id);
        _channelMembers[member.Id] = member;
        BroadcastVoiceState();

        if (isNew && !member.Self)
        {
            MemberMoved?.Invoke(member, true);
        }
    }

    private void HandleVoiceStateDelete(JsonElement data)
    {
        string? userId = null;
        if (data.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var uid))
        {
            userId = uid.GetString();
        }
        else if (data.TryGetProperty("user_id", out var uid2))
        {
            userId = uid2.GetString();
        }

        if (!string.IsNullOrEmpty(userId) && _channelMembers.Remove(userId, out var removed))
        {
            _speakingUsers.Remove(userId);
            BroadcastVoiceState();
            if (!removed.Self)
            {
                MemberMoved?.Invoke(removed, false);
            }
        }
    }

    private VoiceMember? ParseVoiceMember(JsonElement el)
    {
        if (!el.TryGetProperty("user", out var userElem)) return null;

        string id = userElem.GetProperty("id").GetString() ?? "";
        string? avatarHash = userElem.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
        string? nick = el.TryGetProperty("nick", out var nk) && nk.ValueKind == JsonValueKind.String ? nk.GetString() : null;
        string username = userElem.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
            ? gn.GetString()!
            : userElem.GetProperty("username").GetString() ?? "user";

        string displayName = !string.IsNullOrEmpty(nick) ? nick : username;
        bool isSelf = Me is not null && Me.Id == id;

        JsonElement vs = el.TryGetProperty("voice_state", out var vsObj) ? vsObj : el;

        bool selfMute = vs.TryGetProperty("self_mute", out var sm) && sm.GetBoolean();
        bool selfDeaf = vs.TryGetProperty("self_deaf", out var sd) && sd.GetBoolean();
        bool mute = vs.TryGetProperty("mute", out var m) && m.GetBoolean();
        bool deaf = vs.TryGetProperty("deaf", out var d) && d.GetBoolean();
        bool stream = vs.TryGetProperty("self_stream", out var st) && st.GetBoolean();
        bool video = vs.TryGetProperty("self_video", out var sv) && sv.GetBoolean();

        double? volume = el.TryGetProperty("volume", out var vol) && vol.ValueKind == JsonValueKind.Number
            ? vol.GetDouble()
            : null;

        bool localMute = el.TryGetProperty("mute", out var lm) && lm.GetBoolean();
        bool isSpeaking = _speakingUsers.Contains(id);

        if (isSelf)
        {
            _selfMute = selfMute;
            _selfDeaf = selfDeaf;
        }

        return new VoiceMember(
            id,
            displayName,
            AvatarUrl(id, avatarHash),
            isSelf,
            selfMute,
            selfDeaf,
            mute,
            deaf,
            stream,
            video,
            isSpeaking,
            volume,
            localMute
        );
    }

    private void BroadcastVoiceState()
    {
        VoiceChannel? ch = null;
        if (!string.IsNullOrEmpty(_currentChannelId))
        {
            ch = new VoiceChannel(
                _currentChannelId,
                _currentChannelName ?? "",
                _currentGuildId,
                _currentGuildName,
                _currentChannelType
            );
        }

        var members = _channelMembers.Values.OrderByDescending(m => m.Self).ToList();
        Voice = new VoiceState(ch, members, _selfMute, _selfDeaf);
        VoiceChanged?.Invoke(Voice);
    }

    private static string AvatarUrl(string userId, string? hash)
    {
        if (!string.IsNullOrEmpty(hash))
        {
            return $"https://cdn.discordapp.com/avatars/{userId}/{hash}.png?size=128";
        }
        int index = 0;
        if (ulong.TryParse(userId, out ulong num))
        {
            index = (int)((num >> 22) % 6);
        }
        return $"https://cdn.discordapp.com/embed/avatars/{index}.png";
    }

    // ---------- команды управления голосом ----------

    public bool ToggleMute()
    {
        return SetMute(!_selfMute);
    }

    public bool ToggleDeafen()
    {
        return SetDeafen(!_selfDeaf);
    }

    public bool SetMute(bool value)
    {
        _ = SendCommandAsync("SET_VOICE_SETTINGS", new { mute = value });
        return true;
    }

    public bool SetDeafen(bool value)
    {
        _ = SendCommandAsync("SET_VOICE_SETTINGS", new { deaf = value });
        return true;
    }

    public bool Leave()
    {
        _ = SendCommandAsync("SELECT_VOICE_CHANNEL", new { channel_id = (string?)null });
        return true;
    }

    public bool SetUserVolume(string userId, int percent)
    {
        _ = SendCommandAsync("SET_USER_VOICE_SETTINGS", new
        {
            user_id = userId,
            volume = Math.Clamp(percent, 0, 200)
        });
        return true;
    }

    public bool RequestSnapshot()
    {
        _ = QueryVoiceSettingsAsync();
        _ = QueryCurrentVoiceChannelAsync();
        return true;
    }

    private static void Log(Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "DiscordIsland.log");
            File.AppendAllText(path, $"[{DateTime.UtcNow:O}] [RPC] {ex}\n");
        }
        catch { /* пусто */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _sendLock.Dispose();
        _pipe?.Dispose();
    }
}
