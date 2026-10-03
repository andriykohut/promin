using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Educast.MediaPipeline.Decoding;

/// <summary>
/// One picture as the decoder transform produced it, before it is copied to the decoder's own output
/// texture: either a slice of a Direct3D texture the transform owns (hardware decoding; the transform's pool
/// slot stays taken until this is disposed) or NV12 bytes in memory (software decoding).
/// </summary>
internal sealed class DecodedPicture : IDisposable
{
    // Released in this order: the texture and views over the sample's buffer, then the buffer, then the sample.
    private readonly IDisposable?[] _owned;
    private bool _disposed;

    private DecodedPicture(PictureGeometry geometry, ID3D11Texture2D? texture, uint subresource, IMFMediaBuffer? memory, IDisposable?[] owned)
    {
        Geometry = geometry;
        Texture = texture;
        SubresourceIndex = subresource;
        Memory = memory;
        _owned = owned;
    }

    internal PictureGeometry Geometry { get; }

    /// <summary>Hardware decoding: the transform's texture (an array of surfaces); the picture is its slice <see cref="SubresourceIndex"/>. Null for a picture in memory.</summary>
    internal ID3D11Texture2D? Texture { get; }

    internal uint SubresourceIndex { get; }

    /// <summary>Software decoding: the buffer holding the NV12 bytes. Null for a picture on the GPU.</summary>
    internal IMFMediaBuffer? Memory { get; }

    internal bool IsOnGpu => Texture is not null;

    /// <summary>Takes over <paramref name="sample"/>, <paramref name="buffer"/> and <paramref name="dxgi"/> and <paramref name="texture"/>.</summary>
    internal static DecodedPicture OnGpu(
        PictureGeometry geometry, IMFSample sample, IMFMediaBuffer buffer, IMFDXGIBuffer dxgi, ID3D11Texture2D texture, uint subresource) =>
        new(geometry, texture, subresource, null, [texture, dxgi, buffer, sample]);

    /// <summary>Takes over <paramref name="sample"/> and <paramref name="buffer"/>.</summary>
    internal static DecodedPicture InMemory(PictureGeometry geometry, IMFSample sample, IMFMediaBuffer buffer) =>
        new(geometry, null, 0, buffer, [buffer, sample]);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var owned in _owned)
        {
            try
            {
                owned?.Dispose();
            }
            catch (Exception)
            {
                // The rest must still be released.
            }
        }
    }
}
