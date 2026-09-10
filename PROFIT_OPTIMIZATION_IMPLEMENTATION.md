# Profit Optimization — Implementation Report

**What this is.** A record of the rework of AutoUndercutter's purchasing and allocation
strategy, from the percentage-driven, fixed-slot model documented in
`PURCHASING_RESALE_LOGIC.md` (commit `ad1fa36`) to a demand-driven model whose objective is
long-run realised gil.

**Status.** Implemented, built and tested. `dotnet build SmartUndercutBot.sln -c Release`
succeeds with zero warnings; `dotnet test` reports **325 passed, 0 failed**.

---

## 1. The old economic behaviour

| Aspect | Old behaviour |
|---|---|
| Ranking | Five competing allocation strategies, all sorted by portfolio **tier first**. A Core item always beat a Secondary item, which always beat an Opportunistic one, regardless of economics. The winning plan was chosen lexicographically over eight objectives. |
| Profit rate | `ProfitVelocity = profit / MAX(0.25, daysToSell)`. Any stack clearing in under six hours was scored as if it cleared in six hours, so a 20-unit dye stack was awarded **4× its profit** as a daily rate. |
| ROI | Two different bases coexisted. The enforced price ceiling used **post-fee** (landed) cost; the reported `RoiPercent` used **pre-fee** cost, so the displayed number was systematically higher than the one enforced. |
| ROI tiering | Binary: `salesPerDay >= 10` halved the bar to 10%, with no value floor — a cheap fast-selling item got the same discount as a two-million-gil stack of raid food. `Math.Min(minimumRoi, fastMoverRoi)` also meant raising the fast-mover setting above the standard one did nothing at all. |
| Inventory sizing | Fixed `rule.MaximumSaleSlots` (8 / 2 / 1), fixed portfolio percentages, a weekly market-share cap, and a `ComfortableItemTarget` clamp of 1–3 spare stacks per item. None of it responded to how fast the market actually sold. |
| Immediate buys | `ShoppingScoutPolicy.IsExceptional` bought any listing at ≥100% net ROI **on sight**, on the first world it appeared, before any later world was seen. |
| Resale anchor | `MIN(median recent sale, cheapest home listing − 1)`. One 3-unit undercut redefined the value of a 99-stack for the whole trip. |
| Repricing | Undercut the single cheapest competitor by 1 gil, whatever its stack size. |
| Cost basis | `CostBasis = MAX(existing, landedCost)` and `MinimumMarginPercent = MAX(existing, roi)` — both monotonic. One expensive buy pinned a floor that nothing could ever lower. |
| Fees | 5%/5% hardcoded in the request defaults, plus separate literal `0.95`, `1.05` and `÷0.95` constants in three other files. The game's own reported seller fee was read and then ignored. |
| Discovery | A discovered item was pinned `PreferredStock = true` — the strongest tier in the system — on the strength of a single remote statistics call. |

The system was deliberately engineered against the high-ROI/low-value trap, and mostly
succeeded — but it did so through **tier dominance**, not through economics. Remove the
tiering and the dye won. That is the thing this rework replaces.

---

## 2. The new economic objective

One ordering, applied once, in a single greedy pass
(`ProcurementPlannerService.Allocate`, `ProcurementPlannerService.cs:258`):

```
1. Reject anything that fails a safety or margin guard          (price ceiling, per-unit profit)
2. Reject anything beyond what the market's demand supports     (InventoryCoveragePolicy)
3. Rank by AllocationScore = ExpectedGilPerDay × tier weight
4. Then by absolute ExpectedProfit
5. Then by shorter EstimatedDaysToSell                          (liquidity)
6. Then by larger CapitalAtRisk                                 (capital utilisation, late)
7. Then by TourPriority, ItemId, ListingId                      (stable, deterministic)
```

The five competing strategies and the eight-objective lexicographic plan comparison are
gone. Tier no longer dominates: it is a **multiplier on the score**, so an extraordinary
non-Core opportunity can outrank a poor Core one, while a pinned line still gets a real
thumb on the scale.

Capital utilisation sits at step 6 — after profitability and liquidity, as a tie-breaker.
Nothing is bought to raise utilisation; idle gil still beats bad inventory.

---

## 3. New formulas

### 3.1 One fee model, one ROI

`SmartUndercutBot.Core/Services/MarketEconomics.cs` — `FeeModel` is the only place
transaction costs are modelled.

```
LandedCost(price, qty)     = CEIL(price × qty × (1 + BuyerFeePercent/100))
NetUnitProceeds(salePrice) = FLOOR(salePrice × (1 − MarketTaxPercent/100))
NetProceeds(price, qty)    = NetUnitProceeds(price) × qty
ExpectedProfit             = NetProceeds − LandedCost
NetRoiPercent              = (NetProceeds − LandedCost) / LandedCost × 100
```

`NetRoiPercent` is the **only** ROI definition in the codebase. `ProcurementOrder.RoiPercent`
is now an alias for it (`ProcurementModels.cs`), so the planner's gate, the dashboard column,
the decision log and the live pre-purchase re-check all report and enforce the same number.
Rounding stays conservative in both directions: costs round up, proceeds round down.

The price ceiling inverts the same arithmetic so ROI is enforced *once*, by the price filter,
and never re-derived downstream (`FeeModel.MaximumUnitPrice`, `MarketEconomics.cs:48`):

```
roiCeiling    = FLOOR(netUnitProceeds / (1 + requiredRoi/100) / buyerMultiplier)
profitCeiling = FLOOR(MAX(0, netUnitProceeds − minProfitPerUnit) / buyerMultiplier)
ceiling       = MIN(roiCeiling, profitCeiling [, rule.MaximumUnitPrice])
```

### 3.2 ExpectedGilPerDay

`PortfolioPolicy.ExpectedGilPerDay` (`PortfolioPolicy.cs:91`):

```
EstimatedDaysToSell = CLAMP(quantity / salesPerDay, 0.25, 30)
ExpectedGilPerDay   = ExpectedProfit / MAX(ProfitNormalizationDays, EstimatedDaysToSell)
ProfitNormalizationDays = 1.0
```

**Why a 1-day floor rather than 0.25.** A 20-unit stack that clears in six hours does not
produce four times the gil. To realise that rate the bot would have to travel again, find
another underpriced listing of the same item, and buy it — none of which is free or
guaranteed. The old 0.25 floor priced in replenishment that does not happen, and it did so
in direct proportion to how *small* the stack was. Normalising to a full day means a stack
is credited with the gil it actually earns in a day of slot occupancy, and a large stack
that takes two days to clear is compared honestly against it.

`EstimatedDaysToSell` keeps its 0.25 lower clamp because it is a real reported figure used
in the logs; only the *scoring* denominator is normalised.

### 3.3 Inventory coverage

`SmartUndercutBot.Core/Services/InventoryCoveragePolicy.cs`:

```
ownedUnits   = listed units + bagged resale units + units already planned this run
coverageDays = ownedUnits / salesPerDay

targetUnits  = CEIL(salesPerDay × coverageDaysForTier)
ceilingUnits = CEIL(salesPerDay × (coverageDaysForTier + overshootDays))

CanAdd  ⟺  quantity > 0
       AND salesPerDay > 0
       AND ownedUnits < targetUnits                    (below target)
       AND ownedUnits + quantity <= ceilingUnits       (may complete a position, not pile on)
```

Two conditions, both load-bearing. "Below target" stops a full position topping up forever.
"Within the overshoot ceiling" lets one stack complete a position without letting a second
stack land on top of it. **With no reliable velocity the answer is no** — without demand
there is no basis for sizing a position, and the caller falls back to its own limits.

The demand-derived slot cap (`ProcurementPlannerService.EffectiveMaximumSlots`,
`ProcurementPlannerService.cs:376`):

```
demandSlots          = CEIL(targetUnits(salesPerDay, coverage + overshoot) / stackSize)
EffectiveMaximumSlots = MIN(EmergencyMaximumSlotsPerItem,
                            MAX(rule.MaximumSaleSlots, demandSlots))
```

`rule.MaximumSaleSlots` became a **floor, not a ceiling**. A market whose own demand supports
more inventory gets more; `EmergencyMaximumSlotsPerItem` (default 20) is the hard limit no
amount of demand may exceed.

### 3.4 The margin ladder

`PortfolioPolicy.RequiredRoiPercent` (`PortfolioPolicy.cs:51`). Velocity alone no longer earns
the thin bar — a market must be **both** liquid and valuable per slot:

```
liquid   = salesPerDay >= HighVolumeMinimumSalesPerDay      (10/day)
valuable = referenceValuePerSlot >= HighVolumeMinimumValuePerSlot   (150,000 gil)

(liquid && valuable && preferred)  -> CoreHighVolumeRoiPercent   10%
(liquid && valuable)               -> HighVolumeRoiPercent       14%
(!valuable)                        -> LowValueRoiPercent         35%
otherwise (valuable but slow)      -> StandardRoiPercent         20%

required = MAX(that, AbsoluteMinimumRoiPercent)                   8%
```

Three further refinements:

- `referenceValuePerSlot` is `targetSalePrice × rule.TargetStackSize` — a property of the
  *market*, not of whichever listing happened to be seen, so a market cannot slide between
  bars depending on the size of the listing in front of it.
- A market with fewer than three recent sales (`reliableHistory`, `ProcurementPlannerService.cs:161`)
  is held to `StandardRoiPercent` regardless of how liquid it looks. Thin history is not
  evidence of liquidity.
- `AbsoluteMinimumRoiPercent` is applied with `MAX`, so no setting, and no relaxation, can
  ever produce a bar below 8%.

### 3.5 Depth-aware resale anchor

`HomePriceReference.DepthAdjustedLowest` (`HomePriceReference.cs:62`):

```
absorbable = FLOOR(salesPerDay × AnchorAbsorptionDays)
walk listings cheapest-first, accumulating quantity
  -> the first price whose cumulative competing units exceed `absorbable` is the anchor
  -> if the whole board is absorbable, the dearest listing is the anchor
  -> with no velocity or no absorption allowance, the cheapest listing (old behaviour)
```

The model is "how much competing inventory sits below this price, relative to what the market
eats in half a day". If popcorn sells 150 units a day and someone has three units up cheap,
those three are gone within minutes and never touch a 99-stack's economics. A hundred cheap
units in the same market genuinely is a problem, and still moves the anchor.

The same idea is applied to repricing: `MarketSnapshot.AbsorbableUnits` feeds
`PricingStrategyService.DepthAdjustedLowest`, so the bot does not walk a meaningful 99-stack
down to chase a 1-unit undercut it would outlive. Absorption is additionally capped at half
the stack size there, so it stays conservative.

### 3.6 Position cost basis

`SmartUndercutBot.Core/Services/PositionCostPolicy.cs`:

```
blendUnits = MIN(heldUnits, rule.CostBasisUnits)     -- only accounted units may dilute
CostBasis  = holdingsKnown && CostBasis > 0
             ? CEIL((CostBasis × blendUnits + landedCost) / (blendUnits + quantity))
             : MAX(CostBasis, CEIL(landedCost / quantity))
CostBasisUnits  = blendUnits + quantity
AcquisitionFloor = MinimumResalePrice(CostBasis, requiredRoi, minProfitPerUnit, fees)

MinimumResalePrice = ListingPriceForNet(CEIL(MAX(cost × (1 + roi/100), cost + minProfit)))
                   = CEIL(requiredNet / (1 − marketTax/100))
```

---

## 4. Every meaningful change

### 4.1 Ranking and allocation

- **Replaced** five tier-major allocation strategies plus an eight-objective lexicographic
  plan comparison with **one greedy pass** in economic order (§2).
- **Tier is now a weight**, not a sort key: `AllocationScore = ExpectedGilPerDay ×
  ScoreWeightFor(tier)` — Core ×1.25, Secondary ×1.0, Opportunistic ×0.6.
- **Removed** `ProfitVelocity`'s 0.25-day denominator; `ExpectedGilPerDay` normalises to one
  day. `PortfolioPolicy.ProfitVelocity` is retained as an alias so existing callers keep
  working, and now returns the normalised figure.
- **Removed** `profit / cost` as a ranking key entirely.

### 4.2 Inventory sizing

- **Added** `InventoryCoveragePolicy` — coverage days, target units, `CanAdd`, and
  `DemandJustifiedSlots`.
- **Added** `ProcurementEconomicPolicy` — per-tier coverage targets, the margin ladder,
  score weights, the emergency slot limit and the anchor absorption window, all configurable.
- **Demoted** `rule.MaximumSaleSlots` to a floor beneath the demand-derived cap.
- **Demoted** the weekly market-share cap to a backstop: it now applies **only** when
  coverage cannot size a position (no usable velocity).
- **Removed** the `ShoppingRules()` `ComfortableItemTarget` clamp that limited a fully listed
  item to 1–3 spare stacks regardless of demand. `ShoppingRules` is now the identity function.
- **Extended** the coverage check to the bag-listing slot limit and to the live pre-purchase
  guard, so a stale plan cannot create overstock at the board.

### 4.3 Fees and ROI

- **Added** `FeeModel` as the single transaction-cost model; removed the scattered `0.95`,
  `1.05` and `÷0.95` literals from `ProcurementController`, `ProcurementController.Priority`,
  `ProcurementPriceSafety` and `ShoppingScoutPolicy`.
- **Unified** `ProcurementOrder.RoiPercent` and `NetRoiPercent` on landed cost.
- **Added** `Configuration.ObservedMarketTaxPercent` and `Configuration.Fees`.
  `AutomationController.CaptureSellerFee` now **records the tax the game itself reports** in
  the retainer sell window, and every plan request, the live pre-purchase re-check, the
  displayed ROI and the resale floor read that one model. Previously the game's rate was read
  and used only for an earnings display.
- **Fixed a planner/validator disagreement** in `RequiredPurchaseRoi`
  (`ProcurementController.cs:1504`): it now re-derives the bar against the same *reference
  stack value* the planner used, and — for a fill order — under the same relaxed policy, so
  the board check cannot reject what the planner just approved. The bar recorded on the order
  is kept as a floor, so a plan built before a settings change can never buy under a laxer
  rule than the one now in force.
- **Fixed `ProcurementFillRoiPercent` being silently inert.** Once the prior work started
  passing an `Economics` policy, the fill pass's flat ROI parameter stopped being consulted at
  all. Added `ProcurementEconomicPolicy.RelaxedTo(roi)` (`EconomicPolicy.cs:91`), which lowers
  the preferred, high-volume and standard bars to the fill percentage and deliberately leaves
  the **low-value bar and the absolute floor untouched** — so the top-up pass can buy good
  stock a little cheaper, and can never buy junk.

### 4.4 Immediate buys

- **Removed** the immediate-buy path entirely. `ShoppingScoutPolicy.IsExceptional` is gone,
  replaced by the documented constant `ShoppingScoutPolicy.BuysBeforeComparison = false`, and
  `ObservePriorityItem` now always records the observation and advances. Every offer competes
  at the end of the circuit, where the planner can weigh gil/day, absolute profit and coverage
  against each other.
- The per-item scouting log line now prints landed cost, net ROI, gil/day and the coverage
  before and after, so a deferred decision is still explicable.

### 4.5 Cost basis

- **Replaced** the monotonic `MAX(old, new)` ratchet with a weighted-average landed cost over
  the units actually held (§3.6).
- **Added** `PricingRule.CostBasisUnits` (units backing the basis) and
  `PricingRule.AcquisitionFloor` (the floor implied by what the stock cost), kept **separate
  from** `MinimumPrice` so recomputing the automatic floor never overwrites a floor the user
  set by hand. `PricingStrategyService.CalculateFloor` honours whichever binds harder.
- **Stopped ratcheting `MinimumMarginPercent`.** It is now left alone; the automatic floor
  lives in `AcquisitionFloor` and is recomputed from the current basis on every purchase.
- **Added** `PositionCostPolicy.RecordSale` and
  `ProcurementController.ReconcilePositionCosts` (`ProcurementController.cs:1948`), called at
  the start of every scan. Units no longer held are retired from the basis, and a position
  that has sold out clears its basis entirely so the next purchase rebases the item.
  It runs **only** against a complete, verified retainer picture
  (`repricing.LastKnownFreeSaleSlots.HasValue && ProcessAllRetainers`) — a partial pass would
  look like stock that had sold and would drop a live floor.

**The documented approximation.** Exact per-lot accounting is not available: the plugin sees
aggregate holdings, not individual lots. The basis is therefore a weighted average when
holdings are observable, and the old protective high-water mark when they are not
(`holdingsKnown: false`). Only units the existing basis actually accounts for are allowed to
dilute it, so stock of unknown provenance cannot be averaged in at a price it never paid.
**Nothing here ever sells at a loss**: the floor always covers the average landed cost of what
is actually in the bags, plus the margin it was bought against, grossed up through the sale tax.

### 4.6 Discovery

- **Added** `MarketConfidence { Opportunistic, Candidate, Proven }` and
  `MarketConfidencePolicy` (`MarketConfidencePolicy.cs`).
- Discovery now proposes at **Candidate** with `PreferredStock = false` and `TourPriority = 3`.
  A config migration (v43) demotes any previously auto-pinned discovered rule.
- `MarketDiscoveryService.Reassess` folds each refresh into the rule's accumulated evidence.
  A refresh whose price has not lurched (`PriceSpreadPercent <= 25%`) counts as a
  confirmation; one that has **resets the count to zero**, because the count means "this line
  has behaved consistently".
- Promotion to `PreferredStock` requires **all** of: ≥75 units/day, ≥300,000 gil per stack,
  ≥50,000 estimated gil/day, ≥5 agreeing observations, price spread ≤25%, and a **realised**
  profitable cross-world spread (the item has actually been bought at a price that cleared the
  margin bar, evidenced by a non-zero tracked cost basis). The gil/day estimate is deliberately
  computed at the *absolute minimum* margin rather than a hoped-for one.

---

## 5. Configuration

All new settings live on the Shopping tab of the dashboard. Schema version **43**.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| `ProcurementPreferredCoverageDays` | **3.0** | 0.25–7 | Days of demand to hold in preferred/core stock |
| `ProcurementSecondaryCoverageDays` | **1.5** | 0.25–7 | Days of demand to hold in strong secondary stock |
| `ProcurementOpportunisticCoverageDays` | **0.5** | 0.1–2 | Days of demand for dyes/materia and similar |
| `ProcurementCoverageOvershootDays` | **1.0** | 0–1 | How far a single stack may carry holdings past target |
| `ProcurementHighVolumeMinimumSalesPerDay` | **10** | 10–10,000 | Velocity half of "high-volume market" |
| `ProcurementHighVolumeMinimumValuePerSlot` | **150,000** | 150k–100M | Value half of "high-volume market" |
| `ProcurementFastMoverRoiPercent` | **10%** | 8–1000 | Bar for preferred, liquid, valuable stock |
| `ProcurementHighVolumeRoiPercent` | **14%** | 8–1000 | Bar for unpinned liquid, valuable stock |
| `ProcurementMinimumRoiPercent` | **20%** | 0–1000 | Bar for ordinary opportunities |
| `ProcurementLowValueRoiPercent` | **35%** | 8–1000 | Bar for cheap stock |
| `ProcurementAbsoluteMinimumRoiPercent` | **8%** | 8–1000 | Nothing is ever bought below this |
| `ProcurementFillRoiPercent` | **10%** | 0–1000 | Relaxed bar when slots would sit empty |
| `ProcurementAnchorAbsorptionDays` | **0.5** | 0–0.5 | Cheap competing stock the market swallows before it moves the anchor |
| `ProcurementEmergencyMaximumSlotsPerItem` | **20** | 1–60 | Hard concentration limit |
| `OpportunisticPortfolioMaximumPercent` | **10%** | 0–100 | Unchanged; still the junk ceiling |
| `ObservedMarketTaxPercent` | **5%** | auto | Recorded from the game's retainer sell window |

The defaults are chosen to favour exactly the markets described: Caramel Popcorn, Popoto
Potage and the Grade 4 Gemdraughts are pinned, liquid and valuable per slot, so they sit on
the 10% bar with a 3-day coverage target and a demand-derived slot cap — which is what lets
them absorb millions of gil while their live economics hold.

---

## 6. How each class of stock is handled

**Preferred / Core.** Gets the thinnest margin bar (10%), the longest coverage target
(3 days), a ×1.25 score weight and exemption from the buffer gil cap. It does **not** get
automatic precedence over better economics, and it does **not** get permission to overstock:
a preferred item at or past its coverage target stops being bought, and a preferred item whose
margin fails the bar is rejected like anything else. Preference is a thumb on the scale,
applied after the economics, never instead of them.

**Secondary.** Unpinned stock that clears both the velocity and value floors and the
per-slot profit gate. 14% bar, 1.5-day coverage, ×1.0 weight. A genuinely extraordinary
Secondary opportunity can outscore a mediocre Core one.

**Opportunistic.** Everything else — cheap or slow. Held to the strictest bar (35%),
the shortest coverage (0.5 days, which in practice means roughly one stack), a ×0.6 weight,
a share of sale slots capped at 10% of the portfolio *counting stock already listed*, and a
separate capital cap of 10% of the shopping budget. It can never use the relaxed fill bar.
Cheap side profits remain available; they cannot become the portfolio.

---

## 7. Files and classes changed

**New**

| File | Contents |
|---|---|
| `src/SmartUndercutBot.Core/Services/MarketConfidencePolicy.cs` | `MarketConfidenceEvidence`, `MarketConfidencePolicy` |
| `tests/SmartUndercutBot.Core.Tests/ProfitOptimizationTests.cs` | The economic test suite |
| `src/SmartUndercutBot.Core/Models/EconomicPolicy.cs` | `ProcurementEconomicPolicy` |
| `src/SmartUndercutBot.Core/Services/MarketEconomics.cs` | `FeeModel` |
| `src/SmartUndercutBot.Core/Services/InventoryCoveragePolicy.cs` | Coverage maths |
| `src/SmartUndercutBot.Core/Services/PositionCostPolicy.cs` | Weighted-average cost basis |

**Modified**

| File | Change |
|---|---|
| `Core/Services/ProcurementPlannerService.cs` | Single-objective allocation; coverage gating; depth-aware anchor; demand-derived slot cap; richer decision log |
| `Core/Services/PortfolioPolicy.cs` | Margin ladder; `ExpectedGilPerDay`; 1-day normalisation |
| `Core/Services/HomePriceReference.cs` | `DepthAdjustedLowest` |
| `Core/Services/PricingStrategyService.cs` | Depth-aware undercut target; `AcquisitionFloor` in the floor |
| `Core/Services/ProcurementPriceSafety.cs` | Fee model parameter instead of a `÷0.95` literal |
| `Core/Services/ShoppingScoutPolicy.cs` | Immediate-buy path removed |
| `Core/Services/MarketDiscoveryPolicy.cs` | Proposals are Candidates; evidence recorded on the rule |
| `Core/Models/ProcurementModels.cs` | New order metrics; `MarketConfidence`; rule evidence fields; decision-log format |
| `Core/Models/PricingModels.cs` | `CostBasisUnits`, `AcquisitionFloor`, `MarketSnapshot.AbsorbableUnits` |
| `Automation/ProcurementController.cs` | Fee model plumbing; coverage re-check before purchase; `ReconcilePositionCosts`; `RequiredPurchaseRoi` agreement fix; `ShoppingRules` clamp removed |
| `Automation/ProcurementController.Priority.cs` | Relaxed policy for the top-up pass; immediate-buy removal; richer scouting log |
| `Automation/AutomationController.cs` | Records the game-reported sale tax |
| `Automation/BagListingController.cs` | Demand-derived slot limit; absorption-aware bag pricing |
| `Services/MarketDiscoveryService.cs` | `Reassess` — evidence accumulation and promotion |
| `Configuration.cs` | New settings, `Fees`, `EconomicPolicy`, v43 migration and clamps |
| `Windows/DashboardWindow.cs` | UI for the new economic settings |

---

## 8. Tests added

`tests/SmartUndercutBot.Core.Tests/ProfitOptimizationTests.cs` — 23 test cases covering all
ten required scenarios:

| # | Test | Proves |
|---|---|---|
| 1 | `HighRoiTrinketLosesToTheProfitableConsumable` | A 420%-ROI dye ranks below a 30%-ROI gemdraught; >95% of capital goes to the consumable |
| 2 | `PopcornAbsorbsSeveralMillionGilWhileCoverageIsShort` | 3 stacks and >4M gil deployed against a 2-slot rule, because coverage is 0.8 days |
| 3 | `OverstockedCoreItemStopsBuyingHoweverGoodTheMarketLooks`, `SlowMovingPreferredItemCannotStockpileOnPreferenceAlone` | Core overstock and slow preferred stock both stop |
| 4 | `ThinMarginFastMoverOutranksSlowerHighRoiStock` | A 10.5% fast mover beats a 25.3% slow one for the single available slot |
| 5 | `PreferredStockIsNotAnExcuseForAThinOrLosingDeal` (3 cases), `PreferredStockWithNoUsableResaleAnchorIsRejected` | Bad Core deals still rejected |
| 6 | `SpectacularDyesCannotCrowdOutTheConsumablePortfolio` | 8 spectacular dyes take <10% of capital and hit the slot cap |
| 7 | `TinyStacksDoNotGetAFourTimesVelocityBonus` | A 0.25-day stack scores its profit, not 4× |
| 8 | `ATrivialUndercutDoesNotRedefineAHighVolumeMarket`, `ThePlannerPricesAgainstTheDepthAdjustedAnchor` | 3 cheap units ignored; 100 cheap units respected |
| 9 | `AnEarlyWorldBargainDoesNotConsumeCapitalABetterDealNeeds` | A 420% dye on world 1 loses to a gemdraught on world 20 under a tight budget |
| 10 | `DisplayedPlannedAndLiveValidationRoiAgree` | Order ROI = decision-log ROI = live-guard arithmetic, all on landed cost |
| + | `TheMarginBarFollowsVolumeAndValueTogether`, `NothingIsEverBoughtBelowTheAbsoluteFloor`, `TheCostBasisAveragesAndReleasesInsteadOfRatchetingUp`, `TheResaleFloorGrossesUpThroughTheObservedTax`, `DiscoveryProposalsAreCandidatesUntilTheEvidenceSaysOtherwise` | Supporting economics |

**Existing tests changed.** Four controller tests encoded behaviour this rework deliberately
removed, and two of those had been failing since before this work began:

- `RestockingFollowsDemandCoverageRatherThanTheFixedSlotCap` (was
  `AutomaticRestockTargetsPermitSpareStockBesideFullListedItemExposure`) — asserted the
  removed 3-spare-stack clamp; now asserts demand-driven restocking, with a new companion
  test `RestockingStopsOnceDemandCoverageIsSatisfied` for the opposite case.
- `LiquidPreferredMarginCanPurchaseWithinAvailableCapacity` — the zero-capacity case needed
  `ProcurementBagBufferStacks = 0` for "no free slot" to genuinely mean no capacity; the
  margin assertion now checks `CostBasis`/`CostBasisUnits`/`AcquisitionFloor` instead of the
  ratcheted `MinimumMarginPercent`.
- `PurchaseIsCancelledIfExistingBagsCoverTheEmptySlots` — restored as a real fill-order test.
  With the item unpinned, its ~13% ROI falls under the 14% comparison bar and over the 10%
  relaxed bar, so it can only arrive as a fill order, which is what the guard cancels.
- `ListedJunkPushesTheNextPurchasesBackTowardPreferredStock` — the dye exclusion is still
  asserted; the "exactly one purchase" assertion became "only the curated flip, however many
  stacks", since a market this liquid is now allowed more than one.

**Test results:** 325 passed, 0 failed, 0 skipped.

---

## 9. Before / after numbers

All figures computed with the shipped defaults and a 5% buyer fee / 5% sale tax.

### 9.1 Gemdraught HQ ×99 @ 15,000, resale anchor 21,499

| | Old | New |
|---|---|---|
| Landed cost | 1,559,250 | 1,559,250 |
| Net proceeds | 2,021,976 | 2,021,976 |
| Expected profit | 462,726 | 462,726 |
| **Reported ROI** | **31.16%** (pre-fee) | **29.68%** (landed) |
| Enforced bar | 10% (velocity only) | 10% (preferred + liquid + valuable) |
| Days to sell | 2.475 | 2.475 |
| **Profit rate** | 186,960/day | **186,960/day** |
| Allocation score | n/a (tier-major) | 233,700 (×1.25 Core) |

### 9.2 General-Purpose Dye NQ ×20 @ 1,148, resale anchor 6,599

| | Old | New |
|---|---|---|
| Landed cost | 24,108 | 24,108 |
| Expected profit | 101,272 | 101,272 |
| **Reported ROI** | **441%** (pre-fee) | **420%** (landed) |
| Enforced bar | 10% (fast mover — no value floor) | **35%** (low value) |
| Days to sell | 0.333 | 0.333 |
| **Profit rate** | **405,088/day** (0.25 floor ×4) | **101,272/day** |
| Coverage limit | none (2 slots, weekly share) | 0.5 d ≈ **one stack** |
| Allocation score | would win strategies 1 and 4 | 60,763 (×0.6) |

The dye's score falls by 85% — from *beating* the gemdraught's profit rate to less than a
third of it — purely by removing the manufactured turnover and weighting the tier.

### 9.3 Thin-margin fast mover vs fat-margin slow mover

| | Popoto Potage ×99 @ 17,600 | Slow stock ×99 @ 6,500 |
|---|---|---|
| Landed cost | 1,829,520 | 675,675 |
| Expected profit | 192,456 | 170,775 |
| Net ROI | **10.52%** | **25.27%** |
| Sales/day | 120 | 12 |
| Days to sell | 0.825 | 8.25 |
| **ExpectedGilPerDay** | **192,456** | **20,700** |
| Allocation score | 240,570 | 20,700 |

The thin margin wins by 11×, which is the correct answer: the same gil comes back ten times
sooner and goes back to work.

### 9.4 Resale anchor with a trivial undercut

Board: 3 units @ 12,000, 20 units @ 21,500, 99 units @ 22,000. Market sells 150/day.

| | Old | New |
|---|---|---|
| Anchor | `MIN(median, 12,000 − 1)` = **11,999** | absorbable 75 units → **21,999** |
| Ceiling at the 10% bar | 9,869 | 18,094 |
| Result for a 99-stack @ 15,000 | **rejected — looks unprofitable** | **bought, 509,751 profit, 32.7% ROI** |

---

## 10. Limitations and deliberate deferrals

1. **Teleport and venture costs are still not modelled.** A flat `ProcurementTravelReserve`
   is withheld; actual fares never reach expected profit. Bringing them in properly means
   attributing a per-trip cost across a variable basket of purchases, which is a larger change
   than this rework, and the direction of the error is small relative to the margins involved.
2. **Cost basis is aggregate, not per-lot.** See §4.5. The approximation is documented in the
   code and is protective in the ambiguous case.
3. **`salesPerDay` is the whole market's rate, not our share of it.** `EstimatedDaysToSell` is
   therefore an optimistic ranking signal rather than a promise. It is used consistently for
   ranking, so it does not bias between candidates, but the absolute gil/day figures in the
   logs read high. The 1-day normalisation partly compensates.
4. **`LiquidityConfidence` / `PriceConfidence` were not added as first-class metrics.** The
   available data supports the binary `reliableHistory` check (≥3 recent sales) and the
   discovery-side price-spread test; a continuous confidence score would be fake precision on
   a seven-day sample. `MarketDepth` is expressed as the absorption window rather than a
   published metric.
5. **The 30-minute home-anchor staleness window is unchanged.** The live board is re-read for
   the listing before every purchase, but the anchor it is judged against may be up to half an
   hour old.
6. **Discovery remains disabled by default** (`MarketDiscoveryEnabled = false`). The promotion
   ladder is implemented and tested, but turning discovery on is still the user's decision.
7. **`ProcurementOrder.SaleSlots` is always 1.** The per-slot metrics divide by it, so the
   divisor remains inert. Left as-is rather than removing a field a future multi-slot order
   would need.
8. **The plugin version was not bumped and nothing was committed or pushed**, per the explicit
   instruction for this task. `AGENTS.md` standing authorization to publish was not exercised.

---

## Example: What the bot does with 15M gil

A realistic circuit. Wallet 15,000,000 gil, 20 free sale slots, ample bag space, home-world
anchors fresh. Prices are per unit; profit is after the buyer fee and the sale tax.

**What the circuit found**

| Item | Class | Sales/day | Owned | Stack | Buy | Anchor | Landed | Profit | Net ROI | Gil/day | Score |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Caramel Popcorn HQ | Core | 100 | 80 (0.8d) | 99 | 15,000 | 21,499 | 1,559,250 | 462,726 | 29.7% | 462,726 | **578,408** |
| Popoto Potage HQ | Core | 120 | 0 | 99 | 17,600 | 21,499 | 1,829,520 | 192,456 | 10.5% | 192,456 | **240,570** |
| Gemdraught of Water HQ | Core | 40 | 0 | 99 | 15,000 | 21,499 | 1,559,250 | 462,726 | 29.7% | 186,960 | **233,700** |
| Jhinga Curry HQ | Secondary | 60 | 0 | 99 | 12,000 | 16,999 | 1,247,400 | 351,351 | 28.2% | 212,940 | **212,940** |
| Gemdraught of Fire HQ | Core | 40 | 0 | 99 | 16,000 | 21,499 | 1,663,200 | 358,776 | 21.6% | 144,960 | **181,200** |
| Grade XI Materia | Secondary | 25 | 0 | 20 | 8,000 | 14,000 | 168,000 | 98,000 | 58.3% | 98,000 | **98,000** |
| General-Purpose Dye | Opportunistic | 31 | 0 | 20 | 1,148 | 6,599 | 24,108 | 101,272 | **420.1%** | 101,272 | **60,763** |
| Gemdraught of Earth HQ | Core | 40 | 0 | 99 | 19,000 | 21,499 | 1,975,050 | 46,926 | 2.4% | — | **rejected** |

**How the capital is deployed**

The Earth gemdraught never becomes a candidate: at 2.4% net it is below both the 10% core bar
and the 8% absolute floor, so the price ceiling (17,683) excludes the listing. Being pinned
`PreferredStock` buys it nothing.

Everything else is taken in score order until a limit binds:

| # | Purchase | Cost | Running total | Why it stopped or continued |
|---|---|---|---|---|
| 1–3 | Caramel Popcorn ×3 stacks | 4,677,750 | 4,677,750 | 80 → 377 units. Target 300, ceiling 400. A 4th stack would pass the ceiling, and holdings are already at target. **Stop.** |
| 4–5 | Popoto Potage ×2 stacks | 3,659,040 | 8,336,790 | Only two stacks were on offer at that price. Coverage would have allowed four. |
| 6 | Gemdraught of Water ×1 | 1,559,250 | 9,896,040 | Target 120 units; 99 held after. A second stack would reach 198 > 160 ceiling. **Stop.** |
| 7 | Jhinga Curry ×1 | 1,247,400 | 11,143,440 | Secondary, 1.5d target = 90 units. One 99-stack completes the position. |
| 8 | Gemdraught of Fire ×1 | 1,663,200 | 12,806,640 | Same as Water. |
| 9–10 | Grade XI Materia ×2 | 336,000 | 13,142,640 | Secondary, target 38 units. 20 → 40 fills it. **Stop.** |
| 11 | General-Purpose Dye ×1 | 24,108 | **13,166,748** | Opportunistic, 0.5d target = 16 units. One stack. |

**Result**

```
Capital deployed        13,166,748  (87.8% of the wallet)
Held back                1,833,252  (no further qualifying opportunity)
Sale slots used                 11
Expected profit          3,243,215  (24.6% on deployed capital)

By class:   Core         11,559,240   87.8% of deployed
            Secondary     1,583,400   12.0%
            Opportunistic    24,108    0.2%   (cap: 10%)
```

The 420%-ROI dye is bought — it is free money and it is genuinely profitable — but it receives
**0.2% of the capital**, one sale slot and a single stack, because its coverage target is half
a day of a 31/day market. Its spectacular percentage buys it nothing beyond that.

**What the old planner would have done differently**

- **Popcorn would not have been restocked at all.** The weekly market-share cap allowed
  `MAX(99, 25% × 700) = 175` units; 80 were already held, leaving 95 — less than one 99-stack.
  The bot would have travelled the circuit and come home with the best market it has untouched.
- **The dye would have out-scored two of the gemdraughts.** Its old `ProfitVelocity` was
  405,088/day against Water's 186,960/day. Only tier dominance kept it down, and only because
  its resale value per slot (131,980) sat just under the 150,000 floor. A slightly dearer dye
  would have been promoted to Secondary and then beaten real trading stock on rate.
- **A 200%-ROI listing on world 1 could have consumed the budget** before the Popoto Potage or
  the Jhinga Curry were ever seen.
- **A single 3-unit undercut on the home board** would have dragged the popcorn anchor to
  11,999 and made every popcorn listing on the circuit look unprofitable.
- **The displayed ROI on the popcorn would have read 31.2%** while the bot enforced 29.7%.

---

# Marginal Inventory Scoring and Constrained-Capital Allocation

A second, tightly scoped economics pass on top of everything above. Three changes, in
priority order, plus two small correctness fixes. Nothing in the principles section
changed: ROI is still a gate, tier is still a weight rather than a sort key, coverage
is still the overstock protection, and an empty slot is still better than bad stock.

## A. The flaw this pass fixes

Candidate scores were computed once, in `AddCandidate`, before allocation began, and
the allocator then walked a **static** sort. Coverage was updated as stacks were
committed - so the allocator knew it was accumulating inventory - but the *scores*
never moved.

The consequence: three identical listings of Caramel Popcorn carried three identical
scores. The first stack lands on an almost-empty position and starts returning gil
immediately; the third lands behind two days of our own stock and does not finish
selling for nearly four. They were ranked as equals, so the allocator filled one Core
market to its coverage ceiling before considering anything else, however good the
alternatives were.

This was the largest remaining economic error in the planner. It is confirmed, not
theoretical: the 15M walkthrough below shows plain greedy taking three Popcorn stacks
before its first Gemdraught.

## B. Marginal clearing time

`PortfolioPolicy.MarginalDaysToClear` (`PortfolioPolicy.cs:108`):

```
MarginalDaysToClear(owned, quantity, salesPerDay)
    = CLAMP((owned + quantity) / salesPerDay, 0.25, 30)
    = 30                                  when salesPerDay <= 0 or quantity == 0
```

Our own stacks compete with each other. With `owned` units already listed and a market
that absorbs `salesPerDay`, a newly bought stack of `quantity` is not fully liquidated
until `(owned + quantity) / salesPerDay` days have passed - and the capital is
committed for that whole window, not for the `quantity / salesPerDay` a stack would
take against an empty position.

`owned` is everything in front of it: units listed, units in the bags, **and units
committed earlier in this same allocation pass**.

Note the identity `MarginalDaysToClear(0, q, v) == DaysToSell(q, v)`. A market we hold
none of is scored exactly as it was before this change, which is why no existing
economic test moved.

## C. The score

`PortfolioPolicy.MarginalGilPerDay` (`PortfolioPolicy.cs:124`) and
`ProcurementPlannerService.ScoreOf` (`ProcurementPlannerService.cs:255`):

```
MarginalGilPerDay = ExpectedProfit / MAX(ProfitNormalizationDays, MarginalDaysToClear)
AllocationScore   = MarginalGilPerDay * ScoreWeightFor(Tier)
```

The one-day normalisation is unchanged and now applies to the *marginal* figure. Both
halves matter and they guard opposite failure modes:

- the `MAX(1.0, ...)` floor stops a small stack claiming a rate it could only realise
  by finding another underpriced listing and buying it again;
- the marginal numerator stops a large position claiming the rate of its first stack.

**Partial stacks.** A 20-unit lot of a 99-stack item clears in 0.2 days on an empty
position; the floor normalises that to one day, so it scores its own profit - about a
fifth of a full stack's - while still consuming a whole sale slot. It therefore loses
to a full stack for a scarce slot, which is correct, and the marginal term does not
distort it because the term only grows as inventory accumulates.

## D. The allocator

`ProcurementPlannerService.Allocate` (`:279`) is now two stages.

### Stage 1 - repeated best-choice selection (`SelectGreedily`, `:315`)

```
while slots remain and candidates remain:
    for each surviving candidate:
        check the capacity and concentration limits    (drop permanently if failed)
        held  <- units of this item already listed, bagged, or committed this pass
        score <- MarginalGilPerDay(profit, held, quantity, salesPerDay) * tierWeight
    commit the best-scoring candidate
    add its quantity to `held` for that market
```

Every constraint is **monotone** - holdings, spend and slot counts only grow - so a
candidate that fails one can never become feasible later and is dropped for good. The
single exception is the budget check, which leaves the candidate in the pool because
stage 2 may yet free the gil it needs.

Ties break on: absolute profit, then shorter marginal clearing time, then larger
capital deployed (late, as before), then tour priority, item id, listing id, world.
The order is total, so allocation is deterministic.

**Complexity** O(k x n) where k is slots filled and n is candidates - in practice a
few dozen slots against a few hundred listings.

### Stage 2 - bounded constrained-capital repair (`ImproveUnderConstrainedCapital`, `:469`)

Greedy takes the highest-scoring stack it can afford, which under a tight budget can
spend on one candidate worth 300k/day what would have bought two worth 200k/day each.

The repair does not build an optimiser. It re-runs the *same* selection routine a
handful of times with one market's slot allowance reduced, and adopts the result only
if the total objective strictly improves:

```
objective(basket) = sum of the marginal, class-weighted gil/day of every stack,
                    evaluated in the order it was selected

for at most 3 rounds:
    if nothing was blocked on budget: stop
    for each market in the basket, richest first, at most 8:
        try it capped at (its stacks - 1), and at 0
        keep the best strictly-improving alternative
    if none improved: stop
```

**Why slot caps and not banned listings.** The obvious neighbourhood - "ban the
listing greedy picked" - is useless whenever a market has two interchangeable
listings, because the sibling simply takes the slot and the trial is wasted. This was
observed in the 6M walkthrough below before the neighbourhood was redefined. Capping
the market's slot allowance expresses "give this market one fewer stack" directly and
is immune to duplicates. `TheRepairPassIsNotDefeatedByIdenticalSiblingListings` locks
it in.

**Safeguards.** The alternative basket is produced by the same `SelectGreedily`, so
every budget, sale-slot, coverage, per-item concentration, opportunistic slot and
capital, quality and rule constraint is enforced identically - there is no separate
constraint code to drift. Acceptance requires a strict improvement, and the round
count is bounded, so it always terminates. It is skipped entirely unless the budget
actually blocked something, so a large wallet pays nothing for it and behaves exactly
as plain greedy did.

**Complexity** at most 3 x 16 = 48 extra `SelectGreedily` runs, and normally zero.

**The trade it makes.** Maximising gil per day can accept less absolute profit per
trip in exchange for faster capital turnover. That is the stated objective - long-run
realised gil under limited capital - but it is a real trade and worth naming.

## E. Personal sell-through: deferred, with the estimator written and tested

`salesPerDay` is the whole home world's rate. If we are one of four sellers, three
days of "coverage" may really be closer to twelve, and every holding-time and coverage
figure in this document is optimistic by that factor.

**This was investigated and deliberately not shipped.** Be clear about what exists:

| Piece | Status |
|---|---|
| `SellThroughObserver` estimator + `SellThroughObservation` state | Written, 10 tests, **not called by any runtime path** |
| `Configuration.SellThrough` persistence slot | Declared, **never written** |
| Observation wiring into the retainer pass | **Not implemented** |
| Any effect on velocity, coverage or purchasing | **None** |

The estimator is retained as reviewed, tested groundwork so the measurement can be
switched on later without re-deriving the safeguards:

```
available   = previousUnits + purchasedSinceLastReading
soldRate    = MIN((available - currentUnits) / elapsedDays, marketUnitsPerDay)
UnitsPerDay = UnitsPerDay + 0.3 * (soldRate - UnitsPerDay)          (EWMA)
IsReliable  = Samples >= 4 AND ObservedDays >= 3
CaptureShare(market) = CLAMP(UnitsPerDay / market, 0.25, 1)   or null when unreliable
```

Safeguards, each with a test: the first reading only sets a baseline; restocking is
never negative sales; windows shorter than 0.25 days or longer than 14 are discarded;
an unexplained gain teaches nothing rather than counting as negative sales; a rate
above the whole market is capped rather than believed; an empty position cannot
manufacture a sample; sparse history returns `null` rather than a number; and the 0.25
floor means no future correction could cut a market's assumed demand more than
four-fold.

**Why it is deferred rather than wired up.** The repository has no sale signal. It does
not read the game's retainer sale history addon, Universalis sale entries carry no
seller identity, and `WealthHistoryService` tracks gil totals rather than per-item
proceeds. Inventory differencing between two complete retainer passes is the only
available source, and it cannot distinguish a sale from:

- the player manually withdrawing stock from a retainer,
- an item being used or discarded,
- a market listing expiring off the board after 30 days.

All three inflate apparent sell-through, which inflates capture share, which would
raise effective velocity, which would make the bot **buy more**. That is the unsafe
direction. Feeding an unvalidated estimator into position sizing was judged worse than
leaving the known optimism in place, where it is at least documented and constant.

**What would unlock it.** Reading the retainer's own sale history from the game, which
would give confirmed per-item sales with timestamps and prices and make all three
confounds irrelevant. At that point the wiring is small: record a reading per complete
pass, and blend `marketRate x captureShare` into `SalesVelocityPolicy`.

## F. Small correctness fixes

**`SalesVelocityPolicy` treating a reported zero as fact.** Confirmed real. Universalis
returns `0` both for "nothing sold" and for "no data window for this quality", and the
guard `reported is >= 0` took it literally, vetoing the seven-day fallback. An item
with 700 observed HQ sales scored as if it never moved, which then denied it the
volume margin bar and made coverage refuse to size a position. Now `reported is > 0`,
so a zero defers to recorded sales. It still invents nothing: with no corroborating
history the answer is still zero, and one quality is never lent the other's rate. This
is a risk-*increasing* change - it can raise a velocity estimate - so it is worth being
explicit that it only ever defers to directly observed sales.

**`WeeklyShareLimits` overwritten per market.** Confirmed real. The dictionary is keyed
per (item, quality) but written inside the per-market loop, so with two market entries
for one item the last write won and could silently *raise* the cap. It now takes the
minimum of the computed limits. This is a backstop that only applies when velocity
cannot size a position, so the practical effect is small, but the direction was wrong.

## G. Before / after

Same fixture as the 15M walkthrough below. "Before" is the static score the previous
implementation ranked on; "after" is the marginal score.

| Stack | Before (static) | After (marginal) | Marginal days |
|---|---:|---:|---:|
| Popcorn #1 (80 units held) | 578,408 | **323,133** | 1.79 |
| Popcorn #2 | 578,408 | **208,060** | 2.78 |
| Popcorn #3 | 578,408 | **153,424** | 3.77 |
| Popoto #1 | 240,570 | 240,570 | 0.83 |
| Popoto #2 | 240,570 | **145,800** | 1.65 |
| Gemdraught of Water | 233,700 | 233,700 | 2.48 |
| Jhinga Curry | 212,940 | 212,940 | 1.65 |
| Gemdraught of Fire | 181,200 | 181,200 | 2.48 |
| Grade XI Materia #1 | 98,000 | 98,000 | 0.80 |
| Grade XI Materia #2 | 98,000 | **61,250** | 1.60 |
| General-Purpose Dye | 60,763 | 60,763 | 0.65 |

Only repeat stacks moved. Every first stack is scored exactly as before.

### Purchase order

**Before:** Popcorn, Popcorn, Popcorn, Popoto, Popoto, Water, Jhinga, Fire, Materia,
Materia, Dye.

**After:** Popcorn, Popoto, Water, Jhinga, **Popcorn**, Fire, **Popcorn**, Popoto,
Materia, Materia, Dye.

### Where it actually changes the result

With 15M gil and 20 slots nothing binds, so both orderings buy the same eleven stacks
for the same 13,166,748 gil and the same 3,243,215 profit. The ordering still matters -
trips end early on time, bag space or gil, and the order is the priority in which
worlds are revisited - but the honest statement is that **the basket only changes when
a constraint binds.** Three cases where it does:

| Scenario | Before | After |
|---|---|---|
| **5 sale slots**, 15M gil | 3x Popcorn + 2x Popoto<br>8,336,790 gil, 1,773,090 profit, objective 1,070,987 | Popcorn, Popoto, Water, Jhinga, Popcorn<br>**7,754,670 gil, 1,931,985 profit, objective 1,218,403** |
| **6M gil**, 20 slots | 3x Popcorn, Jhinga, Dye<br>5,949,258 gil, 1,840,801 profit | Popcorn, Water, Jhinga, Popcorn, Dye<br>**5,949,258 gil, 1,840,801 profit** (same basket, reached differently) |
| **6M gil**, no Fire/Earth/Materia | greedy: Popcorn, Popoto, Water<br>4,948,020 gil, objective 797,403, 1.05M idle | repair drops Popoto: Popcorn, Water, Jhinga, Popcorn<br>**5,925,150 gil, 1,739,529 profit, objective 977,833** |

The five-slot case is the clearest: **582,120 gil less capital, 158,895 more profit,
and 13.8% more gil per day.** The third case is the repair pass alone, worth +22.6% on
the objective and +977,130 gil of productive deployment.

## H. Tests

`tests/SmartUndercutBot.Core.Tests/MarginalAllocationTests.cs` - 25 cases:

| Requirement | Test |
|---|---|
| Marginal model arithmetic, clamps, zero velocity | `MarginalClearingCountsTheInventoryQueuedInFront` |
| A. Excellent Core market still takes several stacks | `AnExcellentCoreMarketStillReceivesMultipleStacks` |
| B. Later stacks strictly lower priority | `EachAdditionalStackOfTheSameItemScoresStrictlyLower` |
| C. Strong alternatives interleave | `StrongAlternativesInterleaveWithAFillingCoreMarket` |
| D. Dominance still fills deeply | `ADominantCoreMarketStillFillsDeeplyWhenEverythingElseIsWorse` |
| Greedy leaves gil on the table; repair finds it | `TightCapitalPrefersTwoCheaperStacksOverOneExpensiveOne` |
| Repair respects the slot constraint | `TheRepairPassRespectsTheSlotConstraintItCannotBuyAround` |
| Large budget unchanged | `ALargeWalletIsUnaffectedByTheRepairPass` |
| Determinism over repeated runs | `AllocationIsDeterministic` |
| Duplicate sibling listings | `TheRepairPassIsNotDefeatedByIdenticalSiblingListings` |
| One item, many listings | `ManyListingsOfOneItemCannotOutrunItsOwnDemand` |
| Holdings near the coverage target | `HoldingsAlreadyNearTargetAdmitOneStackAndNoMore` |
| Wide price variation within one item | `WidePriceVariationWithinOneItemIsTakenCheapestFirst` |
| Partial stacks | `APartialStackDoesNotOutrankAFullOneForAScarceSlot` |
| Spectacular cheap dye, scarce slots | `ASpectacularDyeStillCannotBuyItsWayIntoAScarceSlot` |
| Unreliable / zero velocity | `NoUsableVelocityMeansNoPositionToSize` |
| Budget never exceeded across six budgets | `ATightBudgetNeverExceedsItselfHoweverManyRepairRoundsRun` |
| Monotonicity in slots | `MoreSlotsNeverProduceAWorseBasket` |
| No discontinuity at the one-day normalisation boundary | `MarginalNormalizationHasNoJumpAtOneDay` |
| Partial stack fits the coverage a full one would breach | `PartialStackFitsRemainingCoverageAndScoresBehindExistingHoldings` |
| Duplicate market rows cannot buy one listing twice | `DuplicateMarketRowsCannotBuyTheSameListingTwiceAcrossWorldCasing` |
| HQ and NQ of one item stay distinct, order-independently | `ListingIdentityKeepsSeparateQualitiesWithSyntheticIds` |
| Repeated market rows take the tighter weekly share | `RepeatedMarketsUseTheTighterWeeklyShareRegardlessOfInputOrder` |
| Opportunistic caps recomputed against the replacement basket | `RepairRebuildsOpportunisticCapsAgainstTheReplacementBasket` |
| The documented 15M basket, and input-order independence | `FifteenMillionWalkthroughMatchesTheDocumentedBasket` |

`tests/SmartUndercutBot.Core.Tests/SellThroughObserverTests.cs` - 10 cases covering
every safeguard in section E, against the estimator directly since nothing calls it.

`SalesVelocityPolicyTests.KnownZeroDoesNotFallBackToPositiveHistory` was replaced by
`ReportedZeroDefersToObservedSalesButInventsNothing`: it asserted the defect described
in section F, and now asserts the corrected behaviour in all three directions.

## I. Limitations of this pass

1. **Marginal clearing assumes our stacks sell strictly in sequence.** In reality
   several of our listings compete simultaneously and the whole position drains in
   parallel. `(owned + quantity) / salesPerDay` is the correct *completion* time for
   the position either way, which is what capital commitment depends on, but it is not
   a model of which individual stack sells first.
2. **It still uses the whole market's rate.** Section E is exactly this problem, and it
   is measured but not corrected.
3. **The repair neighbourhood is one market at a time.** It cannot discover an
   improvement that requires reducing two markets simultaneously. Bounded search was
   the explicit instruction, and the observed cases are all single-market.
4. **The objective sums per-slot rates.** When sale slots rather than gil are the
   binding constraint this slightly favours baskets with more, smaller stacks. The
   slot limit itself is enforced exactly, and `MoreSlotsNeverProduceAWorseBasket`
   guards the direction, but the objective is not slot-normalised.
5. **A candidate rejected at the price ceiling produces no decision-log entry**, so an
   item like the 19,000-gil Gemdraught of Earth simply does not appear in the log. It
   is filtered before candidacy. Pre-existing, and not addressed here.

