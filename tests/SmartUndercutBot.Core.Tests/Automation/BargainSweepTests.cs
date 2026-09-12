using SmartUndercutBot.Automation;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Services;
using Xunit;
using Route = SmartUndercutBot.Core.Tests.Automation.ProcurementControllerTests.Route;

namespace SmartUndercutBot.Core.Tests.Automation;

public sealed class BargainSweepTests
{
    [Theory]
    [InlineData(97, 25u)]
    [InlineData(40, 99u)]
    public void SweepsThousandsOfPreferredUnitsBeyondTheOriginalPlanAndSaleSlotCap(int listings, uint quantity)
    {
        using var run = BulkRoute(listings, quantity);
        Complete(run);
        Assert.True(run.Controller.Plan.Orders.Count < listings);
        Assert.Equal(listings, run.Game.Purchases);
        Assert.Equal(listings * quantity, (uint)run.Game.Inventory);
        Assert.Equal((listings * quantity + 98) / 99, (uint)run.Controller.ResaleBagSlots);
        Assert.True(run.Controller.HoldingForResaleStock);
        Assert.Contains(run.Log.Messages, x => x.Contains("BARGAIN SWEEP COMPLETE"));
        Assert.Equal("Siren", run.Game.World);
        // Confirmed rows intentionally remain visible in this fake. They must not
        // be bought twice even when a refresh still exposes them.
        Assert.True(run.Game.Searches.Count(x => x.World == "Cactuar") > listings);
    }

    [Fact]
    public void SmallWalletBuysEveryAffordablePartStackAndPreservesTravelGil()
    {
        using var run = BulkRoute(97, 25);
        run.Game.Gil = 1_235_846;
        Complete(run);
        Assert.Equal(26, run.Game.Purchases);
        Assert.Equal(8048u, run.Game.Gil);
        Assert.True(run.Game.Gil >= run.Config.Current.ProcurementTravelReserve);
    }

    [Fact]
    public void ExistingStockCountsAgainstTheBulkDemandTarget()
    {
        using var run = BulkRoute(97, 25);
        // Already held stock is listed so the trip can start, while demand must
        // still include it. A 700/day market targets 4,900 total units.
        run.Repricing.ListedStock = [new(1, true, 4800, 49), new(999, false, 11, 11)];
        // A real batch of vacancies permits departure despite the healthy mix.
        run.Repricing.LastKnownFreeSaleSlots = 10;
        Complete(run);
        Assert.Equal(4, run.Game.Purchases);
        Assert.Equal(100, run.Game.Inventory);
    }

    [Fact]
    public void NewlyAppearingCheapListingsAreSweptButTheHigherPriceTierIsLeftAlone()
    {
        using var run = BulkRoute(1, 25);
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => [new(0, item, 1, 1, 1799, 25, true, 2248),
                .. (run.Game.Purchases > 0
                    ? new[] { new LivePurchaseListing(1, item, 2, 2, 1799, 99, true, 8905),
                        new LivePurchaseListing(2, item, 3, 3, 1900, 99, true, 9405) }
                    : [])],
            _ => [],
        };
        Complete(run);
        Assert.Equal(2, run.Game.Purchases);
        Assert.Equal(124, run.Game.Inventory);
    }

    [Fact]
    public void ActualBagReserveStopsTheSweep()
    {
        using var run = BulkRoute(97, 25);
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++)
        {
            if (run.Game.Purchases == 7)
                run.Game.FreeInventorySlots = (uint)run.Config.Current.ProcurementInventoryReserve;
            run.Tick(2);
        }
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Equal(7, run.Game.Purchases);
        Assert.Equal("Siren", run.Game.World);
    }

    [Theory]
    [InlineData("tax")]
    [InlineData("quality")]
    [InlineData("price")]
    public void RefreshedListingsMustStillPassTheLivePurchaseGuards(string change)
    {
        using var run = BulkRoute(97, 25);
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => [new(0, item, 1, 1, 1799, 25, true, 2248),
                .. (run.Game.Purchases > 0 ? new[] { new LivePurchaseListing(1, item, 2, 2,
                    change == "price" ? 1900u : 1799u, 25, change != "quality",
                    change == "tax" ? 20_000u : 2248u) } : [])],
            _ => [],
        };
        Complete(run);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(25, run.Game.Inventory);
    }

    [Fact]
    public void BulkPurchasesRespectAnExplicitTripBudget()
    {
        using var run = BulkRoute(97, 25);
        run.Config.Current.ReinvestAvailableGil = false;
        run.Config.Current.ProcurementBudget = 200_000;
        Complete(run);
        Assert.Equal(4, run.Game.Purchases);
        Assert.InRange(run.Controller.Status.GilSpent, 1u, 200_000u);
    }

    [Fact]
    public void AnUncertainBulkPurchaseStopsWithoutSubmittingTheNextOne()
    {
        using var run = BulkRoute(97, 25);
        run.Game.AutomaticPurchaseConfirmation = false;
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.True(run.Controller.RequiresManualRestart);
        Assert.Equal(1, run.Game.Purchases);
        run.Tick(3600);
        Assert.Equal(1, run.Game.Purchases);
        Assert.False(run.Controller.HoldingForResaleStock);
    }

    [Fact]
    public void HomePriceExpiryCannotAuthorizeMoreBulkPurchases()
    {
        using var run = BulkRoute(97, 25);
        run.Controller.RunNow();
        var expired = false;
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++)
        {
            if (!expired && run.Controller.Status.GilSpent > 0)
            {
                expired = true;
                run.Config.Current.HomePriceMaxAgeMinutes = 5;
                run.Tick(301); // expire the quote without tripping the 20-minute watchdog
            }
            else run.Tick(2);
        }
        Assert.True(expired);
        Assert.Equal(1, run.Game.Purchases);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
        Assert.Contains(run.Log.Messages, x => x.Contains("home reference expired"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExistingRelistingStockHoldsShoppingUntilReplacementsAreLow(bool preferred, bool liveTour)
    {
        using var run = BulkRoute(97, 25);
        run.Config.Current.EnableStockAutomation();
        run.Config.Current.ProcurementRules[0].PreferredStock = preferred;
        run.Config.Current.LiveWorldStockHuntEnabled = liveTour;
        run.Config.Current.ShoppingTripMinimumGil = 1_000_000;
        run.Config.Current.ShoppingTripMinimumFreeSaleSlots = 10;
        run.Game.Inventory = 11 * 99; // one sale below the old 12-stack target
        Assert.True(run.Controller.HoldingForResaleStock);
        Assert.Contains("selling existing stock", run.Controller.ShoppingWaitReason);
        run.Tick(3600);
        Assert.Equal(0, run.Game.Scans);
        Assert.Empty(run.Game.Commands);
        run.Game.Inventory = 5 * 99;
        Assert.False(run.Controller.HoldingForResaleStock);
        run.Config.Current.LiveWorldStockHuntEnabled = false;
        // Ordinary stock still waits for a batch of vacancies once it runs low.
        run.Repricing.LastKnownFreeSaleSlots = 10;
        run.Tick();
        Assert.Equal(1, run.Game.Scans);
    }

    [Theory]
    [InlineData(3, 7)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    public void BulkCoverageMigrationPreservesCustomTargets(int oldDays, int expectedDays)
    {
        var config = new Configuration { Version = 43, ProcurementPreferredCoverageDays = oldDays,
            RepeatMinimumMinutes = 5, RepeatMaximumMinutes = 10 };
        config.Normalize();
        Assert.Equal(expectedDays, config.ProcurementPreferredCoverageDays);
        Assert.Equal(44, config.Version);
        Assert.Equal(5, config.RepeatMinimumMinutes);
        Assert.Equal(5, config.RepeatMaximumMinutes);
    }

    [Fact]
    public void StartingStockAutomationUsesFiveMinuteUndercutChecks()
    {
        var config = new Configuration { RepeatMinimumMinutes = 10, RepeatMaximumMinutes = 20 };
        config.EnableStockAutomation();
        Assert.Equal(5, config.RepeatMinimumMinutes);
        Assert.Equal(5, config.RepeatMaximumMinutes);
    }

    private static Route BulkRoute(int listings, uint quantity)
    {
        var run = new Route(priority: true);
        var config = run.Config.Current;
        config.ContinueShoppingWhenStocked = true;
        config.ProcurementRules[0].PreferredStock = true;
        config.ProcurementRules[0].BagReserveQuantity = 0;
        run.Repricing.LastKnownFreeSaleSlots = 0;
        run.Repricing.ListedStock = [new(999, false, 60, 60)];
        run.Game.Gil = 10_000_000;
        run.Game.AutomaticWorldArrival = run.Game.AutomaticPurchaseConfirmation = true;
        run.Game.DemandMarkets = [new(1, "Popcorn", [], Enumerable.Range(1, 3)
            .Select(i => new ProcurementSale(2700, 700, true, DateTimeOffset.UtcNow.AddDays(-i))).ToArray(),
            HqSalesPerDay: 700m)];
        run.Game.LiveProvider = (world, item) => world switch
        {
            "Siren" => [new(0, item, 9999, 9999, 2700, 99, true, 0)],
            "Cactuar" => Enumerable.Range(1, listings).Select(i => new LivePurchaseListing(i - 1, item,
                (ulong)i, (ulong)i, 1799, quantity, true, (uint)decimal.Floor(1799 * quantity * 0.05m))).ToArray(),
            _ => [],
        };
        return run;
    }

    private static void Complete(Route run)
    {
        run.Controller.RunNow();
        for (var i = 0; i < 2000 && run.Controller.IsActive; i++) run.Tick(2);
        Assert.Equal(ProcurementState.Completed, run.Controller.State);
    }
}
