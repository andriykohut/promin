namespace Promin.MediaPipeline.Encoding;

/// <summary>Reads just enough of an H.264 Annex B byte stream to tell what kind of picture it holds.</summary>
internal static class H264Stream
{
    internal const int NalIdrSlice = 5;
    internal const int NalSps = 7;
    internal const int NalPps = 8;

    internal readonly record struct Summary(bool IsAnnexB, bool HasIdr, bool HasSps, bool HasPps)
    {
        public bool HasParameterSets => HasSps && HasPps;
    }

    /// <summary>
    /// One NAL unit: <see cref="Header"/> is the index of its header byte, <see cref="End"/> the index just
    /// after its last byte (zero bytes that belong to the next start code are not counted).
    /// </summary>
    internal readonly record struct NalUnit(int Header, int End, int Type)
    {
        public int Length => End - Header;
    }

    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    /// <summary>
    /// The NAL units of an Annex B stream, in order. Empty when <paramref name="data"/> is not Annex B (no
    /// start code, or something other than zero bytes before the first one). A unit whose forbidden bit is
    /// set is skipped.
    /// </summary>
    internal static List<NalUnit> NalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<NalUnit>();
        var starts = new List<int>(); // index of each start code's "00 00 01"

        var position = 0;
        while (position < data.Length)
        {
            var found = data[position..].IndexOf(StartCode);
            if (found < 0)
                break;

            var start = position + found;
            var header = start + StartCode.Length;
            if (header >= data.Length)
                break;

            if (starts.Count == 0 && data[..start].IndexOfAnyExcept((byte)0) >= 0)
                return units; // garbage before the first start code: not Annex B

            starts.Add(start);
            position = header + 1;
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var header = starts[i] + StartCode.Length;
            var end = i + 1 < starts.Count ? starts[i + 1] : data.Length;
            while (end > header + 1 && data[end - 1] == 0)
                end--; // zero bytes in front of the next start code are not part of this unit

            var nal = data[header];
            if ((nal & 0x80) == 0) // forbidden_zero_bit must be 0
                units.Add(new NalUnit(header, end, nal & 0x1F));
        }
        return units;
    }

    /// <summary>
    /// Scans the NAL units in <paramref name="data"/>. Not Annex B (no start code, or something other
    /// than zero bytes before the first one) is reported as such, with nothing found.
    /// </summary>
    internal static Summary Inspect(ReadOnlySpan<byte> data)
    {
        var units = NalUnits(data);
        bool hasIdr = false, hasSps = false, hasPps = false;
        foreach (var unit in units)
        {
            switch (unit.Type)
            {
                case NalIdrSlice: hasIdr = true; break;
                case NalSps: hasSps = true; break;
                case NalPps: hasPps = true; break;
            }
        }

        // "Annex B" means a start code was found where only zero bytes precede it, whatever the units were.
        var isAnnexB = units.Count > 0 || HasLeadingStartCode(data);
        return new Summary(isAnnexB, hasIdr, hasSps, hasPps);
    }

    private static bool HasLeadingStartCode(ReadOnlySpan<byte> data)
    {
        var found = data.IndexOf(StartCode);
        return found >= 0 && found + StartCode.Length < data.Length && data[..found].IndexOfAnyExcept((byte)0) < 0;
    }

    /// <summary>True when <paramref name="block"/> holds both a sequence and a picture parameter set.</summary>
    internal static bool IsParameterSetBlock(ReadOnlySpan<byte> block) => Inspect(block).HasParameterSets;

    /// <summary>
    /// The picture size the first sequence parameter set in <paramref name="frame"/> announces, after
    /// cropping. False when there is none or it cannot be read.
    /// </summary>
    internal static bool TryReadFrameSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        foreach (var unit in NalUnits(frame))
        {
            if (unit.Type != NalSps)
                continue;

            var rbsp = RemoveEmulationPrevention(frame[(unit.Header + 1)..unit.End]);
            return TryParseSpsSize(rbsp, out width, out height);
        }
        return false;
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> nalPayload)
    {
        var result = new List<byte>(nalPayload.Length);
        var zeros = 0;
        foreach (var b in nalPayload)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0; // the 0x03 only keeps 00 00 0x from looking like a start code
                continue;
            }
            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. result];
    }

    private static bool TryParseSpsSize(byte[] rbsp, out int width, out int height)
    {
        width = height = 0;
        var bits = new BitReader(rbsp);

        var profile = bits.Bits(8);
        bits.Bits(16); // constraint flags and level_idc
        bits.Ue();     // seq_parameter_set_id

        uint chromaFormat = 1;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = bits.Ue();
            if (chromaFormat == 3)
                bits.Bits(1); // separate_colour_plane_flag
            bits.Ue();        // bit_depth_luma_minus8
            bits.Ue();        // bit_depth_chroma_minus8
            bits.Bits(1);     // qpprime_y_zero_transform_bypass_flag
            if (bits.Bits(1) == 1) // seq_scaling_matrix_present_flag
            {
                var lists = chromaFormat != 3 ? 8 : 12;
                for (var i = 0; i < lists; i++)
                {
                    if (bits.Bits(1) == 1)
                        SkipScalingList(ref bits, i < 6 ? 16 : 64);
                }
            }
        }

        bits.Ue(); // log2_max_frame_num_minus4
        var pocType = bits.Ue();
        if (pocType == 0)
        {
            bits.Ue(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (pocType == 1)
        {
            bits.Bits(1); // delta_pic_order_always_zero_flag
            bits.Ue();    // offset_for_non_ref_pic
            bits.Ue();    // offset_for_top_to_bottom_field
            var cycle = bits.Ue();
            for (var i = 0; i < cycle && !bits.Failed; i++)
                bits.Ue(); // offset_for_ref_frame
        }

        bits.Ue();    // max_num_ref_frames
        bits.Bits(1); // gaps_in_frame_num_value_allowed_flag
        var widthInMbs = bits.Ue() + 1;
        var heightInMapUnits = bits.Ue() + 1;
        var frameMbsOnly = bits.Bits(1);
        if (frameMbsOnly == 0)
            bits.Bits(1); // mb_adaptive_frame_field_flag
        bits.Bits(1);     // direct_8x8_inference_flag

        uint left = 0, right = 0, top = 0, bottom = 0;
        if (bits.Bits(1) == 1) // frame_cropping_flag
        {
            left = bits.Ue();
            right = bits.Ue();
            top = bits.Ue();
            bottom = bits.Ue();
        }

        if (bits.Failed || widthInMbs > 1000 || heightInMapUnits > 1000)
            return false;

        var cropUnitX = chromaFormat is 1 or 2 ? 2u : 1u;
        var cropUnitY = (chromaFormat == 1 ? 2u : 1u) * (2 - frameMbsOnly);
        var w = (long)widthInMbs * 16 - cropUnitX * (left + right);
        var h = (long)(2 - frameMbsOnly) * heightInMapUnits * 16 - cropUnitY * (top + bottom);
        if (w <= 0 || h <= 0)
            return false;

        width = (int)w;
        height = (int)h;
        return true;
    }

    private static void SkipScalingList(ref BitReader bits, int size)
    {
        long last = 8, next = 8;
        for (var j = 0; j < size && !bits.Failed; j++)
        {
            if (next != 0)
            {
                var delta = bits.Se();
                next = (last + delta + 256) % 256;
            }
            last = next == 0 ? last : next;
        }
    }

    // Reads the bit fields of a parameter set (fixed-width and Exp-Golomb). Reading past the end sets
    // Failed and yields zeros, so a truncated unit is reported rather than thrown about.
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public bool Failed { get; private set; }

        public uint Bits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
                value = (value << 1) | Bit();
            return value;
        }

        public uint Ue()
        {
            var zeros = 0;
            while (!Failed && Bit() == 0)
            {
                if (++zeros > 31)
                {
                    Failed = true;
                    return 0;
                }
            }
            if (Failed)
                return 0;
            return (uint)((1UL << zeros) - 1 + Bits(zeros));
        }

        public long Se()
        {
            var k = Ue();
            return (k & 1) == 1 ? (k + 1) / 2 : -(long)(k / 2);
        }

        private uint Bit()
        {
            var index = _position >> 3;
            if (index >= _data.Length)
            {
                Failed = true;
                return 0;
            }
            var bit = (uint)((_data[index] >> (7 - (_position & 7))) & 1);
            _position++;
            return bit;
        }
    }
}
