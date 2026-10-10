using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace Promin.MediaPipeline.Encoding;

/// <summary>A hardware encoder, or Windows' software one, could not be set up. The message says why.</summary>
internal sealed class EncoderSetupException(string message) : InvalidOperationException(message);

/// <summary>
/// One H.264 encoder Media Foundation transform, activated and configured for low delay and no frame
/// reordering. It only does what the transform does (hand a picture in, ask for one out, report events); the
/// accounting that decides when to do which, and how synchronous and event-driven transforms differ, is in
/// <see cref="EncoderPipeline{TInput}"/>. Hardware encoders are usually asynchronous; the software one is
/// synchronous. Not thread-safe.
/// </summary>
internal sealed class EncoderTransform : IEncoderMft<IMFSample>
{
    // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER (mfapi.h).
    private const uint EnumHardware = 0x00000004 | 0x00000040;

    // MF_EVENT_FLAG_NO_WAIT: GetEvent returns at once, with MF_E_NO_EVENTS_AVAILABLE when there is none.
    private const int EventNoWait = 1;
    private const int NoEventsAvailable = unchecked((int)0xC00D3E80);

    // IMFMediaEventGenerator::GetEvent is slot 3 (IUnknown takes 0 to 2).
    private const int GetEventSlot = 3;

    // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (mftransform.h): the transform allocates its own output samples.
    private const int OutputProvidesSamples = 0x100;

    // The software encoder Windows ships: CLSID_CMSH264EncoderMFT (wmcodecdsp.h).
    private static readonly Guid MicrosoftSoftwareEncoderClsid = new("6ca50344-051a-4ded-9779-a43305165e35");
    private const string MicrosoftSoftwareEncoderName = "Microsoft H264 Encoder MFT (software)";

    // Colour description written into the types (mfobjects.h): BT.709 primaries, transfer and matrix, limited range.
    private const uint ColorPrimariesBt709 = 2;
    private const uint ColorTransferBt709 = 5;
    private const uint ColorMatrixBt709 = 1;
    private const uint ColorRangeLimited = 2; // MFNominalRange_16_235
    private const uint H264ProfileBaseline = 66; // eAVEncH264VProfile_Base

    private static RegisterTypeInfo H264Output => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = VideoFormatGuids.H264,
    };

    // ---- state of the activated transform ---------------------------------------------------

    private readonly IMFTransform _transform;
    private readonly IMFActivate? _activate;
    private readonly IMFMediaEventGenerator? _events;
    private readonly CodecApi _codec;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly uint? _profile;
    private readonly List<string> _notes;
    private IReadOnlyList<(string Name, string Reason)> _rejected = [];
    private IMFSample? _ownOutputSample;
    private bool _providesSamples;
    private int _outputBufferSize;
    private bool _disposed;

    internal string Name { get; }
    internal bool IsHardware { get; }
    public bool IsAsynchronous => _events is not null;

    /// <summary>True when input samples are Direct3D textures; false when they are NV12 bytes in memory.</summary>
    internal bool UsesGpuInput { get; }

    /// <summary>The first SPS+PPS (Annex B) the transform published in its output type, or null.</summary>
    internal byte[]? ParameterSets { get; private set; }

    /// <summary>
    /// Why each hardware encoder that was passed over was passed over, then what was configured and what
    /// the transform said about each setting.
    /// </summary>
    internal string Notes => EncoderConfigurationText.Compose(_rejected, _notes);

    private EncoderTransform(
        IMFTransform transform, IMFActivate? activate, IMFMediaEventGenerator? events, CodecApi codec,
        IMFDXGIDeviceManager? deviceManager, bool gpuInput, bool hardware, string name, uint? profile, List<string> notes)
    {
        _transform = transform;
        _activate = activate;
        _events = events;
        _codec = codec;
        _deviceManager = deviceManager;
        UsesGpuInput = gpuInput;
        IsHardware = hardware;
        Name = name;
        _profile = profile;
        _notes = notes;
    }

    // ---- finding and opening an encoder ---------------------------------------------------

    /// <summary>True when Media Foundation lists at least one hardware H.264 encoder.</summary>
    internal static bool AnyHardwareEncoder()
    {
        var found = EnumerateHardwareEncoders();
        try
        {
            return found.Count > 0;
        }
        finally
        {
            foreach (var activate in found)
                activate.Dispose();
        }
    }

    /// <summary>
    /// Opens the first encoder that can be set up: the hardware ones (those on the device's own graphics
    /// vendor first), then Windows' software encoder (only that one when <paramref name="allowHardware"/> is
    /// false). Throws when none can, naming each failure. When one does open after others were passed over,
    /// its <see cref="Notes"/> say which and why.
    /// </summary>
    internal static EncoderTransform Open(ID3D11Device device, int width, int height, int fps, int bitsPerSecond, bool allowHardware = true)
    {
        var rejected = new List<(string Name, string Reason)>();
        var vendor = AdapterVendorId(device);

        var hardware = allowHardware ? EnumerateHardwareEncoders() : [];
        try
        {
            foreach (var activate in OrderByVendor(hardware, vendor))
            {
                var name = FriendlyName(activate) ?? "hardware H.264 encoder";
                try
                {
                    var transform = activate.ActivateObject<IMFTransform>();

                    // Configure takes over the transform and the activation object, on success and on failure:
                    // removing the latter from the list keeps the clean-up below from releasing it again.
                    hardware.Remove(activate);
                    var opened = Configure(transform, activate, hardware: true, name, device, width, height, fps, bitsPerSecond);
                    opened._rejected = rejected;
                    return opened;
                }
                catch (Exception ex) when (IsSetupFailure(ex))
                {
                    rejected.Add((name, ex.Message));
                }
            }
        }
        finally
        {
            foreach (var leftover in hardware)
                Guard(leftover.Dispose);
        }

        try
        {
            var transform = CreateSoftwareTransform();
            var opened = Configure(transform, null, hardware: false, MicrosoftSoftwareEncoderName, device, width, height, fps, bitsPerSecond);
            opened._rejected = rejected;
            return opened;
        }
        catch (Exception ex) when (IsSetupFailure(ex))
        {
            rejected.Add((MicrosoftSoftwareEncoderName, ex.Message));
        }

        throw new InvalidOperationException(
            "No H.264 encoder (hardware or software) could be set up on this machine. " +
            string.Join(" | ", rejected.Select(r => $"{r.Name}: {r.Reason}")));
    }

    private static bool IsSetupFailure(Exception ex) =>
        ex is SharpGenException or EncoderSetupException or COMException or NotSupportedException or DllNotFoundException;

    private static List<IMFActivate> EnumerateHardwareEncoders()
    {
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, EnumHardware, null, H264Output, out var array, out var count);

        var list = new List<IMFActivate>((int)count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (pointer != IntPtr.Zero)
                    list.Add(new IMFActivate(pointer)); // takes over the reference MFTEnumEx handed us
            }
        }
        finally
        {
            if (array != IntPtr.Zero)
                Marshal.FreeCoTaskMem(array);
        }
        return list;
    }

    private static IMFTransform CreateSoftwareTransform()
    {
        // By CLSID rather than by enumeration, so the one Windows ships is the one used and a
        // third-party software encoder that happens to be installed never is.
        var type = Type.GetTypeFromCLSID(MicrosoftSoftwareEncoderClsid, throwOnError: true)!;
        var instance = Activator.CreateInstance(type)
            ?? throw new EncoderSetupException("Windows' software H.264 encoder could not be created.");
        try
        {
            return ComObject.As<IMFTransform>(instance);
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    private static uint? AdapterVendorId(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            return adapter.Description.VendorId;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private static IEnumerable<IMFActivate> OrderByVendor(List<IMFActivate> activates, uint? vendorId)
    {
        if (vendorId is null)
            return activates.ToArray();

        var wanted = $"VEN_{vendorId.Value:X4}";
        // A hardware encoder on the graphics adapter the frames live on can read them in place.
        return activates
            .OrderBy(a => string.Equals(Vendor(a), wanted, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private static string? Vendor(IMFActivate activate) => TryGetString(activate, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute);

    private static string? FriendlyName(IMFActivate activate) => TryGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute);

    private static string? TryGetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

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

    // ---- configuring a transform ----------------------------------------------------------

    // Takes over the transform and the activation object: whatever happens, both are released.
    private static EncoderTransform Configure(
        IMFTransform transform, IMFActivate? activate, bool hardware, string name,
        ID3D11Device device, int width, int height, int fps, int bitsPerSecond)
    {
        var notes = new List<string>();
        IMFAttributes? attributes = null;
        IMFMediaEventGenerator? events = null;
        CodecApi? codec = null;
        IMFDXGIDeviceManager? deviceManager = null;
        EncoderTransform? result = null;
        try
        {
            // 1. Asynchronous transforms stay locked until told otherwise, and nothing else works first.
            attributes = TryGetAttributes(transform);
            var isAsync = attributes is not null
                && attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncValue).Success
                && asyncValue != 0;
            if (isAsync)
            {
                attributes!.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
                events = transform.QueryInterface<IMFMediaEventGenerator>();
                notes.Add("asynchronous (event-driven)");
            }
            else
            {
                notes.Add("synchronous");
            }

            // 2. Direct3D input, where the transform offers it. Otherwise frames are read back to memory.
            var gpuInput = false;
            if (hardware)
            {
                var d3dAware = attributes is not null
                    && attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out var awareValue).Success
                    && awareValue != 0;
                if (d3dAware)
                {
                    deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                    deviceManager.ResetDevice(device).CheckError();
                    try
                    {
                        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)deviceManager.NativePointer);
                        gpuInput = true;
                        notes.Add("input: Direct3D textures");
                    }
                    catch (SharpGenException ex)
                    {
                        deviceManager.Dispose();
                        deviceManager = null;
                        notes.Add($"input: memory (the transform refused the Direct3D device, 0x{ex.ResultCode.Code:X8})");
                    }
                }
                else
                {
                    notes.Add("input: memory (the transform is not Direct3D 11 aware)");
                }
            }
            else
            {
                notes.Add("input: memory");
            }

            // 3. ICodecAPI, for low delay, bitrate and forced keyframes.
            codec = CodecApi.TryCreate(transform)
                ?? throw new EncoderSetupException("The encoder has no ICodecAPI, so it cannot be told to produce a keyframe on demand.");
            var bFramesZero = ConfigureCodec(codec, (uint)bitsPerSecond, notes);

            // 4. Media types: output first, then input, as encoders expect.
            var profile = SetOutputType(transform, width, height, fps, (uint)bitsPerSecond, bFramesZero, notes);
            SetInputType(transform, width, height, fps, notes);

            result = new EncoderTransform(transform, activate, events, codec, deviceManager, gpuInput, hardware, name, profile, notes);
            result.ReadOutputStreamInfo();
            result.ParameterSets = result.ReadParameterSets();

            // 5. Start. Asynchronous transforms answer by asking for input.
            result.SendMessage(TMessageType.MessageNotifyBeginStreaming);
            result.SendMessage(TMessageType.MessageNotifyStartOfStream);

            // 6. Forced keyframes are not optional, the session depends on them. Ask for one now, before the
            // first picture (which is a keyframe anyway, so the request does no harm): an encoder that cannot
            // take the request is passed over. Asking "is it supported" is not the test, since some encoders
            // answer that badly and take the request all the same.
            var forced = codec.SetUInt32(CodecApi.ForceKeyFrame, 1);
            if (forced.Failure)
                throw new EncoderSetupException($"The encoder refused a forced-keyframe request (0x{forced.Code:X8}).");
            notes.Add("forced keyframes: accepted");

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
                Guard(() => events?.Dispose());
                Guard(() => activate?.ShutdownObject());
                Guard(transform.Dispose);
                Guard(() => activate?.Dispose());
                Guard(() => deviceManager?.Dispose());
            }
            throw;
        }
        finally
        {
            attributes?.Dispose();
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
            return null; // synchronous transforms may have none
        }
    }

    // Returns true when the transform accepted "no B-frames", which together with the baseline profile
    // below is what keeps pictures in display order.
    private static bool ConfigureCodec(CodecApi codec, uint bitrate, List<string> notes)
    {
        notes.Add($"low latency: {Describe(Try(codec, CodecApi.LowLatencyMode, c => c.SetBool(CodecApi.LowLatencyMode, true)))}");
        Try(codec, CodecApi.CommonLowLatency, c => c.SetBool(CodecApi.CommonLowLatency, true));

        var rate = Try(codec, CodecApi.RateControlMode, c => c.SetUInt32(CodecApi.RateControlMode, CodecApi.RateControlLowDelayVbr));
        if (rate != Outcome.Set)
        {
            rate = Try(codec, CodecApi.RateControlMode, c => c.SetUInt32(CodecApi.RateControlMode, CodecApi.RateControlCbr));
            notes.Add($"rate control: CBR, {Describe(rate)}");
        }
        else
        {
            notes.Add("rate control: low-delay VBR");
        }

        notes.Add($"bitrate {bitrate}: {Describe(Try(codec, CodecApi.MeanBitRate, c => c.SetUInt32(CodecApi.MeanBitRate, bitrate)))}");

        // The session decides when a keyframe is wanted. Ask for the longest spacing the encoder accepts, so
        // it adds none of its own.
        uint? spacing = null;
        foreach (var candidate in EncoderSettings.KeyframeSpacingLadder)
        {
            if (Try(codec, CodecApi.GopSize, c => c.SetUInt32(CodecApi.GopSize, candidate)) == Outcome.Set)
            {
                spacing = candidate;
                break;
            }
        }
        notes.Add(spacing is { } accepted
            ? $"keyframe spacing: {accepted} pictures"
            : "keyframe spacing: the encoder's own (no value was accepted)");

        var bFrames = Try(codec, CodecApi.DefaultBPictureCount, c => c.SetUInt32(CodecApi.DefaultBPictureCount, 0));
        notes.Add($"B-frames 0: {Describe(bFrames)}");
        return bFrames == Outcome.Set;
    }

    private enum Outcome { Set, NotSupported, Failed }

    private static Outcome Try(CodecApi codec, Guid property, Func<CodecApi, Result> set)
    {
        if (!codec.IsSupported(property))
            return Outcome.NotSupported;
        return set(codec).Success ? Outcome.Set : Outcome.Failed;
    }

    private static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.Set => "set",
        Outcome.NotSupported => "not supported",
        _ => "refused",
    };

    // The output type is tried with the baseline profile (which has no B-frames at all). If the
    // transform will not take that, it is tried without, but only when B-frames were switched off by
    // the property instead; and the colour description is dropped before giving up on a type.
    // Returns the profile the accepted type carries (null: the encoder's default).
    private static uint? SetOutputType(
        IMFTransform transform, int width, int height, int fps, uint bitrate, bool bFramesZero, List<string> notes)
    {
        (bool Baseline, bool Colour)[] attempts = bFramesZero
            ? [(true, true), (true, false), (false, true), (false, false)]
            : [(true, true), (true, false)];

        var errors = new List<string>();
        foreach (var (baseline, colour) in attempts)
        {
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, EncoderSettings.Pack(width, height)).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, EncoderSettings.Pack(fps, 1)).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, EncoderSettings.Pack(1, 1)).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            if (baseline)
                type.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileBaseline).CheckError();
            if (colour)
                SetColour(type);

            try
            {
                transform.SetOutputType(0, type, 0);
                notes.Add($"output H.264 {(baseline ? "baseline" : "default profile")}{(colour ? ", BT.709 limited range" : ", no colour description")}");
                return baseline ? H264ProfileBaseline : null;
            }
            catch (SharpGenException ex)
            {
                errors.Add($"profile={(baseline ? "baseline" : "default")} colour={colour}: 0x{ex.ResultCode.Code:X8}");
            }
        }

        throw new EncoderSetupException("The encoder refused every H.264 output type: " + string.Join("; ", errors));
    }

    private static void SetInputType(IMFTransform transform, int width, int height, int fps, List<string> notes)
    {
        var errors = new List<string>();
        foreach (var colour in new[] { true, false })
        {
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, EncoderSettings.Pack(width, height)).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, EncoderSettings.Pack(fps, 1)).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, EncoderSettings.Pack(1, 1)).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            if (colour)
                SetColour(type);

            try
            {
                transform.SetInputType(0, type, 0);
                notes.Add($"input NV12{(colour ? ", BT.709 limited range" : ", no colour description")}");
                return;
            }
            catch (SharpGenException ex)
            {
                errors.Add($"colour={colour}: 0x{ex.ResultCode.Code:X8}");
            }
        }

        throw new EncoderSetupException("The encoder refused an NV12 input type: " + string.Join("; ", errors));
    }

    private static void SetColour(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, ColorPrimariesBt709).CheckError();
        type.Set(MediaTypeAttributeKeys.TransferFunction, ColorTransferBt709).CheckError();
        type.Set(MediaTypeAttributeKeys.YuvMatrix, ColorMatrixBt709).CheckError();
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, ColorRangeLimited).CheckError();
    }

    private void ReadOutputStreamInfo()
    {
        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & OutputProvidesSamples) != 0;
        // A floor of 1 MiB for when the transform reports a small size. A picture that still does not fit is
        // handed over in pieces (OutputDataBufferIncomplete) and put back together.
        _outputBufferSize = Math.Max(info.Size, 1 << 20);
    }

    public byte[]? ReadParameterSets()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var current = _transform.GetOutputCurrentType(0);
            if (!current.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out var size).Success || size == 0)
                return null;
            var header = current.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            return H264Stream.IsParameterSetBlock(header) ? header : null;
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

    // ---- running (IEncoderMft) ---------------------------------------------------------------

    public InputResult ProcessInput(IMFSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _transform.ProcessInput(0, sample, 0);
            return InputResult.Accepted;
        }
        catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
        {
            return InputResult.NotAccepting;
        }
    }

    public void RequestKeyframe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _codec.SetUInt32(CodecApi.ForceKeyFrame, 1);
        if (result.Failure)
            throw new InvalidOperationException($"The encoder refused a forced keyframe (0x{result.Code:X8}).");
    }

    /// <summary>
    /// The next event from an asynchronous transform, without waiting and without throwing when there is
    /// none: the queue is polled several times per picture, and an exception for each empty poll would be
    /// hundreds of first-chance exceptions a second. <c>GetEvent</c> is called through its vtable for that.
    /// </summary>
    public unsafe MftEvent PollEvent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_events is null)
            return MftEvent.None;

        var self = _events.NativePointer;
        nint pointer = 0;
        var getEvent = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)((void**)*(void***)self)[GetEventSlot];
        var hr = getEvent(self, EventNoWait, &pointer);
        if (hr == NoEventsAvailable)
            return MftEvent.None;
        if (hr < 0)
            throw new InvalidOperationException($"The encoder's event queue failed (0x{hr:X8}).");
        if (pointer == 0)
            return MftEvent.None; // success with no event: nothing to read, and nothing to release

        using var mediaEvent = new IMFMediaEvent(pointer);
        switch (mediaEvent.EventType)
        {
            case MediaEventTypes.TransformNeedInput:
                return MftEvent.NeedInput;
            case MediaEventTypes.TransformHaveOutput:
                return MftEvent.HaveOutput;
            case MediaEventTypes.Error:
                throw new InvalidOperationException($"The encoder reported an error (0x{mediaEvent.Status.Code:X8}).");
            default:
                return MftEvent.Other;
        }
    }

    public OutputResult ProcessOutput(out RawOutput output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        output = default;
        using var assembled = new MemoryStream();
        var cleanPoint = false;

        for (var piece = 0; piece < 8; piece++)
        {
            var buffer = new OutputDataBuffer { StreamID = 0 };
            IMFSample? lent = null;
            if (!_providesSamples)
            {
                lent = OwnOutputSample();
                // ProcessOutput hands back a second wrapper around the sample we gave it, without taking
                // a reference for it; disposing that wrapper releases one. This keeps the count right.
                lent.AddRef();
                buffer.Sample = lent;
            }

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            var returned = buffer.Sample;
            buffer.Events?.Dispose();

            try
            {
                if (result == ResultCode.TransformNeedMoreInput)
                    return Finish(assembled, cleanPoint, out output);

                if (result == ResultCode.TransformStreamChange)
                {
                    Renegotiate();
                    return OutputResult.FormatChanged;
                }

                result.CheckError();
                if (returned is null)
                    return Finish(assembled, cleanPoint, out output);

                AppendSample(returned, assembled);
                cleanPoint |= returned.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success && clean != 0;

                if ((buffer.Status & (int)OutputDataBufferFlags.OutputDataBufferIncomplete) != 0)
                    continue; // the picture did not fit the buffer: collect the rest

                return Finish(assembled, cleanPoint, out output);
            }
            finally
            {
                returned?.Dispose();
            }
        }

        throw new InvalidOperationException("The encoder handed over one picture in more than eight pieces.");
    }

    private static OutputResult Finish(MemoryStream assembled, bool cleanPoint, out RawOutput output)
    {
        if (assembled.Length == 0)
        {
            output = default;
            return OutputResult.NeedMoreInput;
        }
        output = new RawOutput(assembled.ToArray(), cleanPoint);
        return OutputResult.Produced;
    }

    private static void AppendSample(IMFSample sample, MemoryStream destination)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var data, out _, out var length);
        try
        {
            var chunk = new byte[length];
            Marshal.Copy(data, chunk, 0, length);
            destination.Write(chunk);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    // A transform that does not allocate its own output samples needs one lent to it. It is reused.
    private IMFSample OwnOutputSample()
    {
        if (_ownOutputSample is { } existing)
            return existing;

        var sample = MediaFactory.MFCreateSample();
        try
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            sample.AddBuffer(buffer);
        }
        catch
        {
            sample.Dispose();
            throw;
        }
        return _ownOutputSample = sample;
    }

    // The transform changed its output format mid-stream: take the format it now offers, preferring one with
    // the profile the encoder was configured with (a baseline encoder stays baseline), and pick up what
    // depends on it again.
    private void Renegotiate()
    {
        var offered = new List<IMFMediaType>();
        try
        {
            var profiles = new List<uint?>();
            for (var i = 0; i < 32; i++)
            {
                IMFMediaType type;
                try
                {
                    type = _transform.GetOutputAvailableType(0, i);
                }
                catch (SharpGenException)
                {
                    break; // MF_E_NO_MORE_TYPES
                }
                offered.Add(type);
                profiles.Add(type.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile, out var profile).Success ? profile : null);
            }

            var index = OutputTypeChoice.Pick(profiles, _profile);
            if (index < 0)
                throw new InvalidOperationException("The encoder offered no output type after changing its format.");
            _transform.SetOutputType(0, offered[index], 0);
        }
        finally
        {
            foreach (var type in offered)
                Guard(type.Dispose);
        }

        ReadOutputStreamInfo();
        _ownOutputSample?.Dispose();
        _ownOutputSample = null;
        ParameterSets = ReadParameterSets() ?? ParameterSets;
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
        Guard(() => _ownOutputSample?.Dispose());
        Guard(() => _events?.Dispose());
        Guard(_codec.Dispose);
        Guard(() => _activate?.ShutdownObject());
        Guard(_transform.Dispose);
        Guard(() => _activate?.Dispose());
        Guard(() => _deviceManager?.Dispose()); // after the transform that was using it
    }
}
