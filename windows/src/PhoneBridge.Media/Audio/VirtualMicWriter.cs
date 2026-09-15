using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneBridge.Media.Audio;

/// <summary>
/// Presents the phone's microphone to Windows as a selectable input device (FR5).
///
/// It does this indirectly, because Windows provides NO user-mode way to create an audio
/// capture endpoint - no MFCreateVirtualMicrophone, no WASAPI endpoint-creation call, and
/// no user-mode audio driver framework. Endpoints are manufactured from kernel-streaming
/// pins, so it is a signed kernel driver or nothing. See DECISIONS.md D8.
///
/// So: we RENDER into VB-Cable's "CABLE Input" (a playback endpoint). The driver wires
/// render to capture internally, so the audio emerges on "CABLE Output" - a genuine KS
/// capture endpoint that Zoom/Teams/Discord accept exactly like a physical mic. The user
/// selects "CABLE Output" as their microphone.
/// </summary>
public sealed class VirtualMicWriter : IDisposable
{
    /// <summary>The RENDER endpoint we write into. Not the one the user selects.</summary>
    public const string CableInputMatch = "CABLE Input";

    /// <summary>The CAPTURE endpoint the user selects as their microphone.</summary>
    public const string CableOutputMatch = "CABLE Output";

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDevice? _device;

    public bool IsRunning { get; private set; }
    public string? DeviceName { get; private set; }

    public event Action<string>? Log;

    /// <summary>True if VB-Cable is installed, checked by enumerating endpoints.</summary>
    public static bool IsCableInstalled()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return FindCableInput(enumerator) is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Matches on friendly name rather than the default device: the user's speakers are
    /// almost certainly the default render endpoint, and writing the phone's mic there
    /// would play it out loud instead of feeding the virtual microphone.
    /// </summary>
    private static MMDevice? FindCableInput(MMDeviceEnumerator enumerator)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(CableInputMatch, StringComparison.OrdinalIgnoreCase))
                return device;
        }
        return null;
    }

    /// <summary>
    /// Opens the endpoint. <paramref name="sampleRate"/> and <paramref name="channels"/>
    /// describe the incoming phone stream.
    /// </summary>
    /// <returns>null on success, otherwise a human-readable reason.</returns>
    public string? Start(int sampleRate, int channels)
    {
        Stop();

        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = FindCableInput(enumerator);

            if (_device is null)
            {
                return "VB-Cable is not installed. Windows has no user-mode way to create a " +
                       "virtual microphone, so it is required. Install from https://vb-audio.com/Cable/ " +
                       "and reboot.";
            }

            DeviceName = _device.FriendlyName;

            var format = new WaveFormat(sampleRate, 16, channels);
            _buffer = new BufferedWaveProvider(format)
            {
                // Half a second of slack absorbs USB and scheduler jitter. Discarding on
                // overflow rather than blocking keeps the network reader from stalling -
                // dropping stale audio is always better than adding latency to live audio.
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true
            };

            // Shared mode: exclusive would lock every other app out of the endpoint, and
            // 50ms of latency here is irrelevant next to the capture and transport path.
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            _output.Init(_buffer);
            _output.Play();

            IsRunning = true;
            Log?.Invoke($"virtual mic writing into '{DeviceName}' at {sampleRate}Hz {channels}ch");
            return null;
        }
        catch (Exception ex)
        {
            Stop();
            // The usual cause is a format mismatch: VB-Cable's endpoint format is fixed in
            // the Sound control panel and shared mode will not resample for us here.
            return $"could not open '{CableInputMatch}': {ex.Message}. Check that its format in " +
                   $"Sound -> Playback -> Properties -> Advanced is {sampleRate} Hz, 16-bit, " +
                   $"{(channels == 2 ? "stereo" : "mono")}.";
        }
    }

    /// <summary>Queues PCM for playback. Safe to call from the network reader thread.</summary>
    public void Write(byte[] pcm, int offset, int count)
    {
        if (!IsRunning || _buffer is null) return;
        try { _buffer.AddSamples(pcm, offset, count); }
        catch (Exception ex) { Log?.Invoke($"virtual mic write failed: {ex.Message}"); }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _output?.Stop(); } catch { /* already stopped */ }
        _output?.Dispose();
        _output = null;
        _device?.Dispose();
        _device = null;
        _buffer = null;
        DeviceName = null;
    }

    public void Dispose() => Stop();
}
