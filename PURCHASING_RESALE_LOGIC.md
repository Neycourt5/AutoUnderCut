# AutoUndercutter — Purchasing, Resale & Prioritization Logic

**Scope:** a factual map of the economic decision system as currently implemented
(config schema `Version = 43`), after the profit-optimization rework described in
`PROFIT_OPTIMIZATION_IMPLEMENTATION.md`.

**v1.0.0.68:** adaptive scheduling now considers preferred listed stock and its
replacement buffer. Full materia shelves no longer force idle gil to wait for ten
vacancies. Preferred purchases use the spendable wallet; non-preferred purchases
retain separate spare-stock budget and capacity limits. Long circuits refresh
aging home anchors before comparison. The margin ladder remains 10% / 14% / 20% /
35% net after fees, with an 8% absolute floor and stronger evidence required for
thin margins. Market volume is a ranking signal, not a promise of personal sales.

**How to read this document.** Claims are tagged:

- **[IMPL]** — current implemented behaviour, verified by reading the code.
- **[CFG]** — behaviour that only occurs under a particular configuration.
- **[DEAD]** — code that exists but cannot execute in the default/shipped configuration.
- **[OBS]** — an observation or inference, not a direct code statement.

References use `path/to/File.cs -> Type.Member` with line numbers as of the current tree.

> **Terminology.** "Sale slot" = one of the 20 market-board slots a retainer has; with 3
> retainers that is 60, the shipped `ProcurementTargetSaleSlots` default. "Landed cost" =
> purchase price plus the buyer fee. "Home world" = the character's own world, where
> everything is resold. "Coverage" = how many days of a market's own observed demand the
> current holdings represent.

---

## 1. Executive Summary

AutoUndercutter is a two-sided FFXIV market bot. It **buys** stock cheaply on other worlds and
**resells** it on the player's home world, continuously undercutting until it sells.

### The loop

```
Retainer pass (reprice + collect gil)  ──►  List bag stock  ──►  Shopping trip  ──►  Return home
        ▲                                                                                  │
        └──────────────────────────────────────────────────────────────────────────────────┘
```

`StockAutomationController` arms the sub-controllers; `ProcurementController` owns shopping,
`AutomationController` owns repricing, `BagListingController` owns listing bag stock.

### The objective

> **Maximise long-run realised gil by keeping capital deployed in high-value, high-volume
> markets that reliably sell.**

ROI is a **safety floor**, not a ranking key. Once a listing clears its margin bar, candidates
compete on expected gil per day, absolute profit, liquidity and how much of the market's own
demand is already held.

### What it considers

Only items with an **enabled `ProcurementRule`**. There is no open-ended market scan in the
buying path. Rules come from five seeded sets plus optional discovery
(`src/SmartUndercutBot/Services/UniversalisService.cs`):

| Set | Seeded as | Bought? |
|---|---|---|
| 6 curated consumables (4 Grade 4 Gemdraughts, Caramel Popcorn, Popoto Potage) — `CreateFavoriteRules` | `PreferredStock=true`, `TourPriority=0`, HQ-required, stack 99, 8 slots | Yes — the portfolio core |
| `General-Purpose *` / `Wide-Spectrum *` dyes — `CreateBuyableDyeRules` | `TourPriority=1`, stack 20, 2 slots, min 50/wk | Yes |
| Materia grades **XI/XII** — `CreateTradeableMateriaRules` | `TourPriority=2`, stack 20, 1 slot, min 50/wk | Yes |
| All other dyes, all ethers, older materia — `CreateLiquidationRules` | `LiquidateOnly=true` | **Never** — sold from bags only |
| 8 named tomestone materials — `CreateTomeMaterialRules` | `LiquidateOnly=true` | **Never** |
| Discovered food/medicine — `MarketDiscoveryPolicy.Propose` **[CFG, off by default]** | `Confidence=Candidate`, `PreferredStock=false`, `TourPriority=3` | Yes, as ordinary stock |

Note that `MaximumSaleSlots` on these rules is now a **floor**, not a ceiling — see §5.2.

### When something is "profitable"

Profitability is enforced **as a price ceiling**, not as a post-hoc score. For each
item/quality the planner computes the highest price it could pay and still clear both the ROI
floor and the flat per-unit profit floor, then considers only listings at or below it. The ROI
floor is drawn from a **four-rung margin ladder** keyed on velocity *and* value per slot, with
an absolute floor beneath everything. See §4.

### When it buys

**One path.** Every observation is remembered and compared at the end of the circuit
(`ProcurementController.Priority.cs -> FinishPriorityScouting` L197), then the winners are
revisited and re-validated live before purchase. The old immediate-buy path for ≥100% ROI
listings has been removed; `ShoppingScoutPolicy.BuysBeforeComparison` is a documented `false`.
[IMPL]

### How much it buys

**It does not choose a quantity.** It buys whole existing listings, filtered to
`listing.Quantity <= rule.TargetStackSize`. Volume is controlled by *how many listings* it
takes, which is governed primarily by **inventory coverage** — days of the market's own demand
already held — plus budget, slots and concentration limits. See §8.

### When and at what price it resells

Everything is listed on the home world and repriced every retainer pass. Pricing is
**depth-adjusted lowest competitor − 1 gil**, floored at
`max(MinimumPrice, AcquisitionFloor, costBasis × (1 + margin%))`. See §6.

### How it chooses between competing opportunities

A **single scalar score** plus a greedy pass:

```
AllocationScore = ExpectedGilPerDay × tierWeight        (Core 1.25, Secondary 1.0, Opportunistic 0.6)
```

ordered by score, then absolute profit, then liquidity, then capital deployed. Tier is a
weight, not a sort key: an extraordinary non-Core opportunity can beat a poor Core one. [IMPL]

### Role of ROI vs absolute gil vs velocity

- **ROI** is a *gate only* (a price ceiling), never a ranking key.
- **Expected gil per day** is the primary ranking signal.
- **Absolute gil profit** is the first tie-break.
- **Sales velocity** is the most pervasive signal: it sets the margin bar, sizes inventory via
  coverage, derives the per-item slot cap, drives `ExpectedGilPerDay`, shapes the resale anchor
  through market depth, and orders the scan.

### Emergent strategy

> Size every position against what the market actually absorbs, not against a slot count.
> Accept a thin margin on stock that turns over daily, because the gil comes straight back.
> Rank on gil per day, so a 400% return on a trinket loses to a 15% return on a stack of raid
> food. Cap cheap arbitrage at a tenth of the portfolio and half a day of its own demand.
> Deploy capital until nothing qualifying is left — but never spend merely to be invested.
> **An empty slot is explicitly preferred to a slot of junk.**

---

## 2. Complete Purchase Decision Pipeline

### 2.1 Trip-level gates (before any item is examined)

`ProcurementController.cs -> TryAutomaticStart`

```
IF IsStartBlocked OR !AllowAutomaticPurchases OR !AutomaticProcurementEnabled
   OR !IsRetainerListOpen OR repricing.IsActive OR repricing.RequiresManualRestart
   OR !playerState.IsLoaded                                     -> RETURN
IF repricing.LastKnownFreeSaleSlots is null                     -> RETURN   (no verified capacity yet)
IF SpendableGil(...) == 0                                       -> RETURN
IF LiveWorldStockHuntEnabled                                    -> live-hunt branch  [DEAD in shipped config]
IF !newlyAvailableCapacity AND !incomeArrived AND now < nextAutomaticScan -> RETURN
IF HoldingForSaleSlots OR HoldingForGil                         -> RETURN
-> StartScan(AutomaticPurchase)
```

| Gate | Condition | Meaning |
|---|---|---|
| `HoldingForSaleSlots` | Fewer than 10 free slots **and** no preferred replacement shortfall | Healthy portfolios wait for vacancies; thin preferred positions can shop sooner |
| `HoldingForGil` (L196) | `ShoppingBudget < ShoppingTripMinimumGil` (1,000,000) | Travelling with pocket change wastes the trip |

`StartScan` (L307) first calls **`ReconcilePositionCosts()`** (L1948), which retires tracked
cost basis for stock that has since sold. See §6.4.

### 2.2 The circuit

`ProcurementController.Priority.cs -> BeginPriorityShopping` (L304)

1. Filter rules: enabled, non-liquidate, **and** either in the "snipe block"
   (`PreferredStock || AlwaysScout`) or showing ≥ `MinimumWeeklyUnitsSold` in 7-day home sales.
2. Order: snipe-block first → descending home sales/day → `TourPriority` → name.
3. Scan the **home world** first to establish resale anchors.
4. Travel the circuit one data center at a time (`ShoppingScoutPolicy.BuildRoute`), home DC first.
5. Per away world, price up to `PriorityItemsPerWorld` (14) items: the snipe block **always**,
   plus hinted bargains, plus busiest secondary lines, plus a rotation.
6. **Nothing is bought during scouting.** `ObservePriorityItem` (L478) records the observation,
   logs the economics, and advances.
7. Before comparison, if an observed item's home anchor has used half its freshness
   window, return to the home board and refresh the observed item set once. Old
   anchors are cleared before reading, so a failed search cannot fall back to them.
   Rebuild the plan against these prices, then revisit selected deals for live
   price/tax checks. The final purchase guard still rejects expired home quotes.

### 2.3 Per-item pipeline — one item, discovery → BUY/REJECT

`SmartUndercutBot.Core/Services/ProcurementPlannerService.cs -> BuildPlan` (L22)

```
STEP 0  Request sanity                                     [L26-29]
        GilBudget==0 OR FreeSaleSlots<=0 OR FreeInventorySlots<=0
        OR !FeeModel.IsValid  (tax [0,100), fee [0,100])
        OR MinimumRoiPercent outside [0,1000]
        OR MaximumWeeklySalesSharePercent outside (0,100]      -> EMPTY PLAN

STEP 1  Rule lookup                                        [L38]
        !Enabled OR ItemId==0 OR LiquidateOnly                  -> REJECT

STEP 2  Quality eligibility        ResaleStockPolicy.BuyableQuality
        BuyHighQualityOnly=true and item HAS an HQ form -> NQ rejected
        item has NO HQ form (dyes)                      -> NQ allowed

STEP 3  Demand gate                                        [L48]
        sales = home 7-day sales, matching quality, price>0, qty>0
        SUM(quantity) < rule.MinimumWeeklyUnitsSold             -> REJECT

STEP 4  Owned units + weekly-share backstop                [L52-63]
        ownedUnits = listed + bagged + already planned
        observedLimit = MAX(TargetStackSize, FLOOR(weeklyUnits × share%))
        (recorded; only enforced when coverage cannot size a position — see STEP 10)

STEP 5  Depth-adjusted resale anchor                       [L65-83]
        targetSalePrice = MEDIAN(home 7-day sale prices)
        IF HomeWorld set:
            homeListings = home-world listings, same quality, not our retainers
            homeLowest   = HomePriceReference.DepthAdjustedLowest(
                               homeListings, salesPerDay, AnchorAbsorptionDays)
            targetSalePrice = MIN(targetSalePrice, homeLowest − 1)
        targetSalePrice == 0                                    -> REJECT

STEP 6  Margin bar + price ceiling                         [L154-171]
        stackSize             = MAX(1, rule.TargetStackSize)
        referenceValuePerSlot = targetSalePrice × stackSize     ; the MARKET's value, not the listing's
        reliableHistory       = (recent 7-day sale entries >= 3)
        requiredRoi = PortfolioPolicy.RequiredRoiPercent(policy, rule.PreferredStock,
                                                        salesPerDay, referenceValuePerSlot)
        IF !reliableHistory: requiredRoi = MAX(requiredRoi, StandardRoiPercent)
        ceiling = FeeModel.MaximumUnitPrice(targetSalePrice, requiredRoi, minProfitPerUnit)
        IF rule.MaximumUnitPrice > 0: ceiling = MIN(ceiling, rule.MaximumUnitPrice)
        ceiling == 0                                            -> REJECT

STEP 7  Per-listing filter                                 [L173-180]
        REJECT listing IF  ItemId mismatch
                        OR PricePerUnit == 0
                        OR PricePerUnit > ceiling            <-- ROI/profit enforced HERE
                        OR RetainerId ∈ OwnedRetainerIds     <-- never buy from self
                        OR WorldName blank
                        OR Quantity == 0
                        OR IsHighQuality != quality
                        OR Quantity > stackSize

STEP 8  Costing                                            [L182-189]
        landedCost  = FeeModel.LandedCost(price, qty)      = CEIL(price × qty × 1.05)
        netProceeds = FeeModel.NetProceeds(anchor, qty)    = FLOOR(anchor × 0.95) × qty
        REJECT IF netProceeds <= landedCost
        REJECT IF NetRoiPercent(netProceeds, landedCost) < requiredRoi     <-- belt and braces
        REJECT IF netProceeds − landedCost < minProfitPerUnit × qty
        expectedProfit = netProceeds − landedCost

STEP 9  Tier + slot-value gate      AddCandidate           [L219-242]
        tier = PortfolioPolicy.ClassifyCandidate(...)
        IF tier != Core AND ExpectedProfitPerSaleSlot < gates.MinimumProfitPerSaleSlot
                                                                -> REJECT "insufficient slot value"
        AllocationScore = ExpectedGilPerDay × policy.ScoreWeightFor(tier)
        -> CANDIDATE

STEP 10 Allocation — one greedy pass  Allocate             [L258-369]
        order by: AllocationScore desc, ExpectedProfit desc, EstimatedDaysToSell asc,
                  CapitalAtRisk desc, TourPriority asc, ItemId, ListingId
        skip conditions, in order:
          orders.Count >= slotLimit                          -> "no free sale slot remains"
          Opportunistic AND tierSlots + slots > OpportunisticCap -> "opportunistic portfolio cap reached"
          Opportunistic AND oppSpent + cost > budget × Opportunistic% -> "opportunistic capital cap reached"
          itemSlots[item] >= EffectiveMaximumSlots(policy, rule, candidate) -> "already holding N of M sale slots"
          spent + cost > budget                              -> "remaining budget does not cover this stack"
          coverage > 0 AND !InventoryCoveragePolicy.CanAdd(...) -> "demand coverage reached (X d held, Y d target)"
          coverage == 0 AND weekly-share exceeded            -> "weekly market-share limit reached"
        -> else ADD

STEP 11 Live re-validation before submit  PollListings      [L1238]
        -> BUY or SKIP
```

### 2.4 Step 11 — the live pre-purchase guard, in order

`ProcurementController.cs -> PollListings` (L1238). **Sequential early returns; order matters.**

| # | Check | On failure |
|---|---|---|
| 1 | comparison pass under its 20-minute limit | FinishShopping |
| 2 | home reference still fresh (≤ `HomePriceMaxAgeMinutes`, 30) | SkipCurrentOrder |
| 3 | `AllowAutomaticPurchases` still armed | FinishShopping |
| 4 | still on the right world, board open, Lifestream idle | FinishShopping |
| 5 | fill order & empty retainer slots already covered by bags | Skip |
| 6 | fill order & tier == Opportunistic | Skip |
| 7 | tier == Opportunistic & no cap headroom | Skip |
| 8 | rule exists / enabled / non-liquidate / quality OK / under **`EffectiveMaximumSlots`** | Skip |
| 9 | clamp `MaximumAcceptableUnitPrice` and `Quantity = MIN(order.Quantity, TargetStackSize)` | — |
| 10 | live listings ready (retry ×3); **fails closed** on timeout | Skip |
| 11 | `TrySelectLiveListing` matches the plan | Skip |
| 12 | independent re-validation of the returned listing (item, quality, quantity, price ≤ ceiling, not our retainer) | Skip |
| 13 | **demand-coverage re-check** against live holdings | Skip |
| 14 | **profit re-check** with the **server-reported buyer tax** and the observed sale tax | Skip |
| 15 | slots + `ProcurementInventoryReserve` | FinishShopping |
| 16 | `totalCost > SpendableGil()` or `> wallet` | Skip / FinishShopping |
| 17 | `SubmitPurchase` | — |

Check 14 (L1373):

```
totalCost           = live.PricePerUnit × live.Quantity + live.TotalTax     ; SERVER-reported
expectedNetProceeds = configuration.Current.Fees.NetProceeds(order.TargetSalePrice, live.Quantity)
IF expectedNetProceeds < totalCost × (1 + RequiredPurchaseRoi(order)/100)
   OR expectedNetProceeds − totalCost < minProfitPerUnit × live.Quantity   -> SKIP
```

`RequiredPurchaseRoi` (L1504) re-derives the bar from live configuration, using the **same
reference stack value** the planner used, and — for a fill order — the **same relaxed policy**,
so the planner and this check are mathematically identical. The bar recorded on the order is
kept as a floor, so a stale plan can never buy under a laxer rule than the current one. [IMPL]

### 2.5 Ordering concern: could mediocre beat better?

**No.** [OBS] There is no path that commits capital before the whole circuit has been compared.
The only remaining ordering effect is the greedy pass itself: a higher-scoring candidate takes
budget first, which is the intended behaviour.

---

## 3. Opportunity Scoring / Prioritization

### 3.1 The score — marginal, not standalone

The score is computed **during allocation**, not when the candidate is built, because what one
more stack is worth depends on the inventory it will land behind.
`ProcurementPlannerService.ScoreOf` (L255), `PortfolioPolicy.MarginalDaysToClear` (L108) and
`PortfolioPolicy.MarginalGilPerDay` (L124):

```
owned               = units listed + bagged + ALREADY COMMITTED EARLIER IN THIS PASS
MarginalDaysToClear = CLAMP((owned + Quantity) / SalesPerDay, 0.25, 30)
                    = 30                       when SalesPerDay <= 0 or Quantity == 0
MarginalGilPerDay   = ExpectedProfit / MAX(1.0, MarginalDaysToClear) / Slots
AllocationScore     = MarginalGilPerDay × ScoreWeightFor(Tier)
```

Two separate guards, against opposite failure modes: [IMPL]

- The `MAX(1.0, ...)` normalisation removes the `profit × 4` bonus the original `MAX(0.25, ...)`
  handed to any stack that would clear in under six hours. A tiny fast lot cannot manufacture a
  profit rate out of replenishment that would need another shopping trip to realise.
- The **marginal** numerator stops a large position claiming the rate of its first stack. Our own
  stacks compete with each other: with 200 units of a 100/day market already listed, the next
  stack does not finish selling for three days, and the capital is committed for all of it.

Note the identity `MarginalDaysToClear(0, q, v) == DaysToSell(q, v)`. A market we hold none of is
scored exactly as the standalone model scored it; only repeat stacks move. That is what makes an
excellent market's attractiveness decay as its position fills, so other opportunities overtake it
without any category quota saying that they must.

`ExpectedGilPerDay` still exists as the standalone figure and is reported in the log alongside
the marginal one, but nothing ranks on it. [IMPL]

### 3.2 Derived per-order metrics

`ProcurementModels.cs -> ProcurementOrder` (L232+)

```
CapitalAtRisk             = LandedCost (fee included), or FeeModel.Default.LandedCost(price, qty)
ExpectedResaleValue       = TargetSalePrice × Quantity
ExpectedNetProceeds       = CapitalAtRisk + ExpectedProfit
ResaleValuePerSaleSlot    = ExpectedResaleValue / Slots
ExpectedProfitPerSaleSlot = ExpectedProfit / Slots
EstimatedDaysToSell       = PortfolioPolicy.DaysToSell(Quantity, SalesPerDay)
ExpectedGilPerDay         = ExpectedProfit / MAX(1.0, EstimatedDaysToSell) / Slots   ; standalone, reported only
MarginalDaysToClear       = see 3.1                                                  ; what the allocator uses
MarginalGilPerDay         = see 3.1                                                  ; what the allocator ranks on
InventoryCoverageDays     = OwnedUnitsBefore / SalesPerDay
CoverageDaysAfterPurchase = (OwnedUnitsBefore + Quantity) / SalesPerDay
NetRoiPercent             = (ExpectedNetProceeds − CapitalAtRisk) / CapitalAtRisk × 100
RoiPercent                = NetRoiPercent                      ; the SAME number
```

`SalesPerDay` = `SalesVelocityPolicy.DailyUnits` — Universalis `nq/hqSaleVelocity` when it is
**strictly positive** and ≤ 1e9, else 7-day recent sales ÷ 7. A reported `0` means "no data
window for this quality" as often as it means "nothing sold", so it defers to recorded sales
rather than vetoing them; with no recorded sales the answer is still zero, and one quality is
never lent the other's rate. [IMPL]

> `SalesPerDay` is the **whole home world's** rate, not our share of it. Every holding-time and
> coverage figure is optimistic by whatever fraction of the market we actually capture. This is
> measured-but-unapplied; see §17. [OBS]

> There is exactly **one** ROI definition, on landed (post-fee) cost. The old pre-fee display
> basis is gone. [IMPL]

### 3.3 Tier assignment — a weight, not a veto

`PortfolioPolicy.ClassifyCandidate` (L101)

```
IF rule.PreferredStock            -> Core
ELSE IF salesPerDay   >= policy.HighVolumeMinimumSalesPerDay      (10)
     AND valuePerSlot >= policy.HighVolumeMinimumValuePerSlot     (150,000)
     AND profitPerSlot>= gates.MinimumProfitPerSaleSlot           (2,500)
                                  -> Secondary
ELSE                              -> Opportunistic
```

For **already-owned** stock, `ClassifyHolding` (L122) omits the profit term (profit is sunk).
An item with no known market data classifies as Opportunistic — so a retainer full of listed
dye actively consumes the opportunistic cap and blocks buying more.

Tier feeds `ScoreWeightFor` (Core 1.25 / Secondary 1.0 / Opportunistic 0.6) and
`CoverageDaysFor` (3 / 1.5 / 0.5 days). `PortfolioPolicy.Rank` still exists and is used for
per-item display ordering during scouting, but **not** in allocation. [IMPL]

### 3.4 The allocator — repeated selection, then a bounded repair

`ProcurementPlannerService.Allocate` (L279) runs two stages.

**Stage 1, `SelectGreedily` (L315).** Not a static sort. Each iteration re-scores every surviving
candidate against the inventory that now exists — including everything committed earlier in the
same pass — and commits the best:

```
while slots remain and candidates remain:
    for each surviving candidate:
        apply the capacity and concentration limits    (drop permanently if failed)
        held  <- units listed + bagged + committed so far this pass
        score <- MarginalGilPerDay(profit, held, quantity, salesPerDay) × tierWeight
    commit the best-scoring candidate; add its quantity to `held` for that market
```

Ties break, in order: `ExpectedProfit` desc, `MarginalDaysToClear` asc, `CapitalAtRisk` desc
(deploying capital stays a late tie-break, never a reason), `TourPriority` asc, then item id,
listing id and world name. The order is total, so allocation is deterministic.

Every rejection is a **capacity or concentration limit**, not a preference; preference has
already been expressed in the score. All of them are monotone — holdings, spend and slots only
grow — so a candidate that fails one can never become feasible later and is dropped for good. The
one exception is the budget check, which keeps the candidate in the pool because stage 2 may free
the gil it needs. Complexity `O(k × n)` for `k` slots filled and `n` candidates.

**Stage 2, `ImproveUnderConstrainedCapital` (L469).** Greedy takes the best stack it can afford,
which under a tight budget can spend on one candidate worth 300k/day what would have bought two
worth 200k/day each. The repair re-runs the *same* selection routine a few times with one
market's slot allowance reduced, and adopts the result only on a strict improvement:

```
objective(basket) = Σ marginal, class-weighted gil/day of every stack, in selection order

for at most 3 rounds:
    if nothing was blocked on budget: stop
    for each market in the basket, richest first, at most 8:
        try it capped at (its stacks − 1), and at 0
    adopt the best strictly-improving alternative, else stop
```

The neighbourhood is a **per-market slot cap**, not a banned listing: banning the listing greedy
picked achieves nothing when the market has an interchangeable sibling, because the sibling
simply takes the slot. Because alternatives are produced by `SelectGreedily` itself, every
budget, sale-slot, coverage, per-item concentration, opportunistic slot and capital, quality and
rule constraint is enforced identically — there is no second constraint implementation to drift.
At most 48 extra selection runs, and normally **zero**: it is skipped entirely unless the budget
actually blocked something, so a large wallet behaves exactly as plain greedy did. [IMPL]

> The objective maximises gil per day, so under a tight budget it can accept **less absolute
> profit per trip** in exchange for faster capital turnover. That is the intended trade — long-run
> realised gil under limited capital — but it is a real one. [OBS]

### 3.5 Complete list of priority influences

| Variable | Where it enters | Effect |
|---|---|---|
| ROI % | Step 6 price ceiling; Step 8 re-check; `RequiredPurchaseRoi` | **Gate only**, never ranked |
| Expected gil/day | `AllocationScore` | **Primary ranking key** |
| Absolute gil profit | Tie-break 2 | Ranked |
| Holding period | Tie-break 3 | Ranked |
| Capital deployed | Tie-break 4 | Ranked, deliberately late |
| Sales velocity | Margin bar, tier, coverage sizing, slot cap, anchor depth, gil/day, scan order | Strongest single signal |
| Owned units / coverage | `InventoryCoveragePolicy.CanAdd`; live re-check | Hard gate |
| Market depth below a price | `DepthAdjustedLowest` | Sets the anchor |
| Historical sales | Median → anchor; count ≥3 → `reliableHistory` | Anchor + margin bar |
| Market volume (7-day units) | Step 3 gate; weekly-share backstop | Gate |
| Value per sale slot | Margin ladder; tier | Gate |
| Stack size | `TargetStackSize` caps listing size and the reference value | Gate |
| Inventory / retainer slots / gil | `slotLimit`, budget | Hard caps |
| Item category | Seeded `TourPriority` / `PreferredStock` | Tie-break / weight |
| `PreferredStock` | Tier → weight, coverage target, margin bar | Weight, **not** a veto |
| Whitelist / blacklist | The rule list; `LiquidateOnly`, `Enabled=false`, `ExcludedWorlds` | Absolute |

---

## 4. Profit and ROI Math

### 4.1 The one fee model

`SmartUndercutBot.Core/Services/MarketEconomics.cs -> FeeModel`

| Quantity | Value | Source |
|---|---|---|
| Market tax (sale) | `Configuration.ObservedMarketTaxPercent`, default **5%** | **Read from the game's retainer sell window** (`AutomationController.CaptureSellerFee`) and persisted |
| Buyer fee (purchase, planning) | **5%** | `FeeModel.Default` |
| Buyer tax (purchase, at the board) | **server-reported** | `live.TotalTax` |

`Configuration.Fees` is the single accessor; every plan request, the live re-check, the
displayed ROI and the resale floor read it. The scattered `0.95`, `1.05` and `÷0.95` literals
that used to live in `ProcurementController`, `ProcurementController.Priority`,
`ProcurementPriceSafety` and `ShoppingScoutPolicy` are gone. [IMPL]

**Still not modelled:** teleport fares (only a flat `ProcurementTravelReserve` of 5,000 gil is
withheld) and retainer venture costs. [OBS]

### 4.2 The formulas

```
netUnitProceeds = FLOOR(targetSalePrice × (1 − marketTax/100))
buyerFeeMult    = 1 + buyerFee/100
landedCost      = CEIL(pricePerUnit × quantity × buyerFeeMult)
netProceeds     = netUnitProceeds × quantity
expectedProfit  = netProceeds − landedCost                      ; only if netProceeds > landedCost
NetRoiPercent   = expectedProfit / landedCost × 100

roiCeiling      = FLOOR(netUnitProceeds / (1 + requiredRoi/100) / buyerFeeMult)
profitCeiling   = FLOOR(MAX(0, netUnitProceeds − minProfitPerUnit) / buyerFeeMult)
ceiling         = MIN(roiCeiling, profitCeiling [, rule.MaximumUnitPrice])
```

**Rounding:** `FLOOR` on proceeds and ceilings (conservative); `CEIL` on cost (conservative).

### 4.3 The margin ladder

`PortfolioPolicy.RequiredRoiPercent` (L51)

```
liquid   = salesPerDay          >= HighVolumeMinimumSalesPerDay        (10/day)
valuable = referenceValuePerSlot >= HighVolumeMinimumValuePerSlot      (150,000 gil)

(liquid && valuable && preferred) -> CoreHighVolumeRoiPercent          10%
(liquid && valuable)              -> HighVolumeRoiPercent              14%
(!valuable)                       -> LowValueRoiPercent                35%
(valuable && !liquid)             -> StandardRoiPercent                20%

required = MAX(that, AbsoluteMinimumRoiPercent)                         8%
```

Plus: fewer than three recent sales forces `StandardRoiPercent`. And `referenceValuePerSlot`
uses the rule's **target stack size**, not the observed listing quantity, so a market cannot
slide between rungs depending on which listing happened to be in front of it. [IMPL]

The top-up pass (§8.4) applies `ProcurementEconomicPolicy.RelaxedTo(ProcurementFillRoiPercent)`,
which lowers the first, second and fourth rungs to the fill percentage and leaves
`LowValueRoiPercent` and `AbsoluteMinimumRoiPercent` untouched.

### 4.4 Worked example — Grade 4 Gemdraught, HQ

Home median sale 22,000; depth-adjusted cheapest non-self home listing 21,500;
`SalesPerDay = 40`; `TargetStackSize = 99`; `minProfitPerUnit = 100`; `PreferredStock = true`.

```
targetSalePrice       = MIN(22,000, 21,500 − 1) = 21,499
netUnitProceeds       = FLOOR(21,499 × 0.95) = 20,424
referenceValuePerSlot = 21,499 × 99 = 2,128,401   -> valuable
salesPerDay 40 >= 10                              -> liquid
preferred                                         -> requiredRoi = 10%
roiCeiling            = FLOOR(20,424 / 1.10 / 1.05) = 17,683
profitCeiling         = FLOOR((20,424 − 100) / 1.05) = 19,356
ceiling               = 17,683
```

A listing of 99 @ 15,000:

```
landedCost      = CEIL(15,000 × 99 × 1.05) = 1,559,250
netProceeds     = 20,424 × 99             = 2,021,976
expectedProfit  = 462,726
NetRoiPercent   = 462,726 / 1,559,250 × 100 = 29.68%      (reported AND enforced)
EstimatedDaysToSell = 99 / 40 = 2.475
ExpectedGilPerDay   = 462,726 / 2.475 = 186,960
AllocationScore     = 186,960 × 1.25 = 233,700
```

### 4.5 Worked example — General-Purpose dye, NQ

Home median 6,600; depth-adjusted cheapest home listing 6,600; `SalesPerDay = 31`;
`TargetStackSize = 20`.

```
targetSalePrice       = 6,599
netUnitProceeds       = FLOOR(6,599 × 0.95) = 6,269
referenceValuePerSlot = 6,599 × 20 = 131,980  -> BELOW the 150,000 floor
                                              -> requiredRoi = LowValueRoiPercent = 35%
roiCeiling            = FLOOR(6,269 / 1.35 / 1.05) = 4,422
```

A listing of 20 @ 1,148:

```
landedCost      = 24,108
netProceeds     = 125,380
expectedProfit  = 101,272
NetRoiPercent   = 420.1%                          <-- spectacular percentage
EstimatedDaysToSell = 20 / 31 = 0.645
ExpectedGilPerDay   = 101,272 / MAX(1.0, 0.645) = 101,272     (old model: 405,088)
Tier                = Opportunistic (131,980 < 150,000)
AllocationScore     = 101,272 × 0.6 = 60,763
Coverage target     = 0.5 d × 31 = 16 units  -> roughly ONE stack, ever
```

It is bought when there is room. It receives 0.6× weight, one stack, and a tenth of the
portfolio at most. [OBS] This is the anti-trap mechanism, now expressed as economics rather
than as tier dominance.

---

## 5. Minimum / Maximum Limits

**H** = hard-coded, **U** = user-configurable, **C** = computed.

### 5.1 Margins and economics

| Setting | Default | Kind | Meaning |
|---|---|---|---|
| `ProcurementFastMoverRoiPercent` | 10% | U (8–1000) | Bar for preferred + liquid + valuable stock |
| `ProcurementHighVolumeRoiPercent` | 14% | U (8–1000) | Bar for unpinned liquid + valuable stock |
| `ProcurementMinimumRoiPercent` | 20% | U (0–1000) | Bar for ordinary opportunities |
| `ProcurementLowValueRoiPercent` | 35% | U (8–1000) | Bar for cheap stock |
| `ProcurementAbsoluteMinimumRoiPercent` | 8% | U (8–1000) | Nothing is ever bought below this |
| `ProcurementFillRoiPercent` | 10% | U (0–1000) | Relaxed bar for the top-up pass |
| `ProcurementMinimumProfitPerUnit` | 100 | U (0–100M) | Flat gil/unit floor |
| `ProcurementMinimumProfitPerSaleSlot` | 2,500 | U (≤100M) | Slot-worthiness gate for non-Core |
| `ObservedMarketTaxPercent` | 5% | C | Recorded from the game; reset if outside [0,100) |
| `ProcurementHighVolumeMinimumSalesPerDay` | 10 | U (10–10,000) | Velocity half of "high-volume" |
| `ProcurementHighVolumeMinimumValuePerSlot` | 150,000 | U (150k–100M) | Value half of "high-volume" |

### 5.2 Inventory sizing

| Setting | Default | Kind | Meaning |
|---|---|---|---|
| `ProcurementPreferredCoverageDays` | 3.0 | U (0.25–7) | Days of demand held in Core stock |
| `ProcurementSecondaryCoverageDays` | 1.5 | U (0.25–7) | Days of demand held in Secondary stock |
| `ProcurementOpportunisticCoverageDays` | 0.5 | U (0.1–2) | Days of demand held in cheap stock |
| `ProcurementCoverageOvershootDays` | 1.0 | U (0–1) | How far one stack may carry holdings past target |
| `ProcurementEmergencyMaximumSlotsPerItem` | 20 | U (1–60) | Hard concentration limit |
| `ProcurementAnchorAbsorptionDays` | 0.5 | U (0–0.5) | Cheap competing stock the market swallows |
| `rule.MaximumSaleSlots` | 8 / 2 / 1 / 5 | U (1–60) | **A floor** beneath the demand-derived cap |
| `ProcurementWeeklySalesSharePercent` | 25% | U (1–100) | Backstop; only used when velocity is unusable |
| `PreferredPortfolioTargetPercent` | 75% | U (0–100) | Core slot target (reporting + fill shaping) |
| `OpportunisticPortfolioMaximumPercent` | 10% | U (0–100) | Cheap-stock slot **and capital** cap |

### 5.3 Capital, capacity and travel

| Setting | Default | Kind | Meaning |
|---|---|---|---|
| `ProcurementBudget` | 5,000,000 | U | Per-trip cap **when reinvest off** |
| `ReinvestAvailableGil` | true | U | Ignore the per-trip cap, spend the wallet |
| `ProcurementTravelReserve` | 5,000 | U | Always withheld |
| `ProcurementBufferGilPercent` | 20% | U | Cap on spend once bags cover slots (Core exempt) |
| `ProcurementBufferValueTarget` | 1,000,000 | U | Buffer must be worth this |
| `ProcurementBagBufferStacks` | 5 | U (0–50) | Spare stacks when not "continue stocked" |
| `ProcurementTargetSaleSlots` | 60 | U (1–200) | Portfolio size |
| `ProcurementInventoryReserve` | 10 | U (1–100) | Bag slots kept free |
| `ShoppingTripMinimumFreeSaleSlots` | 10 | U (0–60) | Wait for vacancies unless continuous shopping needs preferred replacements |
| `ShoppingTripMinimumGil` | 1,000,000 | U (≤100M) | Won't travel below this |
| `HomePriceMaxAgeMinutes` | 30 | U (5–30) | Resale-anchor staleness |
| `ScoutKnowledgeMaxAgeHours` | 24 | U (1–168) | Away-observation reuse |
| `PriorityWorldsPerTrip` / `PriorityMinutesPerTrip` | 31 / 180 | U | Circuit length |
| `PriorityItemsPerWorld` | 14 | U (1–40) | Items priced per world |
| Excluded worlds | Bismarck, Ravana, Sephirot, Sophia, Zurvan | **H** | Never shop there |

### 5.4 Safety and pricing

| Setting | Default | Kind | Meaning |
|---|---|---|---|
| `MinimumDaysToSell` / `MaximumDaysToSell` | 0.25 / 30 | **H** | Reporting clamps on `EstimatedDaysToSell` |
| `ProfitNormalizationDays` | 1.0 | **H** | Scoring denominator floor |
| `OutlierFractionOfMedian` | 0.5 | **H** | Anchor outlier cut (`WithoutOutliers`) |
| `MaximumListingPrice` | 999,999,999 | **H** | Game cap |
| `MaximumCuratedUnitPrice` | 1,000,000 | **H** | Curated sanity ceiling |
| `PriceWarDropPercent` | 60% | U (0–<100) | Price-war trigger |
| `PricingRule.UndercutAmount` | 1 | U | Undercut step |
| `MarketRequestTimeoutSeconds` / retries | 6 / 2 | U | Live board wait |
| listing search retries | 3 | **H** | `RetryListingRequest` |
| comparison pass limit | 20 min | **H** | Buying phase time box |

### 5.5 Discovery confidence

| Constant | Value | Meaning |
|---|---|---|
| `CandidateMinimumSalesPerDay` | 50 | Floor to be proposed at all |
| `CandidateMinimumStackValue` | 150,000 | Floor to be proposed at all |
| `ProvenMinimumSalesPerDay` | 75 | Promotion floor |
| `ProvenMinimumStackValue` | 300,000 | Promotion floor |
| `ProvenMinimumGilPerDay` | 50,000 | Promotion floor (computed at the *absolute minimum* margin) |
| `ProvenMinimumConfirmations` | 5 | Agreeing refreshes required |
| `ProvenMaximumPriceSpreadPercent` | 25% | Price stability required |
| `MaximumDiscoveredRules` | 12 | Discovered list cap |

---

## 6. Reselling / Listing Logic

### 6.1 Where prices come from

Repricing uses **live in-game Compare Prices** (`MarketDataService.GetSnapshotAsync`), not
Universalis. Bag listing uses Universalis for the home world
(`BagListingController.QueuePricedStock`).

### 6.2 Competitor selection

`PricingStrategyService.Evaluate`:

```
competitors = market.Listings WHERE
      Quantity > 0
  AND PricePerUnit ∈ (0, 999,999,999]
  AND IsQualityAllowed(theirHq, ourHq, rule.QualityFilter)   ; default SameQuality
  AND RetainerId == 0 OR RetainerId ∉ OwnedRetainerIds       ; exclude our retainers
  AND RetainerId != 0 OR RetainerName != ourRetainerName     ; name fallback
```

- **HQ/NQ matters**: default `QualityFilterMode.SameQuality`.
- **Stack depth now matters**: `lowest` is `DepthAdjustedLowest(competitors, market.AbsorbableUnits)`,
  so a 1-unit undercut in a market that turns over hundreds of units a day does not drag a
  99-stack down. `AbsorbableUnits` is supplied by the caller as
  `MIN(stackSize / 2, salesPerDay × ProcurementAnchorAbsorptionDays)` and is `0` when unknown,
  which degrades exactly to the old "cheapest listing wins". [IMPL]
- **Own listings ARE detected**, by retainer ID with a retainer-name fallback.

### 6.3 The pricing algorithm, in order

```
1. Validate listing/rule bounds                        -> InvalidData
2. floor = CalculateFloor(listing, rule)
       configured = MAX(rule.MinimumPrice, rule.AcquisitionFloor)
       costBasis  = listing.AcquisitionCost != 0 ? it : rule.CostBasis
       IF costBasis == 0 -> floor = MAX(1, configured)
       ELSE floor = MAX(configured, CEIL(costBasis × (1 + MinimumMarginPercent/100)))
3. Build competitor set (above)
4. competitors empty                                   -> NoMarketData (no change)
5. lowest = DepthAdjustedLowest(competitors, AbsorbableUnits)
6. Price-war test: lowest < historicalMedian × (1 − PriceWarDropPercent/100)
       IF war AND action == LeaveUnchanged             -> PriceWar (no change)
       IF war AND action == MatchProtectedFloor        -> Update to MAX(floor, protected)
7. currentPrice <= lowest                              -> NoChange
8. WithinTolerance(current, lowest)                    -> WithinTolerance
9. rawTarget = Mode == MatchLowest ? lowest : (lowest > UndercutAmount ? lowest − UndercutAmount : 1)
10. rawTarget < floor                                  -> BelowFloor (no change)
11. rounded = RoundDown(rawTarget, Rounding)           ; None | EndIn99 | EndIn999
12. target = MAX(floor, rounded)
13. target > 999,999,999                               -> InvalidData
14. target == currentPrice                             -> NoChange
15. -> Update(target)
```

### 6.4 Cost basis — weighted average, not a ratchet

After a confirmed buy (`ProcurementController.PollPurchase` L1415 →
`PositionCostPolicy.RecordPurchase`):

```
landedCost  = live.PricePerUnit × live.Quantity + live.TotalTax          ; SERVER-reported tax
unit        = CEIL(landedCost / quantity)
blendUnits  = MIN(heldUnits, rule.CostBasisUnits)
CostBasis   = (holdingsKnown && CostBasis > 0)
              ? CEIL((CostBasis × blendUnits + landedCost) / (blendUnits + quantity))
              : MAX(CostBasis, unit)                                     ; protective fallback
CostBasisUnits   = blendUnits + quantity
AcquisitionFloor = ProcurementPriceSafety.MinimumResalePrice(CostBasis, requiredRoi, minProfit, fees)
                 = CEIL( CEIL(MAX(cost × (1+roi/100), cost + minProfit)) / (1 − marketTax/100) )
```

`MinimumMarginPercent` is **no longer written** by purchases, and `MinimumPrice` is left to the
user; the automatic floor lives in `AcquisitionFloor` and is recomputed from the current basis
every time. [IMPL]

`ProcurementController.ReconcilePositionCosts` (L1948), called at the start of every scan,
retires units that are no longer held (`PositionCostPolicy.RecordSale`) and clears the basis
entirely once the position empties, so the next purchase rebases the item. It runs **only**
against a complete, verified retainer picture, because a partial pass would look like stock
that had sold and would drop a live floor. [IMPL]

**Known approximation:** holdings are aggregate, not per-lot. When holdings cannot be verified
the old high-water mark is kept, and only units the basis already accounts for may dilute it.
The floor therefore always covers at least the average landed cost of what is really in the
bags. [OBS]

### 6.5 Suspicious/stale listing handling

- **Anchor outliers**: `HomePriceReference.WithoutOutliers` drops listings below half the
  median, but only with ≥3 listings. When an absorption allowance is supplied,
  `DepthAdjustedLowest` deliberately skips the outlier filter — substantial cheap inventory
  must not be discarded merely for being cheap; depth is what decides.
- **Safety-seed detection**: `MarketPriceSafety.IsSafetySeedRepresentation` catches the
  999,999,999 placeholder and stack-totals within ±1,000 of it.
- **Curated ceiling**: curated consumables priced above 1,000,000/unit are treated as
  unresolved placeholders and repaired or skipped.
- [OBS] **Repricing still has no outlier rejection**, only the depth adjustment and the floor.

---

## 7. Market Data Sources

| Source | Mechanism | Consumed by | Failure behaviour |
|---|---|---|---|
| **Universalis full** | `GET /api/v2/{scope}/{ids}?listings=100&entries=100&statsWithin=604800000` | Home listings + 7-day sales + velocity → anchor, demand gate, coverage, tiering | 20 ids/batch; 3 attempts on 408/429/5xx; **partial results kept**; throws only if every batch failed |
| **Universalis aggregated** | `GET /api/v2/aggregated/{scope}/{ids}` | `MarketPriceHint` → **route and per-world item choice only** | Tolerated; shopping unaffected |
| **In-game Compare Prices** | `MarketDataService` + `IMarketBoard` packets | Repricing | Timeout → retry |
| **In-game Item Search** | `MarketPurchaseService` + `InfoProxyItemSearch` | **The only source that can authorize a purchase** | `MarketResponseTracker.IsReady` requires the declared row count fully received, no error, 750 ms settle |
| **Retainer sell window** | `RetainerListingService.TryReadSellerFeePercent` | **`ObservedMarketTaxPercent`** — every margin, ceiling and floor | Falls back to 5% |
| **Saddlebag Exchange** | `POST .../ffxivrawstats` | Discovery only, **off by default** | Returns `[]` |
| **Repricing observations** | `AutomationController.ObservedHomePrices` | Seeds home anchors | Falls back to a live home sweep |
| **Local Excel sheets** | `dataManager.GetExcelSheet<Item>()` | Rule seeding, discovery validation | Authoritative for tradability/HQ/stack |

### 7.1 Staleness

| Data | Lifetime |
|---|---|
| Home resale anchor | 30 min; checked at plan build **and again** before purchase |
| Away scout observations | 24 h (`ScoutKnowledgeMaxAgeHours`) |
| Snipe block (`PreferredStock` + `AlwaysScout`) | **never cached** |
| Discovery cache | 24 h |
| Live listing before buy | must be fresh this visit |

### 7.2 API failure — does it fail open?

**No, for purchases.** [IMPL] A purchase requires a live in-game board reading satisfying
`MarketResponseTracker.IsReady`. Universalis is never sufficient. Hints are typed as
`MarketPriceHint` — deliberately *not* `ProcurementMarketListing` — so they cannot reach the
planner as buyable listings.

- Universalis fully down ⇒ scan fails ⇒ retry on interval. **No purchases.**
- Universalis partially down ⇒ fewer candidates. Safe direction.
- Home anchor expired mid-trip ⇒ that order is skipped, or the trip ends to refresh.
- Live board empty/timeout ⇒ 3 retries then skip that item.

[OBS] Soft spot: a home anchor up to 30 minutes old can authorize a purchase, though the live
board is re-read and profit re-checked immediately before buying.

---

## 8. Quantity / Capital Allocation

### 8.1 Quantity is not chosen

The bot buys **whole listings**, constrained to `listing.Quantity <= rule.TargetStackSize` and
clamped again at purchase time.

### 8.2 Inventory coverage — the primary volume control

`InventoryCoveragePolicy` (`InventoryCoveragePolicy.cs`)

```
ownedUnits   = listed units + bagged resale units + units already planned this run
coverageDays = ownedUnits / salesPerDay
targetUnits  = CEIL(salesPerDay × CoverageDaysFor(tier))          ; Core 3, Secondary 1.5, Opp 0.5
ceilingUnits = CEIL(salesPerDay × (CoverageDaysFor(tier) + 1.0))

CanAdd ⟺ quantity > 0 AND salesPerDay > 0
       AND ownedUnits < targetUnits
       AND ownedUnits + quantity <= ceilingUnits
```

With no usable velocity `CanAdd` is **false** and the weekly-share cap takes over as the
backstop. The check is applied in the planner *and* re-applied at the board (§2.4 check 13).

### 8.3 Slot budget

```
AvailablePurchaseSlots() = PlannedSaleSlots(repricing.LastKnownFreeSaleSlots ?? 0)

OrdinaryPurchaseSlots(free):
    free = MIN(free, ProcurementTargetSaleSlots)
    held = ResaleBagSlots                              ; bag stock as sale-stacks
    IF free > held            -> free − held
    IF ContinueShoppingWhenStocked ->
        MIN(MAX(0, free + ComfortableStockTarget − held),
            MAX(0, FreeInventorySlots − ProcurementInventoryReserve))
    ELSE                      -> MAX(0, free + ProcurementBagBufferStacks − held)

preferredTarget = CEIL(actual retainer capacity * PreferredPortfolioTargetPercent / 100)
preferredGap = MAX(0, preferredTarget - eligible preferred listed slots)
preferredReplacementSlots = MAX(0, MIN(preferredGap, ComfortableStockTarget) - preferred bag lots)
; Replacement exception requires continuous shopping and a complete all-retainer check.
; Count actual bag lots, excluding personal reserves and ineligible qualities.
PlannedSaleSlots = MIN(MAX(OrdinaryPurchaseSlots, preferredReplacementSlots), usable bag slots)
NonPreferredSaleSlots = OrdinaryPurchaseSlots

slotLimit (planner)   = MIN(FreeSaleSlots, FreeInventorySlots)
per-item slot ceiling = EffectiveMaximumSlots(policy, rule, candidate)
                      = MIN(EmergencyMaximumSlotsPerItem,
                            MAX(rule.MaximumSaleSlots,
                                CEIL(ceilingUnits / stackSize)))
```

`ResaleBagSlots` is capped per item by the same demand-derived figure, so deep stacks of a
liquid line no longer make a few bag slots look like a full portfolio.

### 8.4 Gil budget and the top-up pass

```
SpendableGil(newTrip):
  available = reinvest ? wallet − reserve : MIN(wallet − reserve, tripCap − spent)
  IF any enabled, non-liquidate preferred rule -> return available
  ELSE -> NonPreferredSpendableGil(newTrip)

NonPreferredSpendableGil(newTrip):
  available = reinvest ? wallet − reserve : MIN(wallet − reserve, tripCap − spent)
  bags = CollectBagStock() EXCLUDING PreferredStock items
  IF MIN(freeSlots, targetSlots) > bags.SaleSlots -> return available
  ; bags already cover the vacancies: apply the buffer cap
  cap = CLAMP(FLOOR((walletAfterReserve + bufferCost) × BufferGilPercent/100) − bufferCost,
              0, walletAfterReserve)
  return MIN(available, cap)
```

Preferred stock is **exempt** from the buffer cap. [IMPL] Both planner entry points,
the top-up pass and the live purchase guard enforce the non-preferred sub-budget.
The planner charges buyer fees and accumulates spending across the whole basket;
the top-up pass subtracts previously selected non-preferred spending and slots.
Reinvestment off still respects the configured trip cap, and travel gil stays reserved.

`TopUpEmptySaleSlots` (`ProcurementController.Priority.cs` L242) runs when the comparison plan
still leaves slots empty:

```
uncovered = MAX(0, MIN(freeSaleSlots, targetSaleSlots) − ResaleBagSlots)
free      = MIN(AvailablePurchaseSlots(), uncovered) − compared.Orders.Count
IF free <= 0 OR ProcurementFillRoiPercent >= ProcurementMinimumRoiPercent -> no top-up
plan with Economics = policy.RelaxedTo(ProcurementFillRoiPercent)
          Portfolio = gates with OpportunisticMaximumPercent = 0
accept only orders whose Tier != Opportunistic
```

The relaxed policy lowers the preferred, high-volume and standard rungs; it does **not** lower
the low-value rung or the absolute floor, and it does **not** change coverage targets. So the
pass can buy good stock a little cheaper and cannot buy junk or overstock. [IMPL]

### 8.5 Anti-overconcentration

| Mechanism | Effect |
|---|---|
| **Inventory coverage** | The primary control: days of the market's own demand |
| `EffectiveMaximumSlots` | Demand-derived per-item slot cap, floored at `rule.MaximumSaleSlots` |
| `EmergencyMaximumSlotsPerItem` | Hard limit (20) no amount of demand may exceed |
| Opportunistic slot cap | ≤10% of portfolio slots, counting stock already listed |
| Opportunistic capital cap | ≤10% of the shopping budget |
| Slot-value gate | ≥`MinimumProfitPerSaleSlot` profit per slot for non-Core |
| Weekly share cap | Backstop only, when velocity is unusable |
| Buffer gil cap | ≤20% of (wallet + buffer cost) in non-preferred bag stock |
| `owned` counting | Listed **and** bagged **and** already-planned stock counts against every cap |

**Can it overspend on one high-ROI, low-value item?** [OBS] No. A cheap item lands in
Opportunistic, is held to the 35% bar, is limited to half a day of its own demand (typically one
stack), is weighted 0.6× in the ranking, and is capped at a tenth of both slots and capital.

**Can it overspend on one excellent item?** Deliberately, yes — up to its coverage target and
the 20-slot emergency limit. That is the intended behaviour for markets like Caramel Popcorn.

---

## 9. High-Value vs Low-Value Opportunities — Direct Answer

**The code has an explicit, layered bias toward high-value, high-throughput stock, expressed as
economics rather than as tiering.** [IMPL]

### 9.1 The defences

1. **ROI is never an objective.** It only produces a price ceiling.
2. **The ranking key is gil per day**, normalised to a one-day window so small stacks cannot
   manufacture a rate.
3. **The margin ladder requires value, not just speed.** A cheap fast item gets the *strictest*
   bar (35%), not the most lenient.
4. **Coverage sizes positions by demand.** Half a day of a 31/day dye market is 16 units.
5. **Slot-value gate.** Non-Core candidates need ≥2,500 gil profit per slot.
6. **Opportunistic caps** on both slots (10%) and capital (10%).
7. **Tier weighting** (1.25 / 1.0 / 0.6) as a final thumb on the scale.

### 9.2 Concrete numerical comparison

| | **A: Gemdraught HQ ×99** | **B: Dye NQ ×20** |
|---|---|---|
| Buy price/unit | 15,000 | 1,148 |
| Resale anchor | 21,499 | 6,599 |
| Landed cost | 1,559,250 | 24,108 |
| Expected profit | **462,726** | 101,272 |
| **NetRoiPercent** | 29.68% | **420.1%** |
| Margin bar applied | 10% | **35%** |
| `ResaleValuePerSaleSlot` | 2,128,401 | 131,980 |
| `SalesPerDay` | 40 | 31 |
| `EstimatedDaysToSell` | 2.475 | 0.645 |
| **ExpectedGilPerDay** | **186,960** | 101,272 |
| Tier / weight | Core / 1.25 | Opportunistic / 0.6 |
| **AllocationScore** | **233,700** | 60,763 |
| Coverage target | 120 units (3 d) | 16 units (0.5 d) |

**Outcome:** A outranks B by 3.8×, on economics alone. Under the old model B's `ProfitVelocity`
was 405,088/day — *higher* than A's — and only tier dominance kept it down. [OBS]

### 9.3 Where bias could still leak

1. **`salesPerDay` is the whole market's rate**, not our share of it, so `EstimatedDaysToSell`
   is optimistic. It is applied consistently, so it does not bias between candidates, but the
   absolute gil/day figures read high. [OBS]
2. **A cheap item that crosses the 150,000 value floor** becomes Secondary and gets the 14%
   bar, a 1.5-day coverage target and a 1.0 weight. This is intended — at that value per slot
   it is no longer a trinket — but the floor is where the boundary sits.
3. **Coverage depends on velocity data.** An item whose Universalis velocity is reported as an
   explicit `0` suppresses the 7-day fallback, which makes `CanAdd` false and falls back to the
   weekly-share cap. Conservative, but a data quirk rather than a decision. [OBS]

---

## 10. Item Categories and Special Cases

| Category | Treatment |
|---|---|
| **Raid food/potions** (4 Gemdraughts, Caramel Popcorn, Popoto Potage) | `PreferredStock`, `TourPriority=0`, HQ-required, `AlwaysScout`, exempt from the buffer gil cap, always Core: 10% bar, 3-day coverage, ×1.25 weight, demand-derived slot cap |
| **General-Purpose / Wide-Spectrum dyes** | Buyable; `TourPriority=1`, stack 20, `MinimumWeeklyUnitsSold=50`. Usually Opportunistic on value per slot |
| **Jet Black / Pure White dyes** | `AlwaysScout=true` — priced on every world |
| **All other dyes, ethers, older materia** | `LiquidateOnly` — sold from bags, never bought |
| **Materia XI/XII** | Buyable; `TourPriority=2`, stack 20. Secondary or Opportunistic on value per slot |
| **8 tomestone materials** | `LiquidateOnly` |
| **Discovered items** | Food/medicine only; ≥150k stack value, ≥50 units/day; seeded `Confidence=Candidate`, `PreferredStock=false`. Promotion requires sustained evidence (§12) |
| Crafting materials, gear, furniture, glamour, minions | **No rules** ⇒ invisible to the buying path |

---

## 11. Important Configuration

See §5 for the full tables. The economically material dashboard controls (Shopping tab) are:

- **Margins:** minimum ROI, high-volume ROI (preferred and unpinned), low-value ROI, absolute
  minimum ROI, fill-up ROI, minimum profit per unit, minimum profit per sale slot.
- **What counts as high volume:** minimum sales/day, minimum stack value.
- **Inventory sizing:** preferred / secondary / opportunistic coverage days, emergency maximum
  slots per item, undercut absorption days.
- **Portfolio shape:** preferred target %, opportunistic maximum %.
- **Capital:** budget, reinvest toggle, travel reserve, buffer gil %, buffer value target.

### 11.1 Settings that are unused, partially used, or unreachable

| Item | Status |
|---|---|
| `LiveWorldStockHuntEnabled` + `BuildLiveMarketPlan` | **[CFG / effectively DEAD]** — the Start button and migration v16 both set it false, and `KeepsRetainersStocked` requires it false |
| `LiveWorldStockThresholdPerItem`, `LiveWorldStockHuntCooldownMinutes`, `LiveWorldStockHuntMaximumItems` | Only meaningful on that unreachable path |
| `GuidedTourMaximumWorlds` | Guided (manual) route only |
| `ProcurementBagBufferStacks` | Only when `ContinueShoppingWhenStocked=false` (default true) |
| `ProcurementDataCenter` | Regional hints hardcode `"North-America"` regardless |
| `ProcurementOrder.SaleSlots` | Always constructed as `1`; the `Slots` divisor is therefore inert |
| `MarketDiscoveryRegion` / `MarketDiscoveryEnabled` | Discovery is off by default |
| `PortfolioPolicy.Rank` | Used for scouting display order only; no longer part of allocation |

---

## 12. Automatic Discovery and Confidence

`MarketConfidencePolicy` (`MarketConfidencePolicy.cs`) and
`MarketDiscoveryService.Reassess`.

```
Opportunistic  -> below the proposal floors; not worth watching
Candidate      -> >= 50 units/day AND >= 150,000 gil per stack
Proven         -> ALL of:
                    >= 75 units/day
                    >= 300,000 gil per stack
                    >= 50,000 estimated gil/day   (computed at the ABSOLUTE MINIMUM margin)
                    >= 5 agreeing observations
                    price spread <= 25% between refreshes
                    a REALISED profitable cross-world spread (non-zero tracked cost basis)

ShouldPin(confidence) ⟺ confidence == Proven      -> sets PreferredStock
```

Each discovery refresh folds into the rule's evidence. A refresh whose price has not lurched
counts as a confirmation; one that has **resets the count to zero**, because the count means
"this line has behaved consistently". The discovery cache is a day long, so five confirmations
is roughly "it has looked like this for most of a week". [IMPL]

A config migration (v43) demotes any rule that was auto-pinned under the old behaviour.

---

## 13. Hidden / Emergent Behaviour and Remaining Concerns

### Resolved by the rework

The following items from the previous audit no longer apply: the 0.25-day small-stack
inflation, the dual ROI bases, the inert fast-mover setting, the shared 10/day constant,
the `CostBasis` and `MinimumMarginPercent` ratchets, the first-match immediate buy, the
single-listing resale anchor, and the auto-pinning of discovered items.

Additionally resolved by the marginal pass: repeat stacks of one market no longer carry the same
score as the first (§3.1); greedy no longer leaves gil unusable under a tight budget (§3.4); a
reported velocity of `0` no longer vetoes recorded sales (§3.2); and the weekly-share cap is no
longer overwritten by a later market row for the same item — it takes the tighter of the two.

### Remaining

1. **[MEDIUM] Transaction costs are still incomplete.** Teleport fares and retainer venture
   costs appear in no profit calculation; only a flat 5,000 gil travel reserve is withheld. On a
   31-world circuit this is real gil. [OBS]
2. **[MEDIUM] `salesPerDay` is the market's rate, not ours.** Every holding-time and gil/day
   figure is therefore optimistic in absolute terms. [OBS]
3. **[LOW] 30-minute anchor staleness authorizes purchases.** The listing is re-read live; the
   anchor may be half an hour old.
4. **[LOW] The outlier filter needs ≥3 listings**, and is deliberately bypassed entirely when a
   depth allowance is supplied — depth, not price, decides there.
5. **[MEDIUM] Marginal clearing assumes our own stacks sell in sequence.** In reality several of
   our listings drain in parallel. `(owned + quantity) / salesPerDay` is the correct *completion*
   time for the position either way, which is what capital commitment depends on, but it is not a
   model of which individual stack sells first. [OBS]
6. **[LOW] Repricing has no outlier rejection**, only the depth adjustment and the floor. A
   deep cheap position is followed down to the floor.
7. **[LOW] `PortfolioSummary` is cached ~2 s** and invalidated after a purchase, so an
   opportunistic-cap check can use slightly stale slot counts.
8. **[LOW] The repair pass reduces one market at a time.** It cannot find an improvement that
   requires cutting two markets simultaneously. Bounded search is deliberate. [OBS]
9. **[LOW] Cost basis is aggregate, not per-lot.** Documented approximation; see §6.4.

---

## 14. Current Strategy in Pseudocode

```text
# ---------- TRIP GATE ----------
IF !armed OR repricing busy OR no completed retainer pass OR wallet−reserve == 0: STOP
IF freeSaleSlots < 10 OR spendableGil < 1_000_000: STAY HOME AND KEEP UNDERCUTTING
IF no capacity change AND no income AND before nextScan: STOP
ReconcilePositionCosts()          # retire basis for stock that has sold

# ---------- CANDIDATE RULES ----------
rules = ProcurementRules WHERE Enabled AND !LiquidateOnly
        AND (PreferredStock OR AlwaysScout OR home7DaySales >= MinimumWeeklyUnitsSold)
order by: snipeBlockFirst, homeSalesPerDay desc, TourPriority, name

# ---------- HOME ANCHORS ----------
seed home prices from repricing observations (<= 30 min old)
scan home world for any rule whose anchor is stale
absorbable = FLOOR(salesPerDay × AnchorAbsorptionDays)
homeAnchor[item] = MIN( median(7-day home sale prices),
                        DepthAdjustedLowest(home listings excl. own, absorbable) − 1 )

# ---------- CIRCUIT ----------
route = data centers one at a time, own DC first, scored by cached Universalis hints
FOR world IN route (<= 31 worlds, <= 180 minutes):
    items = snipeBlock ALWAYS + hinted + busiest secondary + rotation   (<= 14)
    FOR item IN items WHERE observation older than 24h:
        read live board
        record the observation and its economics       # NOTHING IS BOUGHT HERE
    IF !roomToBuy OR spendableGil == 0 OR bags full: BREAK

# ---------- COMPARISON ----------
IF observed home anchors have used half their lifetime:
    refresh observed items once at the home board (clear old quotes first)
markets  = remembered listings WHERE home anchor still fresh
compared = BuildPlan(markets, Economics = configured policy)
IF free slots remain:
    fill = BuildPlan(remaining, Economics = policy.RelaxedTo(FillRoi), opportunisticCap = 0)
    compared += fill orders WHERE tier != Opportunistic
IF compared is empty: GO HOME, LEAVE SLOTS EMPTY       # explicit: empty beats junk

# ---------- BuildPlan (per item, per quality) ----------
IF home7DaySales < MinimumWeeklyUnitsSold: REJECT
anchor      = homeAnchor
net         = FLOOR(anchor × (1 − marketTax/100))
salesPerDay = Universalis velocity, else 7-day sales / 7
refValue    = anchor × TargetStackSize
requiredRoi = MAX( ladder(preferred, salesPerDay >= 10, refValue >= 150k),
                   AbsoluteMinimumRoi )
IF fewer than 3 recent sales: requiredRoi = MAX(requiredRoi, StandardRoi)
ceiling     = MIN( FLOOR(net / (1+requiredRoi/100) / buyerMult),
                   FLOOR((net − minProfitPerUnit) / buyerMult),
                   rule.MaximumUnitPrice if set )
FOR listing WHERE price <= ceiling AND qty <= TargetStackSize
             AND retainer NOT ours AND quality matches:
    landed = CEIL(price × qty × buyerMult)
    profit = net × qty − landed                  ; REJECT if <= 0 or ROI/profit short
    tier   = Core          IF PreferredStock
             Secondary     IF salesPerDay>=10 AND valuePerSlot>=150k AND profitPerSlot>=2500
             Opportunistic OTHERWISE
    IF tier != Core AND profitPerSlot < 2500: REJECT ("insufficient slot value")
    score  = (profit / MAX(1.0, qty/salesPerDay)) × weight(tier)
    ADD candidate

# ---------- ALLOCATE (one greedy pass) ----------
FOR candidate IN candidates ORDER BY score desc, profit desc, days asc, capital desc, tour asc:
    SKIP IF no free slot
    SKIP IF opportunistic AND (slot cap OR capital cap) reached
    SKIP IF itemSlots >= EffectiveMaximumSlots(policy, rule, candidate)
    SKIP IF spent + landed > budget
    SKIP IF !CanAdd(owned, qty, salesPerDay, coverageDays(tier), overshootDays)
    ADD, and count the units toward `owned` for the rest of the pass

# ---------- EXECUTE ----------
FOR order IN plan:
    travel; search item; wait for a COMPLETE server response
    re-check: anchor fresh, rule valid, EffectiveMaximumSlots, live listing matches,
              demand coverage, profit with SERVER-REPORTED buyer tax at RequiredPurchaseRoi,
              slots, inventory reserve, gil
    IF all pass: SUBMIT PURCHASE; confirm via inventory delta (+ yes/no prompt)
    ON confirm: CostBasis        = weighted average landed cost over units held
                CostBasisUnits  += quantity
                AcquisitionFloor = CEIL(required net / (1 − marketTax/100))

# ---------- RESELL (continuous, every retainer pass) ----------
FOR each retainer listing:
    read live Compare Prices; record the seller fee the game reports
    competitors = listings excluding our own retainers, matching quality
    IF none: KEEP
    lowest = DepthAdjustedLowest(competitors, MIN(stack/2, salesPerDay × AbsorptionDays))
    IF lowest < historicalMedian × 0.40: PRICE WAR -> keep (or protected floor)
    IF current <= lowest OR within tolerance: KEEP
    target = lowest − UndercutAmount                 # default 1 gil
    floor  = MAX(MinimumPrice, AcquisitionFloor, CEIL(CostBasis × (1 + MinimumMarginPercent/100)))
    IF target < floor: KEEP                          # never sell below landed cost + margin
    COMMIT MAX(floor, roundDown(target))
```

---

## 15. Key Code Map

| Responsibility | File | Class / Method |
|---|---|---|
| Buy candidate generation + allocation | `Core/Services/ProcurementPlannerService.cs` | `BuildPlan` (L22), `CollectCandidates` (L149), `AddCandidate` (L219), **`ScoreOf` (L255)**, `Allocate` (L279), **`SelectGreedily` (L315)**, **`ImproveUnderConstrainedCapital` (L469)**, `EffectiveMaximumSlots` |
| Live-tour planner | same | `BuildLiveMarketPlan` (L95) — unreachable in the shipped config |
| Margin ladder, tiering, gil/day | `Core/Services/PortfolioPolicy.cs` | `RequiredRoiPercent` (L51), `DaysToSell` (L81), `ExpectedGilPerDay` (L91), **`MarginalDaysToClear` (L108)**, **`MarginalGilPerDay` (L124)**, `ClassifyCandidate`, `Summarize` |
| **Personal sell-through (measured, unused)** | `Core/Services/SellThroughObserver.cs` | `Observe`, `IsReliable`, `CaptureShare` |
| **Transaction costs and ROI** | `Core/Services/MarketEconomics.cs` | `FeeModel` — `LandedCost`, `NetProceeds`, `NetRoiPercent` (L40), `MaximumUnitPrice` (L48), `ListingPriceForNet` (L65) |
| **Inventory coverage** | `Core/Services/InventoryCoveragePolicy.cs` | `CoverageDays` (L17), `TargetUnits` (L23), `CanAdd` (L42), `DemandJustifiedSlots` (L59) |
| **Economic policy / knobs** | `Core/Models/EconomicPolicy.cs` | `ProcurementEconomicPolicy` (L12), `RelaxedTo` (L91), `CoverageDaysFor` (L98), `ScoreWeightFor` (L105) |
| **Position cost basis** | `Core/Services/PositionCostPolicy.cs` | `RecordPurchase` (L29), `RecordSale` (L54) |
| **Discovery confidence** | `Core/Services/MarketConfidencePolicy.cs` | `Classify` (L61), `ShouldPin` (L82), `PriceSpreadPercent` (L89) |
| Sales velocity | `Core/Services/SalesVelocityPolicy.cs` | `DailyUnits` |
| Order metrics | `Core/Models/ProcurementModels.cs` | `ProcurementOrder` (L232+), `PortfolioGates`, `PortfolioDecision` (L54), `MarketConfidence` (L99) |
| **Depth-aware anchor** | `Core/Services/HomePriceReference.cs` | `DepthAdjustedLowest` (L62), `WithoutOutliers` (L30) |
| Resale pricing / undercut | `Core/Services/PricingStrategyService.cs` | `Evaluate`, `CalculateFloor`, `DepthAdjustedLowest`, `IsPriceWar` |
| Resale floor from cost | `Core/Services/ProcurementPriceSafety.cs` | `MinimumResalePrice` (L12) |
| Price sanity / placeholders | `Core/Services/MarketPriceSafety.cs` | `IsSafeAutomaticUnitPrice`, `IsSafetySeedRepresentation` |
| Stock / quality / budget helpers | `Core/Services/ResaleStockPolicy.cs` | `BuyableQuality`, `SpendableGil`, `BufferSpendableGil` |
| Route + per-world item choice | `Core/Services/ShoppingScoutPolicy.cs` | `BuildRoute`, `SelectWorldItems`, `BuysBeforeComparison` |
| Discovery proposals | `Core/Services/MarketDiscoveryPolicy.cs` | `Propose` |
| Shopping orchestration | `Automation/ProcurementController.cs` | `StartScan` (L307), `PollListings` (L1238), `PollPurchase` (L1415), `RequiredPurchaseRoi` (L1504), `ReconcilePositionCosts` (L1948), `PlannedSaleSlots` (L2106) |
| Circuit / comparison / top-up | `Automation/ProcurementController.Priority.cs` | `FinishPriorityScouting` (L197), `TopUpEmptySaleSlots` (L242), `BeginPriorityShopping` (L304), `ObservePriorityItem` (L478) |
| Repricing loop + **seller fee capture** | `Automation/AutomationController.cs` | `EvaluateCurrentListing`, `CaptureSellerFee` |
| Bag listing | `Automation/BagListingController.cs` | `QueuePricedStock`, `BuildStockSummary` |
| Pending stock ledger | `Services/ProcurementLedger.cs` | `RecordPurchase`, `MarkListed`, `PendingSaleSlots` |
| Universalis client | `Services/UniversalisService.cs` | `CreateFavoriteRules`, `ScanAsync`, `FetchPriceHintsAsync` |
| Live board (buying) | `Services/MarketPurchaseService.cs` | `TrySelectLiveListing`, `SubmitPurchase`, `TryConfirmPurchase` |
| **Discovery promotion** | `Services/MarketDiscoveryService.cs` | `ApplyPendingDiscoveries`, `Reassess` |
| All settings + migrations | `Configuration.cs` | `Fees`, `EconomicPolicy`, `PortfolioGates`, migration v43, `Normalize` clamps |
| Settings UI | `Windows/DashboardWindow.cs` | Shopping tab |

---

## 16. Decision Log Output

`PortfolioDecision.ToString()` (`ProcurementModels.cs` L78) is what appears in the log and on
the dashboard:

```
BUY Caramel Popcorn HQ x99 [CORE]: cost 1,559,250, sale 2,021,976 net, profit 462,726
  (29.7% ROI); 100/day, turnover 0.99d, 462,726 gil/day, coverage 0.8d -> 1.8d;
  selected preferred high-volume stock; restocking toward 3.0d coverage

SKIP General-Purpose Dye x20 [OPPORTUNISTIC]: cost 24,108, sale 125,380 net, profit 101,272
  (420.1% ROI); 31/day, turnover 0.65d, 101,272 gil/day, coverage 0.6d -> 1.3d;
  opportunistic portfolio cap reached (6/6 slots)
```

Every number the ranking used is present: landed cost, net proceeds, profit, the one ROI,
velocity, turnover, gil per day, and the coverage before and after.

---

## 17. Own Sell-Through: Measured Groundwork, Not Applied

`SalesPerDay` is the whole home world's rate. If the bot is one of four sellers of a line, the
"3 days of coverage" it targets may really be closer to twelve days of its own selling.

**Nothing in the shipped path corrects for this.** What exists: [IMPL]

| Piece | Status |
|---|---|
| `SellThroughObserver` + `SellThroughObservation` (`Core/Services/SellThroughObserver.cs`) | Written and tested (10 cases) |
| `Configuration.SellThrough` persistence slot | Declared, **never written** |
| Observation wiring into the retainer pass | **Not implemented** |
| Effect on velocity, coverage or purchasing | **None** |

The estimator, if it were fed, would work as:

```
available   = previousUnits + purchasedSinceLastReading
soldRate    = MIN((available − currentUnits) / elapsedDays, marketUnitsPerDay)
UnitsPerDay = UnitsPerDay + 0.3 × (soldRate − UnitsPerDay)            ; EWMA
IsReliable  = Samples >= 4 AND ObservedDays >= 3
CaptureShare = CLAMP(UnitsPerDay / marketRate, 0.25, 1)   or null when unreliable
```

with windows outside `[0.25, 14]` days discarded, restocking added back so it is never negative
sales, an unexplained gain teaching nothing, a rate above the whole market capped, and a 0.25
floor so a bad patch could never cut a market's assumed demand more than four-fold.

**Why it is not wired up.** The repository has no sale signal. It does not read the game's
retainer sale history addon; Universalis sale entries carry no seller identity; and
`WealthHistoryService` tracks gil totals rather than per-item proceeds. Inventory differencing
between two complete retainer passes is the only available source, and it cannot tell a sale from
a manual withdrawal, an item used or discarded, or a listing that expired off the board after 30
days. All three inflate apparent sell-through → inflate capture share → raise effective velocity
→ **buy more**. That is the unsafe direction, so the known optimism is left in place where it is
at least documented and constant. Reading the retainer's own sale history would unlock it. [OBS]

---

## Ambiguities and limits of this analysis

1. **`ProcurementOrder.SaleSlots` is always 1** at every construction site, so the `Slots`
   divisor in the per-slot metrics is inert.
2. **Real FFXIV tax rates were not verified against the game.** The sale tax is now read from
   the retainer sell window at runtime; the *buyer* fee is still assumed 5% at planning time,
   though the server-reported figure is used at the board.
3. **Numeric examples in §4 and §9 use plausible market prices, not observed ones.** The
   formulas are taken verbatim from the code, and every figure quoted is reproduced by a test
   in `tests/SmartUndercutBot.Core.Tests/ProfitOptimizationTests.cs`.
4. **`MarketStatistic` / `ParseStatistics` and `IMarketDiscoveryProvider`** exist and compile,
   but only the `SaddlebagStatisticsProvider` → `MarketDiscoveryService` path was traced, and
   it is disabled by default.
5. **Coverage behaviour under sparse velocity data** is the least-exercised path in live play:
   the unit tests cover it, but a market with erratic Universalis reporting will fall back to
   the weekly-share cap more often than the design intends. [OBS]
