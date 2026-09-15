using System.Net;
using System.Net.Sockets;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Transport;

/// <summary>
/// Accepts the three channel connections the phone dials out over the ADB reverse tunnel.
///
/// Socket roles under <c>adb reverse</c> are the opposite of the intuitive reading: adbd
/// on the device listens on the abstract socket, the phone app connects to it, and this
/// process is the TCP listener on loopback. See docs/PROTOCOL.md §1.
/// </summary>
public sealed class BridgeListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<byte, BridgeChannel> _channels = new();
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int Port { get; }

    /// <summary>Raised once a channel completes its handshake. Never on the UI thread.</summary>
    public event Action<BridgeChannel>? ChannelOpened;
    public event Action<byte, string>? ChannelClosed;
    public event Action<string>? Log;

    public BridgeListener(int port = 0)
    {
        // Loopback only. Binding 0.0.0.0 would expose the bridge to the LAN, which would
        // defeat the wired-only trust model the whole design rests on.
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log?.Invoke($"accept failed: {ex.Message}");
                return;
            }

            // Handshake off the accept loop so one slow or hung peer cannot stall the
            // other two channels from attaching.
            _ = Task.Run(() => HandshakeAsync(client, ct), ct);
        }
    }

    private async Task HandshakeAsync(TcpClient client, CancellationToken ct)
    {
        // Nagle would coalesce our small control writes into 40ms-delayed batches, which
        // is a large fraction of the end-to-end latency budget.
        client.NoDelay = true;
        var stream = client.GetStream();

        try
        {
            var buffer = new byte[Wire.HandshakeLength];

            // A peer that connects and then says nothing must not hold a slot forever.
            // This also covers the adb-forward fallback, where the TCP connect succeeds
            // even when no app is listening on the device.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await ReadExactlyAsync(stream, buffer, timeout.Token).ConfigureAwait(false);

            if (!Wire.TryParseHandshake(buffer, out var channelId, out var status))
            {
                await stream.WriteAsync(Wire.BuildReply(status), ct).ConfigureAwait(false);
                Log?.Invoke($"handshake rejected (status {status})");
                client.Dispose();
                return;
            }

            await _channelLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_channels.TryGetValue(channelId, out var existing))
                {
                    // A reconnect after an unclean drop: the old half-open channel is
                    // stale by definition, so the new connection wins.
                    Log?.Invoke($"channel {channelId} reconnected, dropping stale connection");
                    _channels.Remove(channelId);
                    existing.Close("superseded by a new connection");
                }

                var channel = new BridgeChannel(channelId, client, stream);
                channel.Closed += reason =>
                {
                    _ = RemoveChannelAsync(channelId);
                    ChannelClosed?.Invoke(channelId, reason);
                };
                _channels[channelId] = channel;

                await stream.WriteAsync(Wire.BuildReply(Wire.StatusOk), ct).ConfigureAwait(false);
                Log?.Invoke($"channel {channelId} established");
                ChannelOpened?.Invoke(channel);
            }
            finally
            {
                _channelLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke("handshake timed out - peer connected but sent nothing");
            client.Dispose();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"handshake failed: {ex.Message}");
            client.Dispose();
        }
    }

    private async Task RemoveChannelAsync(byte channelId)
    {
        await _channelLock.WaitAsync().ConfigureAwait(false);
        try { _channels.Remove(channelId); }
        finally { _channelLock.Release(); }
    }

    public static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
        => await ReadExactlyAsync(stream, buffer, 0, buffer.Length, ct).ConfigureAwait(false);

    public static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException($"stream closed after {read} of {count} bytes");
            read += n;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        await _channelLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var c in _channels.Values) c.Dispose();
            _channels.Clear();
        }
        finally { _channelLock.Release(); }

        _cts?.Dispose();
        _channelLock.Dispose();
    }
}

/// <summary>One established, handshaken channel.</summary>
public sealed class BridgeChannel : IDisposable
{
    private readonly TcpClient _client;
    private int _disposed;

    public byte ChannelId { get; }
    public NetworkStream Stream { get; }

    public event Action<string>? Closed;

    internal BridgeChannel(byte channelId, TcpClient client, NetworkStream stream)
    {
        ChannelId = channelId;
        _client = client;
        Stream = stream;
    }

    /// <summary>
    /// Tears the channel down and notifies once. Callers that detect the peer vanishing
    /// (a read throwing, the control heartbeat expiring) call this rather than Dispose,
    /// so the session learns WHY it went.
    /// </summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CloseSockets();
        Closed?.Invoke(reason);
    }

    public void Dispose() => Close("disposed");

    private void CloseSockets()
    {
        try { Stream.Dispose(); } catch { /* already gone */ }
        try { _client.Dispose(); } catch { /* already gone */ }
    }
}
