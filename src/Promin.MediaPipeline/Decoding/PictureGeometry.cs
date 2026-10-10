using System.Buffers.Binary;

namespace Promin.MediaPipeline.Decoding;

/// <summary>
/// The size of a decoded surface and of the picture inside it. H.264 codes in 16-pixel blocks, so the
/// surface (<see cref="CodedWidth"/> by <see cref="CodedHeight"/>) can be larger than the picture
/// (<see cref="PictureWidth"/> by <see cref="PictureHeight"/>, whose top-left corner is at
/// <see cref="OriginX"/>, <see cref="OriginY"/>). <see cref="Stride"/> is the length of a row of the surface
/// in bytes when it is in memory.
/// </summary>
internal readonly record struct PictureGeometry(
    int CodedWidth, int CodedHeight, int PictureWidth, int PictureHeight, int OriginX, int OriginY, int Stride);

/// <summary>
/// Works out the picture's size from what a decoder's output media type says: the display aperture
/// (MF_MT_MINIMUM_DISPLAY_APERTURE) when it has a usable one, otherwise the frame size (MF_MT_FRAME_SIZE).
/// </summary>
internal static class OutputPictureSize
{
    // Larger than any display; keeps a garbled type from asking for an absurd surface.
    private const int MaxDimension = 16384;

    // sizeof(MFVideoArea): two MFOffset (WORD fract, short value) and a SIZE (two longs).
    private const int VideoAreaLength = 16;

    /// <summary>Splits a packed attribute (MF_MT_FRAME_SIZE and friends): the first number in the high half.</summary>
    internal static void Unpack(ulong packed, out int high, out int low)
    {
        high = unchecked((int)(packed >> 32));
        low = unchecked((int)(uint)packed);
    }

    /// <summary>
    /// Reads an <c>MFVideoArea</c>: the whole-pixel part of its offsets and its size. Fractional offsets are
    /// ignored. False when the blob is too short; the values are not judged here.
    /// </summary>
    internal static bool TryParseVideoArea(ReadOnlySpan<byte> blob, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (blob.Length < VideoAreaLength)
            return false;

        x = BinaryPrimitives.ReadInt16LittleEndian(blob[2..]);
        y = BinaryPrimitives.ReadInt16LittleEndian(blob[6..]);
        width = BinaryPrimitives.ReadInt32LittleEndian(blob[8..]);
        height = BinaryPrimitives.ReadInt32LittleEndian(blob[12..]);
        return true;
    }

    /// <summary>
    /// The geometry of an output type. <paramref name="frameSize"/> is its MF_MT_FRAME_SIZE;
    /// <paramref name="apertureBlob"/> its MF_MT_MINIMUM_DISPLAY_APERTURE (null when absent) and
    /// <paramref name="defaultStride"/> its MF_MT_DEFAULT_STRIDE (0 when absent). An aperture that cannot be
    /// right (empty, negative, outside the frame) is ignored. False when the frame size is not credible.
    /// </summary>
    internal static bool TryChoose(ulong frameSize, byte[]? apertureBlob, uint defaultStride, out PictureGeometry geometry)
    {
        geometry = default;
        Unpack(frameSize, out var codedWidth, out var codedHeight);
        if (codedWidth is <= 0 or > MaxDimension || codedHeight is <= 0 or > MaxDimension)
            return false;

        int originX = 0, originY = 0, width = codedWidth, height = codedHeight;
        if (apertureBlob is not null
            && TryParseVideoArea(apertureBlob, out var x, out var y, out var w, out var h)
            && x >= 0 && y >= 0 && w > 0 && h > 0
            && (long)x + w <= codedWidth && (long)y + h <= codedHeight)
        {
            (originX, originY, width, height) = (x, y, w, h);
        }

        // A stride narrower than a row of pixels (or one that is negative, a bottom-up surface) is not believed.
        var stride = unchecked((int)defaultStride);
        if (stride < codedWidth)
            stride = codedWidth;

        geometry = new PictureGeometry(codedWidth, codedHeight, width, height, originX, originY, stride);
        return true;
    }
}
