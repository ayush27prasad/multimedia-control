namespace PhoneBridge.Adb;

public enum DeviceStatus { None, Unauthorized, Offline, Ready }

public sealed record DeviceSnapshot(DeviceStatus Status, AdbDevice? Device, string? Detail)
{
    public static readonly DeviceSnapshot None =
        new(DeviceStatus.None, null, "No device. Check the cable and enable USB debugging.");
}

/// <summary>
/// Polls `adb devices` and raises an event when the attached device changes.
///
/// Polling rather than `adb track-devices`: that would mean speaking the adb server's
/// wire protocol directly, and a 1.5s poll is entirely adequate for a cable being
/// plugged in by hand.
/// </summary>
public sealed class DeviceWatcher : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    private readonly AdbClient _adb;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public DeviceSnapshot Current { get; private set; } = DeviceSnapshot.None;

    /// <summary>Raised only on change, never on every poll.</summary>
    public event Action<DeviceSnapshot>? Changed;
    public event Action<string>? Log;

    public DeviceWatcher(AdbClient adb) => _adb = adb;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = await PollAsync(ct).ConfigureAwait(false);

                // Compare on identity and status, not the whole record: the model string
                // can arrive late on the first poll after attach and would otherwise
                // fire a spurious second change.
                if (snapshot.Status != Current.Status ||
                    snapshot.Device?.Serial != Current.Device?.Serial)
                {
                    Current = snapshot;
                    Log?.Invoke($"device: {snapshot.Status} {snapshot.Device?.Serial ?? ""}".TrimEnd());
                    Changed?.Invoke(snapshot);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log?.Invoke($"device poll failed: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<DeviceSnapshot> PollAsync(CancellationToken ct)
    {
        var devices = await _adb.ListDevicesAsync(ct).ConfigureAwait(false);
        if (devices.Count == 0) return DeviceSnapshot.None;

        var unauthorized = devices.FirstOrDefault(d => d.IsUnauthorized);
        if (unauthorized is not null)
        {
            return new DeviceSnapshot(DeviceStatus.Unauthorized, unauthorized,
                "Unlock the phone and tap 'Allow' on the USB debugging prompt.");
        }

        var ready = devices.FirstOrDefault(d => d.IsReady);
        if (ready is null)
        {
            var first = devices[0];
            return new DeviceSnapshot(DeviceStatus.Offline, first, $"Device is '{first.State}'.");
        }

        // Pin the serial so every later command targets this device even if a second one
        // is attached mid-session.
        _adb.Serial = ready.Serial;

        return new DeviceSnapshot(DeviceStatus.Ready, ready,
            devices.Count > 1 ? $"Using {ready.Serial} ({devices.Count} devices attached)" : null);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _cts.Dispose();
    }
}
