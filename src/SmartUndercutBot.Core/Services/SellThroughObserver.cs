namespace SmartUndercutBot.Core.Services;

/// <summary>
/// What has been measured about how fast <em>we</em> sell one line, as opposed to
/// how fast the whole home world sells it.
///
/// Deliberately tiny: a smoothed rate, how many windows fed it, how much observed
/// time is behind it, and the last complete inventory reading it was measured from.
/// </summary>
/// <param name="UnitsPerDay">Exponentially smoothed units-per-day actually leaving our stock.</param>
/// <param name="Samples">Accepted observation windows behind <paramref name="UnitsPerDay"/>.</param>
/// <param name="ObservedDays">Total accepted elapsed time, so a run of tiny windows cannot look like evidence.</param>
/// <param name="LastUnits">Units held at the last complete observation.</param>
/// <param name="LastObservedAt">When that observation was taken.</param>
public sealed record SellThroughObservation(
    decimal UnitsPerDay = 0m,
    int Samples = 0,
    decimal ObservedDays = 0m,
    ulong LastUnits = 0,
    DateTimeOffset? LastObservedAt = null);

/// <summary>
/// Experimental inventory-difference helper, retained but not called by runtime.
/// These differences are not confirmed sales or a reliable capture-share forecast.
///
/// The planner sizes positions against the whole home world's demand, which is
/// optimistic: if popcorn sells 100 a day and we are one of four sellers, three days
/// of "coverage" may really be closer to twelve. This class accumulates the evidence
/// needed to correct that, from the one signal the plugin genuinely has - the change
/// in our own tracked holdings between two <em>complete</em> retainer passes.
///
/// It is measurement only. Nothing here feeds the planner yet, and the reason is
/// documented in PROFIT_OPTIMIZATION_IMPLEMENTATION.md: inventory differencing cannot
/// tell a sale from a manual withdrawal, an item used, or a listing that expired off
/// the board, and every one of those confounds biases the estimate <em>upward</em> -
/// the direction that would make the bot buy more. Acting on it needs a real sale
/// signal, which would mean reading the retainer's own sale history from the game.
/// </summary>
public static class SellThroughObserver
{
    /// <summary>Windows outside this range are discarded rather than smoothed in.</summary>
    public const decimal MinimumWindowDays = 0.25m;
    public const decimal MaximumWindowDays = 14m;

    /// <summary>
    /// Smoothing weight for a new window. Low enough that one unusual burst moves the
    /// estimate a little and cannot pin it high, high enough that a genuine change in
    /// the market works through in a handful of passes.
    /// </summary>
    public const decimal SmoothingWeight = 0.3m;

    /// <summary>
    /// Evidence required before the estimate is worth quoting at all. Both bars have
    /// to clear: several windows, and several days of real elapsed time behind them.
    /// </summary>
    public const int MinimumSamples = 4;
    public const decimal MinimumObservedDays = 3m;

    /// <summary>
    /// Floor on any future correction. Even a pessimistic reading may never cut a
    /// market's assumed demand by more than this, so sparse or unlucky personal
    /// history cannot collapse a position's sizing to nothing.
    /// </summary>
    public const decimal MinimumCaptureShare = 0.25m;

    /// <summary>
    /// Fold one complete inventory reading into the record.
    ///
    /// <paramref name="purchasedSinceLast"/> is what we bought since the previous
    /// reading; without it every restock would read as negative sales. The caller
    /// must only pass readings from a <em>complete, verified</em> retainer pass -
    /// a partial or failed scan looks exactly like a sell-out, which is why the
    /// window is discarded rather than trusted whenever anything looks wrong.
    /// </summary>
    public static SellThroughObservation Observe(
        SellThroughObservation previous,
        ulong currentUnits,
        ulong purchasedSinceLast,
        DateTimeOffset now,
        decimal marketUnitsPerDay)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var recorded = previous with { LastUnits = currentUnits, LastObservedAt = now };
        if (previous.LastObservedAt is not { } previousAt)
            return recorded;

        var elapsedDays = (decimal)(now - previousAt).TotalDays;
        if (elapsedDays < MinimumWindowDays || elapsedDays > MaximumWindowDays)
            return recorded;

        // Everything we could have sold in the window, and what is actually gone.
        var available = previous.LastUnits + purchasedSinceLast;
        if (available == 0)
            return recorded;
        if (currentUnits > available)
            // More stock than we can account for: a reading we do not understand, so
            // it teaches us nothing. Never treat it as negative sales.
            return recorded;

        var soldRate = (available - currentUnits) / elapsedDays;
        // We cannot capture more of a market than the market absorbs. A reading above
        // that means the difference was not all sales, so cap rather than believe it.
        if (marketUnitsPerDay > 0)
            soldRate = Math.Min(soldRate, marketUnitsPerDay);

        var blended = previous.Samples == 0
            ? soldRate
            : previous.UnitsPerDay + SmoothingWeight * (soldRate - previous.UnitsPerDay);

        return recorded with
        {
            UnitsPerDay = Math.Max(0m, blended),
            Samples = previous.Samples + 1,
            ObservedDays = previous.ObservedDays + elapsedDays,
        };
    }

    /// <summary>Whether enough independent evidence stands behind the estimate to quote it.</summary>
    public static bool IsReliable(SellThroughObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.Samples >= MinimumSamples && observation.ObservedDays >= MinimumObservedDays;
    }

    /// <summary>
    /// The fraction of the market's daily volume we appear to capture, or null when
    /// the evidence is too thin to say. Clamped to a real share: never above the
    /// whole market, never below the floor that stops a bad patch erasing a line.
    /// </summary>
    public static decimal? CaptureShare(SellThroughObservation observation, decimal marketUnitsPerDay)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!IsReliable(observation) || marketUnitsPerDay <= 0)
            return null;
        return Math.Clamp(observation.UnitsPerDay / marketUnitsPerDay, MinimumCaptureShare, 1m);
    }
}
