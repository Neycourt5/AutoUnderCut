using System.Text.Json;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

/// <summary>
/// Keeps the wealth graph's time series, recording one point per completed
/// all-retainer valuation and persisting it so the history survives restarts.
/// </summary>
public sealed class WealthHistoryService : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IFramework framework;
    private readonly Func<PortfolioValuation> readValuation;
    private readonly AutomationLog log;
    private readonly string? file;
    private readonly TimeProvider time;
    private WealthHistory history;
    private DateTimeOffset nextPoll;
    private DateTimeOffset? recordedCompletion;
    private bool dirty;

    public WealthHistoryService(
        IFramework framework,
        Func<PortfolioValuation> readValuation,
        AutomationLog log,
        string? configDirectory,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.readValuation = readValuation;
        this.log = log;
        time = timeProvider ?? TimeProvider.System;
        file = string.IsNullOrWhiteSpace(configDirectory)
            ? null
            : Path.Combine(configDirectory, "wealth-history.json");
        history = new WealthHistory(Load());
        framework.Update += OnUpdate;
    }

    public WealthHistory History => history;

    public void Clear()
    {
        history = new WealthHistory();
        recordedCompletion = null;
        dirty = true;
        Save();
    }

    private void OnUpdate(IFramework _)
    {
        var now = time.GetUtcNow();
        if (now < nextPoll)
            return;
        nextPoll = now.AddSeconds(PollInterval.TotalSeconds);
        try
        {
            var valuation = readValuation();
            // One point per finished scan. Without this a long idle session would
            // re-record the same completed valuation every poll.
            if (valuation.CompletedAt is not { } completed || completed == recordedCompletion)
                return;
            if (WealthHistory.FromValuation(valuation, completed) is not { } sample)
                return;
            recordedCompletion = completed;
            if (!history.Record(sample, MinimumInterval))
                return;
            dirty = true;
            Save();
        }
        catch (Exception ex)
        {
            log.Add(AutomationLogLevel.Warning, $"Could not record the wealth history point: {ex.Message}");
        }
    }

    private IReadOnlyList<WealthSample>? Load()
    {
        if (file is null || !File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<WealthSample>>(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            log.Add(AutomationLogLevel.Warning, $"Could not read the saved wealth history: {ex.Message}");
            return null;
        }
    }

    private void Save()
    {
        if (file is null || !dirty)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(history.Samples));
            dirty = false;
        }
        catch (Exception ex)
        {
            log.Add(AutomationLogLevel.Warning, $"Could not save the wealth history: {ex.Message}");
        }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        Save();
    }
}
