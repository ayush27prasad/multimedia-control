using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace PhoneBridge.Media.Video;

/// <summary>
/// Low-latency H.264 decode via FFmpeg.
///
/// Two settings matter far more than the rest:
///  - AV_CODEC_FLAG_LOW_DELAY: do not buffer for reordering.
///  - SLICE threading, never FRAME threading. Frame threading adds N frames of latency,
///    which is the exact opposite of what this pipeline exists for.
///
/// The encoder emits no B-frames, so decode is 1-in/1-out and typically under 5ms.
/// </summary>
public sealed class FFmpegH264Decoder : IVideoDecoder
{
    private readonly CodecContext _codec;
    private readonly Frame _frame = new();
    private readonly Packet _packet = new();

    private VideoFrameConverter? _converter;
    private Frame? _bgraFrame;
    private byte[]? _buffer;
    private int _bufferWidth, _bufferHeight;

    public FFmpegH264Decoder()
    {
        // FindDecoderById returns Codec (a struct), not Codec? — it throws if missing.
        var decoder = Codec.FindDecoderById(AVCodecID.H264);

        _codec = new CodecContext(decoder)
        {
            Flags = AV_CODEC_FLAG.LowDelay,
            ThreadCount = Math.Min(Environment.ProcessorCount, 4),
            // Slice threading parallelises WITHIN a frame. Frame threading (1) would
            // pipeline across frames and add latency proportional to the thread count.
            // FF_THREAD_SLICE is a C #define (value 2) that this binding does not emit.
            ThreadType = 2
        };
        _codec.Open(decoder);
    }

    public DecodedFrame? Decode(ReadOnlySpan<byte> annexB, long ptsNanos)
    {
        if (annexB.Length == 0) return null;

        unsafe
        {
            fixed (byte* p = annexB)
            {
                _packet.Data = new DataPointer(p, annexB.Length);
                _packet.Pts = ptsNanos;

                try
                {
                    _codec.SendPacket(_packet);
                }
                catch (FFmpegException)
                {
                    // A packet the decoder cannot use - typically the first frames before
                    // an IDR has arrived. Not fatal; the next keyframe recovers.
                    return null;
                }
            }
        }

        var result = _codec.ReceiveFrame(_frame);
        if (result != CodecResult.Success) return null;

        return Convert(_frame, ptsNanos);
    }

    /// <summary>
    /// Converts the decoder's native pixel format (almost always YUV420P) to BGRA, which
    /// is what both WPF's WriteableBitmap and the virtual camera's ring buffer expect.
    /// </summary>
    private DecodedFrame Convert(Frame source, long ptsNanos)
    {
        var width = source.Width;
        var height = source.Height;

        if (_converter is null || _bufferWidth != width || _bufferHeight != height)
        {
            _converter?.Dispose();
            _bgraFrame?.Dispose();

            _converter = new VideoFrameConverter();
            _bgraFrame = Frame.CreateVideo(width, height, AVPixelFormat.Bgra);
            _buffer = new byte[width * height * 4];
            _bufferWidth = width;
            _bufferHeight = height;
        }

        _converter.ConvertFrame(source, _bgraFrame!, SWS.FastBilinear);

        unsafe
        {
            var src = (byte*)_bgraFrame!.Data[0];
            var stride = _bgraFrame.Linesize[0];
            var rowBytes = width * 4;

            // Copy row by row: FFmpeg's stride is padded for alignment and is usually
            // wider than the visible row, so a single block copy would skew the image.
            fixed (byte* dst = _buffer!)
            {
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * stride, dst + (long)y * rowBytes, rowBytes, rowBytes);
                }
            }
        }

        return new DecodedFrame(width, height, _buffer!, ptsNanos);
    }

    public void Flush()
    {
        unsafe
        {
            try { avcodec_flush_buffers(_codec); }
            catch { /* nothing decoded yet */ }
        }
    }

    public void Dispose()
    {
        _converter?.Dispose();
        _bgraFrame?.Dispose();
        _frame.Dispose();
        _packet.Dispose();
        _codec.Dispose();
    }
}
