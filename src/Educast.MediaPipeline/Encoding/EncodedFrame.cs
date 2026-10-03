namespace Educast.MediaPipeline.Encoding;

/// <summary>
/// One encoded picture as an H.264 Annex B byte stream. <paramref name="IsKeyframe"/> is read from the
/// bytes the encoder actually produced (an IDR slice is present), not from what was asked for.
/// <paramref name="FrameId"/> counts the frames an encoder has returned, from 0, with no gaps.
/// </summary>
public readonly record struct EncodedFrame(byte[] Data, bool IsKeyframe, uint FrameId);
