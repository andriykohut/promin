using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

public class H264StreamTests
{
    // NAL unit header bytes: forbidden_zero_bit 0, nal_ref_idc, nal_unit_type.
    private const byte Sps = 0x67;       // type 7
    private const byte Pps = 0x68;       // type 8
    private const byte Idr = 0x65;       // type 5
    private const byte Slice = 0x41;     // type 1, a P or B slice
    private const byte Aud = 0x09;       // type 9

    private static readonly byte[] Long = [0, 0, 0, 1];
    private static readonly byte[] Short = [0, 0, 1];

    private static byte[] Stream(byte[] startCode, params byte[][] nals) =>
        nals.SelectMany(n => startCode.Concat(n)).ToArray();

    private static byte[] Nal(byte header, params byte[] payload) => [header, .. payload];

    [Fact]
    public void Inspect_FindsSpsPpsAndIdr_InAKeyframe()
    {
        var data = Stream(Long, Nal(Sps, 0x42, 0x00), Nal(Pps, 0xCE), Nal(Idr, 0x88, 0x84));

        var summary = H264Stream.Inspect(data);

        Assert.Equal(new H264Stream.Summary(IsAnnexB: true, HasIdr: true, HasSps: true, HasPps: true), summary);
    }

    [Fact]
    public void Inspect_SeesNoIdr_InAnInterFrame()
    {
        var data = Stream(Long, Nal(Aud, 0xF0), Nal(Slice, 0x9A, 0x01));

        var summary = H264Stream.Inspect(data);

        Assert.True(summary.IsAnnexB);
        Assert.False(summary.HasIdr);
        Assert.False(summary.HasSps);
        Assert.False(summary.HasPps);
    }

    [Fact]
    public void Inspect_ReadsThreeByteStartCodes()
    {
        var data = Stream(Short, Nal(Sps, 1), Nal(Pps, 2), Nal(Idr, 3));

        var summary = H264Stream.Inspect(data);

        Assert.True(summary.IsAnnexB);
        Assert.True(summary.HasIdr);
        Assert.True(summary.HasParameterSets);
    }

    [Fact]
    public void Inspect_FindsAnIdrThatIsNotTheFirstNalUnit()
    {
        var data = Stream(Long, Nal(Aud, 0xF0), Nal(Sps, 1), Nal(Pps, 2), Nal(Slice, 7), Nal(Idr, 3));

        Assert.True(H264Stream.Inspect(data).HasIdr);
    }

    [Fact]
    public void Inspect_AcceptsLeadingZeroBytesBeforeTheFirstStartCode()
    {
        byte[] data = [0, 0, 0, 0, 0, .. Stream(Long, Nal(Idr, 3))];

        var summary = H264Stream.Inspect(data);

        Assert.True(summary.IsAnnexB);
        Assert.True(summary.HasIdr);
    }

    [Fact]
    public void Inspect_ReportsNotAnnexB_ForLengthPrefixedData()
    {
        // 4-byte big-endian length, then an IDR NAL unit: the AVCC layout, not a byte stream.
        byte[] data = [0, 0, 0, 5, Idr, 1, 2, 3, 4];

        var summary = H264Stream.Inspect(data);

        Assert.False(summary.IsAnnexB);
        Assert.False(summary.HasIdr);
    }

    [Fact]
    public void Inspect_ReportsNotAnnexB_WhenGarbageComesBeforeTheFirstStartCode()
    {
        byte[] data = [0x12, 0x34, .. Stream(Long, Nal(Idr, 3))];

        Assert.False(H264Stream.Inspect(data).IsAnnexB);
    }

    [Fact]
    public void Inspect_IgnoresNalUnitsWithTheForbiddenBitSet()
    {
        var data = Stream(Long, Nal(0xE5, 1, 2)); // forbidden_zero_bit set, type bits say IDR

        Assert.False(H264Stream.Inspect(data).HasIdr);
    }

    [Fact]
    public void Inspect_HandlesEmptyAndTinyInput()
    {
        Assert.Equal(default, H264Stream.Inspect([]));
        Assert.Equal(default, H264Stream.Inspect([0, 0, 1])); // a start code with no NAL header after it
        Assert.Equal(default, H264Stream.Inspect([0, 0]));
    }

    [Fact]
    public void WithParameterSets_PrependsTheHeader_ToAnIdrThatLacksThem()
    {
        var header = Stream(Long, Nal(Sps, 1), Nal(Pps, 2));
        var frame = Stream(Long, Nal(Idr, 9, 9));

        var result = H264Stream.WithParameterSets(frame, header);

        Assert.Equal([.. header, .. frame], result);
        var summary = H264Stream.Inspect(result);
        Assert.True(summary.HasIdr);
        Assert.True(summary.HasParameterSets);
    }

    [Fact]
    public void WithParameterSets_LeavesAnIdrThatAlreadyHasThem()
    {
        var header = Stream(Long, Nal(Sps, 1), Nal(Pps, 2));
        var frame = Stream(Long, Nal(Sps, 5), Nal(Pps, 6), Nal(Idr, 9));

        Assert.Same(frame, H264Stream.WithParameterSets(frame, header));
    }

    [Fact]
    public void WithParameterSets_LeavesInterFramesAlone()
    {
        var header = Stream(Long, Nal(Sps, 1), Nal(Pps, 2));
        var frame = Stream(Long, Nal(Slice, 9));

        Assert.Same(frame, H264Stream.WithParameterSets(frame, header));
    }

    [Fact]
    public void WithParameterSets_LeavesTheFrameAlone_WhenThereIsNoUsableHeader()
    {
        var frame = Stream(Long, Nal(Idr, 9));

        Assert.Same(frame, H264Stream.WithParameterSets(frame, null));
        Assert.Same(frame, H264Stream.WithParameterSets(frame, []));
        Assert.Same(frame, H264Stream.WithParameterSets(frame, Stream(Long, Nal(Sps, 1)))); // no PPS in it
    }

    [Fact]
    public void IsParameterSetBlock_NeedsBothAnSpsAndAPps()
    {
        Assert.True(H264Stream.IsParameterSetBlock(Stream(Long, Nal(Sps, 1), Nal(Pps, 2))));
        Assert.False(H264Stream.IsParameterSetBlock(Stream(Long, Nal(Sps, 1))));
        Assert.False(H264Stream.IsParameterSetBlock(Stream(Long, Nal(Pps, 2))));
        Assert.False(H264Stream.IsParameterSetBlock([]));
    }
}
