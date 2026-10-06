using Educast.MediaPipeline.Decoding;
using Educast.MediaPipeline.Encoding;
using Educast.Protocol.Broadcast;

namespace Educast.Agent;

/// <summary>
/// <see cref="IFramePresenter"/> over the H.264 decoder and the render window: decode the frame, then draw the
/// picture it gave. It owns no decisions (what to offer, when to wait for a keyframe, which picture of a waiting
/// batch to show: <see cref="ClientAgentSession"/>). Used on the display thread only, where the window and the
/// decoder's texture live.
/// </summary>
internal sealed class DecoderPresenter(MediaFoundationH264Decoder decoder, FullscreenRenderWindow window, Action<string> log)
    : IFramePresenter
{
    // A decoder that is being refused often would fill the log; the first few say why, then now and then.
    private const int ReportFirst = 5;
    private const int ReportEvery = 100;
    private int _refused;
    private DecodedFrame? _decoded;

    public bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
    {
        // The decoder reuses its output, so the previous picture is not valid after this call.
        _decoded = null;
        window.ForgetPicture();

        if (!decoder.TryDecode(new EncodedFrame(encodedFrame.ToArray(), isKeyframe, frameId), out var picture))
        {
            _refused++;
            if (_refused <= ReportFirst || _refused % ReportEvery == 0)
            {
                log($"The decoder gave no picture for frame {frameId} ({(isKeyframe ? "keyframe" : "delta")}): " +
                    $"{decoder.LastFailure ?? "none came back"}. Refused so far: {_refused}.");
            }

            return false;
        }

        _decoded = picture;
        return true;
    }

    public void PresentDecoded()
    {
        if (_decoded is { } picture)
        {
            window.PresentFrame(picture.Texture, picture.Width, picture.Height);
        }
    }
}
