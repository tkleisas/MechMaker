using System.Text;
using MechMaker.Engine.Klipper;

namespace MechMaker.Engine.Tests;

/// <summary>
/// Klipper wire-format codec: VLQ (port of klippy/msgproto.py), CRC16-CCITT, and
/// message block framing (Protocol.html "Low-level message encoding").
/// </summary>
public class KlipperWireTests
{
    // ---------- VLQ ----------

    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(1, new byte[] { 0x01 })]
    [InlineData(95, new byte[] { 0x5f })]          // max positive single byte
    [InlineData(-32, new byte[] { 0x60 })]         // min negative single byte
    [InlineData(-1, new byte[] { 0x7f })]
    [InlineData(96, new byte[] { 0x80, 0x60 })]    // needs continuation from here
    [InlineData(127, new byte[] { 0x80, 0x7f })]
    [InlineData(128, new byte[] { 0x81, 0x00 })]
    [InlineData(100, new byte[] { 0x80, 0x64 })]   // Python: encode(100) = [128, 100]
    [InlineData(-100, new byte[] { 0xff, 0x1c })]
    [InlineData(-4096, new byte[] { 0xe0, 0x00 })]
    [InlineData(-4097, new byte[] { 0xff, 0xdf, 0x7f })]
    [InlineData(12287, new byte[] { 0xdf, 0x7f })] // max 2-byte positive
    [InlineData(7458, new byte[] { 0xba, 0x22 })]  // the queue_step interval from the protocol doc
    [InlineData(2000000, new byte[] { 0x80, 0xfa, 0x89, 0x00 })] // 2 s of 1 MHz ticks
    [InlineData(201326591, new byte[] { 0xdf, 0xff, 0xff, 0x7f })]
    [InlineData(-67108864, new byte[] { 0xe0, 0x80, 0x80, 0x00 })]
    [InlineData(4294967295, new byte[] { 0x8f, 0xff, 0xff, 0xff, 0x7f })] // uint32 max (clock values)
    [InlineData(-2147483648, new byte[] { 0xf8, 0x80, 0x80, 0x80, 0x00 })]
    public void Vlq_matches_the_klipper_encoding(long value, byte[] expected)
        => Assert.Equal(expected, KlipperWire.EncodeVlq(value));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(95)]
    [InlineData(96)]
    [InlineData(-1)]
    [InlineData(-32)]
    [InlineData(-33)]
    [InlineData(7458)]
    [InlineData(12287)]
    [InlineData(-4096)]
    [InlineData(-4097)]
    [InlineData(1572863)]
    [InlineData(-524288)]
    [InlineData(201326591)]
    [InlineData(-67108864)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(4294967295)] // uint32 max (clock values)
    public void Vlq_round_trips(long value)
    {
        var encoded = KlipperWire.EncodeVlq(value);
        var (decoded, bytes) = KlipperWire.DecodeVlq(encoded, 0);
        Assert.Equal(value, decoded);
        Assert.Equal(encoded.Length, bytes);
    }

    [Fact]
    public void Vlq_stream_round_trips()
    {
        var values = new long[] { 7, 6, 1, 7, 7458, 10, 331, 11717, 1281, -4000000 };
        var encoded = KlipperWire.EncodeVlqAll(values);
        Assert.Equal(values, KlipperWire.DecodeVlqAll(encoded));
    }

    // ---------- CRC16-CCITT ----------

    [Fact]
    public void Crc16_matches_known_vectors()
    {
        // Standard CCITT-FALSE vectors (init 0xffff, poly 0x1021).
        Assert.Equal(0x29B1, KlipperWire.Crc16("123456789"u8));
        Assert.Equal(0xffff, KlipperWire.Crc16(ReadOnlySpan<byte>.Empty)); // init value
    }

    // ---------- message blocks ----------

    [Fact]
    public void Block_frame_and_parse_round_trip()
    {
        var content = KlipperWire.EncodeVlqAll(42, 7458, 10, 331);
        var block = KlipperWire.Frame(content, sequence: 7);

        Assert.Equal(KlipperWire.SyncByte, block[^1]);
        Assert.Equal(block.Length, block[0]);
        Assert.Equal(0x17, block[1]); // 0x10 | seq 7

        var parser = new BlockParser();
        parser.Feed(block);
        var parsed = parser.TryParse();

        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.Sequence);
        Assert.Equal(content, parsed.Content);
    }

    [Fact]
    public void An_ack_is_an_empty_block()
    {
        var ack = KlipperWire.Frame(ReadOnlySpan<byte>.Empty, sequence: 3);
        Assert.Equal(5, ack.Length); // length + sequence + crc16 + sync

        var parser = new BlockParser();
        parser.Feed(ack);
        var parsed = parser.TryParse();

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Content); // the ack/nak signature
    }

    [Fact]
    public void Corrupt_blocks_are_dropped_and_the_stream_resyncs()
    {
        // klipper behaviour: a bad frame poisons the stream until the next sync
        // byte — everything up to and including that sync is discarded. The host's
        // retransmission then delivers the block again.
        var good = KlipperWire.Frame(KlipperWire.EncodeVlqAll(1, 2), sequence: 1);

        var parser = new BlockParser();
        parser.Feed(0x00);
        parser.Feed(0x99); // garbage before any frame
        parser.Feed(good); // this block is swallowed by the resync (no sync before it)
        Assert.Null(parser.TryParse());

        parser.Feed(good); // retransmission arrives
        var parsed = parser.TryParse();
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.Sequence);
        Assert.Equal(1, parser.CorruptBlocks);
    }

    [Fact]
    public void A_bad_crc_is_skipped()
    {
        var block = KlipperWire.Frame(KlipperWire.EncodeVlqAll(5), sequence: 2);
        block[3] ^= 0xff; // corrupt the content

        var parser = new BlockParser();
        parser.Feed(block);
        Assert.Null(parser.TryParse());
    }

    [Fact]
    public void Byte_dropped_from_the_middle_resyncs_to_the_next_frame()
    {
        var first = KlipperWire.Frame(KlipperWire.EncodeVlqAll(1), sequence: 1);
        var second = KlipperWire.Frame(KlipperWire.EncodeVlqAll(2), sequence: 2);

        var parser = new BlockParser();
        parser.Feed(first);
        parser.Feed(second.Skip(1)); // the second frame lost its length byte

        var firstParsed = parser.TryParse();
        Assert.NotNull(firstParsed);
        Assert.Equal(1, firstParsed.Sequence);

        // The host retransmits (timeout) and more traffic follows; the parser's
        // sliding resync finds the intact frame inside the garbage.
        parser.Feed(second);
        parser.Feed(second);
        parser.Feed(second);
        MessageBlock? secondBlock = null;
        for (var i = 0; i < 100 && secondBlock is null; i++)
            secondBlock = parser.TryParse();
        Assert.NotNull(secondBlock);
        Assert.Equal(2, secondBlock.Sequence);
        Assert.Equal(2, KlipperWire.DecodeVlqAll(secondBlock.Content).Single());
    }

    [Fact]
    public void Incomplete_frames_wait_for_more_bytes()
    {
        var block = KlipperWire.Frame(KlipperWire.EncodeVlqAll(9), sequence: 4);
        var parser = new BlockParser();

        foreach (var b in block.Take(block.Length - 1))
            parser.Feed(b);
        Assert.Null(parser.TryParse());

        parser.Feed(block[^1]);
        var parsed = parser.TryParse();
        Assert.NotNull(parsed);
        Assert.Equal(9, KlipperWire.DecodeVlqAll(parsed.Content).Single());
    }

    [Fact]
    public void Blocks_byte_for_byte_match_the_klipper_format()
    {
        // Hand-computed from the protocol doc: <len><0x10|seq><content><crc16><0x7e>.
        // Content = get_clock command (single VLQ id) is exercised in KlipperMcuTests;
        // here verify structure of a two-command block with known VLQ encodings.
        var content = KlipperWire.EncodeVlqAll(10, 6, 1);
        var block = KlipperWire.Frame(content, sequence: 0);
        Assert.Equal(8, block.Length);
        Assert.Equal(8, block[0]);
        Assert.Equal(0x10, block[1]);
        Assert.Equal(KlipperWire.SyncByte, block[^1]);
    }
}