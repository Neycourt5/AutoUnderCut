using SmartUndercutBot.Core.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests;

public sealed class ContinuousLoopPolicyTests
{
    [Theory]
    [InlineData(20, 5)]
    [InlineData(60, 12)]
    [InlineData(200, 20)]
    public void ComfortableTargetScalesWithRetainersWithinBounds(int capacity, int expected)
        => Assert.Equal(expected, ResaleStockPolicy.ComfortableBagTarget(capacity));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 2)]
    [InlineData(20, 3)]
    public void PerItemSpareTargetsLeaveRoomForDiversification(int listed, int expected)
        => Assert.Equal(expected, ResaleStockPolicy.ComfortableItemTarget(listed));

    [Fact]
    public void ExistingSettingsEnableComfortableStockOnUpgrade()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{\"Version\":24,\"ProcurementBagBufferStacks\":5}")!;
        config.Normalize();
        Assert.True(config.ContinueShoppingWhenStocked);
        // Compare against a fresh config so this does not need editing every
        // migration; the point is that an upgrade lands on the current version.
        Assert.Equal(new Configuration().Version, config.Version);
    }

    [Theory]
    [InlineData("North-America,Oceania", "North-America")]
    [InlineData("Oceania,Materia,Ravana", "North-America")]
    [InlineData("Aether, Sophia ,Primal", "Aether,Primal")]
    [InlineData("", "North-America")]
    public void SavedScopesExcludeOceania(string oldScope, string expected)
    {
        var config = new Configuration { Version = 23, ProcurementDataCenter = oldScope };
        config.Normalize();
        Assert.Equal(expected, config.ProcurementDataCenter);
    }

    [Fact]
    public void OverdueRetainerCheckRunsImmediatelyAfterBagFill()
    {
        var schedule = new RetainerRunSchedule();
        var now = DateTimeOffset.UtcNow;
        schedule.CompletePass(now, TimeSpan.FromMinutes(5), false);
        Assert.Equal(now.AddMinutes(7), schedule.CompletePass(now.AddMinutes(7), TimeSpan.FromMinutes(5), true));
    }

    [Fact]
    public void BufferSpendingCannotExceedItsOriginalCapitalShareAcrossTrips()
    {
        uint wallet = 995_000;
        ulong owned = 0;
        for (var i = 0; i < 100; i++)
        {
            var budget = ResaleStockPolicy.BufferSpendableGil(wallet, owned, 20m);
            var spend = Math.Min(budget, 50_000u);
            wallet -= spend;
            owned += spend;
        }
        Assert.Equal(199_000UL, owned);
        Assert.Equal(796_000u, wallet);
        Assert.Equal(0u, ResaleStockPolicy.BufferSpendableGil(wallet, owned, 20m));
    }
}
