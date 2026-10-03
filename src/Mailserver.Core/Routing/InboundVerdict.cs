namespace Mailserver.Core.Routing;

/// <summary>Result of the spam checks for a message received on port 25.</summary>
/// <param name="Discard">Score reached the delete threshold; delivered nowhere unless a rule allow-lists it.</param>
public sealed record InboundVerdict(double Score, bool IsSpam, bool Discard, IReadOnlyList<string> Reasons)
{
    public static InboundVerdict Clean { get; } = new(0, false, false, []);
}
