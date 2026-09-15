using System.Runtime.InteropServices;

namespace PhoneBridge.Media.VirtualCam;

/// <summary>
/// Publishes decoded frames into shared memory for the virtual camera media source.
///
/// WHY THIS EXISTS AT ALL
/// Microsoft documents no way to push frames into MFCreateVirtualCamera. There is no
/// IMFVirtualCamera::WriteFrame. The media source is a separate COM DLL that Windows
/// loads into svchost.exe running as LOCAL SERVICE - it is not code in this process. So
/// frames must cross a process AND a security boundary, and every shipping
/// implementation solves it the same way: a Global\ shared-memory ring buffer.
/// See DECISIONS.md D7 and docs/VCAM-SHM.md for the layout.
///
/// THE TWO THINGS THAT GO WRONG
/// 1. ACLs. The reader is LOCAL SERVICE in session 0. A section created with default
///    permissions is invisible to it, and the symptom is a silent black camera with no
///    error anywhere. Hence the explicit SDDL below.
/// 2. Torn frames. Reader and writer share no lock, so each slot carries a seqlock: the
///    sequence number is odd while a write is in progress and even when the slot is
///    settled. A reader that sees an odd value, or a different value after reading,
///    retries. This is why .NET's MemoryMappedFile API is not used - it offers no way to
///    supply a security descriptor, so the section is created by P/Invoke instead.
/// </summary>
public sealed unsafe class SharedMemoryFrameWriter : IDisposable
{
    public const uint Magic = 0x50425643;   // "PBVC"
    public const uint LayoutVersion = 1;

    /// <summary>
    /// Ring depth. Three is enough to decouple a writer from a reader sampling at a
    /// different phase, without adding latency - the reader always takes the newest
    /// settled slot, so deeper buffers would only age the frame it sees.
    /// </summary>
    public const int SlotCount = 3;

    private const int ControlSize = 4096;
    private const int SlotHeaderSize = 64;   // seq + pts + padding to a cache-line boundary

    /// <summary>
    /// Local Service and Local System get read; Administrators and the creating owner get
    /// full control. Local Service is the one that actually matters - it is the account
    /// the Frame Server streams under.
    /// </summary>
    private const string Sddl = "D:(A;;GR;;;LS)(A;;GR;;;SY)(A;;GA;;;BA)(A;;GA;;;CO)";

    private readonly string _controlName;
    private readonly string _frameName;

    private IntPtr _controlHandle = IntPtr.Zero;
    private IntPtr _frameHandle = IntPtr.Zero;
    private byte* _control;
    private byte* _frames;

    private int _width, _height, _fps;
    private long _slotStride;
    private ulong _writeIndex;

    public bool IsOpen => _control is not null;
    public event Action<string>? Log;

    public SharedMemoryFrameWriter(string instance = "default")
    {
        // The Global\ namespace is mandatory: a Local\ name resolves per-session, and the
        // reader lives in session 0 while we live in the interactive session.
        _controlName = $@"Global\PhoneBridgeVCam_{instance}_ctl";
        _frameName = $@"Global\PhoneBridgeVCam_{instance}_frames";
    }

    /// <summary>
    /// Creates (or re-creates) the sections for a given geometry.
    /// </summary>
    /// <returns>null on success, otherwise a human-readable reason.</returns>
    public string? Open(int width, int height, int fps)
    {
        Close();

        _width = width;
        _height = height;
        _fps = fps;

        var frameBytes = (long)width * height * 4;              // BGRA32
        _slotStride = SlotHeaderSize + frameBytes;
        var totalFrameBytes = _slotStride * SlotCount;

        var sd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out sd, IntPtr.Zero))
            {
                return $"could not build the security descriptor (win32 {Marshal.GetLastWin32Error()})";
            }

            var attrs = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0
            };

            _controlHandle = CreateSection(ref attrs, ControlSize, _controlName, out var err1);
            if (_controlHandle == IntPtr.Zero) return $"could not create the control section (win32 {err1})";

            _frameHandle = CreateSection(ref attrs, totalFrameBytes, _frameName, out var err2);
            if (_frameHandle == IntPtr.Zero) return $"could not create the frame section (win32 {err2})";

            _control = (byte*)MapViewOfFile(_controlHandle, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)ControlSize);
            if (_control is null) return $"could not map the control section (win32 {Marshal.GetLastWin32Error()})";

            _frames = (byte*)MapViewOfFile(_frameHandle, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)totalFrameBytes);
            if (_frames is null) return $"could not map the frame section (win32 {Marshal.GetLastWin32Error()})";

            WriteControlBlock();
            Log?.Invoke($"vcam shared memory open: {width}x{height}@{fps}, {SlotCount} slots, {totalFrameBytes / 1024}KiB");
            return null;
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    private static IntPtr CreateSection(ref SECURITY_ATTRIBUTES attrs, long size, string name, out int error)
    {
        var handle = CreateFileMappingW(
            INVALID_HANDLE_VALUE, ref attrs, PAGE_READWRITE,
            (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), name);
        error = Marshal.GetLastWin32Error();
        return handle;
    }

    private void WriteControlBlock()
    {
        var p = _control;
        Write32(p, 0, Magic);
        Write32(p, 4, LayoutVersion);
        Write32(p, 8, (uint)_width);
        Write32(p, 12, (uint)_height);
        Write32(p, 16, (uint)_fps);
        Write32(p, 20, 1);                      // pixel format: 1 = BGRA32
        Write32(p, 24, SlotCount);
        Write64(p, 32, (ulong)_slotStride);
        Write64(p, 40, 0);                      // writeIndex - bumped per frame
        Write64(p, 48, (ulong)DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Publishes one BGRA frame. Must be exactly width*height*4 bytes.
    /// Cheap enough to call from the decode loop.
    /// </summary>
    public void WriteFrame(ReadOnlySpan<byte> bgra, long ptsNanos)
    {
        if (_frames is null || _control is null) return;

        var expected = (long)_width * _height * 4;
        if (bgra.Length != expected)
        {
            // A geometry change that has not reached Open() yet. Dropping is correct -
            // writing a wrong-sized frame would corrupt the slot.
            Log?.Invoke($"frame size mismatch: got {bgra.Length}, expected {expected} - dropping");
            return;
        }

        var slot = (int)(_writeIndex % SlotCount);
        var slotPtr = _frames + slot * _slotStride;
        var seqPtr = (ulong*)slotPtr;

        // Seqlock. Odd means "in progress", even means "settled".
        var seq = *seqPtr;
        Volatile.Write(ref *seqPtr, seq | 1);

        // The reader must never observe the payload write before the odd sequence number,
        // nor the even number before the payload is complete.
        Thread.MemoryBarrier();

        *(long*)(slotPtr + 8) = ptsNanos;
        bgra.CopyTo(new Span<byte>(slotPtr + SlotHeaderSize, (int)expected));

        Thread.MemoryBarrier();
        Volatile.Write(ref *seqPtr, (seq | 1) + 1);

        _writeIndex++;
        Write64(_control, 40, _writeIndex);
        // Heartbeat lets the reader tell "no new frames" from "writer died", so it can
        // fall back to a placeholder rather than freezing on the last frame forever.
        Write64(_control, 48, (ulong)DateTime.UtcNow.Ticks);
    }

    public void Close()
    {
        if (_control is not null) { UnmapViewOfFile((IntPtr)_control); _control = null; }
        if (_frames is not null) { UnmapViewOfFile((IntPtr)_frames); _frames = null; }
        if (_controlHandle != IntPtr.Zero) { CloseHandle(_controlHandle); _controlHandle = IntPtr.Zero; }
        if (_frameHandle != IntPtr.Zero) { CloseHandle(_frameHandle); _frameHandle = IntPtr.Zero; }
    }

    public void Dispose() => Close();

    private static void Write32(byte* p, int offset, uint value) => *(uint*)(p + offset) = value;
    private static void Write64(byte* p, int offset, ulong value) => *(ulong*)(p + offset) = value;

    // ---- interop -----------------------------------------------------------

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(
        IntPtr hFile, ref SECURITY_ATTRIBUTES lpAttributes, uint flProtect,
        uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint sddlRevision, out IntPtr securityDescriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
