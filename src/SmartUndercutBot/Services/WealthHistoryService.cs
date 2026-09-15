using System.Text.Json;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

/// <summary>Persists complete account estimates, including changes to saved stock and wallet values.</summary>
public sealed class WealthHistoryService : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IFramework framework;
    private readonly Func<PortfolioValuation> readValuation;
    private readonly Func<ulong>? characterId;
    private readonly Func<bool>? canRecord;
    private readonly AutomationLog log;
    private readonly string? configDirectory;
    private readonly TimeProvider time;
    private string? file;
    private ulong activeCharacter;
    private WealthHistory history = new();
    private DateTimeOffset nextPoll;
    private bool dirty;
    private bool primaryValidated;

    public WealthHistoryService(
        IFramework framework,
        Func<PortfolioValuation> readValuation,
        AutomationLog log,
        string? configDirectory,
        TimeProvider? timeProvider = null,
        Func<ulong>? characterId = null,
        Func<bool>? canRecord = null)
    {
        this.framework = framework;
        this.readValuation = readValuation;
        this.log = log;
        this.configDirectory = string.IsNullOrWhiteSpace(configDirectory) ? null : configDirectory;
        this.characterId = characterId;
        this.canRecord = canRecord;
        time = timeProvider ?? TimeProvider.System;
        if (characterId is null)
        {
            file = HistoryPath(null);
            history = new WealthHistory(Load(file));
        }
        framework.Update += OnUpdate;
    }

    public WealthHistory History => history;

    public void Clear()
    {
        history = new WealthHistory();
        nextPoll = default;
        dirty = true;
        Save();
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            if (!SelectCharacter())
                return;
            var now = time.GetUtcNow();
            if (now < nextPoll)
                return;
            nextPoll = now + PollInterval;
            // Retry a failed write even when no new valuation has arrived.
            Save();
            if (canRecord?.Invoke() == false)
                return;

            var valuation = readValuation();
            if (valuation.CompletedAt is not { } completed ||
                WealthHistory.FromValuation(valuation, now) is not { } sample)
                return;
            // A saved full estimate remains useful while the bell is closed.
            // Revisit it when its components change, even if CompletedAt is old.
            if (history.Samples.LastOrDefault() is { } previous &&
                previous.Gil == sample.Gil && previous.ListedNet == sample.ListedNet &&
                previous.BagNet == sample.BagNet && previous.Total == sample.Total &&
                completed <= previous.At)
                return;
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

    private string? HistoryPath(ulong? id) => configDirectory is null
        ? null
        : Path.Combine(configDirectory, id is { } value ? $"wealth-history-{value}.json" : "wealth-history.json");

    private bool SelectCharacter()
    {
        if (characterId is null)
            return true;
        var id = characterId();
        if (id == 0)
            return false;
        if (id == activeCharacter)
            return true;

        Save();
        // Do not abandon unsaved data when a write has temporarily failed.
        if (dirty)
            return false;
        var destination = HistoryPath(id);
        var legacy = HistoryPath(null);
        if (destination is not null && legacy is not null && (File.Exists(legacy) || File.Exists(legacy + ".bak")))
        {
            // Claim the unscoped file once; another character must never inherit it.
            if (!File.Exists(destination) && !File.Exists(destination + ".bak"))
            {
                if (File.Exists(legacy))
                    File.Move(legacy, destination);
                if (File.Exists(legacy + ".bak"))
                    File.Move(legacy + ".bak", destination + ".bak", true);
            }
            else
            {
                if (File.Exists(legacy))
                    File.Move(legacy, legacy + ".migrated", true);
                if (File.Exists(legacy + ".bak"))
                    File.Move(legacy + ".bak", legacy + ".bak.migrated", true);
            }
        }
        file = destination;
        history = new WealthHistory(Load(file));
        activeCharacter = id;
        nextPoll = default;
        return true;
    }

    private IReadOnlyList<WealthSample>? Load(string? path)
    {
        primaryValidated = false;
        if (path is null)
            return null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<List<WealthSample>>(File.ReadAllText(candidate));
                if (loaded is null || loaded.Any(x => x is null))
                    throw new JsonException("The saved history did not contain any sample data.");
                primaryValidated = candidate == path;
                // Repair a damaged primary on the next poll even if the latest
                // saved estimate is unchanged and produces no additional point.
                dirty = !primaryValidated;
                return loaded;
            }
            catch (Exception ex)
            {
                log.Add(AutomationLogLevel.Warning, $"Could not read the saved wealth history ({Path.GetFileName(candidate)}): {ex.Message}");
            }
        }
        return null;
    }

    private void Save()
    {
        if (!dirty)
            return;
        if (file is null)
        {
            dirty = false;
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, history.Samples);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(file))
                File.Replace(temporary, file, primaryValidated ? file + ".bak" : null);
            else
                File.Move(temporary, file);
            primaryValidated = true;
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
