using Educast.Protocol.Video;

namespace Educast.Master.Core.Wall;

/// <summary>One row of the picker's Sharpness group: the choice and its text with the real rate.</summary>
public sealed record SharpnessChoice(Sharpness Value, string Text);
