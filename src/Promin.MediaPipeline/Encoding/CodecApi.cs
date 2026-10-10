using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace Promin.MediaPipeline.Encoding;

/// <summary>
/// The encoder's ICodecAPI interface (rate control, low latency, forced keyframes). Vortice does not
/// wrap it, so it is called through the interface's vtable. Slot numbers are from strmif.h:
/// IUnknown takes 0..2, then IsSupported 3, IsModifiable 4, GetParameterRange 5, GetParameterValues 6,
/// GetDefaultValue 7, GetValue 8, SetValue 9.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid InterfaceId = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    // codecapi.h (Windows SDK 10.0.22621.0).
    internal static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid DefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    internal static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid CommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    internal static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    // eAVEncCommonRateControlMode
    internal const uint RateControlCbr = 0;
    internal const uint RateControlLowDelayVbr = 4;

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;
    private const ulong VariantTrue = 0xFFFF; // VARIANT_TRUE is -1 as a 16-bit VARIANT_BOOL

    // A VARIANT is 24 bytes on 64-bit Windows (the union is as large as VT_RECORD's two pointers).
    // vt sits at offset 0 and the value at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct NativeVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public ulong Value;
    }

    private nint _pointer;

    private CodecApi(nint pointer) => _pointer = pointer;

    /// <summary>The ICodecAPI of <paramref name="transform"/>, or null when it has none.</summary>
    internal static CodecApi? TryCreate(ComObject transform)
    {
        var result = transform.QueryInterface(InterfaceId, out var pointer);
        return result.Success && pointer != 0 ? new CodecApi(pointer) : null;
    }

    /// <summary>True when the encoder supports this property. S_OK means yes; S_FALSE means no (and counts as a success code).</summary>
    internal bool IsSupported(Guid property)
    {
        ThrowIfDisposed();
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(3))(_pointer, &property);
        return hr == 0;
    }

    internal Result SetUInt32(Guid property, uint value) => SetValue(property, VtUi4, value);

    internal Result SetBool(Guid property, bool value) => SetValue(property, VtBool, value ? VariantTrue : 0);

    private Result SetValue(Guid property, ushort type, ulong value)
    {
        ThrowIfDisposed();
        var variant = new NativeVariant { Type = type, Value = value };
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, NativeVariant*, int>)Slot(9))(_pointer, &property, &variant);
        return new Result(hr);
    }

    private void* Slot(int index) => ((void**)*(void***)_pointer)[index];

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_pointer == 0, this);

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
            Marshal.Release(pointer);
    }
}
