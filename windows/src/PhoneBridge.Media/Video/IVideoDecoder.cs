namespace PhoneBridge.Media.Video;

/// <summary>A decoded frame. <see cref="Bgra"/> is reused between calls - copy before storing.</summary>
public readonly record struct DecodedFrame(int Width, int Height, byte[] Bgra, long PtsNanos);

/// <summary>
/// H.264 decode, behind an interface so the backend can be swapped.
///
/// The default is FFmpeg. If it proves awkward to deploy, a Media Foundation MFT with
/// CODECAPI_AVLowLatencyMode is the alternative - it needs no native binaries shipped but
/// costs a lot of COM interop. Nothing outside this folder should depend on which is used.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Feeds one Annex-B packet. Config packets (SPS/PPS) return null and are not an error.
    /// </summary>
    DecodedFrame? Decode(ReadOnlySpan<byte> annexB, long ptsNanos);

    /// <summary>Discards reference state. Call on a geometry change or after a gap.</summary>
    void Flush();
}
