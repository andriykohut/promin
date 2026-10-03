using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

// The picture size a stream announces in its sequence parameter set, so a test can check what was encoded.
public class H264SpsSizeTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]  // 1088 coded, 8 rows cropped
    [InlineData(1280, 718)]   // 720 coded, 2 rows cropped
    [InlineData(606, 1080)]   // 608 coded, 2 columns cropped
    [InlineData(1920, 802)]
    [InlineData(1366, 768)]
    [InlineData(640, 360)]
    public void Baseline_ReadsTheSizeFromTheFrame(int width, int height)
    {
        var frame = Frame(new Sps(Profile: 66, width, height));

        Assert.True(H264Stream.TryReadFrameSize(frame, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    public void HighProfile_ReadsTheSizeToo(int width, int height)
    {
        var frame = Frame(new Sps(Profile: 100, width, height));

        Assert.True(H264Stream.TryReadFrameSize(frame, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public void PictureOrderCountType0_IsSkippedCorrectly()
    {
        var frame = Frame(new Sps(Profile: 66, 1280, 720, PocType: 0));

        Assert.True(H264Stream.TryReadFrameSize(frame, out var w, out var h));
        Assert.Equal((1280, 720), (w, h));
    }

    [Fact]
    public void ALargeSquareSize_IsStillRead_WithEmulationPreventionBytesWhereTheWriterNeededThem()
    {
        // The writer inserts 0x03 wherever two zero bytes would be followed by 0..3, as an encoder does.
        var frame = Frame(new Sps(Profile: 66, 1536, 1536));

        Assert.True(H264Stream.TryReadFrameSize(frame, out var w, out var h));
        Assert.Equal((1536, 1536), (w, h));
    }

    [Fact]
    public void ReadsTheSpsThatIsNotTheFirstNalUnit()
    {
        var sps = Frame(new Sps(Profile: 66, 800, 600));
        byte[] aud = [0, 0, 0, 1, 0x09, 0xF0];

        Assert.True(H264Stream.TryReadFrameSize([.. aud, .. sps], out var w, out var h));
        Assert.Equal((800, 600), (w, h));
    }

    [Fact]
    public void AFrameWithoutASps_HasNoSize()
    {
        Assert.False(H264Stream.TryReadFrameSize([0, 0, 0, 1, 0x65, 0x88, 0x84], out _, out _));
        Assert.False(H264Stream.TryReadFrameSize([], out _, out _));
    }

    [Fact]
    public void ATruncatedSps_HasNoSize_AndDoesNotThrow()
    {
        Assert.False(H264Stream.TryReadFrameSize([0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1F], out _, out _));
    }

    private static byte[] Frame(Sps sps) => [0, 0, 0, 1, .. sps.Build(), 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 0, 1, 0x65, 0x88, 0x84];

    // Builds a sequence parameter set the way the standard lays it out, so the reader is tested against fields
    // chosen here rather than against bytes copied from the reader's own idea of them.
    private sealed record Sps(int Profile, int Width, int Height, int PocType = 2)
    {
        public byte[] Build()
        {
            var bits = new List<bool>();
            void Bits(uint value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
            void Ue(uint value)
            {
                var code = value + 1;
                var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
                Bits(0, length - 1);
                Bits(code, length);
            }

            Bits((uint)Profile, 8);
            Bits(0xC0, 8);   // constraint flags
            Bits(31, 8);     // level_idc
            Ue(0);           // seq_parameter_set_id
            if (Profile is 100 or 110 or 122 or 244)
            {
                Ue(1);       // chroma_format_idc: 4:2:0
                Ue(0);       // bit_depth_luma_minus8
                Ue(0);       // bit_depth_chroma_minus8
                Bits(0, 1);  // qpprime_y_zero_transform_bypass_flag
                Bits(0, 1);  // seq_scaling_matrix_present_flag
            }
            Ue(0);           // log2_max_frame_num_minus4
            Ue((uint)PocType);
            if (PocType == 0)
                Ue(4);       // log2_max_pic_order_cnt_lsb_minus4
            Ue(1);           // max_num_ref_frames
            Bits(0, 1);      // gaps_in_frame_num_value_allowed_flag
            var widthInMbs = (Width + 15) / 16;
            var heightInMbs = (Height + 15) / 16;
            Ue((uint)(widthInMbs - 1));
            Ue((uint)(heightInMbs - 1));
            Bits(1, 1);      // frame_mbs_only_flag
            Bits(1, 1);      // direct_8x8_inference_flag
            var cropRight = (widthInMbs * 16 - Width) / 2;
            var cropBottom = (heightInMbs * 16 - Height) / 2;
            if (cropRight > 0 || cropBottom > 0)
            {
                Bits(1, 1);  // frame_cropping_flag
                Ue(0);
                Ue((uint)cropRight);
                Ue(0);
                Ue((uint)cropBottom);
            }
            else
            {
                Bits(0, 1);
            }
            Bits(0, 1);      // vui_parameters_present_flag
            Bits(1, 1);      // rbsp_stop_one_bit
            while (bits.Count % 8 != 0)
                bits.Add(false);

            var rbsp = new List<byte>();
            for (var i = 0; i < bits.Count; i += 8)
            {
                byte b = 0;
                for (var j = 0; j < 8; j++)
                    b = (byte)((b << 1) | (bits[i + j] ? 1 : 0));
                rbsp.Add(b);
            }

            var nal = new List<byte> { 0x67 };
            var zeros = 0;
            foreach (var b in rbsp)
            {
                if (zeros >= 2 && b <= 3)
                {
                    nal.Add(3);
                    zeros = 0;
                }
                nal.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }
            return [.. nal];
        }
    }
}
