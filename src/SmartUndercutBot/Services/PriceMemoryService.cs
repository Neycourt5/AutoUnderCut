using System.Text.Json;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

/// <summary>
/// Keeps <see cref="PriceMemory"/> on disk, one file per character next to the
/// portfolio cache: <c>price-memory-&lt;character&gt;.json</c> with a <c>.bak</c>.
///
/// Writes are atomic (temporary file, flush, replace) and never rotate a damaged
/// primary over the last readable backup, the same scheme as the portfolio cache.
/// Saving runs off the framework thread at most every two minutes while something
/// changed, and once more on dispose or a character change, so a busy shopping
/// circuit costs no frame time.
/// </summary>
public sealed class PriceMemoryService : IDisposable
{
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(2);

    private readonly string directory;
    private readonly Func<ulong> characterId;
    private readonly AutomationLog log;
    private readonly TimeProvider timeProvider;
    private readonly object saveSync = new();
    private ulong activeCharacter;
    private long savedRevision = -1;
    private DateTimeOffset lastSaveAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastPruneAt = DateTimeOffset.MinValue;
    private bool primaryValidated;
    private Task? saving;

    public PriceMemoryService(string directory, Func<ulong> characterId, AutomationLog log, TimeProvider? timeProvider = null)
    {
        this.directory = directory;
        this.characterId = characterId;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The live memory. Replaced, never mutated in place, when the character changes.</summary>
    public PriceMemory Memory { get; private set; } = new();

    public ulong ActiveCharacter => activeCharacter;
    public DateTimeOffset? LastSavedAt => lastSaveAt == DateTimeOffset.MinValue ? null : lastSaveAt;
    private string PathFor(ulong character) => Path.Combine(directory, $"price-memory-{character}.json");

    /// <summary>Cheap enough to call every frame: loads on a character change and saves when due.</summary>
    public void Update()
    {
        var id = characterId();
        if (id != 0 && id != activeCharacter)
        {
            Flush();
            Load(id);
        }
        if (activeCharacter == 0)
            return;
        var now = timeProvider.GetUtcNow();
        if (now - lastPruneAt >= TimeSpan.FromHours(1))
        {
            lastPruneAt = now;
            Memory.Prune(now);
        }
        if (Memory.Revision == savedRevision || now - lastSaveAt < SaveInterval)
            return;
        lock (saveSync)
        {
            if (saving is { IsCompleted: false })
                return;
            var data = Memory.ToData();
            var revision = Memory.Revision;
            var path = PathFor(activeCharacter);
            lastSaveAt = now;
            saving = Task.Run(() => Write(path, data, revision));
        }
    }

    /// <summary>Write anything unsaved now, on the calling thread. Used on dispose and character change.</summary>
    public void Flush()
    {
        if (activeCharacter == 0)
            return;
        Task? pending;
        lock (saveSync)
            pending = saving;
        try { pending?.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { /* the task logs its own failure */ }
        if (Memory.Revision != savedRevision)
            Write(PathFor(activeCharacter), Memory.ToData(), Memory.Revision);
    }

    private void Load(ulong id)
    {
        activeCharacter = id;
        primaryValidated = false;
        var path = PathFor(id);
        PriceMemoryData? data = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                data = JsonSerializer.Deserialize<PriceMemoryData>(File.ReadAllText(candidate))
                       ?? throw new InvalidDataException("Empty price memory file.");
                primaryValidated = candidate == path;
                break;
            }
            catch (Exception ex)
            {
                log.Add(AutomationLogLevel.Warning, $"PRICE MEMORY: could not read {Path.GetFileName(candidate)}: {ex.Message}");
            }
        }
        Memory = new PriceMemory(data);
        var now = timeProvider.GetUtcNow();
        Memory.Prune(now);
        lastPruneAt = now;
        // A backup recovery still has to repair the damaged primary on the next save.
        savedRevision = primaryValidated ? Memory.Revision : -1;
        if (data is not null)
            log.Add(AutomationLogLevel.Information,
                $"PRICE MEMORY: loaded {Memory.WorldSnapshotCount:N0} world reading(s), {Memory.HomeSnapshotCount:N0} home " +
                $"reading(s), {Memory.SaleCount:N0} recorded sale(s) and {Memory.Candidates.Count:N0} scan candidate(s).");
    }

    private void Write(string path, PriceMemoryData data, long revision)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data);
                stream.Flush(flushToDisk: true);
            }
            lock (saveSync)
            {
                if (File.Exists(path))
                    File.Replace(temporary, path, primaryValidated ? path + ".bak" : null);
                else
                    File.Move(temporary, path);
                primaryValidated = true;
                if (revision > savedRevision)
                    savedRevision = revision;
            }
        }
        catch (Exception ex)
        {
            log.Add(AutomationLogLevel.Warning, $"PRICE MEMORY: could not save: {ex.Message}");
        }
    }

    public void Dispose() => Flush();
}
