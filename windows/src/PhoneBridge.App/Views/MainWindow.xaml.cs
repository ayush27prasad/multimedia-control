using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PhoneBridge.Adb;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Media.Audio;
using PhoneBridge.Media.Video;
using PhoneBridge.Media.VirtualCam;

namespace PhoneBridge.App.Views;

public sealed record NetworkRow(string Ssid, string Detail);

public partial class MainWindow : Window
{
    private BridgeSession? _session;
    private WriteableBitmap? _preview;
    private readonly ObservableCollection<NetworkRow> _networks = new();
    private readonly StringBuilder _log = new();

    /// <summary>
    /// Set while the code is pushing authoritative state into the controls, so the Click
    /// handlers can tell a programmatic change from a user click. Without this, every
    /// state echo would fire a command back at the peer and the two apps would ping-pong.
    /// </summary>
    private bool _suppressEvents;

    private int _frameCount;
    private DateTime _lastFpsSample = DateTime.UtcNow;
    private double _measuredFps;

    // ---- mic waveform --------------------------------------------------------
    private const int WaveformBarCount = 40;
    private readonly DispatcherTimer _waveformTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<Rectangle> _waveformBars = new();

    public MainWindow()
    {
        InitializeComponent();
        NetworkList.ItemsSource = _networks;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _waveformTimer.Tick += (_, _) => RedrawMicWaveform();
        BuildMicWaveformBars();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var adbPath = AdbClient.LocateAdb();
        if (adbPath is null)
        {
            StatusText.Text = "adb.exe not found";
            StatusDetail.Text = "Run scripts\\setup-windows.ps1, which downloads platform-tools into windows\\tools\\adb.";
            StatusDot.Fill = (Brush)FindResource("Danger");
            return;
        }

        AppendLog($"using adb at {adbPath}");

        _session = new BridgeSession(adbPath);
        _session.Log += AppendLogThreadSafe;
        _session.StateChanged += () => Dispatcher.BeginInvoke(RefreshUi);
        _session.FrameDecoded += OnFrameDecoded;

        // The virtual mic is a hard dependency for M4 and the reason is non-obvious, so
        // surface it once at startup rather than at first use.
        if (!VirtualMicWriter.IsCableInstalled())
        {
            AppendLog("VB-Cable not detected - the virtual microphone will be unavailable. " +
                      "Windows has no user-mode way to create one; install from https://vb-audio.com/Cable/");
            MicWaveformLabel.Text = "Waveform of audio arriving from the phone " +
                                     "(VB-Cable not installed - Zoom/the browser can't use it as a mic yet)";
        }
        if (!VirtualCameraHost.IsSupported())
        {
            AppendLog("Windows 11 build 22000+ required for the virtual camera. Preview still works.");
        }

        _waveformTimer.Start();
        await _session.StartAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _waveformTimer.Stop();
        if (_session is not null) await _session.DisposeAsync();
    }

    // ---- mic waveform --------------------------------------------------------

    /// <summary>Creates the fixed set of bars once; <see cref="OnMicWaveformSizeChanged"/> lays them out.</summary>
    private void BuildMicWaveformBars()
    {
        MicWaveform.Children.Clear();
        _waveformBars.Clear();
        for (var i = 0; i < WaveformBarCount; i++)
        {
            var bar = new Rectangle
            {
                Fill = (Brush)FindResource("Success"),
                RadiusX = 1,
                RadiusY = 1
            };
            _waveformBars.Add(bar);
            MicWaveform.Children.Add(bar);
        }
    }

    private void OnMicWaveformSizeChanged(object sender, SizeChangedEventArgs e) => LayOutMicWaveformBars();

    private void LayOutMicWaveformBars()
    {
        var width = MicWaveform.ActualWidth;
        var height = MicWaveform.ActualHeight;
        if (width <= 0 || height <= 0 || _waveformBars.Count == 0) return;

        var slot = width / WaveformBarCount;
        var barWidth = Math.Max(1, slot - 2);

        for (var i = 0; i < _waveformBars.Count; i++)
        {
            var bar = _waveformBars[i];
            bar.Width = barWidth;
            Canvas.SetLeft(bar, i * slot);
        }
    }

    /// <summary>
    /// Polled from the session's mic-level ring buffer rather than driven by an event per
    /// audio packet (~100/s) - this decouples the audio thread entirely from UI redraw
    /// cost and matches the FPS counter's existing polling style.
    /// </summary>
    private void RedrawMicWaveform()
    {
        if (_session is null || _waveformBars.Count == 0) return;
        if (MicWaveform.ActualWidth <= 0) return;

        var height = MicWaveform.ActualHeight;
        var levels = _session.GetRecentMicLevels(WaveformBarCount);

        for (var i = 0; i < _waveformBars.Count && i < levels.Length; i++)
        {
            // Floor of 2px so silence still reads as "a flat line", not "nothing is here".
            var barHeight = Math.Max(2, levels[i] * height);
            var bar = _waveformBars[i];
            bar.Height = barHeight;
            Canvas.SetTop(bar, (height - barHeight) / 2);
        }
    }

    // ---- rendering ---------------------------------------------------------

    private void OnFrameDecoded(DecodedFrame frame)
    {
        // Marshal to the UI thread. Background priority so a burst of frames cannot
        // starve input handling - dropping a preview frame is always preferable.
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_preview is null || _preview.PixelWidth != frame.Width || _preview.PixelHeight != frame.Height)
            {
                _preview = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                PreviewImage.Source = _preview;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }

            _preview.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Bgra, frame.Width * 4, 0);

            _frameCount++;
            var elapsed = (DateTime.UtcNow - _lastFpsSample).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _measuredFps = _frameCount / elapsed;
                _frameCount = 0;
                _lastFpsSample = DateTime.UtcNow;
                PreviewStats.Text = $"{frame.Width}x{frame.Height}  {_measuredFps:F1} fps";
            }
        });
    }

    private void RefreshUi()
    {
        if (_session is null) return;

        _suppressEvents = true;
        try
        {
            var (text, detail, brushKey) = _session.Status switch
            {
                SessionStatus.Connected => ("Connected", _session.StatusDetail, "Success"),
                SessionStatus.Connecting => ("Connecting", _session.StatusDetail, "Warning"),
                SessionStatus.Unauthorized => ("Device not authorised", _session.StatusDetail, "Warning"),
                SessionStatus.Error => ("Error", _session.StatusDetail, "Danger"),
                _ => ("No device", _session.StatusDetail, "TextMuted")
            };
            StatusText.Text = text;
            StatusDetail.Text = detail ?? "";
            StatusDot.Fill = (Brush)FindResource(brushKey);
            DeviceText.Text = _session.AttachedDevice is { } d ? $"{d.Model} ({d.Serial})" : "";

            var connected = _session.Status == SessionStatus.Connected;
            var cam = _session.Device.Camera;
            var mic = _session.Device.Mic;
            var wifi = _session.Wifi;

            CameraToggle.IsChecked = cam.Streaming;
            CameraToggle.IsEnabled = connected;
            RearButton.IsEnabled = connected && cam.Facing != CameraState.FacingBack;
            FrontButton.IsEnabled = connected && cam.Facing != CameraState.FacingFront;
            CameraDetail.Text = cam.Error
                ?? $"{cam.Width}x{cam.Height} @ {cam.Fps}fps, {cam.BitrateKbps / 1000} Mb/s, {cam.Facing}";

            VCamToggle.IsChecked = _session.IsVirtualCameraRunning;
            VCamToggle.IsEnabled = VirtualCameraHost.IsSupported();

            MicToggle.IsChecked = mic.Streaming;
            MicToggle.IsEnabled = connected;
            MuteToggle.IsChecked = mic.Muted;
            MuteToggle.IsEnabled = connected && mic.Streaming;
            MicDetail.Text = mic.Error ?? $"PCM {mic.SampleRate / 1000}kHz {(mic.Channels == 2 ? "stereo" : "mono")}, {mic.Source}";

            WifiToggle.IsChecked = wifi.Enabled;
            WifiToggle.IsEnabled = connected || _session.Status == SessionStatus.Connecting;

            // Prefer the phone's own reading: it updates on a NetworkCallback, whereas our
            // ADB view waits for the next poll.
            var currentSsid = _session.Observed.Ssid ?? wifi.Ssid;
            WifiCurrent.Text = currentSsid is not null
                ? $"connected: {currentSsid}   {_session.Observed.Rssi ?? wifi.Rssi} dBm"
                : wifi.Enabled ? "not connected" : "radio off";

            WifiError.Text = wifi.LastError ?? "";
            WifiError.Visibility = wifi.LastError is null ? Visibility.Collapsed : Visibility.Visible;

            var strength = _session.Observed.InRange.ToDictionary(r => r.Ssid, r => r.Rssi);
            _networks.Clear();
            foreach (var net in wifi.SavedNetworks.OrderByDescending(n =>
                         strength.TryGetValue(n.Ssid, out var r) ? r : int.MinValue))
            {
                var inRange = strength.TryGetValue(net.Ssid, out var rssi);
                _networks.Add(new NetworkRow(net.Ssid,
                    net.Ssid == currentSsid ? "connected" : inRange ? $"{rssi} dBm" : "out of range"));
            }

            TetherStatus.Text = _session.Device.Tether.Active
                ? "Active - this PC can route through the phone"
                : "Not active";

            if (!cam.Streaming)
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewStats.Text = "";
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    // ---- command handlers --------------------------------------------------

    private async void OnCameraToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;
        await _session.SendCommandAsync(Commands.CameraSetStreaming,
            new { on = CameraToggle.IsChecked == true });
    }

    private async void OnFacingClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;
        var facing = (string)((FrameworkElement)sender).Tag;
        await _session.SendCommandAsync(Commands.CameraSetFacing, new { facing });
    }

    private async void OnQualityClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;
        var choice = (string)((FrameworkElement)sender).Tag;
        // Bitrate scales with pixel count. Both sit well inside the link budget, so
        // quality is the priority rather than bandwidth (FR3).
        var (w, h, kbps) = choice == "1080" ? (1920, 1080, 25_000) : (1280, 720, 12_000);
        await _session.SendCommandAsync(Commands.CameraSetFormat,
            new { width = w, height = h, bitrateKbps = kbps });
    }

    private async void OnMicToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;
        await _session.SendCommandAsync(Commands.MicSetStreaming, new { on = MicToggle.IsChecked == true });
    }

    private async void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;
        await _session.SendCommandAsync(Commands.MicSetMuted, new { muted = MuteToggle.IsChecked == true });
    }

    private async void OnTetherClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        await _session.SendCommandAsync(Commands.TetherOpenSettings);
    }

    private async void OnWifiToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;
        var error = await _session.RunWifiCommandAsync(Commands.WifiSetEnabled,
            new { enabled = WifiToggle.IsChecked == true });
        if (error is not null) AppendLog($"wifi toggle: {error}");
    }

    private async void OnWifiRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        await _session.RunWifiCommandAsync(Commands.WifiRefresh);
    }

    private async void OnWifiScanClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        AppendLog("scanning...");
        var error = await _session.RunWifiCommandAsync(Commands.WifiScan);
        if (error is not null) AppendLog($"scan: {error}");
    }

    private void OnVCamToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _session is null) return;

        if (VCamToggle.IsChecked == true)
        {
            var error = _session.StartVirtualCamera();
            if (error is not null)
            {
                AppendLog($"virtual camera: {error}");
                VCamDetail.Text = error;
                VCamToggle.IsChecked = false;
            }
        }
        else
        {
            _session.StopVirtualCamera();
        }
    }

    private async void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        AppendLog("manual reconnect requested");
        await _session.RunWifiCommandAsync(Commands.WifiRefresh);
    }

    /// <summary>
    /// Copies everything needed to diagnose a Wi-Fi parsing problem. The raw ADB output
    /// matters most: the parsers are tolerant but MIUI 14's exact formats are unverified.
    /// </summary>
    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        var sb = new StringBuilder();
        sb.AppendLine("# PhoneBridge diagnostics");
        sb.AppendLine($"generated: {DateTime.Now:O}");
        sb.AppendLine($"status: {_session.Status} {_session.StatusDetail}");
        sb.AppendLine($"device: {_session.AttachedDevice?.Model} {_session.AttachedDevice?.Serial}");
        sb.AppendLine();
        sb.AppendLine("## parsed wifi state");
        sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(_session.Wifi,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
        sb.AppendLine("## raw: cmd wifi status");
        sb.AppendLine(_session.LastRawWifiStatus);
        sb.AppendLine();
        sb.AppendLine("## raw: cmd wifi list-networks");
        sb.AppendLine(_session.LastRawWifiNetworks);
        sb.AppendLine();
        sb.AppendLine("## raw: cmd wifi list-scan-results");
        sb.AppendLine(_session.LastRawWifiScan);
        sb.AppendLine();
        sb.AppendLine("## log");
        sb.Append(_log);

        try
        {
            Clipboard.SetText(sb.ToString());
            AppendLog("diagnostics copied to clipboard");
        }
        catch (Exception ex)
        {
            AppendLog($"clipboard unavailable: {ex.Message}");
        }
    }

    // ---- logging -----------------------------------------------------------

    private void AppendLogThreadSafe(string message) =>
        Dispatcher.BeginInvoke(() => AppendLog(message));

    private void AppendLog(string message)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        // Trim from the front so a long session cannot grow the buffer without bound.
        if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);

        LogText.Text = _log.ToString();
        LogScroll.ScrollToEnd();
    }
}
