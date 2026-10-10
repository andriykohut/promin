using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Tests.Encoding;

public class H264ParameterSetsTests
{
    private static readonly byte[] Code = [0, 0, 0, 1];

    private static byte[] Nal(byte header, params byte[] payload) => [.. Code, header, .. payload];

    private static byte[] Sps(byte marker) => Nal(0x67, 0x42, 0x00, marker);

    private static byte[] Pps(byte marker) => Nal(0x68, 0xCE, marker);

    private static byte[] Idr => Nal(0x65, 0x88, 0x84, 0x21);

    private static byte[] Slice => Nal(0x41, 0x9A, 0x24);

    [Fact]
    public void AnIdrWithoutParameterSets_GetsTheOnesRememberedFromAnEarlierKeyframe()
    {
        var sets = new H264ParameterSets();
        sets.Observe([.. Sps(1), .. Pps(2), .. Idr]);

        var result = sets.Complete(Idr, out var added);

        Assert.True(added);
        Assert.Equal([.. Sps(1), .. Pps(2), .. Idr], result);
    }

    [Fact]
    public void AnIdrThatAlreadyHasThem_IsReturnedUnchanged()
    {
        var sets = new H264ParameterSets();
        sets.Observe([.. Sps(1), .. Pps(2), .. Idr]);
        byte[] frame = [.. Sps(5), .. Pps(6), .. Idr];

        var result = sets.Complete(frame, out var added);

        Assert.False(added);
        Assert.Same(frame, result);
    }

    [Fact]
    public void ParameterSetsThatChange_ReplaceTheRememberedOnes()
    {
        var sets = new H264ParameterSets();
        sets.Observe([.. Sps(1), .. Pps(2), .. Idr]);
        sets.Observe([.. Sps(7), .. Pps(8), .. Idr]); // the encoder changed its mind

        var result = sets.Complete(Idr, out _);

        Assert.Equal([.. Sps(7), .. Pps(8), .. Idr], result);
    }

    [Fact]
    public void ASpsAndAPpsCanArriveInSeparatePictures()
    {
        var sets = new H264ParameterSets();
        sets.Observe([.. Sps(1), .. Slice]);
        sets.Observe([.. Pps(2), .. Slice]);

        Assert.Equal([.. Sps(1), .. Pps(2), .. Idr], sets.Complete(Idr, out _));
    }

    [Fact]
    public void AnIdrThatHasASpsButNoPps_IsGivenBothInOrder()
    {
        var sets = new H264ParameterSets();
        sets.Observe([.. Sps(1), .. Pps(2), .. Idr]);
        byte[] frame = [.. Sps(1), .. Idr];

        // A PPS in front of its SPS would be out of order, so both go in front (the repeated SPS is identical).
        Assert.Equal([.. Sps(1), .. Pps(2), .. Sps(1), .. Idr], sets.Complete(frame, out var added));
        Assert.True(added);
    }

    [Fact]
    public void APictureThatIsNotAnIdr_IsNeverTouched()
    {
        var sets = new H264ParameterSets();
        sets.Observe([.. Sps(1), .. Pps(2), .. Idr]);

        var slice = Slice;

        Assert.Same(slice, sets.Complete(slice, out var added));
        Assert.False(added);
    }

    [Fact]
    public void ParameterSetsFromTheOutputType_AreUsedUntilTheStreamShowsItsOwn()
    {
        var sets = new H264ParameterSets();
        sets.Seed([.. Sps(1), .. Pps(2)]);

        Assert.Equal([.. Sps(1), .. Pps(2), .. Idr], sets.Complete(Idr, out _));

        sets.Observe([.. Sps(9), .. Pps(9), .. Idr]);
        Assert.Equal([.. Sps(9), .. Pps(9), .. Idr], sets.Complete(Idr, out _));
    }

    [Fact]
    public void ASeedThatIsNotParameterSets_IsIgnored()
    {
        var sets = new H264ParameterSets();
        sets.Seed([.. Slice]);

        Assert.Throws<InvalidOperationException>(() => sets.Complete(Idr, out _));
    }

    [Fact]
    public void AnIdrWithNothingRememberedAndNothingInIt_Throws_ExplainingWhy()
    {
        var sets = new H264ParameterSets();

        var ex = Assert.Throws<InvalidOperationException>(() => sets.Complete(Idr, out _));

        Assert.Contains("parameter sets", ex.Message);
    }

    [Fact]
    public void ThreeByteStartCodes_AreRememberedAndRewrittenWithFourByteOnes()
    {
        var sets = new H264ParameterSets();
        byte[] withShortCodes = [0, 0, 1, 0x67, 0x42, 0x00, 1, 0, 0, 1, 0x68, 0xCE, 2, 0, 0, 1, 0x65, 0x88];
        sets.Observe(withShortCodes);

        Assert.Equal([.. Sps(1), .. Pps(2), .. Idr], sets.Complete(Idr, out _));
    }

    [Fact]
    public void ObservingAFrameThatIsNotAnnexB_ChangesNothing()
    {
        var sets = new H264ParameterSets();
        sets.Observe([0, 0, 0, 5, 0x67, 1, 2, 3, 4]);

        Assert.Throws<InvalidOperationException>(() => sets.Complete(Idr, out _));
    }
}
