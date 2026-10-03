namespace Educast.MediaPipeline.Encoding;

/// <summary>NV12: a full-size luma plane, then one interleaved half-height chroma plane.</summary>
internal static class Nv12Layout
{
    internal static int Size(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// How many bytes a mapped NV12 surface with this row pitch must hold: the chroma plane follows the
    /// luma plane at the same pitch, and its last row needs only <paramref name="width"/> bytes.
    /// </summary>
    internal static int MinimumMappedLength(int rowPitch, int width, int height) =>
        rowPitch * (height + height / 2 - 1) + width;

    /// <summary>
    /// Copies a mapped NV12 surface (rows <paramref name="rowPitch"/> bytes apart) into
    /// <paramref name="destination"/> with no padding, which is the layout an encoder reads from memory.
    /// </summary>
    internal static void Pack(ReadOnlySpan<byte> mapped, int rowPitch, int width, int height, Span<byte> destination)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"NV12 needs a positive, even size; got {width}x{height}.");
        if (rowPitch < width)
            throw new ArgumentException($"The row pitch ({rowPitch}) is narrower than the width ({width}).", nameof(rowPitch));
        if (mapped.Length < MinimumMappedLength(rowPitch, width, height))
            throw new ArgumentException("The mapped surface is too short for this size and pitch.", nameof(mapped));
        if (destination.Length < Size(width, height))
            throw new ArgumentException("The destination is too short for this size.", nameof(destination));

        var rows = height + height / 2; // luma rows, then chroma rows
        for (var row = 0; row < rows; row++)
            mapped.Slice(row * rowPitch, width).CopyTo(destination.Slice(row * width, width));
    }
}
