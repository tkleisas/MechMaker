namespace MechMaker.Engine.Klipper;

/// <summary>
/// Klipper wire-format primitives: VLQ integers, CRC16-CCITT, and message block
/// framing (<c>len seq content crc16 sync</c>) — byte-compatible with the real
/// protocol (see klippy/msgproto.py and Protocol.html) so a stock Klipper host
/// could talk to the simulated MCU.
/// </summary>
public static class KlipperWire
{
    public const byte SyncByte = 0x7e;
    public const int MinBlockSize = 5;   // length + sequence + crc16 + sync
    public const int MaxBlockSize = 64;

    // ---------- VLQ ----------
    // Port of msgproto.py PT_uint32.encode/parse: 7-bit groups, least-significant
    // first, continuation bit 0x80 on all but the last byte. The first byte's
    // bits 5-6 act as sign extension ((c & 0x60) == 0x60 => negative), giving the
    // documented ranges: 1 byte: -32..95, 2: -4096..12287, 3: -524288..1572863,
    // 4: -67108864..201326591, 5: full uint32/int32.

    public static byte[] EncodeVlq(long v)
    {
        var outBytes = new List<byte>(5);
        if (v >= 0xc000000 || v < -0x4000000)
            outBytes.Add((byte)(((v >> 28) & 0x7f) | 0x80));
        if (v >= 0x180000 || v < -0x80000)
            outBytes.Add((byte)(((v >> 21) & 0x7f) | 0x80));
        if (v >= 0x3000 || v < -0x1000)
            outBytes.Add((byte)(((v >> 14) & 0x7f) | 0x80));
        if (v >= 0x60 || v < -0x20)
            outBytes.Add((byte)(((v >> 7) & 0x7f) | 0x80));
        outBytes.Add((byte)(v & 0x7f));
        return outBytes.ToArray();
    }

    /// <summary>Decodes one VLQ at <paramref name="offset"/>; returns value + bytes consumed.</summary>
    public static (long Value, int Bytes) DecodeVlq(IReadOnlyList<byte> data, int offset)
    {
        var c = data[offset];
        long v = c & 0x7f;
        if ((c & 0x60) == 0x60)
            v |= unchecked((long)-0x20); // sign-extend (bits 5+6 set: -32..-1 encoded in the low 7 bits)
        var count = 1;
        while ((c & 0x80) != 0)
        {
            c = data[offset + count];
            v = (v << 7) | (c & 0x7f);
            count++;
        }
        return (v, count);
    }

    /// <summary>Convenience: decode all VLQs in a content span.</summary>
    public static List<long> DecodeVlqAll(byte[] data)
    {
        var values = new List<long>();
        var offset = 0;
        while (offset < data.Length)
        {
            var (value, bytes) = DecodeVlq(data, offset);
            values.Add(value);
            offset += bytes;
        }
        return values;
    }

    /// <summary>Encodes a sequence of VLQ integers into one content buffer.</summary>
    public static byte[] EncodeVlqAll(params long[] values)
    {
        var content = new List<byte>();
        foreach (var value in values)
            content.AddRange(EncodeVlq(value));
        return content.ToArray();
    }

    // ---------- CRC16-CCITT (poly 0x1021, init 0xffff) ----------

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xffff;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    // ---------- message blocks ----------

    /// <summary>Frames one block: length, sequence (0x10 | seq), content, crc16, sync.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> content, byte sequence)
    {
        var length = MinBlockSize + content.Length;
        if (length > MaxBlockSize)
            throw new ArgumentException($"Message block content exceeds {MaxBlockSize - MinBlockSize} bytes.");
        var block = new byte[length];
        block[0] = (byte)length;
        block[1] = (byte)(0x10 | (sequence & 0x0f));
        content.CopyTo(block.AsSpan(2));
        var crc = Crc16(block.AsSpan(0, 2 + content.Length));
        block[^3] = (byte)(crc >> 8);
        block[^2] = (byte)(crc & 0xff);
        block[^1] = SyncByte;
        return block;
    }
}

/// <summary>A parsed message block: 4-bit sequence + content bytes (the VLQ command stream).</summary>
public sealed record MessageBlock(byte Sequence, byte[] Content);

/// <summary>
/// Incremental block parser — a faithful port of klipper's
/// <c>command_find_block</c> (src/command.c): a frame is <c>len seq content crc16 sync</c>
/// with the sequence byte's high nibble 0x10; on error, leading sync bytes are
/// ignored and everything up to (and including) the next sync is discarded.
/// Sending the corresponding nak is the connection layer's job; a corrupt-block
/// counter is exposed for it.
/// </summary>
public sealed class BlockParser
{
    private const byte SeqMask = 0x0f;
    private const byte SeqDest = 0x10;

    private readonly List<byte> _buffer = [];
    private bool _needSync;

    /// <summary>Blocks discarded due to bad CRC or framing (nak triggers).</summary>
    public int CorruptBlocks { get; private set; }

    public void Feed(byte b) => _buffer.Add(b);

    public void Feed(IEnumerable<byte> bytes) => _buffer.AddRange(bytes);

    public void Clear()
    {
        _buffer.Clear();
        _needSync = false;
        CorruptBlocks = 0;
    }

    /// <summary>
    /// Parses the next complete block; null when more bytes are needed. Mirrors
    /// <c>command_find_block</c>: validates length, sequence high nibble, trailing
    /// sync, and CRC; on failure discards through the next sync byte.
    /// </summary>
    public MessageBlock? TryParse()
    {
        while (true)
        {
            if (_needSync)
            {
                // Discard bytes until the next sync (inclusive).
                var syncIndex = _buffer.IndexOf(KlipperWire.SyncByte);
                if (syncIndex < 0)
                {
                    _buffer.Clear();
                    return null;
                }
                _buffer.RemoveRange(0, syncIndex + 1);
                _needSync = false;
            }

            if (_buffer.Count < KlipperWire.MinBlockSize)
                return null;

            var msglen = _buffer[0];
            if (msglen is < KlipperWire.MinBlockSize or > KlipperWire.MaxBlockSize)
            {
                // Error: leading sync bytes are ignored outright, otherwise discard
                // to the next sync.
                if (_buffer[0] == KlipperWire.SyncByte)
                    _buffer.RemoveAt(0);
                else
                    _needSync = true;
                CorruptBlocks++;
                continue;
            }

            if ((_buffer[1] & ~SeqMask) != SeqDest)
            {
                _needSync = true;
                CorruptBlocks++;
                continue;
            }

            if (_buffer.Count < msglen)
                return null; // need more data

            if (_buffer[msglen - 1] != KlipperWire.SyncByte)
            {
                _needSync = true;
                CorruptBlocks++;
                continue;
            }

            var contentLength = msglen - KlipperWire.MinBlockSize;
            var crc = KlipperWire.Crc16(_buffer.Take(2 + contentLength).ToArray());
            var expected = (ushort)((_buffer[msglen - 3] << 8) | _buffer[msglen - 2]);
            if (crc != expected)
            {
                _needSync = true;
                CorruptBlocks++;
                continue;
            }

            var content = _buffer.Skip(2).Take(contentLength).ToArray();
            var sequence = (byte)(_buffer[1] & SeqMask);
            _buffer.RemoveRange(0, msglen);
            return new MessageBlock(sequence, content);
        }
    }
}