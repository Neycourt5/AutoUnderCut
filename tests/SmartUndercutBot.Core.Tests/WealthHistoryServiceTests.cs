using System.Text.Json;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class WealthHistoryServiceTests
{
    [Fact]
    public void ACompletedEstimateIsPersistedAndNotRepeatedWhileIdleOrAfterRestart()
    {
        using var fixture = new Fixture();
        using (var service = fixture.CreateService())
        {
            fixture.Pulse();
            fixture.Pulse(TimeSpan.FromMinutes(20));
            Assert.Single(service.History.Samples);
            Assert.True(File.Exists(fixture.HistoryFile));
        }

        using var restored = fixture.CreateService();
        fixture.Pulse();
        Assert.Equal(9_000ul, Assert.Single(restored.History.Samples).Total);
    }

    [Fact]
    public void WalletAndBagChangesUpdateTheGraphWithoutAnotherBellScan()
    {
        using var fixture = new Fixture();
        using var service = fixture.CreateService();
        fixture.Pulse();
        fixture.Valuation = fixture.Valuation with
        {
            CurrentGil = 900,
            EstimatedBagNetValue = 2_000,
            ProjectedWealthMarketAligned = 11_400,
        };
        fixture.Pulse(TimeSpan.FromSeconds(6));

        Assert.Equal(2, service.History.Samples.Count);
        var point = service.History.Samples[^1];
        Assert.Equal(900ul, point.Gil);
        Assert.Equal(2_000ul, point.BagNet);
        Assert.Equal(11_400ul, point.Total);
    }

    [Fact]
    public void TravelAndInventoryLoadingDoNotRecordTemporaryZeroBalances()
    {
        using var fixture = new Fixture { UseCharacters = true };
        using var service = fixture.CreateService();
        fixture.Pulse();
        fixture.Ready = false;
        fixture.Valuation = fixture.Valuation with { CurrentGil = 0, ProjectedWealthMarketAligned = 0 };
        fixture.Pulse(TimeSpan.FromSeconds(6));
        Assert.Equal(9_000ul, Assert.Single(service.History.Samples).Total);

        fixture.Ready = true;
        fixture.Character = 0;
        fixture.Pulse(TimeSpan.FromSeconds(6));
        Assert.Equal(9_000ul, Assert.Single(service.History.Samples).Total);
    }

    [Fact]
    public void AnIncompleteOrSingleRetainerEstimateCannotReplaceTheSavedFullAccountPoint()
    {
        using var fixture = new Fixture();
        using var service = fixture.CreateService();
        fixture.Pulse();
        fixture.Valuation = fixture.Valuation with { IsComplete = false, ProjectedWealthMarketAligned = 100 };
        fixture.Pulse(TimeSpan.FromSeconds(6));
        fixture.Valuation = fixture.Valuation with { IsComplete = true, IsFullBellRun = false };
        fixture.Pulse(TimeSpan.FromSeconds(6));
        Assert.Equal(9_000ul, Assert.Single(service.History.Samples).Total);
    }

    [Fact]
    public void CharacterHistoriesAreIndependentAndLegacyHistoryMigratesOnlyOnce()
    {
        using var fixture = new Fixture { UseCharacters = true, Ready = false };
        File.WriteAllText(fixture.LegacyFile, JsonSerializer.Serialize(new[]
        {
            new WealthSample(fixture.Clock.GetUtcNow(), 200, 800, 1_000),
        }));
        using var service = fixture.CreateService();
        fixture.Pulse();
        Assert.Equal(1_000ul, Assert.Single(service.History.Samples).Total);
        Assert.True(File.Exists(fixture.HistoryFile));
        Assert.False(File.Exists(fixture.LegacyFile));

        fixture.Character = 22;
        fixture.Pulse();
        Assert.Empty(service.History.Samples);
        fixture.Ready = true;
        fixture.Pulse(TimeSpan.FromSeconds(6));
        Assert.Equal(9_000ul, Assert.Single(service.History.Samples).Total);

        fixture.Character = 11;
        fixture.Ready = false;
        fixture.Pulse();
        Assert.Equal(1_000ul, Assert.Single(service.History.Samples).Total);
    }

    [Fact]
    public void ACorruptPrimaryFallsBackToTheBackupAndDoesNotOverwriteItWithCorruptData()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.HistoryFile, "{interrupted write");
        var backup = JsonSerializer.Serialize(new[]
        {
            new WealthSample(fixture.Clock.GetUtcNow().AddHours(-1), 200, 800, 1_000),
        });
        File.WriteAllText(fixture.HistoryFile + ".bak", backup);
        using var service = fixture.CreateService();
        Assert.Equal(1_000ul, Assert.Single(service.History.Samples).Total);

        fixture.Pulse();
        Assert.Equal(2, service.History.Samples.Count);
        Assert.Equal(backup, File.ReadAllText(fixture.HistoryFile + ".bak"));
        Assert.Equal(2, JsonSerializer.Deserialize<List<WealthSample>>(File.ReadAllText(fixture.HistoryFile))!.Count);
        Assert.Contains(fixture.Log.Messages, x => x.Contains("Could not read the saved wealth history"));
    }

    [Fact]
    public void FailedSaveIsRetriedEvenWhenTheEstimateHasNotChanged()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.HistoryFile);
        using var service = fixture.CreateService();
        fixture.Pulse();
        Assert.Single(service.History.Samples);
        Assert.False(File.Exists(fixture.HistoryFile));

        Directory.Delete(fixture.HistoryFile);
        fixture.Pulse(TimeSpan.FromSeconds(6));
        Assert.Equal(9_000ul, Assert.Single(
            JsonSerializer.Deserialize<List<WealthSample>>(File.ReadAllText(fixture.HistoryFile))!).Total);
    }

    [Fact]
    public void ClearStartsAgainFromTheSavedEstimateWithoutRequiringAnotherScan()
    {
        using var fixture = new Fixture();
        using var service = fixture.CreateService();
        fixture.Pulse();
        service.Clear();
        Assert.Empty(service.History.Samples);
        fixture.Pulse();
        Assert.Equal(9_000ul, Assert.Single(service.History.Samples).Total);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SmartUndercutWealthTests", Guid.NewGuid().ToString("N"));
        private readonly TestFramework framework = new();
        public TestClock Clock { get; } = new();
        public AutomationLog Log { get; } = new();
        public ulong Character { get; set; } = 11;
        public bool UseCharacters { get; init; }
        public bool Ready { get; set; } = true;
        public PortfolioValuation Valuation { get; set; }
        public string LegacyFile => Path.Combine(directory, "wealth-history.json");
        public string HistoryFile => UseCharacters ? Path.Combine(directory, $"wealth-history-{Character}.json") : LegacyFile;

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Valuation = new PortfolioValuationService().Calculate(
                [new(1, "Retainer", 0, 42, "Potion", 1, 8_500, 8_500, true, 0)],
                [new(1, "Retainer", 0, 0)],
                500, Clock.GetUtcNow(), Clock.GetUtcNow(), true, true, 1);
        }

        public WealthHistoryService CreateService() => new(
            framework, () => Valuation, Log, directory, Clock,
            UseCharacters ? () => Character : null, () => Ready);

        public void Pulse(TimeSpan elapsed = default)
        {
            Clock.Advance(elapsed);
            framework.Pulse();
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class TestFramework : IFramework
    {
        public event Action<IFramework>? Update;
        public void Pulse() => Update?.Invoke(this);
    }
}
