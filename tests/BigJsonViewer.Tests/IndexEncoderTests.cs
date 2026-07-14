using BigJsonViewer.BenchmarkKernels;

namespace BigJsonViewer.Tests;

public sealed class IndexEncoderTests
{
    private static readonly long[] Offsets = [1, 127, 128, 129, 16_384, 1_000_000, long.MaxValue];

    [Fact]
    public void Fixed64RoundTripsOffsets()
    {
        var encoded = new byte[Offsets.Length * sizeof(long)];
        var decoded = new long[Offsets.Length];

        var written = IndexEncoder.EncodeFixed64(Offsets, encoded);
        var consumed = IndexEncoder.DecodeFixed64(encoded, decoded);

        Assert.Equal(encoded.Length, written);
        Assert.Equal(written, consumed);
        Assert.Equal(Offsets, decoded);
    }

    [Fact]
    public void DeltaVarintRoundTripsOffsets()
    {
        var encoded = new byte[Offsets.Length * 10];
        var decoded = new long[Offsets.Length];

        var written = IndexEncoder.EncodeDeltaVarint(Offsets, encoded);
        var consumed = IndexEncoder.DecodeDeltaVarint(encoded.AsSpan(0, written), decoded);

        Assert.Equal(written, consumed);
        Assert.Equal(Offsets, decoded);
        Assert.True(written < Offsets.Length * sizeof(long));
    }

    [Fact]
    public void DeltaVarintRejectsUnsortedOffsets()
    {
        var destination = new byte[32];

        Assert.Throws<ArgumentException>(() => IndexEncoder.EncodeDeltaVarint([10, 9], destination));
    }

    [Fact]
    public void EncodersRejectSmallDestinations()
    {
        Assert.Throws<ArgumentException>(() => IndexEncoder.EncodeFixed64(Offsets, new byte[1]));
        Assert.Throws<ArgumentException>(() => IndexEncoder.EncodeDeltaVarint(Offsets, new byte[1]));
    }

    [Fact]
    public void DecoderRejectsTruncatedAndOverflowingVarints()
    {
        var destination = new long[1];

        Assert.Throws<ArgumentException>(() => IndexEncoder.DecodeDeltaVarint([0x80], destination));
        Assert.Throws<ArgumentException>(() => IndexEncoder.DecodeDeltaVarint(
            [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02],
            destination));
    }
}
