using System.Runtime.InteropServices;
using Educast.MediaPipeline.Encoding;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace Educast.MediaPipeline.Decoding;

/// <summary>Windows' H.264 decoder could not be set up. The message says why.</summary>
internal sealed class DecoderSetupException(string message) : InvalidOperationException(message);

/// <summary>
/// Windows' own H.264 decoder transform, activated and configured for low delay. It only does what the
/// transform does (hand bytes in, ask for a picture out, follow a format change); the accounting that decides
/// when to do which is in <see cref="DecoderPipeline{TPicture}"/>. Given a Direct3D device manager it decodes on
/// the GPU where the driver can and quietly falls back to software where it cannot, so what came out of each
/// call is looked at, not assumed. The transform is synchronous. Not thread-safe.
/// </summary>
internal sealed class DecoderTransform : IDecoderMft<DecodedPicture>
{
    // The H.264 decoder Windows ships: CLSID_CMSH264DecoderMFT (wmcodecdsp.h). Looked up by CLSID, so a
    // third-party decoder that happens to be installed is never the one used.
    private static readonly Guid MicrosoftDecoderClsid = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    internal const string DecoderName = "Microsoft H264 Video Decoder MFT";

    // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (mftransform.h): the transform allocates its own output samples.
    private const int OutputProvidesSamples = 0x100;

    // Input samples are stamped at a steady 30 a second: frames arrive when the screen changes, but a decoder
    // wants timestamps that only go forward.
    private const long SampleTicks = 10_000_000 / 30;

    private readonly IMFTransform _transform;
    private readonly CodecApi? _codec;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly List<string> _notes;
    private string? _formatNote;
    private string? _outputKindNote;
    private bool _providesSamples;
    private int _outputBufferSize;
    private int _outputAlignment;
    private long _inputCount;
    private bool _disposed;

    internal bool UsesDeviceManager { get; }

    /// <summary>Whether the decoder took the low-delay request, by either of the two ways it is made.</summary>
    internal bool LowLatencyAccepted { get; }

    /// <summary>True when pictures come out as Direct3D textures (GPU decoding), false when in memory; null before the first picture.</summary>
    internal bool? OutputIsOnGpu { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>What was configured, how the decoder answered, and (once known) the output it produces.</summary>
    internal string Notes => string.Join("; ", _notes.Concat(new[] { _formatNote, _outputKindNote }.OfType<string>()));

    private DecoderTransform(
        IMFTransform transform, CodecApi? codec, IMFDXGIDeviceManager? deviceManager, bool lowLatency, List<string> notes)
    {
        _transform = transform;
        _codec = codec;
        _deviceManager = deviceManager;
        UsesDeviceManager = deviceManager is not null;
        LowLatencyAccepted = lowLatency;
        _notes = notes;
    }

    // ---- opening ----------------------------------------------------------------------------

    /// <summary>
    /// Activates and configures the decoder. <paramref name="allowHardware"/> false does not give it the
    /// Direct3D device, which makes it decode in software (so that path can be exercised on a machine that
    /// has a GPU decoder). Throws <see cref="DecoderSetupException"/> when it cannot be set up.
    /// </summary>
    internal static DecoderTransform Open(ID3D11Device device, bool allowHardware)
    {
        var transform = CreateTransform();
        var notes = new List<string>();
        IMFAttributes? attributes = null;
        CodecApi? codec = null;
        IMFDXGIDeviceManager? deviceManager = null;
        DecoderTransform? result = null;
        try
        {
            attributes = TryGetAttributes(transform);

            // 1. This decoder is synchronous. One that says otherwise is not the one this code is written for.
            var isAsync = attributes is not null
                && attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncValue).Success
                && asyncValue != 0;
            if (isAsync)
                throw new DecoderSetupException("The H.264 decoder reports itself asynchronous (event-driven); only a synchronous one is supported.");
            notes.Add("synchronous");

            // 2. The Direct3D device, where the decoder takes one: it then decodes on the GPU if it can.
            if (!allowHardware)
            {
                notes.Add("Direct3D device: not given (software decoding requested)");
            }
            else if (attributes is null
                || !attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out var awareValue).Success
                || awareValue == 0)
            {
                notes.Add("Direct3D device: not given (the decoder is not Direct3D 11 aware)");
            }
            else
            {
                deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                deviceManager.ResetDevice(device).CheckError();
                try
                {
                    transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)deviceManager.NativePointer);
                    notes.Add("Direct3D device: accepted");
                }
                catch (SharpGenException ex)
                {
                    deviceManager.Dispose();
                    deviceManager = null;
                    notes.Add($"Direct3D device: refused, software decoding (0x{ex.ResultCode.Code:X8})");
                }
            }

            // 3. Low delay and no reordering, before the types are set. Asked for in both ways the decoder knows
            // (the MF_LOW_LATENCY attribute and ICodecAPI's AVLowLatencyMode share one key); the answer to each is recorded.
            var attributeAccepted = attributes is not null && attributes.Set(CodecApi.LowLatencyMode, 1u).Success;
            codec = CodecApi.TryCreate(transform);
            var codecOutcome = "no ICodecAPI";
            var codecAccepted = false;
            if (codec is not null)
            {
                if (!codec.IsSupported(CodecApi.LowLatencyMode))
                {
                    codecOutcome = "not supported";
                }
                else
                {
                    var set = codec.SetBool(CodecApi.LowLatencyMode, true);
                    codecAccepted = set.Success;
                    codecOutcome = codecAccepted ? "set" : $"refused (0x{set.Code:X8})";
                }
            }
            var lowLatency = attributeAccepted || codecAccepted;
            notes.Add($"low latency: {(lowLatency ? "accepted" : "NOT accepted")} (MF_LOW_LATENCY {(attributeAccepted ? "set" : "refused")}, ICodecAPI AVLowLatencyMode {codecOutcome})");

            result = new DecoderTransform(transform, codec, deviceManager, lowLatency, notes);
            result.SetInputType();

            // The size is not known until the stream says it, and the decoder may not offer an output type
            // until it has seen an SPS; in that case the type is chosen when the first picture is asked for.
            result.NegotiateOutputAtOpen();

            result.SendMessage(TMessageType.MessageNotifyBeginStreaming);
            result.SendMessage(TMessageType.MessageNotifyStartOfStream);
            return result;
        }
        catch
        {
            if (result is not null)
            {
                result.Dispose(); // it owns everything created so far
            }
            else
            {
                Guard(() => codec?.Dispose());
                Guard(transform.Dispose);
                Guard(() => deviceManager?.Dispose());
            }
            throw;
        }
        finally
        {
            attributes?.Dispose();
        }
    }

    private static IMFTransform CreateTransform()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(MicrosoftDecoderClsid, throwOnError: false);
            instance = type is null ? null : Activator.CreateInstance(type);
            if (instance is null)
                throw new DecoderSetupException("Windows' H.264 decoder is not installed (on N editions of Windows it comes with the Media Feature Pack).");
            return ComObject.As<IMFTransform>(instance);
        }
        catch (COMException ex)
        {
            throw new DecoderSetupException($"Windows' H.264 decoder could not be created (0x{ex.HResult:X8}); on N editions of Windows it comes with the Media Feature Pack.");
        }
        finally
        {
            if (instance is not null)
                Marshal.ReleaseComObject(instance);
        }
    }

    private static IMFAttributes? TryGetAttributes(IMFTransform transform)
    {
        try
        {
            return transform.Attributes;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private void SetInputType()
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.MixedInterlaceOrProgressive).CheckError();
        try
        {
            _transform.SetInputType(0, type, 0);
        }
        catch (SharpGenException ex)
        {
            throw new DecoderSetupException($"The H.264 decoder refused an H.264 input type (0x{ex.ResultCode.Code:X8}).");
        }
        _notes.Add("input: H.264 Annex B, size taken from the stream");
    }

    private void NegotiateOutputAtOpen()
    {
        if (TryNegotiateOutput(out var error))
            _notes.Add("output: NV12 chosen at open");
        else
            _notes.Add($"output: NV12 to be chosen once the stream has started ({error})");
    }

    // Chooses an NV12 output type from those the decoder offers now (the offer depends on the stream it has
    // seen). NV12 is what stays on the GPU and what the display step converts.
    private bool TryNegotiateOutput(out string? error)
    {
        error = null;
        for (var i = 0; i < 64; i++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException ex)
            {
                error ??= $"the decoder offers no more output types (0x{ex.ResultCode.Code:X8})";
                break; // MF_E_NO_MORE_TYPES, or no types yet
            }

            using (type)
            {
                if (!type.GetGUID(MediaTypeAttributeKeys.Subtype, out var subtype).Success || subtype != VideoFormatGuids.NV12)
                    continue;

                try
                {
                    _transform.SetOutputType(0, type, 0);
                    ReadOutputStreamInfo();
                    error = null;
                    return true;
                }
                catch (SharpGenException ex)
                {
                    error = $"the decoder refused its own NV12 output type (0x{ex.ResultCode.Code:X8})";
                }
            }
        }

        error ??= "the decoder offers no NV12 output type";
        return false;
    }

    private void ReadOutputStreamInfo()
    {
        try
        {
            var info = _transform.GetOutputStreamInfo(0);
            _providesSamples = (info.Flags & OutputProvidesSamples) != 0;
            _outputBufferSize = info.Size;
            _outputAlignment = info.Alignment;
        }
        catch (SharpGenException)
        {
            _providesSamples = false;
            _outputBufferSize = 0;
            _outputAlignment = 0;
        }
    }

    /// <summary>The geometry of the output type in force now, or null when it cannot be read (no type yet, or a type that makes no sense).</summary>
    private PictureGeometry? ReadGeometry()
    {
        try
        {
            using var current = _transform.GetOutputCurrentType(0);
            if (!current.GetUInt64(MediaTypeAttributeKeys.FrameSize, out var frameSize).Success)
                return null;

            byte[]? aperture = null;
            if (current.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out var apertureSize).Success && apertureSize > 0)
                aperture = current.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);

            var stride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var strideValue).Success ? strideValue : 0u;
            return OutputPictureSize.TryChoose(frameSize, aperture, stride, out var geometry) ? geometry : null;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private void SendMessage(TMessageType message)
    {
        try
        {
            _transform.ProcessMessage(message, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode == Result.NotImplemented)
        {
            // Not every transform implements every notification.
        }
    }

    // ---- running (IDecoderMft) ----------------------------------------------------------------

    public DecoderInputResult ProcessInput(byte[] data, bool isKeyframe, bool discontinuity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var sample = MediaFactory.MFCreateSample();
            using (var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length))
            {
                buffer.Lock(out var destination, out _, out _);
                try
                {
                    Marshal.Copy(data, 0, destination, data.Length);
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.CurrentLength = data.Length;
                sample.AddBuffer(buffer);
            }

            sample.SampleTime = _inputCount * SampleTicks;
            sample.SampleDuration = SampleTicks;
            if (isKeyframe)
                sample.Set(SampleAttributeKeys.CleanPoint, 1u);
            if (discontinuity)
                sample.Set(SampleAttributeKeys.Discontinuity, 1u);

            _transform.ProcessInput(0, sample, 0);
            _inputCount++;
            return DecoderInputResult.Accepted;
        }
        catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
        {
            return DecoderInputResult.NotAccepting;
        }
        catch (SharpGenException ex)
        {
            LastError = Describe("the decoder refused the input", ex);
            return DecoderInputResult.Rejected;
        }
    }

    public DecoderOutputResult ProcessOutput(out DecodedPicture? picture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        picture = null;

        IMFSample? lent = null;
        IMFSample? returned = null;
        var pictureOwnsSample = false;
        try
        {
            var buffer = new OutputDataBuffer { StreamID = 0 };
            if (!_providesSamples)
            {
                // A transform that does not allocate its own output samples needs one lent to it. Each
                // picture gets a fresh one: the picture is handed on and read later, so a reused sample
                // would be overwritten by the next output.
                lent = CreateOutputSample();
                // ProcessOutput hands back a second wrapper around the sample we gave it, without taking a
                // reference for it; disposing that wrapper releases one. This keeps the count right.
                lent.AddRef();
                buffer.Sample = lent;
            }

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            returned = buffer.Sample;
            buffer.Events?.Dispose();

            if (result == ResultCode.TransformNeedMoreInput)
                return DecoderOutputResult.NeedMoreInput;

            // A new SPS (the first one, or one with a new size) changes the output format; so does a type that
            // was never set. Take up the format the decoder offers now and ask again.
            if (result == ResultCode.TransformStreamChange || result == ResultCode.TransformTypeNotSet)
                return Renegotiate();

            if (result.Failure)
            {
                LastError = $"the decoder failed to produce a picture (0x{result.Code:X8})";
                return DecoderOutputResult.Failed;
            }

            var sample = lent ?? returned;
            if (sample is null)
                return DecoderOutputResult.NeedMoreInput;

            picture = BuildPicture(sample, out var error);
            if (picture is null)
            {
                LastError = error;
                return DecoderOutputResult.Failed;
            }
            pictureOwnsSample = true;
            return DecoderOutputResult.Produced;
        }
        catch (SharpGenException ex)
        {
            LastError = Describe("the decoder failed to produce a picture", ex);
            return DecoderOutputResult.Failed;
        }
        finally
        {
            if (lent is not null)
            {
                // The extra wrapper holds the reference taken by AddRef above.
                if (ReferenceEquals(returned, lent))
                    lent.Release();
                else
                    returned?.Dispose();
                if (!pictureOwnsSample)
                    lent.Dispose();
            }
            else if (!pictureOwnsSample)
            {
                returned?.Dispose();
            }
        }
    }

    // Takes over the sample on success (the picture releases it); the caller releases it otherwise.
    private DecodedPicture? BuildPicture(IMFSample sample, out string? error)
    {
        error = null;
        var geometry = ReadGeometry();
        if (geometry is not { } known)
        {
            error = "the decoder's output type has no usable size";
            return null;
        }

        if (sample.BufferCount < 1)
        {
            error = "the decoder produced a picture with no buffer";
            return null;
        }

        IMFMediaBuffer? first = sample.GetBufferByIndex(0);
        IMFDXGIBuffer? dxgi = null;
        try
        {
            dxgi = first.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgi is not null)
            {
                var pointer = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
                if (pointer == 0)
                {
                    error = "the decoder's picture has no texture";
                    return null;
                }

                // The pointer carries a reference of its own, taken over by the wrapper.
                var texture = new ID3D11Texture2D(pointer);
                var picture = DecodedPicture.OnGpu(known, sample, first, dxgi, texture, dxgi.SubresourceIndex);
                dxgi = null;
                first = null;
                NoteOutputKind(onGpu: true);
                return picture;
            }

            first.Dispose();
            first = null;
            var contiguous = sample.ConvertToContiguousBuffer();
            var inMemory = DecodedPicture.InMemory(known, sample, contiguous);
            NoteOutputKind(onGpu: false);
            return inMemory;
        }
        finally
        {
            dxgi?.Dispose();
            first?.Dispose();
        }
    }

    private void NoteOutputKind(bool onGpu)
    {
        if (OutputIsOnGpu == onGpu)
            return;
        OutputIsOnGpu = onGpu;
        _outputKindNote = onGpu
            ? "decoding: hardware accelerated (pictures come out as Direct3D textures)"
            : "decoding: software (pictures come out in memory and are uploaded to the GPU)";
    }

    // The sample a transform that does not provide its own is lent. A fresh one each time.
    private IMFSample CreateOutputSample()
    {
        var size = Math.Max(_outputBufferSize, 1);
        var sample = MediaFactory.MFCreateSample();
        try
        {
            // The alignment the transform reports is a byte count; the aligned-buffer call takes it minus one.
            using var buffer = _outputAlignment > 1
                ? MediaFactory.MFCreateAlignedMemoryBuffer(size, _outputAlignment - 1)
                : MediaFactory.MFCreateMemoryBuffer(size);
            sample.AddBuffer(buffer);
        }
        catch
        {
            sample.Dispose();
            throw;
        }
        return sample;
    }

    private DecoderOutputResult Renegotiate()
    {
        if (!TryNegotiateOutput(out var error))
        {
            LastError = $"the decoder changed its output format and no NV12 type could be taken up: {error}";
            return DecoderOutputResult.Failed;
        }

        var geometry = ReadGeometry();
        _formatNote = geometry is { } g
            ? $"output type: NV12 surface {g.CodedWidth}x{g.CodedHeight}, picture {g.PictureWidth}x{g.PictureHeight} at ({g.OriginX},{g.OriginY}), stride {g.Stride}"
            : "output type: NV12, size not readable";
        return DecoderOutputResult.FormatChanged;
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
            SendMessage(TMessageType.MessageNotifyStartOfStream);
        }
        catch (SharpGenException ex)
        {
            LastError = Describe("the decoder could not be flushed", ex);
        }
    }

    private static string Describe(string what, SharpGenException ex) => $"{what} (0x{ex.ResultCode.Code:X8})";

    // Disposal must run to the end: one step failing must not skip the releases after it.
    private static void Guard(Action step)
    {
        try
        {
            step();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Politely, and without caring whether a transform that is already unwell answers. Every step runs
        // whether or not the one before it threw.
        Guard(() => SendMessage(TMessageType.MessageNotifyEndOfStream));
        Guard(() => SendMessage(TMessageType.MessageNotifyEndStreaming));
        Guard(() => SendMessage(TMessageType.MessageCommandFlush));
        if (_deviceManager is not null)
            Guard(() => _transform.ProcessMessage(TMessageType.MessageSetD3DManager, 0)); // let go of the device
        Guard(() => _codec?.Dispose());
        Guard(_transform.Dispose);
        Guard(() => _deviceManager?.Dispose()); // after the transform that was using it
    }
}
