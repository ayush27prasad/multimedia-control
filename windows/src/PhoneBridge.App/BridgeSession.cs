using System.Text.Json;
using PhoneBridge.Adb;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Core.Transport;
using PhoneBridge.Media.Audio;
using PhoneBridge.Media.Video;
using PhoneBridge.Media.VirtualCam;

namespace PhoneBridge.App;

public enum SessionStatus { NoDevice, Unauthorized, Connecting, Connected, Error }

/// <summary>
/// Desktop counterpart to the phone's BridgeService. Owns the ADB link, the listener, and
/// the media sinks.
///
/// Authority split (docs/PROTOCOL.md §4.2): this side authors the wifi slice and only
/// mirrors the device slice. It never applies an optimistic local change to either - it
/// sends a command and renders the authoritative echo, which is what makes the two UIs
/// structurally unable to disagree.
/// </summary>
public sealed class BridgeSession : IAsyncDisposable
{
    private static readonly TimeSpan WifiPollInterval = TimeSpan.FromSeconds(3);

    private readonly AdbClient _adb;
    private readonly DeviceWatcher _watcher;
    private readonly WifiController _wifi;
    private readonly AdbTunnel _tunnel;
    private readonly VirtualMicWriter _mic = new();
    private readonly SharedMemoryFrameWriter _vcamFrames = new();
    private readonly VirtualCameraHost _vcam = new();

    private BridgeListener? _listener;
    private ControlChannel? _control;
    private IVideoDecoder? _decoder;
    private CancellationTokenSource? _sessionCts;
    private Task? _wifiPollTask;

    private int _vcamWidth, _vcamHeight;

    // ---- mic level meter -----------------------------------------------------
    // Independent of VirtualMicWriter/VB-Cable: this reflects what arrived over the wire
    // from the phone, so it can prove "the PC is receiving audio" even when VB-Cable is
    // not installed and nothing downstream (browser, Zoom) can pick the mic up yet.
    private const int MicLevelRingSize = 256;
    private readonly float[] _micLevelRing = new float[MicLevelRingSize];
    private readonly object _micLevelLock = new();
    private int _micLevelWritePos;

    public SessionStatus Status { get; private set; } = SessionStatus.NoDevice;
    public string? StatusDetail { get; private set; }
    public DeviceState Device { get; private set; } = new();
    public WifiState Wifi { get; private set; } = new();
    public ObservedWifi Observed { get; private set; } = new();
    public AdbDevice? AttachedDevice => _watcher.Current.Device;

    /// <summary>Latest decoded frame, for the preview. Buffer is reused - copy to retain.</summary>
    public event Action<DecodedFrame>? FrameDecoded;
    public event Action? StateChanged;
    public event Action<string>? Log;

    /// <summary>Set of commands sent and not yet acked, driving the pending affordance.</summary>
    public IReadOnlySet<string> Pending => _pending;
    private readonly HashSet<string> _pending = new();

    public BridgeSession(string adbPath)
    {
        _adb = new AdbClient(adbPath);
        _adb.Log += m => Log?.Invoke(m);

        _watcher = new DeviceWatcher(_adb);
        _watcher.Log += m => Log?.Invoke(m);
        _watcher.Changed += OnDeviceChanged;

        _wifi = new WifiController(_adb);
        _wifi.Log += m => Log?.Invoke(m);

        _tunnel = new AdbTunnel(_adb);
        _tunnel.Log += m => Log?.Invoke(m);

        _mic.Log += m => Log?.Invoke(m);
        _vcamFrames.Log += m => Log?.Invoke(m);
        _vcam.Log += m => Log?.Invoke(m);
    }

    public async Task StartAsync()
    {
        await _adb.StartServerAsync().ConfigureAwait(false);
        _watcher.Start();
    }

    // ---- device lifecycle --------------------------------------------------

    private void OnDeviceChanged(DeviceSnapshot snapshot)
    {
        _ = Task.Run(async () =>
        {
            await TeardownSessionAsync().ConfigureAwait(false);

            switch (snapshot.Status)
            {
                case DeviceStatus.Ready:
                    await EstablishSessionAsync().ConfigureAwait(false);
                    break;
                case DeviceStatus.Unauthorized:
                    SetStatus(SessionStatus.Unauthorized, snapshot.Detail);
                    break;
                default:
                    SetStatus(SessionStatus.NoDevice, snapshot.Detail);
                    break;
            }
        });
    }

    private async Task EstablishSessionAsync()
    {
        SetStatus(SessionStatus.Connecting, "Setting up the USB tunnel...");
        _sessionCts = new CancellationTokenSource();
        var ct = _sessionCts.Token;

        try
        {
            _listener = new BridgeListener();
            _listener.Log += m => Log?.Invoke(m);
            _listener.ChannelOpened += OnChannelOpened;
            _listener.ChannelClosed += OnChannelClosed;
            _listener.Start();

            var error = await _tunnel.EstablishAsync(_listener.Port, ct).ConfigureAwait(false);
            if (error is not null)
            {
                SetStatus(SessionStatus.Error, error);
                return;
            }

            // Under reverse the phone is the dialling side, so it must be running for
            // anything to happen. Saves the user picking the phone up.
            await _tunnel.LaunchPhoneAppAsync(ct).ConfigureAwait(false);

            SetStatus(SessionStatus.Connecting, "Waiting for the phone app...");

            // Wi-Fi polling is independent of the control channel: this data comes from
            // ADB, so it is available and useful even before the phone app connects.
            _wifiPollTask = Task.Run(() => WifiPollLoopAsync(ct), ct);
        }
        catch (Exception ex)
        {
            SetStatus(SessionStatus.Error, ex.Message);
        }
    }

    private async Task TeardownSessionAsync()
    {
        _sessionCts?.Cancel();

        if (_wifiPollTask is not null)
        {
            try { await _wifiPollTask.ConfigureAwait(false); } catch { /* shutdown */ }
            _wifiPollTask = null;
        }

        if (_control is not null) { await _control.DisposeAsync().ConfigureAwait(false); _control = null; }
        if (_listener is not null) { await _listener.DisposeAsync().ConfigureAwait(false); _listener = null; }

        _decoder?.Dispose();
        _decoder = null;
        _mic.Stop();
        ClearMicLevels();

        await _tunnel.RemoveAllAsync().ConfigureAwait(false);

        // Clear mirrored state rather than leaving it stale: showing a network list that
        // no longer reflects the phone is worse than showing nothing.
        Device = new DeviceState();
        Wifi = new WifiState();
        Observed = new ObservedWifi();
        _pending.Clear();

        _sessionCts?.Dispose();
        _sessionCts = null;
        StateChanged?.Invoke();
    }

    // ---- channels ----------------------------------------------------------

    private void OnChannelOpened(BridgeChannel channel)
    {
        switch (channel.ChannelId)
        {
            case Wire.ChannelControl: _ = Task.Run(() => StartControlAsync(channel)); break;
            case Wire.ChannelVideo:   _ = Task.Run(() => VideoLoopAsync(channel, _sessionCts?.Token ?? default)); break;
            case Wire.ChannelAudio:   _ = Task.Run(() => AudioLoopAsync(channel, _sessionCts?.Token ?? default)); break;
        }
    }

    private void OnChannelClosed(byte channelId, string reason)
    {
        Log?.Invoke($"channel {channelId} closed: {reason}");
        if (channelId == Wire.ChannelControl)
        {
            SetStatus(SessionStatus.Connecting, "Phone disconnected, waiting for it to return...");
        }
    }

    private async Task StartControlAsync(BridgeChannel channel)
    {
        var control = new ControlChannel(channel);
        control.Log += m => Log?.Invoke(m);
        control.MessageReceived += OnControlMessageAsync;
        control.Dead += reason =>
        {
            Log?.Invoke($"control channel dead: {reason}");
            // Close the socket too, or the listener keeps a half-open channel in its map
            // and rejects the phone's reconnect as "channel already open".
            channel.Close(reason);
            SetStatus(SessionStatus.Connecting, "Phone disconnected, waiting for it to return...");
        };
        control.Start();
        _control = control;

        await control.SendHelloAsync(new HelloPayload
        {
            Role = Origins.Pc,
            Caps = new List<string> { "wifi.control", "vcam", "vmic" }
        }).ConfigureAwait(false);

        SetStatus(SessionStatus.Connected, null);

        // Push the current wifi snapshot immediately so the phone is not blank while it
        // waits for the next poll tick.
        await PushWifiStateAsync().ConfigureAwait(false);
    }

    private async Task OnControlMessageAsync(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageTypes.Hello:
                Log?.Invoke($"phone hello: {envelope.Payload}");
                break;

            case MessageTypes.State:
            {
                var payload = Deserialize<StatePayload>(envelope.Payload);
                if (payload is null) return;

                // Only the device scope is accepted from the phone; a wifi snapshot from
                // the peer would mean it is authoring state it does not own.
                if (payload.Scope != StatePayload.ScopeDevice) return;

                if (payload.Camera is not null || payload.Mic is not null || payload.Tether is not null)
                {
                    Device = Device with
                    {
                        Camera = payload.Camera ?? Device.Camera,
                        Mic = payload.Mic ?? Device.Mic,
                        Tether = payload.Tether ?? Device.Tether
                    };
                }
                if (payload.ObservedWifi is not null) Observed = payload.ObservedWifi;

                StateChanged?.Invoke();
                break;
            }

            case MessageTypes.Cmd:
            {
                var payload = Deserialize<CmdPayload>(envelope.Payload);
                if (payload is null)
                {
                    if (_control is not null)
                        await _control.SendAckAsync(envelope.Seq, false, "undecodable command").ConfigureAwait(false);
                    return;
                }
                var error = await ExecuteWifiCommandAsync(payload).ConfigureAwait(false);
                if (_control is not null)
                    await _control.SendAckAsync(envelope.Seq, error is null, error).ConfigureAwait(false);
                await PushWifiStateAsync().ConfigureAwait(false);
                break;
            }

            case MessageTypes.Ack:
            {
                var ack = Deserialize<AckPayload>(envelope.Payload);
                if (ack is not null && !ack.Ok) Log?.Invoke($"phone rejected our command: {ack.Error}");
                _pending.Clear();
                StateChanged?.Invoke();
                break;
            }
        }
    }

    /// <summary>Commands the phone asks US to run, because only ADB can do them.</summary>
    private async Task<string?> ExecuteWifiCommandAsync(CmdPayload payload)
    {
        var ct = _sessionCts?.Token ?? default;
        switch (payload.Cmd)
        {
            case Commands.WifiSetEnabled:
            {
                if (!payload.Args.TryGetProperty("enabled", out var e))
                    return "missing 'enabled'";
                return await _wifi.SetEnabledAndVerifyAsync(e.GetBoolean(), ct).ConfigureAwait(false);
            }

            case Commands.WifiRefresh:
                await PushWifiStateAsync().ConfigureAwait(false);
                return null;

            case Commands.WifiScan:
            {
                var result = await _wifi.StartScanAsync(ct).ConfigureAwait(false);
                if (!result.Ok) return $"scan failed: {result.Combined}";
                // Results are not immediate; give the radio time before re-reading.
                await Task.Delay(3000, ct).ConfigureAwait(false);
                await PushWifiStateAsync().ConfigureAwait(false);
                return null;
            }

            default:
                return $"'{payload.Cmd}' is not a desktop command";
        }
    }

    // ---- wifi authority ----------------------------------------------------

    private async Task WifiPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PushWifiStateAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log?.Invoke($"wifi poll failed: {ex.Message}"); }

            try { await Task.Delay(WifiPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PushWifiStateAsync()
    {
        var ct = _sessionCts?.Token ?? default;
        Wifi = await _wifi.ReadStateAsync(ct).ConfigureAwait(false);
        StateChanged?.Invoke();

        if (_control is not null)
        {
            await _control.SendStateAsync(new StatePayload
            {
                Scope = StatePayload.ScopeWifi,
                Wifi = Wifi
            }).ConfigureAwait(false);
        }
    }

    // ---- media -------------------------------------------------------------

    private async Task VideoLoopAsync(BridgeChannel channel, CancellationToken ct)
    {
        try
        {
            var reader = new FrameReader(channel.Stream);
            var header = await reader.ReadVideoStreamHeaderAsync(ct).ConfigureAwait(false);
            Log?.Invoke($"video stream: {header.Codec} {header.Width}x{header.Height}");

            _decoder?.Dispose();
            _decoder = new FFmpegH264Decoder();

            while (!ct.IsCancellationRequested)
            {
                var packet = await reader.ReadPacketAsync(ct, (w, h) =>
                {
                    // Geometry changed (rotation). Reference frames are invalid now.
                    Log?.Invoke($"geometry change: {w}x{h}");
                    _decoder?.Flush();
                }).ConfigureAwait(false);

                if (packet is null) continue;
                var p = packet.Value;

                var frame = _decoder.Decode(p.Payload, p.PtsNanos);
                if (frame is null) continue;

                var f = frame.Value;
                FrameDecoded?.Invoke(f);
                PublishToVirtualCamera(f);
            }
        }
        catch (OperationCanceledException) { /* session ending */ }
        catch (Exception ex)
        {
            // Closing with a reason is what lets the session distinguish "phone stopped
            // the camera" from "the link died".
            channel.Close($"video loop ended: {ex.Message}");
        }
    }

    private void PublishToVirtualCamera(DecodedFrame frame)
    {
        if (!_vcam.IsRunning) return;

        if (_vcamWidth != frame.Width || _vcamHeight != frame.Height)
        {
            // Geometry change: the sections are sized for the old frame, so recreate them.
            var error = _vcamFrames.Open(frame.Width, frame.Height, Device.Camera.Fps);
            if (error is not null) { Log?.Invoke($"vcam shm reopen failed: {error}"); return; }
            _vcamWidth = frame.Width;
            _vcamHeight = frame.Height;
        }

        _vcamFrames.WriteFrame(frame.Bgra, frame.PtsNanos);
    }

    private async Task AudioLoopAsync(BridgeChannel channel, CancellationToken ct)
    {
        try
        {
            var reader = new FrameReader(channel.Stream);
            var header = await reader.ReadAudioStreamHeaderAsync(ct).ConfigureAwait(false);
            Log?.Invoke($"audio stream: {header.SampleRate}Hz {header.Channels}ch {header.BitsPerSample}-bit");

            var error = _mic.Start(header.SampleRate, header.Channels);
            if (error is not null)
            {
                // Not fatal to the loop: the level meter below still proves audio is
                // arriving from the phone even when VB-Cable can't take it, which is
                // exactly the distinction that matters when diagnosing "no mic in Zoom".
                Log?.Invoke($"virtual mic unavailable: {error}");
            }

            while (!ct.IsCancellationRequested)
            {
                var packet = await reader.ReadPacketAsync(ct).ConfigureAwait(false);
                if (packet is null) continue;
                PushMicLevel(packet.Value.Payload);
                _mic.Write(packet.Value.Payload, 0, packet.Value.Payload.Length);
            }
        }
        catch (OperationCanceledException) { /* session ending */ }
        catch (Exception ex)
        {
            channel.Close($"audio loop ended: {ex.Message}");
        }
        finally
        {
            _mic.Stop();
            ClearMicLevels();
        }
    }

    /// <summary>
    /// Peak amplitude of one PCM S16LE chunk, normalised to 0..1, pushed into a small
    /// ring buffer the UI polls to draw a waveform. Cheap enough to run on every 10ms
    /// chunk (a single pass over ~a thousand samples) and independent of whether
    /// VirtualMicWriter could open VB-Cable, so it proves receipt from the phone even
    /// when nothing downstream can consume the audio yet.
    /// </summary>
    private void PushMicLevel(byte[] pcm)
    {
        short peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var abs = sample < 0 ? (short)-sample : sample;
            if (abs > peak) peak = abs;
        }
        var level = peak / 32768f;

        lock (_micLevelLock)
        {
            _micLevelRing[_micLevelWritePos] = level;
            _micLevelWritePos = (_micLevelWritePos + 1) % MicLevelRingSize;
        }
    }

    private void ClearMicLevels()
    {
        lock (_micLevelLock)
        {
            Array.Clear(_micLevelRing);
            _micLevelWritePos = 0;
        }
    }

    /// <summary>
    /// Snapshot of the most recent levels, oldest first, for the UI to draw. Never blocks
    /// the audio thread for more than the copy itself.
    /// </summary>
    public float[] GetRecentMicLevels(int count)
    {
        count = Math.Min(count, MicLevelRingSize);
        var result = new float[count];
        lock (_micLevelLock)
        {
            for (var i = 0; i < count; i++)
            {
                var idx = (_micLevelWritePos - count + i + MicLevelRingSize * 2) % MicLevelRingSize;
                result[i] = _micLevelRing[idx];
            }
        }
        return result;
    }

    // ---- virtual camera device ---------------------------------------------

    public string? StartVirtualCamera()
    {
        var error = _vcamFrames.Open(Device.Camera.Width, Device.Camera.Height, Device.Camera.Fps);
        if (error is not null) return error;

        _vcamWidth = Device.Camera.Width;
        _vcamHeight = Device.Camera.Height;
        return _vcam.Start();
    }

    public void StopVirtualCamera()
    {
        _vcam.Stop();
        _vcamFrames.Close();
        _vcamWidth = _vcamHeight = 0;
    }

    public bool IsVirtualCameraRunning => _vcam.IsRunning;

    // ---- outbound commands (UI entry point) --------------------------------

    /// <summary>
    /// Sends a device command to the phone. Never applied locally: the phone owns the
    /// device slice, so we render the echo rather than guessing.
    /// </summary>
    public async Task SendCommandAsync(string cmd, object? args = null)
    {
        var control = _control;
        if (control is null) return;

        _pending.Add(cmd);
        StateChanged?.Invoke();

        await control.SendCommandAsync(cmd, args).ConfigureAwait(false);

        // Safety net so a lost ack cannot leave the affordance stuck forever.
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            if (_pending.Remove(cmd)) StateChanged?.Invoke();
        });
    }

    /// <summary>Runs a wifi command locally - the UI path equivalent of a phone request.</summary>
    public async Task<string?> RunWifiCommandAsync(string cmd, object? args = null)
    {
        var json = JsonSerializer.SerializeToElement(args ?? new { }, BridgeJson.Options);
        var error = await ExecuteWifiCommandAsync(new CmdPayload { Cmd = cmd, Args = json }).ConfigureAwait(false);
        await PushWifiStateAsync().ConfigureAwait(false);
        return error;
    }

    public string LastRawWifiStatus => _wifi.LastRawStatus;
    public string LastRawWifiNetworks => _wifi.LastRawNetworks;
    public string LastRawWifiScan => _wifi.LastRawScan;

    // ---- helpers -----------------------------------------------------------

    private static T? Deserialize<T>(JsonElement element) where T : class
    {
        try { return element.Deserialize<T>(BridgeJson.Options); }
        catch (JsonException) { return null; }
    }

    private void SetStatus(SessionStatus status, string? detail)
    {
        Status = status;
        StatusDetail = detail;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownSessionAsync().ConfigureAwait(false);
        await _watcher.DisposeAsync().ConfigureAwait(false);
        await _tunnel.DisposeAsync().ConfigureAwait(false);
        StopVirtualCamera();
        _mic.Dispose();
        _vcamFrames.Dispose();
        _vcam.Dispose();
        await _adb.KillServerAsync().ConfigureAwait(false);
    }
}
