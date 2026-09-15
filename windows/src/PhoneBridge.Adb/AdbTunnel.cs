using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Adb;

public enum TunnelMode { Reverse, Forward }

/// <summary>
/// Creates and tears down the ADB tunnel that carries every channel.
///
/// REVERSE is preferred: adbd on the device listens on the abstract socket, the phone app
/// connects to it, and we get a genuine connect event. Under FORWARD the phone listens
/// and our TCP connect succeeds whenever the rule exists - even with no app running - so
/// readiness cannot be inferred from the socket alone. See docs/PROTOCOL.md §1.
/// </summary>
public sealed class AdbTunnel : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private bool _established;

    public TunnelMode Mode { get; private set; } = TunnelMode.Reverse;
    public int LocalPort { get; private set; }

    public event Action<string>? Log;

    public AdbTunnel(AdbClient adb) => _adb = adb;

    /// <summary>
    /// Establishes the tunnel to <paramref name="localPort"/>, trying reverse then forward.
    /// </summary>
    /// <returns>null on success, otherwise a human-readable reason.</returns>
    public async Task<string?> EstablishAsync(int localPort, CancellationToken ct = default)
    {
        LocalPort = localPort;
        await RemoveAllAsync(ct).ConfigureAwait(false);

        var spec = $"localabstract:{Wire.SocketName}";

        // adb reverse <remote> <local>: adbd listens on `remote` ON THE DEVICE and pipes
        // to `local` on this machine.
        var reverse = await _adb.RunAsync(
            new[] { "reverse", spec, $"tcp:{localPort}" }, ct).ConfigureAwait(false);

        if (reverse.Ok)
        {
            Mode = TunnelMode.Reverse;
            _established = true;
            Log?.Invoke($"reverse tunnel up: {spec} -> tcp:{localPort}");
            return null;
        }

        Log?.Invoke($"adb reverse failed ({reverse.Combined}); trying forward");

        // adb forward <local> <remote>: we listen here, adbd connects on the device.
        var forward = await _adb.RunAsync(
            new[] { "forward", $"tcp:{localPort}", spec }, ct).ConfigureAwait(false);

        if (forward.Ok)
        {
            Mode = TunnelMode.Forward;
            _established = true;
            Log?.Invoke($"forward tunnel up: tcp:{localPort} -> {spec} (fallback mode)");
            return null;
        }

        return $"could not establish an ADB tunnel. reverse: {reverse.Combined} | forward: {forward.Combined}";
    }

    /// <summary>
    /// Clears our rules. Called before establishing so a stale rule from a crashed run
    /// cannot leave the phone half-attached to a dead listener - without a rule the
    /// phone's connect() is refused immediately, which is the failure we want.
    /// </summary>
    public async Task RemoveAllAsync(CancellationToken ct = default)
    {
        var spec = $"localabstract:{Wire.SocketName}";
        // Targeted removal only: --remove-all would also drop rules belonging to
        // scrcpy or Android Studio, which are not ours to touch.
        await _adb.RunAsync(new[] { "reverse", "--remove", spec }, ct).ConfigureAwait(false);
        if (LocalPort > 0)
        {
            await _adb.RunAsync(new[] { "forward", "--remove", $"tcp:{LocalPort}" }, ct).ConfigureAwait(false);
        }
        _established = false;
    }

    /// <summary>
    /// Launches the phone app. Under REVERSE the phone is the dialling side, so it must
    /// actually be running for anything to happen; this saves the user picking it up.
    /// </summary>
    public async Task<bool> LaunchPhoneAppAsync(CancellationToken ct = default)
    {
        var result = await _adb.ShellAsync(ct,
            "am", "start", "-n", "com.superbryn.phonebridge/.ui.MainActivity").ConfigureAwait(false);

        if (!result.Ok || result.Combined.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke($"could not launch the phone app: {result.Combined}");
            return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_established) return;
        try { await RemoveAllAsync().ConfigureAwait(false); } catch { /* shutting down */ }
    }
}
