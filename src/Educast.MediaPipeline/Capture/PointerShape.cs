namespace Educast.MediaPipeline.Capture;

/// <summary>The three pixel layouts Desktop Duplication reports for a mouse pointer.</summary>
public enum PointerShapeKind
{
    /// <summary>1 bit per pixel: an AND mask in the top half of the buffer, an XOR mask in the bottom half.</summary>
    Monochrome,

    /// <summary>32-bit BGRA with straight (non-premultiplied) alpha.</summary>
    Color,

    /// <summary>32-bit BGRA where alpha is a mask: 0x00 replaces the destination, 0xFF XORs it.</summary>
    MaskedColor,
}

/// <summary>
/// A mouse pointer image. <paramref name="Height"/> is the pointer's own height in pixels. For
/// <see cref="PointerShapeKind.Monochrome"/> the buffer is therefore <c>2 * Height</c> rows of
/// <paramref name="Stride"/> bytes (AND mask, then XOR mask); for the colour kinds it is
/// <c>Height</c> rows of 32-bit BGRA. Note that Desktop Duplication itself reports twice the
/// pointer height for monochrome shapes: whoever builds this from a DXGI shape halves it.
/// </summary>
public sealed record PointerShape(PointerShapeKind Kind, int Width, int Height, int Stride, byte[] Pixels);
