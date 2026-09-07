using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public sealed record WealthSample(DateTimeOffset At, ulong Gil, ulong ListedNet, ulong Total);

/// <summary>
/// A thinned time series of account wealth, recorded from completed retainer
/// valuations. Only complete full-bell scans are worth plotting: a partial scan
/// sees some retainers and would draw a cliff that never happened.
/// </summary>
public sealed class WealthHistory
{
    public const int MaximumSamples = 720;

    private readonly List<WealthSample> samples = [];

    public WealthHistory(IEnumerable<WealthSample>? existing = null)
    {
        if (existing is null)
            return;
        samples.AddRange(existing.Where(x => x is not null).OrderBy(x => x.At));
        Thin();
    }

    public IReadOnlyList<WealthSample> Samples => samples;

    /// <summary>
    /// Adds a sample. Within <paramref name="minimumInterval"/> of the newest one it
    /// replaces that sample instead of appending, so a burst of scans updates the
    /// latest point rather than flooding the series.
    /// </summary>
    public bool Record(WealthSample sample, TimeSpan minimumInterval)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (samples.Count > 0)
        {
            var newest = samples[^1];
            // An out-of-order sample would draw the line backwards; ignore it.
            if (sample.At < newest.At)
                return false;
            if (sample.At - newest.At < minimumInterval)
            {
                samples[^1] = sample;
                return true;
            }
        }
        samples.Add(sample);
        Thin();
        return true;
    }

    /// <summary>Samples inside the window, oldest first. A zero or negative window returns everything.</summary>
    public IReadOnlyList<WealthSample> Within(TimeSpan window, DateTimeOffset now) =>
        window <= TimeSpan.Zero
            ? samples.ToArray()
            : samples.Where(x => x.At >= now - window).ToArray();

    /// <summary>Change between the first and last sample of a window, or null when there is nothing to compare.</summary>
    public (long Change, TimeSpan Span)? ChangeOver(TimeSpan window, DateTimeOffset now)
    {
        var range = Within(window, now);
        if (range.Count < 2)
            return null;
        var first = range[0];
        var last = range[^1];
        return ((long)last.Total - (long)first.Total, last.At - first.At);
    }

    public static WealthSample? FromValuation(PortfolioValuation valuation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        // Anything less than a finished full-bell pass is a partial view of the
        // account and would plot as a drop that never happened.
        if (!valuation.IsComplete || !valuation.IsFullBellRun)
            return null;
        return new(at, valuation.CurrentGil, valuation.EstimatedNetMarketAligned,
            valuation.ProjectedWealthMarketAligned);
    }

    // Halve the resolution of the oldest half rather than dropping the start of the
    // series, so a long history keeps its shape instead of losing where it began.
    private void Thin()
    {
        if (samples.Count <= MaximumSamples)
            return;
        var keep = new List<WealthSample>(samples.Count);
        var half = samples.Count / 2;
        for (var i = 0; i < samples.Count; i++)
            if (i >= half || i % 2 == 0)
                keep.Add(samples[i]);
        samples.Clear();
        samples.AddRange(keep);
        if (samples.Count > MaximumSamples)
            samples.RemoveRange(0, samples.Count - MaximumSamples);
    }
}
