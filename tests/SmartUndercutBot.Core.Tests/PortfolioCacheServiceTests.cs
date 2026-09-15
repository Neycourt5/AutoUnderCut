using System.Text.Json;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class PortfolioCacheServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SmartUndercutBot-PortfolioTests-" + Guid.NewGuid());
    private readonly AutomationLog log = new();
    private ulong characterId = 100;

    [Fact]
    public void RestoresListingsBalancesBagStockAndCompletionAcrossRestart()
    {
        var expected = Snapshot(500);
        var first = Service();
        Assert.True(first.SelectCharacter(out var initial));
        Assert.Null(initial);
        first.Save(expected);

        var restarted = Service();
        Assert.True(restarted.SelectCharacter(out var actual));

        AssertSnapshotEqual(expected, actual);
        Assert.True(actual!.Listings[0].IsHighQuality);
        Assert.Equal(BagValuationSource.HomeMarket, actual.BagStock[0].Source);
    }

    [Fact]
    public void LoadingDoesNotSwitchCharacterOrOverwriteLastObservedHoldings()
    {
        var service = Service();
        service.SelectCharacter(out _);
        service.Save(Snapshot(500));

        characterId = 0;
        Assert.False(service.SelectCharacter(out _));
        Assert.False(service.CanSave);
        service.Save(Snapshot(0));

        characterId = 100;
        Assert.False(service.SelectCharacter(out _));
        Assert.True(service.CanSave);
        Assert.True(Service().SelectCharacter(out var restored));
        Assert.Equal(500u, restored!.PlayerGil);
        Assert.Single(restored.Listings);
    }

    [Fact]
    public void KeepsCharactersSeparateAndRestoresEachOnReturn()
    {
        var service = Service();
        service.SelectCharacter(out _);
        service.Save(Snapshot(500));

        characterId = 200;
        Assert.False(service.CanSave);
        // Identity changed before the next selection: never write the other
        // character's readings into the previous character's file.
        service.Save(Snapshot(9_999));
        Assert.True(service.SelectCharacter(out var secondInitial));
        Assert.Null(secondInitial);
        service.Save(Snapshot(800));

        characterId = 100;
        Assert.True(service.SelectCharacter(out var first));
        Assert.Equal(500u, first!.PlayerGil);
        characterId = 200;
        Assert.True(service.SelectCharacter(out var second));
        Assert.Equal(800u, second!.PlayerGil);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Listings\":[null],\"Retainers\":[],\"BagStock\":[]}")]
    public void RecoversBackupAndPreservesItWhenRepairingDamagedPrimary(string damaged)
    {
        var initial = Service();
        initial.SelectCharacter(out _);
        initial.Save(Snapshot(500));
        initial.Save(Snapshot(800));
        Assert.True(File.Exists(PrimaryPath + ".bak"), string.Join(Environment.NewLine, log.Messages));
        File.WriteAllText(PrimaryPath, damaged);

        var recoveredService = Service();
        Assert.True(recoveredService.SelectCharacter(out var recovered));
        Assert.Equal(500u, recovered!.PlayerGil);
        recoveredService.Save(recovered);
        Assert.Equal(500u, Read(PrimaryPath)!.PlayerGil);

        // Damage the repaired primary before another update. The same valid
        // backup must survive the repair rather than becoming the corrupt file.
        File.WriteAllText(PrimaryPath, damaged);
        Assert.True(Service().SelectCharacter(out var recoveredAgain));
        Assert.Equal(500u, recoveredAgain!.PlayerGil);
    }

    [Fact]
    public void InvalidPrimaryWithoutBackupReturnsNoSnapshotAndCanRecoverOnSave()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PrimaryPath, "{}");
        var service = Service();

        Assert.True(service.SelectCharacter(out var invalid));
        Assert.Null(invalid);
        service.Save(Snapshot(500));
        Assert.DoesNotContain(log.Messages, x => x.Contains("Could not save"));
        Assert.True(Service().SelectCharacter(out var valid));
        Assert.Equal(500u, valid!.PlayerGil);
        Assert.NotEmpty(log.Messages);
    }

    private string PrimaryPath => Path.Combine(directory, $"portfolio-{characterId}.json");
    private PortfolioCacheService Service() => new(directory, () => characterId, log);
    private static SavedPortfolio? Read(string path) => JsonSerializer.Deserialize<SavedPortfolio>(File.ReadAllText(path));

    private static SavedPortfolio Snapshot(uint playerGil) => new(
        [new(1, "Alpha", 0, 10, "Potion", 99, 5_300, 4_699, true, 5m, true)],
        [new(1, "Alpha", 10_000, 5m), new(2, "Beta", 20_000, 3m)],
        playerGil,
        DateTimeOffset.Parse("2026-09-15T12:00:00Z"),
        DateTimeOffset.Parse("2026-09-15T12:03:00Z"),
        true, true, 2,
        [new(10, "Potion", true, 198, 4_699, BagValuationSource.HomeMarket, 5m)]);

    private static void AssertSnapshotEqual(SavedPortfolio expected, SavedPortfolio? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory))
                File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
