using Promin.Protocol.Messages;

namespace Promin.Protocol.Sending;

/// <summary>
/// Thrown by an <see cref="IReceiveTarget"/> to refuse a whole send. The agent answers the
/// console with <see cref="Outcome"/>.
/// </summary>
public sealed class SendRefusedException(SendOutcome outcome)
    : Exception($"The send was refused: {outcome}.")
{
    public SendOutcome Outcome { get; } = outcome;
}
