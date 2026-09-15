using System.Text.Json;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Services;

public sealed record SavedPortfolio(
    PortfolioListingEstimate[] Listings,
    PortfolioRetainerBalance[] Retainers,
    uint PlayerGil,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsFullBellRun,
    bool IsComplete,
    int ExpectedRetainers,
    PortfolioBagStockEstimate[] BagStock);

/// <summary>Last observed holdings, keyed by character rather than visited world.</summary>
public sealed class PortfolioCacheService(string directory, Func<ulong> characterId, AutomationLog log)
{
    private ulong activeCharacter;
    private string? lastSaved;
    private bool primaryValidated;
    private string FilePath => Path.Combine(directory, $"portfolio-{activeCharacter}.json");
    public bool CanSave => activeCharacter != 0 && characterId() == activeCharacter;

    public bool SelectCharacter(out SavedPortfolio? saved)
    {
        saved = null;
        var id = characterId();
        // Loading during DC travel is not a new character or an empty portfolio.
        if (id == 0 || id == activeCharacter)
            return false;
        activeCharacter = id;
        lastSaved = null;
        primaryValidated = false;
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<SavedPortfolio>(File.ReadAllText(path));
                if (loaded is null || !IsStructurallyValid(loaded))
                    throw new InvalidDataException("Incomplete portfolio file.");
                saved = loaded;
                primaryValidated = path == FilePath;
                // A recovered backup still needs to repair the damaged primary
                // on the next save, even when its contents are unchanged.
                if (primaryValidated)
                    lastSaved = JsonSerializer.Serialize(saved);
                return true;
            }
            catch (Exception ex)
            {
                log.Add(AutomationLogLevel.Warning, $"Could not read saved portfolio: {ex.Message}");
            }
        }
        return true;
    }

    public void Save(SavedPortfolio saved)
    {
        if (!CanSave) return;
        try
        {
            ArgumentNullException.ThrowIfNull(saved);
            if (!IsStructurallyValid(saved))
                throw new InvalidDataException("Incomplete portfolio snapshot.");
            var json = JsonSerializer.Serialize(saved);
            if (json == lastSaved) return;
            Directory.CreateDirectory(directory);
            var temporary = FilePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, saved);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath))
                // Do not rotate a damaged primary over the last readable backup.
                File.Replace(temporary, FilePath, primaryValidated ? FilePath + ".bak" : null);
            else
                File.Move(temporary, FilePath);
            lastSaved = json;
            primaryValidated = true;
        }
        catch (Exception ex)
        {
            log.Add(AutomationLogLevel.Warning, $"Could not save portfolio: {ex.Message}");
        }
    }

    private static bool IsStructurallyValid(SavedPortfolio saved) =>
        saved.Listings is not null && saved.Retainers is not null && saved.BagStock is not null &&
        saved.Listings.All(x => x is not null) && saved.Retainers.All(x => x is not null) &&
        saved.BagStock.All(x => x is not null);
}
