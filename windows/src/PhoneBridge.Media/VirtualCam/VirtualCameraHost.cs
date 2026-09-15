using System.Runtime.InteropServices;

namespace PhoneBridge.Media.VirtualCam;

/// <summary>
/// Registers the virtual camera with the Windows 11 Frame Server (FR2).
///
/// The good news: MFCreateVirtualCamera requires NO code signing and NO MSIX packaging.
/// It needs Windows 11 build 22000+, and a one-time elevated regsvr32 of the media source
/// DLL into HKLM. It is the only approach visible to BOTH Media Foundation and DirectShow
/// consumers, so Teams, Zoom, Chrome, Discord, OBS and the Camera app all see one device.
///
/// This class only creates and starts the camera. The frames come from
/// <see cref="SharedMemoryFrameWriter"/>, because the media source runs in a different
/// process - see docs/VCAM-SHM.md.
/// </summary>
public sealed class VirtualCameraHost : IDisposable
{
    /// <summary>
    /// Must match the CLSID the native media source registers under. Changing one without
    /// the other produces a camera that appears and then fails to start.
    /// </summary>
    public const string MediaSourceClsid = "{D9BCA1C4-7E4F-4A1B-9F3E-5B2A7C6D8E10}";

    private IntPtr _virtualCamera = IntPtr.Zero;

    public bool IsRunning { get; private set; }
    public string FriendlyName { get; }

    public event Action<string>? Log;

    /// <param name="friendlyName">
    /// Note: the pipeline appends "Windows Virtual Camera" to whatever is passed here.
    /// The final displayed string is not fully under our control.
    /// </param>
    public VirtualCameraHost(string friendlyName = "PhoneBridge Camera")
        => FriendlyName = friendlyName;

    public static bool IsSupported()
    {
        // 22000 is Windows 11 21H2. There is no Windows 10 backport of this API.
        var v = Environment.OSVersion.Version;
        return v.Major >= 10 && v.Build >= 22000;
    }

    /// <returns>null on success, otherwise a human-readable reason.</returns>
    public string? Start()
    {
        if (!IsSupported())
        {
            return "MFCreateVirtualCamera requires Windows 11 build 22000 or newer. " +
                   "On Windows 10 the fallback is a DirectShow filter, which is invisible to " +
                   "Media Foundation consumers such as the Camera app. See DECISIONS.md D7.";
        }

        try
        {
            var hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
            if (hr < 0) return $"MFStartup failed (hr 0x{hr:X8})";

            hr = MFCreateVirtualCamera(
                MFVirtualCameraType.SoftwareCameraSource,
                // Session lifetime: the camera disappears when this process exits, so a
                // crash cannot strand a zombie device that lingers until logoff.
                MFVirtualCameraLifetime.Session,
                MFVirtualCameraAccess.CurrentUser,
                FriendlyName,
                MediaSourceClsid,
                null, 0,
                out _virtualCamera);

            if (hr < 0)
            {
                return hr switch
                {
                    unchecked((int)0x80070005) =>
                        "Access denied creating the virtual camera. Two usual causes: the camera " +
                        "privacy toggle in Settings -> Privacy -> Camera is off, or the media source " +
                        "DLL is somewhere Local Service cannot read. Install it under %ProgramData% " +
                        "and register from there - a path under your user profile will not work.",
                    unchecked((int)0x80040154) =>
                        $"The media source is not registered. Run, elevated: " +
                        $"regsvr32 PhoneBridge.VirtualCam.dll (CLSID {MediaSourceClsid}).",
                    _ => $"MFCreateVirtualCamera failed (hr 0x{hr:X8})"
                };
            }

            var camera = (IMFVirtualCamera)Marshal.GetObjectForIUnknown(_virtualCamera);
            camera.Start(IntPtr.Zero);

            IsRunning = true;
            Log?.Invoke($"virtual camera started: {FriendlyName}");
            return null;
        }
        catch (InvalidCastException ex)
        {
            Stop();
            return "Could not marshal IMFVirtualCamera - the IID or the method order in " +
                   $"VirtualCameraHost is wrong. Take both from the Windows 11 SDK's " +
                   $"mfvirtualcamera.h. ({ex.Message})";
        }
        catch (Exception ex)
        {
            Stop();
            return $"virtual camera start failed: {ex.Message}";
        }
    }

    public void Stop()
    {
        if (_virtualCamera != IntPtr.Zero)
        {
            try
            {
                var camera = (IMFVirtualCamera)Marshal.GetObjectForIUnknown(_virtualCamera);
                camera.Stop();
                camera.Remove();
            }
            catch { /* already torn down */ }

            Marshal.Release(_virtualCamera);
            _virtualCamera = IntPtr.Zero;
        }

        if (IsRunning)
        {
            try { MFShutdown(); } catch { /* already shut down */ }
            IsRunning = false;
        }
    }

    public void Dispose() => Stop();

    // ---- interop -----------------------------------------------------------

    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_NOSOCKET = 1;

    private enum MFVirtualCameraType { SoftwareCameraSource = 0 }
    private enum MFVirtualCameraLifetime { Session = 0, System = 1 }
    private enum MFVirtualCameraAccess { CurrentUser = 0, AllUsers = 1 }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfsensorgroup.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateVirtualCamera(
        MFVirtualCameraType type,
        MFVirtualCameraLifetime lifetime,
        MFVirtualCameraAccess access,
        string friendlyName,
        string sourceId,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? categories,
        uint categoryCount,
        out IntPtr virtualCamera);

    /// <summary>
    /// !! VERIFY THIS IID BEFORE FIRST RUN !!
    ///
    /// This value could NOT be verified on the machine this was written on (macOS, no
    /// Windows SDK). Take the real IID from <c>mfvirtualcamera.h</c> in the Windows 11
    /// SDK - search for <c>MIDL_INTERFACE</c> above <c>IMFVirtualCamera</c> - or read it
    /// off smourier/VCamSample.
    ///
    /// A wrong IID surfaces as an InvalidCastException on GetObjectForIUnknown, which
    /// reads like a marshalling bug rather than a wrong constant. The method-order in
    /// this interface matters just as much: COM dispatches by vtable slot, so a
    /// mis-ordered declaration calls the wrong function with no error at all.
    /// </summary>
    [ComImport]
    [Guid("cb2273d0-8c2b-4d1c-8f2e-0f2b4a4e1b6a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFVirtualCamera
    {
        void AddDeviceSourceInfo([MarshalAs(UnmanagedType.LPWStr)] string deviceSourceInfo);
        void AddProperty(IntPtr key, uint type, IntPtr data, uint dataSize);
        void AddRegistryEntry([MarshalAs(UnmanagedType.LPWStr)] string entryName,
                              [MarshalAs(UnmanagedType.LPWStr)] string subkeyPath,
                              uint valueType, IntPtr value, uint valueSize);
        void SetMediaSource(IntPtr mediaSource);
        void SendCameraProperty(IntPtr propertySet, uint propertyId, uint propertyFlags,
                                IntPtr propertyPayload, uint propertyPayloadLength,
                                IntPtr data, uint dataLength, out uint dataWritten);
        void CreateSyncEvent(Guid kseventSet, uint kseventId, uint kseventFlags,
                             IntPtr eventHandle, out IntPtr syncEvent);
        void CreateSyncSemaphore(Guid kseventSet, uint kseventId, uint kseventFlags,
                                 int semaphoreAdjustment, out IntPtr syncSemaphore);
        void Start(IntPtr callback);
        void Stop();
        void Remove();
        void GetMediaSource(out IntPtr mediaSource);
    }
}
