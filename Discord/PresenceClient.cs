using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicIsland.Discord;

/// <summary>
/// Лёгкий клиент Discord Rich Presence: отдельное IPC-подключение (\\.\pipe\discord-ipc-N)
/// со своим client_id только ради команды SET_ACTIVITY — без авторизации, как это делают игры.
/// Каждый client_id даёт свою карточку в профиле, поэтому музыка и звонок живут на двух подключениях.
/// </summary>
sealed class PresenceClient : IDisposable
{
    readonly string _clientId;
    readonly string _tag;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    CancellationTokenSource _cts = new();
    NamedPipeClientStream? _pipe;
    string? _lastPayload;
    bool _disposed;

    public PresenceClient(string clientId, string tag)
    {
        _clientId = clientId;
        _tag = tag;
    }

    /// <summary>ID вписан и похож на настоящий (не заглушка).</summary>
    public bool Configured => ulong.TryParse(_clientId, out ulong id) && id > 1000;

    public void Start()
    {
        if (!Configured || _disposed) return;
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        _ = Task.Run(WorkerLoopAsync);
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    /// <summary>Отправить карточку (null — убрать). Повторяется автоматически после переподключения.</summary>
    public void SetActivity(JsonObject? activity)
    {
        if (!Configured) return;
        var request = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = activity,
            },
            ["nonce"] = Guid.NewGuid().ToString("N"),
        };
        string payload = request.ToJsonString();
        _lastPayload = payload;
        _ = Task.Run(async () =>
        {
            try { await SendFrameAsync(1, payload, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log(ex); }
        });
    }

    async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = await ConnectAsync(_cts.Token).ConfigureAwait(false);
                if (pipe is null)
                {
                    await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                _pipe = pipe;
                await SendFrameAsync(0, $"{{\"v\":1,\"client_id\":\"{_clientId}\"}}", _cts.Token).ConfigureAwait(false);
                if (_lastPayload != null)
                    await SendFrameAsync(1, _lastPayload, _cts.Token).ConfigureAwait(false);
                await ReadLoopAsync(pipe, _cts.Token).ConfigureAwait(false);
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
                _pipe = null;
                if (pipe is not null)
                {
                    try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }

            if (!_cts.IsCancellationRequested && !_disposed)
                await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
        }
    }

    static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(250);
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
            }
        }
        return null;
    }

    async Task SendFrameAsync(int opcode, string json, CancellationToken ct)
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

    static async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        byte[] header = new byte[8];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            await ReadExactAsync(pipe, header, ct).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (length < 0 || length > 4_000_000)
                throw new InvalidDataException($"Некорректная длина кадра IPC: {length}");
            byte[] payload = new byte[length];
            await ReadExactAsync(pipe, payload, ct).ConfigureAwait(false);
        }
    }

    static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Соединение с Discord IPC разорвано");
            total += read;
        }
    }

    void Log(Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "DiscordIsland.log");
            File.AppendAllText(path, $"[{DateTime.UtcNow:O}] [Presence:{_tag}] {ex}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
