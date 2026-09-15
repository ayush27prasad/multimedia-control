using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Transport;

/// <summary>A decoded media packet. <see cref="Payload"/> is owned by the callee.</summary>
public readonly record struct MediaPacket(
    long PtsNanos,
    bool IsConfig,
    bool IsKeyframe,
    byte[] Payload);

/// <summary>
/// Reads the framed media channels (docs/PROTOCOL.md §3).
///
/// Framing is strict and has no resync marker: a single mis-parsed length desynchronises
/// the stream permanently. Every failure path therefore tears the channel down rather
/// than attempting to recover in place - the reconnect logic rebuilds it cleanly.
/// </summary>
public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[Wire.FrameHeaderLength];

    /// <summary>
    /// Guards against a corrupt length field allocating absurd buffers. 32 MiB is far
    /// above any real H.264 frame at the bitrates we use, so this can only trip on a bug
    /// or a desync - both of which should kill the channel.
    /// </summary>
    private const int MaxPayload = 32 * 1024 * 1024;

    public FrameReader(Stream stream) => _stream = stream;

    /// <summary>Reads the 16-byte video stream header sent once before any frames.</summary>
    public async Task<Wire.VideoStreamHeader> ReadVideoStreamHeaderAsync(CancellationToken ct)
    {
        var buf = new byte[16];
        await BridgeListener.ReadExactlyAsync(_stream, buf, ct).ConfigureAwait(false);
        return Wire.ParseVideoStreamHeader(buf);
    }

    /// <summary>Reads the 20-byte audio stream header sent once before any frames.</summary>
    public async Task<Wire.AudioStreamHeader> ReadAudioStreamHeaderAsync(CancellationToken ct)
    {
        var buf = new byte[20];
        await BridgeListener.ReadExactlyAsync(_stream, buf, ct).ConfigureAwait(false);
        return Wire.ParseAudioStreamHeader(buf);
    }

    /// <summary>
    /// Reads one packet. A session packet (geometry change) surfaces as
    /// <paramref name="geometryChange"/> with a null return.
    /// </summary>
    public async Task<MediaPacket?> ReadPacketAsync(
        CancellationToken ct,
        Action<int, int>? geometryChange = null)
    {
        await BridgeListener.ReadExactlyAsync(_stream, _header, ct).ConfigureAwait(false);
        var header = Wire.ParseFrameHeader(_header);

        if (!header.IsMedia)
        {
            geometryChange?.Invoke(header.SessionWidth, header.SessionHeight);
            return null;
        }

        if (header.PayloadLength is < 0 or > MaxPayload)
        {
            throw new InvalidDataException(
                $"payload length {header.PayloadLength} out of range - stream desynchronised");
        }

        var payload = new byte[header.PayloadLength];
        await BridgeListener.ReadExactlyAsync(_stream, payload, 0, payload.Length, ct).ConfigureAwait(false);

        return new MediaPacket(header.PtsNanos, header.IsConfig, header.IsKeyframe, payload);
    }
}
