namespace Promin.Agent;

/// <summary>
/// Where the agent keeps what it keeps. Identity and pins belong to the Windows account (the private key is in
/// that account's key store); the room code is machine-wide. The agent's folder is deliberately not the
/// console's, so console and agent can run on one PC under one account as two different devices.
/// </summary>
/// <param name="AgentDirectory">Per account: identity, pinned console PCs and the log.</param>
/// <param name="RoomCodeFile">Machine-wide: the room code.</param>
internal sealed record AgentPaths(string AgentDirectory, string RoomCodeFile)
{
    /// <summary>Public certificate and device id (<c>DeviceIdentity.LoadOrCreate</c>); the private key is not in it.</summary>
    public string IdentityDirectory => Path.Combine(AgentDirectory, "identity");

    /// <summary>The pinned console PCs.</summary>
    public string MastersFile => Path.Combine(AgentDirectory, "masters.json");

    public string LogFile => Path.Combine(AgentDirectory, "agent.log");

    /// <summary><c>%ProgramData%\Promin\Agent</c> (identity, pins, log) and <c>%ProgramData%\Promin\agent.json</c> (room code).</summary>
    public static AgentPaths ForThisMachine() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Promin", "Agent"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Promin", "agent.json"));
}
