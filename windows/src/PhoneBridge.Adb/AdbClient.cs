using System.Diagnostics;
using System.Text;

namespace PhoneBridge.Adb;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
    public bool Ok => ExitCode == 0;

    /// <summary>
    /// A command outside WifiShellCommand's NON_PRIVILEGED_COMMANDS allowlist is rejected
    /// for the shell uid with a SecurityException. Distinguishing that from a command
    /// that ran and merely failed is what lets the UI say something useful.
    /// </summary>
    public bool IsPermissionDenied =>
        Combined.Contains("SecurityException", StringComparison.OrdinalIgnoreCase);

    public bool IsUnknownCommand =>
        Combined.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
        Combined.Contains("unknown option", StringComparison.OrdinalIgnoreCase);
}

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsReady => State.Equals("device", StringComparison.OrdinalIgnoreCase);
    public bool IsUnauthorized => State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Thin wrapper over the bundled adb.exe.
///
/// Uses a PRIVATE server port (default 5137) rather than the standard 5037. This is the
/// single highest-value robustness decision in the project: on 5037 our bundled adb and
/// the user's Android Studio / scrcpy / Vysor repeatedly kill and restart each other's
/// servers when versions differ, surfacing as random unattributable disconnects. See
/// DECISIONS.md D9.
/// </summary>
public sealed class AdbClient
{
    public const int DefaultServerPort = 5137;

    private readonly string _adbPath;
    private readonly int _serverPort;

    public string? Serial { get; set; }

    public event Action<string>? Log;

    public AdbClient(string adbPath, int serverPort = DefaultServerPort)
    {
        _adbPath = adbPath;
        _serverPort = serverPort;
    }

    /// <summary>Locates adb.exe: bundled copy first, then PATH.</summary>
    public static string? LocateAdb()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "adb", "adb.exe"),
            Path.Combine(baseDir, "tools", "adb", "adb.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "tools", "adb", "adb.exe"))
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default, int timeoutMs = 15_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Every invocation inherits the private port, including the implicit server start.
        psi.Environment["ANDROID_ADB_SERVER_PORT"] = _serverPort.ToString();

        if (!string.IsNullOrEmpty(Serial))
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(Serial);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new AdbResult(-1, "", $"failed to start adb: {ex.Message}");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new AdbResult(-1, "", $"adb timed out after {timeoutMs}ms");
        }

        var stdout = (await stdOutTask.ConfigureAwait(false)).TrimEnd();
        var stderr = (await stdErrTask.ConfigureAwait(false)).TrimEnd();

        return new AdbResult(process.ExitCode, stdout, stderr);
    }

    public Task<AdbResult> ShellAsync(CancellationToken ct, params string[] args) =>
        RunAsync(new[] { "shell" }.Concat(args), ct);

    /// <summary>Starts the server explicitly so the first real command does not pay for it.</summary>
    public async Task<bool> StartServerAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "start-server" }, ct, timeoutMs: 30_000).ConfigureAwait(false);
        if (!result.Ok) Log?.Invoke($"adb start-server failed: {result.Combined}");
        return result.Ok;
    }

    /// <summary>Kills only OUR server, identified by the private port - never the user's on 5037.</summary>
    public Task KillServerAsync(CancellationToken ct = default) =>
        RunAsync(new[] { "kill-server" }, ct);

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "devices", "-l" }, ct).ConfigureAwait(false);
        if (!result.Ok) return Array.Empty<AdbDevice>();

        var devices = new List<AdbDevice>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))
                            ?.Substring("model:".Length)
                            ?.Replace('_', ' ')
                        ?? "";

            devices.Add(new AdbDevice(parts[0], parts[1], model));
        }
        return devices;
    }

    public async Task<string> GetPropAsync(string key, CancellationToken ct = default)
    {
        var result = await ShellAsync(ct, "getprop", key).ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : "";
    }
}
