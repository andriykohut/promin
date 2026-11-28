using Promin.Protocol.Updates;

namespace Promin.Master.Core.Updates;

/// <summary>A downloaded setup whose project signature has been checked: what the console runs or offers an agent PC.</summary>
/// <param name="SetupPath">The setup on this PC.</param>
/// <param name="Version">The release it belongs to, as its signature covers it.</param>
/// <param name="Signature">The project's signature on it, from its <c>.sig</c>.</param>
public sealed record UpdateFile(string SetupPath, ReleaseVersion Version, byte[] Signature);
