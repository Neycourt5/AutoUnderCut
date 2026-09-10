using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

/// <summary>
/// The evidence a discovered market has accumulated about itself. Everything here
/// is observed, never assumed: a remote statistics call supplies the volume and
/// price figures, and the confirmation count records how many separate refreshes
/// have agreed.
/// </summary>
/// <param name="SalesPerDay">Units the market moves per day.</param>
/// <param name="StackValue">Gil one full target stack is worth at the observed price.</param>
/// <param name="ExpectedGilPerDay">Gil per day the line could plausibly generate.</param>
/// <param name="Confirmations">Separate discovery refreshes that have agreed so far.</param>
/// <param name="PriceSpreadPercent">
/// How far the observed price has moved between refreshes, as a percentage of the
/// lower figure. A line whose price swings wildly is not a place to park capital.
/// </param>
/// <param name="HasProfitableSpread">Whether a cross-world buy price has actually cleared the margin bar.</param>
public sealed record MarketConfidenceEvidence(
    decimal SalesPerDay,
    ulong StackValue,
    decimal ExpectedGilPerDay,
    int Confirmations,
    decimal PriceSpreadPercent,
    bool HasProfitableSpread);

/// <summary>
/// How much a market has proved about itself, and therefore how much of the
/// portfolio it is allowed to become.
///
/// The old behaviour pinned a discovered item straight to <c>PreferredStock</c> on
/// the strength of one remote statistics call, which handed the strongest tier in
/// the system - core weighting, the thinnest margin bar, exemption from the buffer
/// gil cap - to something that had never been observed twice. Promotion now walks
/// a ladder, and every rung needs evidence the previous one did not have.
/// </summary>
public static class MarketConfidencePolicy
{
    /// <summary>A line worth watching: real volume and a stack worth a retainer slot.</summary>
    public const decimal CandidateMinimumSalesPerDay = 50m;
    public const ulong CandidateMinimumStackValue = 150_000;

    /// <summary>
    /// A line that has behaved like core stock: high volume, a valuable stack,
    /// meaningful daily gil, a price that has held still, and a cross-world spread
    /// that actually cleared the margin bar rather than merely looking wide.
    /// </summary>
    public const decimal ProvenMinimumSalesPerDay = 75m;
    public const ulong ProvenMinimumStackValue = 300_000;
    public const decimal ProvenMinimumGilPerDay = 50_000m;
    public const decimal ProvenMaximumPriceSpreadPercent = 25m;

    /// <summary>
    /// Separate refreshes that have to agree before a market is promoted. The
    /// discovery cache is a day long, so this is roughly "it has looked like this
    /// for most of a week", not "it looked like this once".
    /// </summary>
    public const int ProvenMinimumConfirmations = 5;

    public static MarketConfidence Classify(MarketConfidenceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.SalesPerDay < CandidateMinimumSalesPerDay ||
            evidence.StackValue < CandidateMinimumStackValue)
            return MarketConfidence.Opportunistic;
        return evidence.SalesPerDay >= ProvenMinimumSalesPerDay &&
               evidence.StackValue >= ProvenMinimumStackValue &&
               evidence.ExpectedGilPerDay >= ProvenMinimumGilPerDay &&
               evidence.Confirmations >= ProvenMinimumConfirmations &&
               evidence.PriceSpreadPercent <= ProvenMaximumPriceSpreadPercent &&
               evidence.HasProfitableSpread
            ? MarketConfidence.Proven
            : MarketConfidence.Candidate;
    }

    /// <summary>
    /// Only a market that has proved itself is pinned as core stock. Everything
    /// below that trades on its own economics through the normal tiering, which is
    /// where a genuinely excellent line will show up anyway.
    /// </summary>
    public static bool ShouldPin(MarketConfidence confidence) => confidence == MarketConfidence.Proven;

    /// <summary>
    /// Price movement between two observations, as a percentage of the smaller one.
    /// Unknown or zero prices report a spread large enough to block promotion,
    /// because absence of evidence is not evidence of stability.
    /// </summary>
    public static decimal PriceSpreadPercent(uint previousPrice, uint currentPrice)
    {
        if (previousPrice == 0 || currentPrice == 0)
            return decimal.MaxValue;
        var low = Math.Min(previousPrice, currentPrice);
        var high = Math.Max(previousPrice, currentPrice);
        return (high - low) * 100m / low;
    }
}
