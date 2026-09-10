namespace SmartUndercutBot.Core.Models;

/// <summary>
/// How much inventory of a market to hold, expressed in days of that market's own
/// observed demand, and what margin each class of stock has to clear.
///
/// The design intent: capital is scarce and productive. Inventory is sized against
/// what the market actually absorbs rather than against a fixed slot count, so an
/// exceptional high-volume line can take real money while a slow line cannot
/// accumulate dead stock however good its percentage return looks.
/// </summary>
public sealed record ProcurementEconomicPolicy(
    // Target days of demand to hold, by class of stock.
    decimal PreferredCoverageDays = 3m,
    decimal SecondaryCoverageDays = 1.5m,
    decimal OpportunisticCoverageDays = 0.5m,
    // A stack rarely lands exactly on the target. Buying is only permitted while
    // holdings are below target, and may overshoot by at most this much - so one
    // stack may complete a position, but a second cannot pile on top of it.
    decimal CoverageOvershootDays = 1m,
    // What counts as a high-volume market worth a thinner margin. Velocity alone is
    // not enough: a 5,000 gil item that sells quickly is not the same capital
    // proposition as a two-million-gil stack of raid food.
    decimal HighVolumeMinimumSalesPerDay = 10m,
    ulong HighVolumeMinimumValuePerSlot = 150_000,
    // Margin bars, thinnest first. Nothing goes below AbsoluteMinimumRoiPercent.
    decimal CoreHighVolumeRoiPercent = 10m,
    decimal HighVolumeRoiPercent = 14m,
    decimal StandardRoiPercent = 20m,
    decimal LowValueRoiPercent = 35m,
    decimal AbsoluteMinimumRoiPercent = 8m,
    // Scoring preference, not permission. Pinned stock gets a thumb on the scale;
    // it does not get to beat a materially better opportunity.
    decimal PreferredScoreWeight = 1.25m,
    decimal SecondaryScoreWeight = 1m,
    decimal OpportunisticScoreWeight = 0.6m,
    // Safety ceiling on how many sale slots one item may ever occupy, whatever the
    // demand maths says.
    int EmergencyMaximumSlotsPerItem = 20,
    // How much cheap competing inventory the market swallows before it should move
    // our resale anchor, expressed in days of demand.
    decimal AnchorAbsorptionDays = 0.5m)
{
    public static ProcurementEconomicPolicy Default { get; } = new();

    /// <summary>
    /// Behaviour for callers that have not opted in: no coverage shaping, no tier
    /// weighting, and a single flat margin bar. Keeps unconfigured callers on plain
    /// profitability rather than silently applying portfolio policy they never set.
    /// </summary>
    public static ProcurementEconomicPolicy Unrestricted { get; } = new(
        // Zero coverage days means "do not shape inventory by demand at all".
        PreferredCoverageDays: 0m,
        SecondaryCoverageDays: 0m,
        OpportunisticCoverageDays: 0m,
        CoverageOvershootDays: 0m,
        CoreHighVolumeRoiPercent: 0m,
        HighVolumeRoiPercent: 0m,
        StandardRoiPercent: 0m,
        LowValueRoiPercent: 0m,
        AbsoluteMinimumRoiPercent: 0m,
        PreferredScoreWeight: 1m,
        SecondaryScoreWeight: 1m,
        OpportunisticScoreWeight: 1m,
        EmergencyMaximumSlotsPerItem: int.MaxValue,
        AnchorAbsorptionDays: 0m);

    /// <summary>
    /// A single flat margin bar with no coverage shaping and no tier weighting - the
    /// behaviour a caller gets when it supplies only a minimum ROI and no policy.
    /// </summary>
    public static ProcurementEconomicPolicy Flat(decimal roiPercent) => Unrestricted with
    {
        CoreHighVolumeRoiPercent = roiPercent,
        HighVolumeRoiPercent = roiPercent,
        StandardRoiPercent = roiPercent,
        LowValueRoiPercent = roiPercent,
    };

    public decimal CoverageDaysFor(PortfolioTier tier) => tier switch
    {
        PortfolioTier.Core => PreferredCoverageDays,
        PortfolioTier.Secondary => SecondaryCoverageDays,
        _ => OpportunisticCoverageDays,
    };

    public decimal ScoreWeightFor(PortfolioTier tier) => tier switch
    {
        PortfolioTier.Core => PreferredScoreWeight,
        PortfolioTier.Secondary => SecondaryScoreWeight,
        _ => OpportunisticScoreWeight,
    };
}
