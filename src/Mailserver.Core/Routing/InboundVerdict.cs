namespace Mailserver.Core.Routing;

/// <summary>One spam test that contributed to the score, e.g. SPF_FAIL = 3.5.</summary>
public sealed record SpamTest(string Name, double Score)
{
    public override string ToString() => $"{Name}={Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>Result of the spam checks for a message received on port 25.</summary>
/// <param name="Discard">Score reached the delete threshold; delivered nowhere unless a rule allow-lists it.</param>
/// <param name="TraceId">SMTP session id; links the delivery entries in the spam log to the checks.</param>
public sealed record InboundVerdict(double Score, bool IsSpam, bool Discard, IReadOnlyList<SpamTest> Tests, string? TraceId = null)
{
    public static InboundVerdict Clean { get; } = new(0, false, false, []);
}
