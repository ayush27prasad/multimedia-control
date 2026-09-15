namespace PhoneBridge.Core.Protocol;

/// <summary>
/// Wire constants and frame decoding. The authoritative specification is
/// docs/PROTOCOL.md; this file and the Kotlin <c>Protocol.kt</c> must not drift from it.
///
/// All multi-byte integers are big-endian.
/// </summary>
public static class Wire
{
    public const int Version = 1;

    /// <summary>Abstract-namespace socket name. Same in both tunnel modes; only the role differs.</summary>
    public const string SocketName = "phonebridge";

    public const byte ChannelVideo = 1;
    public const byte ChannelAudio = 2;
    public const byte ChannelControl = 3;

    public const int HandshakeLength = 10;
    public const int ReplyLength = 6;
    public const int FrameHeaderLength = 12;

    public const int StatusOk = 0;
    public const int StatusBadMagic = 1;
    public const int StatusVersionMismatch = 2;
    public const int StatusChannelBusy = 3;

    /// <summary>Anything longer is a desync, not a message. There is no resync marker in NDJSON.</summary>
    public const int MaxControlLine = 256 * 1024;

    public static readonly byte[] MagicClient = "PBRG"u8.ToArray();
    public static readonly byte[] MagicOk = "PBOK"u8.ToArray();
    public static readonly byte[] MagicNo = "PBNO"u8.ToArray();

    private const byte FlagMedia = 0x80;
    private const byte FlagConfig = 0x40;
    private const byte FlagKeyframe = 0x20;

    // ---- handshake ---------------------------------------------------------

    public static bool TryParseHandshake(ReadOnlySpan<byte> buffer, out byte channel, out int status)
    {
        channel = 0;
        status = StatusOk;

        if (buffer.Length < HandshakeLength) { status = StatusBadMagic; return false; }
        if (!buffer[..4].SequenceEqual(MagicClient)) { status = StatusBadMagic; return false; }

        var version = buffer[4];
        if (version != Version) { status = StatusVersionMismatch; return false; }

        channel = buffer[5];
        if (channel is not (ChannelVideo or ChannelAudio or ChannelControl))
        {
            status = StatusBadMagic;
            return false;
        }
        return true;
    }

    public static byte[] BuildReply(int status)
    {
        var reply = new byte[ReplyLength];
        var magic = status == StatusOk ? MagicOk : MagicNo;
        magic.CopyTo(reply, 0);
        reply[4] = Version;
        reply[5] = (byte)status;
        return reply;
    }

    // ---- stream headers ----------------------------------------------------

    public readonly record struct VideoStreamHeader(string Codec, int Width, int Height);
    public readonly record struct AudioStreamHeader(int Format, int SampleRate, int Channels, int BitsPerSample);

    public static VideoStreamHeader ParseVideoStreamHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < 16) throw new InvalidDataException("short video stream header");
        if (!b[..4].SequenceEqual("PBV1"u8)) throw new InvalidDataException("bad video stream magic");
        return new VideoStreamHeader(
            System.Text.Encoding.ASCII.GetString(b.Slice(4, 4)),
            ReadInt32(b, 8),
            ReadInt32(b, 12));
    }

    public static AudioStreamHeader ParseAudioStreamHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < 20) throw new InvalidDataException("short audio stream header");
        if (!b[..4].SequenceEqual("PBA1"u8)) throw new InvalidDataException("bad audio stream magic");
        return new AudioStreamHeader(ReadInt32(b, 4), ReadInt32(b, 8), ReadInt32(b, 12), ReadInt32(b, 16));
    }

    // ---- frame headers -----------------------------------------------------

    public readonly record struct FrameHeader(
        bool IsMedia,
        bool IsConfig,
        bool IsKeyframe,
        long PtsNanos,
        int PayloadLength,
        int SessionWidth,
        int SessionHeight);

    public static FrameHeader ParseFrameHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < FrameHeaderLength) throw new InvalidDataException("short frame header");

        var flags = b[0];
        var isMedia = (flags & FlagMedia) != 0;

        if (!isMedia)
        {
            // Session packet: geometry changed. Decoder must flush and reconfigure.
            return new FrameHeader(false, false, false, 0, 0, ReadInt32(b, 4), ReadInt32(b, 8));
        }

        // Top 5 bits of the 61-bit PTS share byte 0 with the flags.
        long pts = (long)(flags & 0x1F) << 56;
        for (var i = 1; i <= 7; i++)
        {
            pts |= (long)b[i] << (8 * (7 - i));
        }

        return new FrameHeader(
            IsMedia: true,
            IsConfig: (flags & FlagConfig) != 0,
            IsKeyframe: (flags & FlagKeyframe) != 0,
            PtsNanos: pts,
            PayloadLength: ReadInt32(b, 8),
            SessionWidth: 0,
            SessionHeight: 0);
    }

    public static int ReadInt32(ReadOnlySpan<byte> b, int offset) =>
        (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
}
