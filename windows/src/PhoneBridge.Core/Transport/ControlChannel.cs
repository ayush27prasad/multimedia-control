using System.Text;
using System.Text.Json;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Transport;

/// <summary>
/// NDJSON control channel: one JSON object per line, bidirectional.
/// The desktop is the authority for the wifi slice; the phone for the device slice.
/// </summary>
public sealed class ControlChannel : IAsyncDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private const int MissedPingsBeforeDead = 3;

    private readonly BridgeChannel _channel;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private long _seq;
    private int _outstandingPings;
    private Task? _readLoop;
    private Task? _heartbeat;

    public event Func<Envelope, Task>? MessageReceived;
    public event Action<string>? Dead;
    public event Action<string>? Log;

    public ControlChannel(BridgeChannel channel)
    {
        _channel = channel;
        _writer = new StreamWriter(channel.Stream, new UTF8Encoding(false), 8192, leaveOpen: true)
        {
            AutoFlush = false   // flushed explicitly after each line
        };
    }

    public void Start()
    {
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _heartbeat = Task.Run(() => HeartbeatAsync(_cts.Token));
    }

    public long NextSeq() => Interlocked.Increment(ref _seq);

    // ---- outbound ----------------------------------------------------------

    public async Task SendAsync(string type, object payload, long? seq = null)
    {
        var envelope = new
        {
            t = type,
            seq = seq ?? NextSeq(),
            origin = Origins.Pc,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            p = payload
        };

        var line = JsonSerializer.Serialize(envelope, BridgeJson.Options);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(line).ConfigureAwait(false);
            await _writer.WriteAsync('\n').ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseDead($"write failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendStateAsync(StatePayload payload) => SendAsync(MessageTypes.State, payload);

    public Task SendHelloAsync(HelloPayload payload) => SendAsync(MessageTypes.Hello, payload);

    /// <returns>The seq used, so the caller can correlate the ack.</returns>
    public async Task<long> SendCommandAsync(string cmd, object? args = null)
    {
        var seq = NextSeq();
        await SendAsync(MessageTypes.Cmd, new { cmd, args = args ?? new { } }, seq).ConfigureAwait(false);
        return seq;
    }

    public Task SendAckAsync(long ackSeq, bool ok, string? error = null) =>
        SendAsync(MessageTypes.Ack, new AckPayload { AckSeq = ackSeq, Ok = ok, Error = error });

    // ---- inbound -----------------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(_channel.Stream, Encoding.UTF8, false, 16384, leaveOpen: true);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    RaiseDead("phone closed the control channel");
                    return;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Length > Wire.MaxControlLine)
                {
                    // NDJSON has no resync marker, so an oversized line means the stream
                    // is desynchronised and cannot be recovered in place.
                    RaiseDead($"control line exceeded {Wire.MaxControlLine} bytes - stream desynchronised");
                    return;
                }

                Interlocked.Exchange(ref _outstandingPings, 0);

                Envelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<Envelope>(line, BridgeJson.Options);
                }
                catch (JsonException ex)
                {
                    // One malformed message is not fatal; a peer bug should not take the
                    // link down.
                    Log?.Invoke($"undecodable control message, skipping: {ex.Message}");
                    continue;
                }
                if (envelope is null) continue;

                if (envelope.Type == MessageTypes.Ping)
                {
                    var nonce = envelope.Payload.ValueKind == JsonValueKind.Object &&
                                envelope.Payload.TryGetProperty("nonce", out var n)
                        ? n.GetString() ?? ""
                        : "";
                    await SendAsync(MessageTypes.Pong, new PingPayload { Nonce = nonce }).ConfigureAwait(false);
                    continue;
                }
                if (envelope.Type == MessageTypes.Pong) continue;

                var handler = MessageReceived;
                if (handler is not null) await handler(envelope).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            RaiseDead($"control read failed: {ex.Message}");
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);

                if (Volatile.Read(ref _outstandingPings) >= MissedPingsBeforeDead)
                {
                    RaiseDead($"no response to {MissedPingsBeforeDead} pings");
                    return;
                }
                Interlocked.Increment(ref _outstandingPings);
                await SendAsync(MessageTypes.Ping,
                    new PingPayload { Nonce = Guid.NewGuid().ToString("N")[..8] }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private int _deadRaised;
    private void RaiseDead(string reason)
    {
        if (Interlocked.Exchange(ref _deadRaised, 1) != 0) return;
        Dead?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var t in new[] { _readLoop, _heartbeat })
        {
            if (t is null) continue;
            try { await t.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        try { _writer.Dispose(); } catch { /* already gone */ }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
