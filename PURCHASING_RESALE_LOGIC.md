# AutoUndercutter — Purchasing, Resale & Prioritization Logic

**Scope:** a factual map of the economic decision system as implemented at commit `ad1fa36`
(config schema `Version = 42`). No application code was changed to produce this document.

**How to read this document.** Claims are tagged:

- **[IMPL]** — current implemented behaviour, verified by reading the code.
- **[CFG]** — behaviour that only occurs under a particular configuration.
- **[DEAD]** — code that exists but cannot execute in the default/shipped configuration.
- **[OBS]** — my observation or inference, not a direct code statement.

References use `path/to/File.cs -> Type.Member` with line numbers as of commit `ad1fa36`.

> **Terminology.** "Sale slot" = one of the 20 market-board slots a retainer has; with 3 retainers
> that is 60, the shipped `ProcurementTargetSaleSlots` default. "Landed cost" = purchase price plus
> the 5% market-board buyer fee. "Home world" = the character's own world, where everything is resold.

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

### What it considers

Only items with an **enabled `ProcurementRule`**. There is no open-ended market scan in the buying
path. Rules come from five seeded sets plus optional discovery
(`src/SmartUndercutBot/Services/UniversalisService.cs`, L73–150):

| Set | Seeded as | Bought? |
|---|---|---|
| 6 curated consumables (4 Grade 4 Gemdraughts, Caramel Popcorn, Popoto Potage) — `CreateFavoriteRules` L73 | `PreferredStock=true`, `TourPriority=0`, HQ-required, 8 slots | Yes — the portfolio core |
| `General-Purpose *` / `Wide-Spectrum *` dyes — `CreateBuyableDyeRules` L114 | `TourPriority=1`, stack 20, 2 slots, min 50/wk | Yes |
| Materia grades **XI/XII** — `CreateTradeableMateriaRules` L142 | `TourPriority=2`, stack 20, **1 slot**, min 50/wk | Yes |
| All other dyes, all ethers, older materia — `CreateLiquidationRules` L100 | `LiquidateOnly=true` | **Never** — sold from bags only |
| 8 named tomestone materials — `CreateTomeMaterialRules` L130 | `LiquidateOnly=true` | **Never** |

### What it ignores

`LiquidateOnly` rules are filtered out at the top of both planners
(`ProcurementPlannerService.cs -> BuildPlan` L24, `BuildLiveMarketPlan` L146) and again in the
pre-purchase guard. Items with no rule are invisible to the buying path entirely.

### When something is "profitable"

Profitability is enforced **as a price ceiling**, not as a post-hoc score. For each item/quality the
planner computes the highest price it could pay and still clear both the ROI floor and the flat
per-unit profit floor, then considers only listings at or below it. **The ROI floor itself is
velocity-tiered**: anything selling ≥10 units/day at home is held to the lower "fast mover" bar
(10% by default) instead of the standard bar (20%). See §4.

### When it buys

Two paths:

1. **Immediate ("exceptional")** — during scouting, if net expected profit ≥ 100% of landed cost
   (`ShoppingScoutPolicy.IsExceptional` L74), it buys on the spot.
2. **Deferred (normal)** — everything else is remembered and compared at the end of the circuit
   (`ProcurementController.Priority.cs -> FinishPriorityScouting` L195), then the winners are
   revisited and re-validated live before purchase.

### How much it buys

**It does not choose a quantity.** It buys whole existing listings, filtered to
`listing.Quantity <= rule.TargetStackSize`. Volume is controlled by *how many listings* it takes,
via sale-slot caps, per-item caps, a weekly market-share cap, and budget. See §8.

### When and at what price it resells

Everything is listed on the home world and repriced every retainer pass. Pricing is
**lowest competitor − 1 gil**, floored at `max(MinimumPrice, costBasis × (1 + margin%))`. See §6.

### How it chooses between competing opportunities

Not a scalar score. A **three-tier portfolio model** plus a **lexicographic objective over five
candidate plans**. Tier dominates everything: a Core item always outranks a Secondary item, which
always outranks an Opportunistic one, regardless of ROI or profit. See §3.

### Role of ROI vs absolute gil vs velocity

- **ROI** is a *gate only* (a price ceiling), never a ranking key. `ProcurementOrder.RoiPercent` is
  documented in-code as "kept as a safety guard rather than the objective".
- **Absolute gil profit** is objective #4 in the plan comparison, and the primary key of strategy 2.
- **Sales velocity** (units/day) is the most pervasive signal: it sets tier eligibility, **lowers the
  ROI gate** for fast movers, drives `ProfitVelocity` (objective #3), and orders the scan.

### Emergent strategy

> Hold ~75% of retainer slots in pinned high-value consumables. Allow a minority of slots for
> genuinely liquid, high-value secondary stock. Cap cheap arbitrage at ~10% of slots no matter how
> good its percentage return. Accept a thin margin on stock that turns over daily, because idle gil
> earns nothing. Undercut aggressively but never below landed cost.
> **An empty slot is explicitly preferred to a slot of junk.**

[OBS] The system is deliberately engineered *against* the classic "high ROI on cheap items" trap.
§9 examines how well that holds.

---

## 2. Complete Purchase Decision Pipeline

### 2.1 Trip-level gates (before any item is examined)

`src/SmartUndercutBot/Automation/ProcurementController.cs -> TryAutomaticStart` (L1700)

```
IF IsStartBlocked OR !AllowAutomaticPurchases OR !AutomaticProcurementEnabled
   OR !IsRetainerListOpen OR repricing.IsActive OR repricing.RequiresManualRestart
   OR !playerState.IsLoaded                                     -> RETURN
IF repricing.LastKnownFreeSaleSlots is null                     -> RETURN   (no verified capacity yet)
IF SpendableGil(wallet, travelReserve, reinvest:true, 0) == 0   -> RETURN
IF LiveWorldStockHuntEnabled                                    -> live-hunt branch, then RETURN
IF !newlyAvailableCapacity AND !incomeArrived AND now < nextAutomaticScan -> RETURN
IF HoldingForSaleSlots OR HoldingForGil                         -> RETURN
-> StartScan(AutomaticPurchase)
```

Two "hold" gates keep it home (`ProcurementController.cs` L188, L196):

| Gate | Condition | Meaning |
|---|---|---|
| `HoldingForSaleSlots` | `FreeSaleSlots < ShoppingTripMinimumFreeSaleSlots` (10) | Not enough empty slots to justify a trip |
| `HoldingForGil` | `ShoppingBudget < ShoppingTripMinimumGil` (1,000,000) | Travelling with pocket change wastes the trip |

### 2.2 The circuit

`ProcurementController.Priority.cs -> BeginPriorityShopping` (L296)

1. Filter rules: enabled, non-liquidate, **and** either in the "snipe block"
   (`PreferredStock || AlwaysScout`) or showing ≥ `MinimumWeeklyUnitsSold` in 7-day home sales.
2. Order: snipe-block first → descending home sales/day → `TourPriority` → name.
3. Scan the **home world** first to establish resale anchors.
4. Travel the circuit one data center at a time (`ShoppingScoutPolicy.BuildRoute`), home DC first.
5. Per away world, price up to `PriorityItemsPerWorld` (14) items: the snipe block **always**, plus
   hinted bargains (¼ of the stop), plus busiest secondary lines, plus a rotation
   (`ShoppingScoutPolicy.SelectWorldItems`).

### 2.3 Per-item pipeline — one item, discovery → BUY/REJECT

`src/SmartUndercutBot.Core/Services/ProcurementPlannerService.cs -> BuildPlan` (L13–132)

```
STEP 0  Request sanity                                     [L16-19]
        GilBudget==0 OR FreeSaleSlots<=0 OR FreeInventorySlots<=0
        OR MarketTaxPercent  outside [0,100]
        OR BuyerFeePercent   outside [0,100]
        OR MinimumRoiPercent outside [0,1000]
        OR FastMoverRoiPercent > 1000
        OR MaximumWeeklySalesSharePercent outside (0,100]      -> EMPTY PLAN

STEP 1  Rule lookup                                        [L24]
        !Enabled OR ItemId==0 OR LiquidateOnly                  -> REJECT

STEP 2  Quality eligibility        ResaleStockPolicy.BuyableQuality [L67]
        BuyHighQualityOnly=true and item HAS an HQ form -> NQ rejected
        item has NO HQ form (dyes)                      -> NQ allowed

STEP 3  Demand gate                                        [L43]
        sales = home 7-day sales, matching quality, price>0, qty>0
        SUM(quantity) < rule.MinimumWeeklyUnitsSold             -> REJECT

STEP 4  Weekly market-share cap                            [L50-56]
        observedLimit = MAX(rule.TargetStackSize,
                            FLOOR(weeklyUnits × MaximumWeeklySalesSharePercent/100))
        remaining     = MAX(0, observedLimit − ownedUnits)
        (recorded here, enforced later in Allocate)

STEP 5  Resale anchor                                      [L58-76]
        targetSalePrice = MEDIAN(home 7-day sale prices)
        IF HomeWorld set:
            homeListings = home-world listings, same quality, not our retainers
            homeLowest   = MIN(HomePriceReference.WithoutOutliers(homeListings))   [L69]
            targetSalePrice = MIN(targetSalePrice, homeLowest − 1)                 [L73]
        targetSalePrice == 0                                    -> REJECT          [L75]

STEP 6  Price ceiling — the ROI + profit gate              [L78-93]
        netUnitProceeds = FLOOR(targetSalePrice × (1 − MarketTax/100))     ; tax = 5
        buyerFeeMult    = 1 + BuyerFee/100                                 ; fee = 5
        salesPerDay     = SalesVelocityPolicy.DailyUnits(market, quality)  [L80]
        requiredRoi     = PortfolioPolicy.RequiredRoiPercent(              [L81]
                              MinimumRoiPercent, FastMoverRoiPercent, salesPerDay)
                        = (FastMoverRoi >= 0 AND salesPerDay >= 10)
                              ? MIN(MinimumRoi, FastMoverRoi)      ; 10% by default
                              : MinimumRoi                         ; 20% by default
        roiCeiling      = FLOOR(netUnitProceeds / (1+requiredRoi/100) / buyerFeeMult)
        profitCeiling   = FLOOR(MAX(0, netUnitProceeds − minProfitPerUnit) / buyerFeeMult)
        ceiling         = MIN(roiCeiling, profitCeiling)
        IF rule.MaximumUnitPrice > 0: ceiling = MIN(ceiling, rule.MaximumUnitPrice)
        ceiling == 0                                            -> REJECT          [L92]

STEP 7  Per-listing filter                                 [L95-102]
        REJECT listing IF  ItemId mismatch
                        OR PricePerUnit == 0
                        OR PricePerUnit > ceiling            <-- ROI/profit enforced HERE
                        OR RetainerId ∈ OwnedRetainerIds     <-- never buy from self
                        OR WorldName blank
                        OR Quantity == 0
                        OR IsHighQuality != quality
                        OR Quantity > MAX(1, rule.TargetStackSize)

STEP 8  Profit computation                                 [L104-107]
        totalCost = PurchaseCost = CEIL(price × qty × buyerFeeMult)        [L396]
        totalNet  = (ulong)(uint)netUnitProceeds × qty                     [L105]
        totalNet <= totalCost                                   -> REJECT
        expectedProfit = totalNet − totalCost

STEP 9  Tier + slot-value gate      AddCandidate            [L233-244]
        tier = PortfolioPolicy.ClassifyCandidate(...)                      [L73]
        IF tier != Core AND ExpectedProfitPerSaleSlot < gates.MinimumProfitPerSaleSlot
                                                                -> REJECT  [L239]
        else -> CANDIDATE

STEP 10 Allocation across 5 strategies  Allocate            [L254-339]
        per-strategy skip conditions, in order:
          orders.Count >= slotLimit                          -> skip
          Opportunistic AND tierSlots+slots > OpportunisticCap -> skip     [L303]
          itemSlots[item] >= rule.MaximumSaleSlots            -> skip
          spent + cost > budget                              -> skip
          boughtUnits + qty > weeklyShareLimit                -> skip
        -> else ADD to that plan

STEP 11 Plan selection (lexicographic)                      [L342-357]

STEP 12 Live re-validation before submit  PollListings      [L1233-1392]
        -> BUY or SKIP
```

### 2.4 Step 12 — the live pre-purchase guard, in order

`ProcurementController.cs -> PollListings` (L1233). **Sequential early returns; order matters.**

| # | Check | Line | On failure |
|---|---|---|---|
| 1 | comparison pass under its 20-minute limit | L1236 | FinishShopping |
| 2 | home reference still fresh (≤30 min) | L1242 | SkipCurrentOrder |
| 3 | `AllowAutomaticPurchases` still armed | ~L1247 | FinishShopping |
| 4 | still on the right world, board open, Lifestream idle | ~L1252 | FinishShopping |
| 5 | fill order & empty retainer slots already covered | L1264 | Skip |
| 6 | fill order & tier == Opportunistic | L1272 | Skip |
| 7 | tier == Opportunistic & no cap headroom (`OpportunisticHeadroom <= 0`) | L1278 | Skip |
| 8 | rule exists / enabled / non-liquidate / quality OK / under `MaximumSaleSlots` | L1283 | Skip |
| 9 | clamp `MaximumAcceptableUnitPrice` and `Quantity = MIN(order.Quantity, TargetStackSize)` | L1291–1297 | — |
| 10 | live listings ready (retry ×3 via `RetryListingRequest`) | L1299, L1304, L1486 | Skip on timeout (L1312) |
| 11 | `TrySelectLiveListing` matches the plan | L1331 | Skip |
| 12 | independent re-validation of the returned listing | L1340 | Skip (L1356) |
| 13 | **profit re-check with server-reported buyer tax** | L1359-1361 | Skip |
| 14 | slots + `ProcurementInventoryReserve` | L1366 | FinishShopping |
| 15 | `totalCost > SpendableGil()` (if budget 0 ⇒ go home) | L1371-1376 | Skip / FinishShopping |
| 16 | `SubmitPurchase` | — | — |

Check 13 (L1359):

```
expectedNetProceeds = FLOOR(TargetSalePrice × 0.95) × liveQty       ; 0.95 HARDCODED
IF expectedNetProceeds < totalCost × (1 + RequiredPurchaseRoi(order)/100)
   OR expectedNetProceeds − totalCost < minProfitPerUnit × liveQty  -> SKIP
```

`totalCost` here is `GetPurchaseCost(live)` = `price × qty + live.TotalTax`, i.e. the
**server-reported** tax rather than the assumed 5%.

`RequiredPurchaseRoi` (L1481) mirrors the planner's velocity tiering:

```
order.IsFillOrder ? ProcurementFillRoiPercent                       ; 10%
                  : PortfolioPolicy.RequiredRoiPercent(MinimumRoi, FastMoverRoi, order.SalesPerDay)
```

### 2.5 Ordering concern: could mediocre beat better?

**Yes, in one specific place — and it is prevented everywhere else.** [OBS]

*The immediate-buy path bypasses cross-item comparison.*
`ProcurementController.Priority.cs -> ObservePriorityItem` (L470–545): candidates for **this one
item** are ordered by tier → `ExpectedProfit` → `PricePerUnit` (L515–516), and if the winner clears
`IsExceptional` (≥100% net ROI, L517) it buys **immediately**, consuming a slot and budget before any
later world is seen. A 100%-ROI cheap item bought on world 1 can therefore block a 60%-ROI, far more
valuable item on world 20. The slot-value gate and opportunistic cap still apply, because the order
came out of `BuildPlan`.

*Everything else is properly comparative* — the deferred path compares complete plans (§3.4).

---

## 3. Opportunity Scoring / Prioritization

**There is no single numeric score.** Priority is (a) tier, (b) five competing allocation
strategies, (c) a lexicographic choice between the resulting plans.

### 3.1 Tier assignment — the dominant signal

`src/SmartUndercutBot.Core/Services/PortfolioPolicy.cs -> ClassifyCandidate` (L73–89)

```
IF rule.PreferredStock            -> Core
ELSE IF salesPerDay   >= 10                     (SecondaryMinimumSalesPerDay, L28)
     AND valuePerSlot >= 150,000                (SecondaryMinimumValuePerSlot, L29)
     AND profitPerSlot>= gates.MinimumProfitPerSaleSlot   (default 2,500)
                                  -> Secondary
ELSE                              -> Opportunistic
```

For **already-owned** stock, `ClassifyHolding` (L91) omits the profit term (profit is sunk).
An item with **no known market data** classifies as Opportunistic — so a retainer full of listed dye
actively consumes the opportunistic cap and blocks buying more.

`Rank` (L45): Core=0, Secondary=1, Opportunistic=2. Every allocation strategy is `OrderBy(Rank)`
first (`Tiered()`, L375), so **tier strictly dominates all other signals**.

### 3.2 Derived per-order metrics

`src/SmartUndercutBot.Core/Models/ProcurementModels.cs -> ProcurementOrder` (L188–232)

```
Slots                     = MAX(1, SaleSlots)                        ; always 1 in practice
ExpectedResaleValue       = TargetSalePrice × Quantity                        [L209]
ResaleValuePerSaleSlot    = ExpectedResaleValue / Slots                       [L210]
ExpectedProfitPerSaleSlot = ExpectedProfit / Slots                            [L211]
EstimatedDaysToSell       = CLAMP(Quantity / SalesPerDay, 0.25, 30)           [L212, policy L57]
ProfitVelocity            = ExpectedProfit / MAX(0.25, EstimatedDaysToSell) / Slots  [L215]
RoiPercent                = ExpectedProfit / (PricePerUnit × Quantity) × 100  [L218]
```

`SalesPerDay` = `SalesVelocityPolicy.DailyUnits` — Universalis `nq/hqSaleVelocity` if present and in
`[0, 1e9]`, else 7-day recent sales ÷ 7.

> `RoiPercent` divides by **pre-fee** cost while the planner's ceiling uses **post-fee** cost.
> `RoiPercent` is display/logging only; it is never used in a comparison. [IMPL]

### 3.3 The five allocation strategies

`ProcurementPlannerService.cs -> Allocate` (L273–277). All tier-major:

| # | Ordering after tier | Line |
|---|---|---|
| 1 | `ProfitVelocity` desc, cost asc | L273 |
| 2 | `ExpectedProfit` desc, cost asc | L274 |
| 3 | `ExpectedProfit / cost` desc, cost asc ← the ROI-ish strategy | L275 |
| 4 | `SalesPerDay` desc, `ProfitVelocity` desc, cost asc | L276 |
| 5 | `ResaleValuePerSaleSlot` desc, `TourPriority` asc, cost asc | L277 |

Each produces a full plan under all caps. `TourPriority` appears **only** in strategy 5 as a
tie-break — manual category preference cannot outrank value or demand.

### 3.4 Plan selection — the actual objective function

`ProcurementPlannerService.cs -> Allocate` (L342–357), evaluated strictly in order:

```
1. MAX  MIN(coreSlotsAdded, opening.CoreDeficit)      -- close the core deficit first
2. MIN  MAX(0, opportunisticSlots − OpportunisticCap) -- never exceed the cheap-stock cap
3. MAX  SUM(ProfitVelocity) over non-Opportunistic orders
4. MAX  SUM(ExpectedProfit)
5. MAX  order count                                   -- slot occupancy is LAST
6. MAX  distinct item count                           -- diversification
7. MIN  distinct world count                          -- travel cost
8. MIN  TotalCost
```

The in-code comment at L344 states the intent: *"Slot occupancy is deliberately last: a cheap
low-value item must never win merely by filling one more slot."*

### 3.5 Complete list of priority influences

| Variable | Where it enters | Effect |
|---|---|---|
| ROI % | Step 6 price ceiling; `RequiredPurchaseRoi` | **Gate only**, never ranked |
| Absolute gil profit | Objective 4; strategies 1–3 | Ranked |
| Purchase price | `Cost()` tie-break (asc) in all strategies | Cheaper wins ties |
| Expected sale price | `TargetSalePrice` → all value metrics | Ranked via value/slot |
| Sales velocity / per day | **ROI gate tier**, portfolio tier gate, `ProfitVelocity`, strategy 4, scan order | Strongest single signal |
| Market volume (7-day units) | Step 3 gate; Step 4 share cap | Gate |
| Competing listings count | Only via `HomePriceReference` outlier filter | Indirect |
| Historical sales | Median → resale anchor (Step 5) | Sets the anchor |
| Stack size | `TargetStackSize` caps listing size; buffer slot maths | Gate |
| Quantity available | Listing quantity; must be ≤ `TargetStackSize` | Gate |
| Inventory slots | `slotLimit = MIN(FreeSaleSlots, FreeInventorySlots)` | Hard cap |
| Retainer slots | `AvailablePurchaseSlots` → `PlannedSaleSlots` | Hard cap |
| Available gil | `SpendableGil()` → budget | Hard cap |
| Item category | Only via seeded `TourPriority` / `PreferredStock` | Tie-break / pin |
| Item level | **Not used anywhere** | — |
| Crafting/consumable status | Discovery only (`IsFoodOrMedicine`) | Discovery filter |
| Materia | Seeded rules; XI/XII buyable at 1 slot, rest liquidate-only | Category rule |
| Manual priorities | `TourPriority`, `PreferredStock`, `AlwaysScout` | Pin / tie-break |
| Whitelist | The rule list *is* the whitelist | Absolute |
| Blacklist | `LiquidateOnly`, `Enabled=false`, `ProcurementTravelPolicy.ExcludedWorlds` | Absolute |
| Favorites | `CreateFavoriteRules` → `PreferredStock` | Pins to Core |
| Max price | `rule.MaximumUnitPrice` (0 = unlimited) | Caps ceiling |
| Min price | `PricingRule.MinimumPrice` (resale side only) | Floor |

---

## 4. Profit and ROI Math

### 4.1 Constants — and what is ignored

| Quantity | Value | Source | Configurable? |
|---|---|---|---|
| Market tax (sale) | **5%** | `ProcurementModels.cs` L156 / L180 (`MarketTaxPercent = 5m`) | **No** — no caller ever passes it |
| Buyer fee (purchase) | **5%** | `ProcurementModels.cs` L157 / L181 (`BuyerFeePercent = 5m`) | **No** — no caller ever passes it |
| Live pre-purchase net | **×0.95** | `ProcurementController.cs` L1359, hardcoded literal | No |
| Resale floor gross-up | **÷0.95** | `ProcurementPriceSafety.cs` L10, hardcoded literal | No |
| Exceptional-buy fee | **×1.05** | `ShoppingScoutPolicy.cs` L74, hardcoded literal | No |

[IMPL] Verified: `grep "MarketTaxPercent:\|BuyerFeePercent:"` across `src/` returns **no call sites**.
Both always take their record defaults.

**Fees the model accounts for:** the 5% market-board sale tax and a 5% purchase-side fee. At purchase
time the *actual* server-reported tax is used (`live.TotalTax`).

**Fees and costs ignored:** [OBS]

- **Retainer venture/upkeep** — not modelled.
- **Teleport cost** — only a flat `ProcurementTravelReserve` (5,000 gil) is withheld; actual
  teleport fares are never deducted from expected profit. A 31-world circuit has real gil cost that
  appears in no profit calculation.
- **The live seller fee is read but unused in decisions.** `RetainerListingService.TryReadSellerFeePercent`
  is read at `AutomationController.cs` L911 but flows **only** into `PortfolioListingEstimate` for the
  Earnings display. Buy and resale maths use the hardcoded 5%.
- **Opportunity cost of time/travel** — partially proxied by `ProfitVelocity`, never in gil.

### 4.2 The formulas

```
netUnitProceeds = FLOOR(targetSalePrice × 0.95)
buyerFeeMult    = 1.05
requiredRoi     = (salesPerDay >= 10) ? MIN(20, 10) = 10        ; fast mover
                                      : 20                       ; standard
totalCost       = CEIL(pricePerUnit × quantity × 1.05)
totalNet        = (uint)netUnitProceeds × quantity
expectedProfit  = totalNet − totalCost                           ; only if totalNet > totalCost

roiCeiling      = FLOOR(netUnitProceeds / (1 + requiredRoi/100) / 1.05)
profitCeiling   = FLOOR(MAX(0, netUnitProceeds − minProfitPerUnit) / 1.05)
ceiling         = MIN(roiCeiling, profitCeiling [, rule.MaximumUnitPrice])
```

**ROI basis:** the *ceiling* is derived so that net proceeds ÷ landed cost ≥ 1 + requiredRoi — i.e.
ROI against **landed (post-fee) cost**, computed **per unit**, applied to the **whole stack**.
The reported `RoiPercent` uses **pre-fee** cost — a different basis (see §12).

**Rounding:** `FLOOR` on proceeds and ceilings (conservative); `CEIL` on cost (conservative);
integer truncation when `netUnitProceeds` is cast to `uint`.

### 4.3 Worked example — Grade 4 Gemdraught, HQ (a fast mover)

Home median sale 22,000; cheapest non-self home listing 21,500; `SalesPerDay = 40`;
`MinimumRoi=20`, `FastMoverRoi=10`, `minProfitPerUnit=100`, `TargetStackSize=99`.

```
targetSalePrice = MIN(22,000, 21,500 − 1) = 21,499
netUnitProceeds = FLOOR(21,499 × 0.95) = 20,424
requiredRoi     = salesPerDay 40 >= 10  ->  MIN(20, 10) = 10        <-- fast-mover bar
roiCeiling      = FLOOR(20,424 / 1.10 / 1.05) = FLOOR(17,683.1) = 17,683
profitCeiling   = FLOOR((20,424 − 100) / 1.05) = FLOOR(19,356.2) = 19,356
ceiling         = MIN(17,683, 19,356) = 17,683
```

At the old 20% bar the ceiling would have been 16,209 — the fast-mover rule raises the maximum
acceptable price by **1,474 gil per unit** for this item.

A listing of 99 @ 15,000:

```
totalCost      = CEIL(15,000 × 99 × 1.05) = 1,559,250
totalNet       = 20,424 × 99             = 2,021,976
expectedProfit = 462,726
RoiPercent     = 462,726 / (15,000×99) × 100 = 31.16%     (reported; pre-fee basis)
ResaleValuePerSaleSlot    = 21,499 × 99 = 2,128,401
ExpectedProfitPerSaleSlot = 462,726
DaysToSell     = CLAMP(99/40, 0.25, 30) = 2.475
ProfitVelocity = 462,726 / 2.475 = 186,960 gil/day
```

Tier: `PreferredStock=true` ⇒ **Core**.

### 4.4 Worked example — General-Purpose dye, NQ

Home median 7,000; cheapest home listing 6,600; `SalesPerDay = 60`; `TargetStackSize=20`.

```
targetSalePrice = MIN(7,000, 6,599) = 6,599
netUnitProceeds = FLOOR(6,599 × 0.95) = 6,269
requiredRoi     = 60 >= 10  ->  MIN(20, 10) = 10
roiCeiling      = FLOOR(6,269 / 1.10 / 1.05) = 5,427
```

A listing of 20 @ 1,148:

```
totalCost      = CEIL(1,148 × 20 × 1.05) = 24,108
totalNet       = 6,269 × 20              = 125,380
expectedProfit = 101,272
RoiPercent     = 101,272 / 22,960 × 100  = 441%          <-- spectacular percentage
ResaleValuePerSaleSlot = 6,599 × 20 = 131,980            <-- BELOW the 150,000 floor
```

Tier: not preferred; `valuePerSlot 131,980 < 150,000` ⇒ **Opportunistic**, despite 441% ROI.
It is capped at ~10% of portfolio slots and can never outrank a Core gemdraught.

[OBS] This is the anti-trap mechanism working as designed.

---

## 5. Minimum / Maximum Limits

**H** = hard-coded, **U** = user-configurable, **C** = computed.

| Setting | Default | Kind | Location | Meaning | Enforced at |
|---|---|---|---|---|---|
| `ProcurementMinimumRoiPercent` | 20% | U (0–1000) | Configuration.cs L92, clamp L580 | Standard ROI bar on landed cost | `BuildPlan` L81; `RequiredPurchaseRoi` L1481 |
| `ProcurementFastMoverRoiPercent` | 10% | U (0–1000) | Configuration.cs L47, clamp L549 | Lower ROI bar for ≥10 units/day | `PortfolioPolicy.RequiredRoiPercent` L39 |
| `ProcurementFillRoiPercent` | 10% | U (0–1000) | Configuration.cs L41 | Lower bar for the top-up pass | `TopUpEmptySaleSlots` L239 |
| `ProcurementMinimumProfitPerUnit` | 100 | U (0–100M) | Configuration.cs L93, clamp L581 | Flat gil/unit floor | `profitCeiling` L86; `PollListings` L1361 |
| `ProcurementMinimumProfitPerSaleSlot` | 2,500 | U (≤100M) | Configuration.cs L54 | Slot-worthiness gate | `AddCandidate` L239 |
| `SecondaryMinimumSalesPerDay` | 10 | **H** | PortfolioPolicy.cs L28 | Velocity floor for Secondary **and** the fast-mover ROI threshold | `ClassifyCandidate` L73; `RequiredRoiPercent` L39 |
| `SecondaryMinimumValuePerSlot` | 150,000 | **H** | PortfolioPolicy.cs L29 | Value floor for Secondary | `ClassifyCandidate/Holding` |
| `PreferredPortfolioTargetPercent` | 75% | U (0–100) | Configuration.cs L50 | Core slot target | `PortfolioPolicy.Summarize` L102; objective 1 |
| `OpportunisticPortfolioMaximumPercent` | 10% | U (0–100) | Configuration.cs L51 | Cheap-stock cap | `Allocate` L303; objective 2; `PollListings` |
| `rule.MaximumUnitPrice` | 0 (off) | U | ProcurementModels.cs | Hard max price/unit | `BuildPlan` L90 |
| `rule.TargetStackSize` | 99 / 20 / 5 | U (1–999) | — | Max listing size bought; resale stack | `BuildPlan` L101; `PollListings` L1297 |
| `rule.MaximumSaleSlots` | 8 / 2 / 1 / 5 | U (1–60) | — | Max slots per item | `Allocate`; `CollectBagStock` L2003 |
| `rule.MinimumWeeklyUnitsSold` | 20 / 50 / 0 | U (0–1M) | — | Demand gate | `BuildPlan` L43 |
| `ProcurementWeeklySalesSharePercent` | 25% | U (1–100) | Configuration.cs L85 | Max share of weekly volume held | `BuildPlan` L50 → `Allocate` |
| `ProcurementBudget` | 5,000,000 | U (1k–100M) | Configuration.cs L32 | Per-trip cap **when reinvest off** | `ResaleStockPolicy.SpendableGil` L77 |
| `ReinvestAvailableGil` | true | U | Configuration.cs L33 | Ignore per-trip cap, spend wallet | same |
| `ProcurementTravelReserve` | 5,000 | U (≤100M) | Configuration.cs L84 | Always withheld | same |
| `ProcurementBufferGilPercent` | 20% | U (0–100) | Configuration.cs L83 | Cap on spend once bags cover slots | `BufferSpendableGil` L85 |
| `ProcurementBufferValueTarget` | 1,000,000 | U (≤999,999,999) | Configuration.cs L38 | Buffer must be worth this | `BufferIsComfortable` L52 |
| `ProcurementBagBufferStacks` | 5 | U (0–50) | Configuration.cs L35 | Spare stacks when not "continue stocked" | `PlannedSaleSlots` L2019 |
| `ProcurementTargetSaleSlots` | 60 | U (1–200) | Configuration.cs L90 | Portfolio size | slot maths |
| `ProcurementInventoryReserve` | 10 | U (1–100) | Configuration.cs L91 | Bag slots kept free | planner; `PollListings` L1366 |
| `ShoppingTripMinimumFreeSaleSlots` | 10 | U (0–60) | Configuration.cs L78 | Won't travel below this | `HoldingForSaleSlots` L188 |
| `ShoppingTripMinimumGil` | 1,000,000 | U (≤100M) | Configuration.cs L81 | Won't travel below this | `HoldingForGil` L196 |
| `HomePriceMaxAgeMinutes` | 30 | U (5–30) | Configuration.cs L69 | Resale-anchor staleness | `HomeReferenceMaxAge` L456 |
| `ScoutKnowledgeMaxAgeHours` | 24 | U (1–168) | Configuration.cs L73 | Away-observation reuse | `ScoutKnowledgeIsFresh` L158 |
| `MarketRequestTimeoutSeconds` | 6 | U (2–60) | Configuration.cs L20 | Live board wait | `MarketDataService` |
| `MarketRequestRetryCount` | 2 | U (0–5) | Configuration.cs L22 | Retries | `AutomationController` |
| listing search retries | 3 | **H** | ProcurementController.cs L1486 | `RetryListingRequest` | shopping |
| `MaximumUpdatesPerSession` | 200 | U (1–200) | Configuration.cs L24 | Reprice writes per session | `EvaluateCurrentListing` L1115 |
| `MaximumListingPrice` | 999,999,999 | **H** | PricingStrategyService.cs L12 | Game cap | pricing |
| `MaximumCuratedUnitPrice` | 1,000,000 | **H** | MarketPriceSafety.cs L13 | Curated sanity ceiling | bag listing |
| `OutlierFractionOfMedian` | 0.5 | **H** | HomePriceReference.cs L23 | Anchor outlier cut | `WithoutOutliers` L30 |
| `MinimumDaysToSell` / `MaximumDaysToSell` | 0.25 / 30 | **H** | PortfolioPolicy.cs L16-17 | Velocity clamps | `DaysToSell` L57 |
| `PriorityWorldsPerTrip` | 31 | U (1–40) | Configuration.cs L66 | Worlds per circuit | `PriorityTripShouldReturn` |
| `PriorityMinutesPerTrip` | 180 | U (5–480) | Configuration.cs L67 | Time away | same |
| comparison pass limit | 20 min | **H** | ProcurementController.cs L1236 | Buying phase time box | `PollListings` |
| `PriorityItemsPerWorld` | 14 | U (1–40) | Configuration.cs L103 | Items priced per world | `SelectWorldItems` |
| `MinimumStackValue` (discovery) | 150,000 | **H** | MarketDiscoveryPolicy.cs L15 | Discovery value floor | `Propose` |
| `MinimumUnitsSoldPerDay` (discovery) | 50 | **H** | MarketDiscoveryPolicy.cs L16 | Discovery velocity floor | `Propose` |
| `MaximumDiscoveredRules` | 12 | **H** | MarketDiscoveryPolicy.cs L22 | Discovery list cap | `Propose` |
| `PricingRule.MinimumPrice` | 1 (raised on buy) | U/C | PricingModels.cs | Resale floor | `CalculateFloor` L90 |
| `PricingRule.MinimumMarginPercent` | 0 (raised on buy) | C | — | Resale margin floor | `CalculateFloor` L90 |
| `PricingRule.UndercutAmount` | 1 | U | PricingModels.cs | Undercut step | `Evaluate` |
| `PriceWarDropPercent` | 60% | U (0–<100) | PricingModels.cs | Price-war trigger | `IsPriceWar` L100 |
| Excluded worlds | Bismarck, Ravana, Sephirot, Sophia, Zurvan | **H** | ProcurementTravelPolicy.cs L5 | Never shop there | `CanShopOnWorld` L11 |

Computed (**C**) values: `AvailablePurchaseSlots` (L1842), `PlannedSaleSlots` (L2019),
`ComfortableBagTarget` = `CLAMP((slots+4)/5, 5, 20)` (ResaleStockPolicy L8),
`ComfortableItemTarget` = `CLAMP((listed+3)/4, 1, 3)` (L11), `PortfolioCapacitySlots`,
`CoreTarget` / `OpportunisticCap` (`PortfolioPolicy.Summarize` L102), `SpendableGil` (L1848).

---

## 6. Reselling / Listing Logic

### 6.1 Where prices come from

Repricing uses **live in-game Compare Prices**, not Universalis
(`MarketDataService.cs -> GetSnapshotAsync`). Bag listing uses **Universalis** for the home world
(`BagListingController.cs -> QueuePricedStock` L314).

### 6.2 Competitor selection

`src/SmartUndercutBot.Core/Services/PricingStrategyService.cs -> Evaluate` (L14), competitor set at L31–40:

```
competitors = market.Listings WHERE
      Quantity > 0
  AND PricePerUnit ∈ (0, 999,999,999]
  AND IsQualityAllowed(theirHq, ourHq, rule.QualityFilter)   ; default SameQuality
  AND RetainerId == 0 OR RetainerId ∉ OwnedRetainerIds       ; exclude our retainers
  AND RetainerId != 0 OR RetainerName != ourRetainerName     ; name fallback
```

- **HQ/NQ matters**: default `QualityFilterMode.SameQuality`; other modes are configurable.
- **Stack size does NOT matter**: a 1-unit listing at 6,599 and a 99-stack at 6,599 are treated
  identically. Only `PricePerUnit` is compared. [IMPL]
- **Own listings ARE detected**, by retainer ID with a retainer-name fallback.

### 6.3 The pricing algorithm, in order

```
1. Validate listing/rule bounds                        -> InvalidData      [L21-27]
2. floor = CalculateFloor(listing, rule)                                   [L29, L90]
       costBasis = listing.AcquisitionCost != 0 ? it : rule.CostBasis
       IF costBasis == 0 -> floor = MAX(1, rule.MinimumPrice)
       ELSE floor = MAX(rule.MinimumPrice,
                        CEIL(costBasis × (1 + MinimumMarginPercent/100)))
3. Build competitor set (above)                                            [L31-40]
4. competitors empty                                   -> NoMarketData (no change)
5. lowest = MIN(competitor prices)                                         [L47]
6. Price-war test: lowest < historicalMedian × (1 − PriceWarDropPercent/100)  [L48, L100]
       IF war AND action == LeaveUnchanged             -> PriceWar (no change)
       IF war AND action == MatchProtectedFloor:
            protectedFloor = MAX(floor, FLOOR(median × (1 − drop/100)))
            -> Update to protectedFloor (or NoChange)
7. currentPrice <= lowest                              -> NoChange   (already cheapest)
8. WithinTolerance(current, lowest)                    -> WithinTolerance (no change)
       |current − lowest| <= AbsoluteTolerance  (if > 0)
       OR |current − lowest|×100/lowest <= PercentageTolerance (if > 0)
9. rawTarget = Mode == MatchLowest ? lowest
                                   : (lowest > UndercutAmount ? lowest − UndercutAmount : 1)
10. rawTarget < floor                                  -> BelowFloor (no change)
11. rounded = RoundDown(rawTarget, Rounding)           ; None | EndIn99 | EndIn999
12. target = MAX(floor, rounded)
13. target > 999,999,999                               -> InvalidData      [L77]
14. target == currentPrice                             -> NoChange
15. -> Update(target)
```

- **Minimum undercut** = `UndercutAmount` (default **1 gil**).
- **Maximum undercut** = none; bounded only by the floor.
- **Price-war avoidance**: yes — default `PriceWarDropPercent = 60%`, default action
  `LeaveUnchanged` (do nothing while the market is crashed).
- **Refuses to reprice** on: NoMarketData, PriceWar, NoChange, WithinTolerance, BelowFloor,
  InvalidData, dry-run (`!AllowAutomaticWrites`), or session update cap reached.

### 6.4 Cost floor pinned by purchases

After a confirmed buy (`ProcurementController.cs -> PollPurchase` L1398, mutation at L1442–1454):

```
landedCostPerUnit = CEIL((price×qty + serverTax) / qty)                    [L1442]
rule.CostBasis            = MAX(existing, landedCostPerUnit)               [L1445]  ; monotonic
rule.MinimumMarginPercent = MAX(existing, RequiredPurchaseRoi(order))      [L1446]  ; monotonic
rule.MinimumPrice         = MAX(existing, ProcurementPriceSafety.MinimumResalePrice(...)) [L1448]
```

```
MinimumResalePrice(cost, roi%, minProfit)                    ; ProcurementPriceSafety.cs L5
  = CEIL( CEIL( MAX(cost × (1+roi/100), cost + minProfit) ) / 0.95 )      [L10]
```

The `/0.95` grosses the required *net* up to a *listing* price so the 5% sale tax still leaves the
margin intact.

[IMPL] Note the deliberate coupling added in `ad1fa36`: because `RequiredPurchaseRoi` is now
velocity-tiered, a fast mover bought at a 10% bar also pins a **10%** resale margin — the comment at
L1476 explains that pinning 20% on stock bought at 10% "would just park the stack."

### 6.5 Suspicious/stale listing handling

- **Anchor outliers**: buying ignores home listings below half the median (`HomePriceReference` L30–40).
- **Safety-seed detection**: `MarketPriceSafety.IsSafetySeedRepresentation` catches the 999,999,999
  placeholder and stack-totals within ±1,000 of it.
- **Curated ceiling**: curated consumables priced above 1,000,000/unit are treated as unresolved
  placeholders and repaired from a known-safe or historical price, or skipped.
- [OBS] **Repricing itself has no outlier rejection.** A single 1-gil competitor listing is a valid
  `lowest`; only the floor prevents following it down.

---

## 7. Market Data Sources

| Source | Endpoint / mechanism | Consumed by | Failure behaviour |
|---|---|---|---|
| **Universalis full** | `GET /api/v2/{scope}/{ids}?listings=100&entries=100&statsWithin=604800000` (`UniversalisService.ScanAsync` L176) | Home listings + 7-day sales + velocity → resale anchor, demand gate, tiering | 20 ids/batch; 3 attempts on 408/429/500/502/503/504; **partial results kept**; throws only if *every* batch failed |
| **Universalis aggregated** | `GET /api/v2/aggregated/{scope}/{ids}`, 100 ids/req (`FetchPriceHintsAsync` L259) | `MarketPriceHint` → **route and per-world item choice only** | Failed batches logged and tolerated; hints reduced; shopping unaffected |
| **In-game Compare Prices** | `MarketDataService` + `IMarketBoard` packets | Repricing (its only pricing source) | Timeout (6s) → retry; `LastRequestSawAnyPacket` distinguishes "nothing arrived" |
| **In-game Item Search** | `MarketPurchaseService` + `InfoProxyItemSearch` | **The only source that can authorize a purchase** | `MarketResponseTracker.IsReady` requires the declared row count fully received, no error, 750 ms settle |
| **Saddlebag Exchange** | `POST docs.saddlebagexchange.com/api/ffxivrawstats`, 45 s timeout | Discovery only, **off by default** | Returns `[]`, logs, curated rules unaffected |
| **Repricing observations** | `AutomationController.ObservedHomePrices` | Seeds home anchors for free | Falls back to a live home sweep |
| **Local Excel sheets** | `dataManager.GetExcelSheet<Item>()` | Rule seeding, discovery validation | Authoritative for tradability/HQ/stack |

### 7.1 Staleness

| Data | Lifetime | Enforced |
|---|---|---|
| Home resale anchor | 30 min (`HomePriceMaxAgeMinutes`, clamped 5–30) | `HomeReferenceMaxAge` L456 — checked at plan build **and again** before purchase (L1242) |
| Away scout observations | 24 h (`ScoutKnowledgeMaxAgeHours`) | `ScoutKnowledgeIsFresh` L158 |
| Snipe block (`PreferredStock` + `AlwaysScout`) | **never cached** | `ScoutKnowledgeIsFresh` returns false for these |
| Discovery cache | 24 h (`MarketDiscoveryCacheHours`) | `MarketDiscoveryService.RefreshIfDue` |
| Live listing before buy | must be fresh this visit | `MarketResponseTracker.IsReady` |

### 7.2 API failure — does it fail open?

**No, for purchases.** [IMPL] A purchase requires a live in-game board reading that satisfies
`MarketResponseTracker.IsReady`. Universalis is never sufficient. Hints are typed as
`MarketPriceHint` — deliberately *not* `ProcurementMarketListing` — so they cannot reach the planner
as buyable listings.

Specific failure behaviours:

- Universalis fully down ⇒ `ScanAsync` throws ⇒ scan fails ⇒ retry on interval. **No purchases.**
- Universalis partially down ⇒ partial data used ⇒ **fewer** candidates. Safe direction.
- Aggregate hints down ⇒ routing falls back to rotation. Shopping continues.
- Saddlebag down ⇒ `[]`; curated rules stand.
- Home anchor expired mid-trip ⇒ that order is skipped (L1242) or the trip ends to refresh.
- Live board empty/timeout ⇒ 3 retries then skip that item.

[OBS] One soft spot: **stale-but-fresh-enough data is used**. A home anchor up to 30 minutes old
authorizes a purchase, and away observations up to 24 hours old select which deals to revisit —
though the revisit re-reads the live board and re-checks profit before buying.

---

## 8. Quantity / Capital Allocation

### 8.1 Quantity is not chosen

The bot buys **whole listings**. The only quantity constraints are
`listing.Quantity <= MAX(1, rule.TargetStackSize)` (`BuildPlan` L101) and the clamp at purchase time
(`PollListings` L1297: `Quantity = MIN(currentOrder.Quantity, TargetStackSize)`).

### 8.2 Slot budget

```
AvailablePurchaseSlots()  = PlannedSaleSlots(repricing.LastKnownFreeSaleSlots ?? 0)   [L1842]

PlannedSaleSlots(free):                                                              [L2019]
    free = MIN(free, ProcurementTargetSaleSlots)      ; 60
    held = ResaleBagSlots                              ; bag stock as sale-stacks
    IF free > held            -> free − held                        (fill real vacancies)
    IF ContinueShoppingWhenStocked ->
        MIN(MAX(0, free + ComfortableBagTarget − held),
            MAX(0, FreeInventorySlots − ProcurementInventoryReserve))
    ELSE                      -> MAX(0, free + ProcurementBagBufferStacks − held)

slotLimit (planner) = MIN(FreeSaleSlots, FreeInventorySlots)
```

`ResaleBagSlots` is capped per item by `rule.MaximumSaleSlots` (`CollectBagStock` L2003) — so 999
materia under a 1-slot rule counts as **1** buffer slot, not 50. (Before that cap, deep stacks made a
few bag slots look like a full portfolio and stopped shopping entirely.)

### 8.3 Gil budget

```
SpendableGil(newTrip):                                                               [L1848]
  available = ResaleStockPolicy.SpendableGil(wallet, travelReserve, reinvest, tripCap, spent)  [L77]
            = reinvest ? wallet − reserve
                       : MIN(wallet − reserve, tripCap − spent)

  bags = CollectBagStock() EXCLUDING PreferredStock items
  IF MIN(freeSlots, targetSlots) > bags.SaleSlots -> return available   (vacancies exist)

  ; bags already cover the vacancies: apply the buffer cap
  bufferCost = Σ quantity × GetEffectiveRule(item).CostBasis
  cap = BufferSpendableGil(wallet−reserve, bufferCost, ProcurementBufferGilPercent)   [L85]
      = CLAMP(FLOOR((walletAfterReserve + bufferCost) × pct/100) − bufferCost,
              0, walletAfterReserve)
  return MIN(available, cap)
```

[OBS] Preferred (Core) stock is **exempt** from the buffer cap — gil keeps flowing into gemdraughts
while cheap stock is restrained.

### 8.4 Anti-overconcentration

| Mechanism | Effect |
|---|---|
| `rule.MaximumSaleSlots` | Hard per-item slot cap (curated 8, dyes 2, materia 1) |
| Weekly share cap | `MAX(TargetStackSize, 25% × weeklyUnits) − owned` units |
| `ShoppingRules()` re-cap (L2033) | When bags already cover slots, per-item spare limited to `ComfortableItemTarget` = 1–3 |
| Opportunistic cap | ≤10% of portfolio slots for low-value/low-velocity stock |
| Slot-value gate | ≥2,500 gil profit per slot for non-Core |
| Buffer gil cap | ≤20% of (wallet + buffer cost) in non-preferred bag stock |
| `owned` counting | Listed **and** bagged stock counts against every cap |

**Answer to "can it overspend on one high-ROI, low-value item?"**
[OBS] Largely no. A cheap item is Opportunistic → capped at 10% of slots → and its per-item
`MaximumSaleSlots` (2 for dyes, 1 for materia) binds first. The residual risk is the immediate-buy
path (§2.5), which can spend budget on a 100%-ROI cheap item before better stock is seen — but that
order is still subject to the slot-value gate and the opportunistic cap.

---

## 9. High-Value vs Low-Value Opportunities — Direct Answer

**The code has an explicit, layered bias AGAINST high-ROI/low-value items.** This is unusual and
deliberate. [IMPL]

### 9.1 The five defences

1. **ROI is never an objective.** It only produces a price ceiling. No sort key is ROI, except
   strategy 3 (`profit/cost`), which is still tier-major and only one of five candidate plans.
2. **Tier dominance.** `Tiered()` (L375) sorts by tier first in every strategy. A Core gemdraught is
   considered before any dye, always.
3. **Value floor for Secondary.** `SecondaryMinimumValuePerSlot = 150,000` gil of resale value per
   slot. Below that, no amount of velocity or ROI earns better than Opportunistic.
4. **Slot-value gate.** Non-Core candidates need ≥`MinimumProfitPerSaleSlot` (2,500) profit per slot
   or they are rejected outright with reason "insufficient slot value" (L239).
5. **Opportunistic cap.** ≤10% of portfolio slots, counting already-listed stock.

Plus: plan objective 5 (slot occupancy) is *last*, with the comment at L344 saying a cheap low-value
item must never win by filling one more slot; and `FinishPriorityScouting` (L214) will leave slots
empty rather than buy junk.

### 9.2 Concrete numerical comparison

| | **A: Gemdraught HQ ×99** | **B: Dye NQ ×20** |
|---|---|---|
| Buy price/unit | 15,000 | 1,148 |
| Resale anchor | 21,499 | 6,599 |
| `totalCost` | 1,559,250 | 24,108 |
| `expectedProfit` | **462,726** | 101,272 |
| `RoiPercent` (reported) | 31% | **441%** |
| `ResaleValuePerSaleSlot` | 2,128,401 | 131,980 |
| `SalesPerDay` | 40 | 60 |
| `DaysToSell` | 2.475 | 0.333 |
| `ProfitVelocity` | 186,960/day | **303,816/day** |
| **Tier** | **Core** (pinned) | **Opportunistic** (131,980 < 150,000) |

**Outcome:** A wins every strategy because tier sorts first. B is capped at 10% of slots.

Note B's `ProfitVelocity` is *higher* (304k vs 187k gil/day) — B returns its smaller profit roughly
7× faster. If tiering were removed, B would win strategies 1 and 4.
**Tier is the only thing preventing that.** [OBS]

### 9.3 Where the bias can still leak

1. **The immediate-buy path** (§2.5) — no cross-item comparison; the first-seen exceptional deal wins.
2. **A dye priced just over the value floor.** At `ResaleValuePerSaleSlot ≥ 150,000` (e.g. 20 × 7,500)
   with ≥10 sales/day and ≥2,500 profit/slot, a dye becomes **Secondary** and then competes on
   `ProfitVelocity`, where small fast stacks score very well because of the 0.25-day clamp.
   Worked: 20 units clearing in <6 h ⇒ `DaysToSell` floors at 0.25 ⇒ `ProfitVelocity = profit × 4`.
   A 101,272-gil profit scores 405,088/day, beating the gemdraught's 186,960/day. It still cannot
   outrank Core, but it outranks other Secondary stock. **This is the sharpest remaining edge.** [OBS]
3. **`MinimumDaysToSell = 0.25` systematically favours small stacks** — any stack clearing in under
   six hours gets the same denominator, so profit-per-day is inflated for tiny lots.
4. **The fast-mover ROI cut applies to cheap items too.** `RequiredRoiPercent` keys only on
   `salesPerDay >= 10`; it has no value floor. A cheap, fast-selling item gets the same 10% bar as
   popcorn, so its price ceiling rises and more cheap listings qualify. They still land in
   Opportunistic and hit the 10% cap, but the candidate pool grows. [OBS]

---

## 10. Item Categories and Special Cases

| Category | Treatment | Source |
|---|---|---|
| **Raid food/potions** (4 Gemdraughts, Caramel Popcorn, Popoto Potage) | `PreferredStock`, `TourPriority=0`, HQ-required, `MaximumSaleSlots=8`, `AlwaysScout`, exempt from the buffer gil cap, always Core, listed as 99-stacks | `UniversalisService.CreateFavoriteRules` L73; `FavoriteNames` L39 |
| **General-Purpose / Wide-Spectrum dyes** | Buyable; `TourPriority=1`, stack 20, `MaximumSaleSlots=2`, `MinimumWeeklyUnitsSold=50`, `AllowHighQuality` from the sheet's `CanBeHq` | `CreateBuyableDyeRules` L114 |
| **Jet Black / Pure White dyes** | `AlwaysScout=true` — priced on every world (snipe lines) | Configuration.cs L158; backfill L512 |
| **All other dyes** | `LiquidateOnly` — sold from bags, never bought, no reserve | `CreateLiquidationRules` L100 |
| **Materia XI/XII** | Buyable; `TourPriority=2`, stack 20, **`MaximumSaleSlots=1`**, `MinimumWeeklyUnitsSold=50` | `CreateTradeableMateriaRules` L142 |
| **All other materia** | `LiquidateOnly` | `CreateLiquidationRules` L100 |
| **Ethers** (whole-word match; excludes "Aethersand") | `LiquidateOnly` | `CreateLiquidationRules` L100 |
| **8 tomestone materials** | `LiquidateOnly`, stack 20, 5 slots, no reserve | `CreateTomeMaterialRules` L130 |
| **Discovered items** | Food/medicine only (`Meal`, `Medicine`, `Seafood`, `Ingredient`); ≥150k stack value, ≥50 units/day; seeded `PreferredStock=true`, `TourPriority=0` | `MarketDiscoveryPolicy.Propose` |
| Crafting materials, gear, furniture, glamour, minions | **No rules** ⇒ invisible to the buying path | — |

**Category-driven pricing differences:** curated consumables get a 1,000,000 gil/unit sanity ceiling
and are listed as full 99-stacks; everything else uses `rule.TargetStackSize` and the general safety
check.

**Absent:** no item-level logic, no crafted-vs-gathered distinction, no vendor-price comparison, no
glamour/furniture handling, no seasonal or patch awareness.

---

## 11. Important Configuration

### 11.1 Economically material settings

| Config property | UI label (Shopping tab) | Default | Range | Consumed at |
|---|---|---|---|---|
| `ProcurementMinimumRoiPercent` | Minimum expected return after fees | 20 | 0–1000 | planner ceiling L81; `RequiredPurchaseRoi` L1481 |
| `ProcurementFastMoverRoiPercent` | **High-volume ROI %** | 10 | 0–1000 | `RequiredRoiPercent` L39 (DashboardWindow L1091) |
| `ProcurementFillRoiPercent` | Fill-up ROI % for empty slots | 10 | 0–1000 | `TopUpEmptySaleSlots` L239 |
| `ProcurementMinimumProfitPerUnit` | Minimum profit per unit | 100 | 0–100M | `profitCeiling` L86 |
| `ProcurementMinimumProfitPerSaleSlot` | — | 2,500 | ≤100M | `AddCandidate` L239 |
| `PreferredPortfolioTargetPercent` | Portfolio shape | 75 | 0–100 | `Summarize` L102 |
| `OpportunisticPortfolioMaximumPercent` | Portfolio shape | 10 | 0–100 | `Allocate` L303 |
| `ProcurementBudget` | Maximum gil per trip | 5,000,000 | 1k–100M | only when `ReinvestAvailableGil=false` |
| `ReinvestAvailableGil` | Spend whatever gil is in the wallet | true | — | `SpendableGil` L77 |
| `ProcurementTravelReserve` | Gil to keep for travel | 5,000 | ≤100M | `SpendableGil` L77 |
| `ProcurementBufferGilPercent` | Buffer budget when sale slots are covered | 20 | 0–100 | `BufferSpendableGil` L85 |
| `ProcurementBufferValueTarget` | Spare stock worth at least | 1,000,000 | ≤999,999,999 | `BufferIsComfortable` L52 |
| `BuyHighQualityOnly` | Only buy high-quality stock | true | — | `BuyableQuality` L67 |
| `ProcurementWeeklySalesSharePercent` | Maximum stock to hold, as % of weekly sales | 25 | 1–100 | `BuildPlan` L50 |
| `ProcurementTargetSaleSlots` | Maximum sale slots to fill | 60 | 1–200 | slot maths |
| `ProcurementInventoryReserve` | Bag slots to keep free | 10 | 1–100 | planner; L1366 |
| `ShoppingTripMinimumFreeSaleSlots` | — | 10 | 0–60 | `HoldingForSaleSlots` L188 |
| `ShoppingTripMinimumGil` | — | 1,000,000 | ≤100M | `HoldingForGil` L196 |
| `HomePriceMaxAgeMinutes` | — | 30 | 5–30 | `HomeReferenceMaxAge` L456 |
| `ScoutKnowledgeMaxAgeHours` | Remember away-world prices for (hours) | 24 | 1–168 | `ScoutKnowledgeIsFresh` L158 |
| `PriorityWorldsPerTrip` | Worlds per shopping trip | 31 | 1–40 | trip end |
| `PriorityMinutesPerTrip` | Minutes away per trip | 180 | 5–480 | trip end |
| `PriorityItemsPerWorld` | — | 14 | 1–40 | `SelectWorldItems` |
| `ContinueShoppingWhenStocked` | Keep a comfortable stock in bags | true | — | `PlannedSaleSlots` L2019 |
| `ProcurementBagBufferStacks` | — | 5 | 0–50 | `PlannedSaleSlots` (only when above is false) |
| `MarketDiscoveryEnabled` | — | **false** | — | `MarketDiscoveryService.RefreshIfDue` |
| `GlobalRule.*` / `PerItemRules` | Pricing tab | see PricingModels.cs | — | `PricingStrategyService` |

### 11.2 Settings that are unused, partially used, or unreachable

| Item | Status |
|---|---|
| `ProcurementPlanRequest.MarketTaxPercent` / `BuyerFeePercent` | **[DEAD as configuration]** — parameters exist (L156–157, L180–181), no caller ever supplies them; always 5%/5%. No UI. |
| `TryReadSellerFeePercent` (live game seller fee) | **[Partially used]** — read at `AutomationController.cs` L911, but only surfaces in the Earnings portfolio display; never affects buy or resale maths |
| `LiveWorldStockHuntEnabled` + `BuildLiveMarketPlan` (L135) | **[CFG / effectively DEAD]** — the property defaults `true` (Configuration.cs L106) on a *fresh* config; migration v15 (L309) sets it true, then migration v16 (L324) sets it `false`, and `EnableStockAutomation()` (L138, behind the Start button) sets it `false`. `KeepsRetainersStocked` (L122–124) requires it false. In the shipped flow the live-tour planner does not run. |
| `LiveWorldStockThresholdPerItem`, `LiveWorldStockHuntCooldownMinutes`, `LiveWorldStockHuntMaximumItems` | Only meaningful on that unreachable path |
| `GuidedTourMaximumWorlds` | Guided (manual) route only |
| `ProcurementBagBufferStacks` | Only when `ContinueShoppingWhenStocked=false` (default true) |
| `ProcurementDataCenter` | Normalised by `ProcurementTravelPolicy.ShoppingScope`; regional hints hardcode `"North-America"` at `ProcurementController.Priority.cs` L61 regardless of this setting |
| `ProcurementOrder.SaleSlots` | Always constructed as `1`; the `Slots` divisor in the per-slot metrics is therefore inert |
| `MarketDiscoveryRegion` | Only used when discovery is enabled (off by default) |

---

## 12. Hidden / Emergent Behaviour

1. **[HIGH] `MinimumDaysToSell = 0.25` inflates small stacks.**
   `ProfitVelocity = profit / MAX(0.25, days)` (PortfolioPolicy L63). Any stack clearing in under six
   hours gets the same denominator, so a 20-unit lot and a 5-unit lot score identically per gil of
   profit. Systematically favours small, fast lots in strategies 1 and 4.

2. **[HIGH] Two different ROI bases coexist.** The planner's ceiling uses **post-fee** cost;
   `ProcurementOrder.RoiPercent` (L218, shown in logs, `PortfolioDecision`, and the dashboard) uses
   **pre-fee** cost. The displayed ROI is therefore ~5% higher than the ROI actually enforced.

3. **[HIGH] `RequiredRoiPercent` can only ever *lower* the bar.** `Math.Min(minimumRoi, fastMoverRoi)`
   (PortfolioPolicy L41) means setting the high-volume ROI *above* the standard ROI has **no effect at
   all** — the UI accepts 0–1000 for it, but any value ≥ `ProcurementMinimumRoiPercent` is silently
   inert. A user raising it to demand more margin on fast movers would see nothing change. [OBS]

4. **[MEDIUM] One constant serves two unrelated purposes.** `SecondaryMinimumSalesPerDay = 10`
   is both the Secondary-tier velocity floor **and** the fast-mover ROI threshold. Changing the tier
   floor would silently move the ROI discount boundary, and vice versa. They are not independently
   configurable.

5. **[MEDIUM] `netUnitProceeds` truncates twice.** `FLOOR(price × 0.95)` then a `(uint)` cast inside
   `totalNet` (L105). On cheap items this is a meaningful relative loss, biasing slightly conservative.

6. **[MEDIUM] `CostBasis` is monotonically non-decreasing.** `MAX(existing, landedCostPerUnit)`
   (L1445). Buy once at a high price and the resale floor stays high forever, even after buying the
   same item much cheaper later. Nothing ever lowers it.

7. **[MEDIUM] `MinimumMarginPercent` also only ratchets up** (L1446). A *fill* order (10%) can never
   lower a floor previously set at 20%, but a normal slow-mover order raises it to 20% permanently —
   including for an item that later qualifies as a fast mover at 10%.

8. **[MEDIUM] First-match immediate buy.** `IsExceptional` (L517) triggers on the first qualifying
   listing in circuit order; no comparison against later worlds.

9. **[MEDIUM] The anchor uses `homeLowest − 1`, not the median, whenever a home listing exists** (L73).
   One competitor undercutting hard (but above the 50%-of-median outlier line) drags the resale
   anchor — and therefore the whole price ceiling — down for the entire trip.

10. **[LOW] The outlier filter needs ≥3 listings.** With 1–2 home listings, a single bad price becomes
    the anchor unfiltered (`HomePriceReference.cs` L35).

11. **[LOW] `ShoppingRules()` mutates `MaximumSaleSlots` on clones** (L2033) to
    `listed + ComfortableItemTarget(listed)`. Since `ComfortableItemTarget = CLAMP((listed+3)/4, 1, 3)`,
    an item with 0 listed gets 1 and one with 8 listed gets 3 — *already-successful* items are allowed
    proportionally more spare stock.

12. **[LOW] Plan ties break toward fewer worlds then lower cost** (objectives 7–8), a mild bias toward
    concentrating purchases on a single world.

13. **[LOW] `PortfolioSummary` is cached ~2 s** and invalidated after a purchase
    (`portfolioSummary = null`, L1454), so an opportunistic-cap check can use slightly stale slot counts.

14. **[LOW] `SalesVelocityPolicy` accepts `0` as a valid reported velocity.** The guard is
    `reported is >= 0 and <= 1_000_000_000m`, so an explicit Universalis `0` suppresses the 7-day
    fallback, yielding `DaysToSell = 30` (the max clamp) rather than a computed rate — and also
    denies that item the fast-mover ROI discount.

15. **[LOW] `remainingUnits` is keyed per (item, quality) but written per market** (L56), so with
    multiple market entries for the same item the last one wins.

---

## 13. Suspicious or Potentially Suboptimal Logic

### HIGH

**H1 — Small-stack bias via the `DaysToSell` floor.**
`ProfitVelocity` is objective #3 and the primary sort in strategies 1 and 4. The 0.25-day clamp scores
any small lot as if it clears in six hours. A 20-unit dye stack with 101k profit scores 405k gil/day;
a 99-unit gemdraught stack with 463k profit scores 187k gil/day. Only tier dominance prevents the dye
from winning — and the moment a cheap item crosses the 150k value floor into Secondary, this bias
becomes active against other Secondary stock.

**H2 — Two ROI bases; reported ROI overstates enforced ROI.**
`RoiPercent` (pre-fee) is what appears in `PortfolioDecision`, logs and the dashboard. The enforced
gate is post-fee. Anyone tuning `ProcurementMinimumRoiPercent` against the displayed numbers is
calibrating against the wrong figure.

**H3 — `CostBasis` ratchet can permanently strand an item.**
One expensive purchase sets a floor that never decreases. Combined with the `MinimumResalePrice`
gross-up, an item bought once at a bad price may sit unsellable (always `BelowFloor`) indefinitely,
consuming a retainer slot. Nothing in the code lowers `CostBasis`.

**H4 — The high-volume ROI setting is silently inert above the standard ROI.**
`Math.Min` (PortfolioPolicy L41) means the UI's 0–1000 range is misleading: only values *below*
`ProcurementMinimumRoiPercent` do anything. A user who sets it to 30 expecting a stricter bar on fast
movers gets exactly the old 20% behaviour with no feedback.

### MEDIUM

**M1 — The immediate-buy path is not comparative.** (§2.5) Spends slots and budget on the first
≥100%-ROI listing found, before better opportunities later in the circuit are seen.

**M2 — The resale anchor is competitor-driven, not sales-driven.** `MIN(median, homeLowest − 1)` means
one aggressive undercutter sets the ceiling for the whole trip, suppressing otherwise-good buys.

**M3 — Transaction costs are incomplete.** Teleport fares are not deducted from expected profit (only
a flat 5,000 gil reserve is withheld). Retainer venture costs are likewise absent.

**M4 — The live seller fee is read but ignored.** The game reports the actual retainer sale tax;
decisions use a hardcoded 5%. If the real rate differs (city-state discounts), every margin is wrong
in the same direction.

**M5 — Repricing has no outlier protection.** Buying filters anchor outliers; repricing does not. A
1-gil competitor becomes `lowest` and the bot undercuts to its floor. `PriceWarDropPercent = 60%` only
fires relative to the *historical median*, which requires history to be present.

**M6 — Stack size is ignored in competitor comparison.** Undercutting a 1-unit listing by 1 gil to
sell a 99-stack is treated as equivalent to undercutting another 99-stack. Real markets price these
differently.

**M7 — The fast-mover discount has no value floor.** It keys only on `salesPerDay >= 10`, so cheap
high-velocity junk gets the same 10% bar as popcorn, widening the candidate pool at the bottom end.
The tier caps still contain it, but the discount was justified in-code by popcorn's economics, not by
a cheap dye's.

### LOW

**L1 — 30-minute anchor staleness authorizes purchases.** Prices can move within that window; the live
board is re-read for the *listing*, but the *anchor* may be up to 30 minutes old.

**L2 — Hardcoded `0.95` / `1.05` literals** in three places diverge from the (also hardcoded) request
defaults. Any future tax change requires edits in four locations.

**L3 — The outlier filter is inactive below 3 listings.**

**L4 — The weekly-share cap floors at one full stack.** `MAX(TargetStackSize, …)` means that for a
99-stack item the 25% share cap is inert until weekly volume exceeds 396 units.

**L5 — Discovery pins to `PreferredStock=true`** (Core tier, exempt from the buffer gil cap) on the
strength of remote statistics alone. Off by default, but a discovered item immediately receives the
strongest possible tier with no live validation of its resale behaviour.

---

## 14. Current Strategy in Pseudocode

```text
# ---------- TRIP GATE ----------
IF !armed OR repricing busy OR no completed retainer pass OR wallet−reserve == 0: STOP
IF LiveWorldStockHuntEnabled: (live-tour branch; not reached in shipped config) STOP
IF freeSaleSlots < 10 OR spendableGil < 1_000_000: STAY HOME AND KEEP UNDERCUTTING
IF no capacity change AND no income AND before nextScan: STOP

# ---------- CANDIDATE RULES ----------
rules = ProcurementRules WHERE Enabled AND !LiquidateOnly
        AND (PreferredStock OR AlwaysScout OR home7DaySales >= MinimumWeeklyUnitsSold)
order rules by: snipeBlockFirst, homeSalesPerDay desc, TourPriority, name

# ---------- HOME ANCHORS ----------
seed home prices from repricing observations (<= 30 min old)
scan home world for any rule whose anchor is stale
homeAnchor[item] = MIN( median(7-day home sale prices),
                        MIN(WithoutOutliers(home listings excluding own)) − 1 )

# ---------- CIRCUIT ----------
route = data centers one at a time, own DC first, scored by cached Universalis hints
FOR world IN route (<= 31 worlds, <= 180 minutes):
    items = snipeBlock ALWAYS + hinted(¼) + busiest secondary + rotation   (<= 14)
    FOR item IN items WHERE observation older than 24h:
        read live board
        candidate = BuildPlan(this listing vs homeAnchor)
        IF candidate AND expectedProfit >= 100% of landedCost:
            BUY NOW (after live re-validation)      # not compared against later worlds
        ELSE:
            remember listing for end-of-circuit comparison
    IF !roomToBuy OR spendableGil == 0 OR bags full: BREAK

# ---------- COMPARISON ----------
markets = remembered listings WHERE home anchor still fresh
compared = BuildPlan(markets, minRoi = 20%, fastMoverRoi = 10%)
IF free slots remain:
    fill = BuildPlan(remaining listings, minRoi = 10%, opportunisticCap = 0)
    compared += fill orders WHERE tier != Opportunistic
IF compared is empty: GO HOME, LEAVE SLOTS EMPTY     # explicit: empty beats junk

# ---------- BuildPlan (per item, per quality) ----------
IF home7DaySales < MinimumWeeklyUnitsSold: REJECT
shareLimit  = MAX(TargetStackSize, 25% × weeklyUnits) − ownedUnits
anchor      = homeAnchor
net         = FLOOR(anchor × 0.95)
salesPerDay = Universalis velocity, else 7-day sales / 7
requiredRoi = (salesPerDay >= 10) ? MIN(minRoi, fastMoverRoi) : minRoi
ceiling     = MIN( FLOOR(net / (1+requiredRoi/100) / 1.05),
                   FLOOR((net − minProfitPerUnit) / 1.05),
                   rule.MaximumUnitPrice if set )
FOR listing WHERE price <= ceiling AND qty <= TargetStackSize
             AND retainer NOT ours AND quality matches:
    cost   = CEIL(price × qty × 1.05)
    profit = net × qty − cost                    ; REJECT if <= 0
    tier   = Core          IF PreferredStock
             Secondary     IF salesPerDay>=10 AND valuePerSlot>=150k AND profitPerSlot>=2500
             Opportunistic OTHERWISE
    IF tier != Core AND profitPerSlot < 2500: REJECT ("insufficient slot value")
    ADD candidate

# ---------- ALLOCATE ----------
FOR each of 5 strategies (all tier-major):
    #1 profitVelocity  #2 profit  #3 profit/cost  #4 salesPerDay  #5 valuePerSlot
    greedily add candidates subject to:
        slotLimit, opportunisticCap, rule.MaximumSaleSlots, budget, weeklyShareLimit
CHOOSE plan lexicographically:
    1 close core deficit
    2 do not exceed opportunistic cap
    3 max non-opportunistic profitVelocity
    4 max absolute profit
    5 max slots used                # deliberately last
    6 max distinct items
    7 min distinct worlds
    8 min cost

# ---------- EXECUTE ----------
FOR order IN plan:
    travel; search item; wait for a COMPLETE server response
    re-check: anchor fresh, rule valid, tier caps, live listing matches,
              profit with SERVER-REPORTED tax at RequiredPurchaseRoi(order),
              slots, inventory reserve, gil
    IF all pass: SUBMIT PURCHASE; confirm via inventory delta (+ yes/no prompt)
    ON confirm: CostBasis            = MAX(CostBasis, landedCost)        # ratchets up only
                MinimumMarginPercent = MAX(existing, RequiredPurchaseRoi(order))
                MinimumPrice         = MAX(existing, CEIL(requiredNet / 0.95))

# ---------- RESELL (continuous, every retainer pass) ----------
FOR each retainer listing:
    read live Compare Prices
    competitors = listings excluding our own retainers, matching quality
    IF none: KEEP
    lowest = MIN(competitors)
    IF lowest < historicalMedian × 0.40: PRICE WAR -> keep (or protected floor)
    IF current <= lowest: KEEP
    IF |current − lowest| within tolerance: KEEP
    target = lowest − UndercutAmount                 # default 1 gil
    floor  = MAX(MinimumPrice, CEIL(CostBasis × (1 + MinimumMarginPercent/100)))
    IF target < floor: KEEP                          # never sell below landed cost + margin
    COMMIT MAX(floor, roundDown(target))
```

---

## 15. Key Code Map

| Responsibility | File | Class / Method | Notes |
|---|---|---|---|
| Buy candidate generation + allocation | `src/SmartUndercutBot.Core/Services/ProcurementPlannerService.cs` | `BuildPlan` (L13), `AddCandidate` (L233), `Allocate` (L254), `Tiered` (L375), `PurchaseCost` (L396) | The economic core |
| Live-tour planner | same | `BuildLiveMarketPlan` (L135) | Unreachable in the shipped config |
| Tiering, ROI bar, velocity maths | `Core/Services/PortfolioPolicy.cs` | `RequiredRoiPercent` (L39), `Rank` (L45), `DaysToSell` (L57), `ProfitVelocity` (L63), `ClassifyCandidate` (L73), `ClassifyHolding` (L91), `Summarize` (L102) | Hardcoded 10/day and 150k floors |
| Sales velocity | `Core/Services/SalesVelocityPolicy.cs` | `DailyUnits` | Universalis velocity → 7-day fallback |
| Order metrics | `Core/Models/ProcurementModels.cs` | `ProcurementOrder` (L188–232), `PortfolioGates` (L21), `ProcurementPlanRequest` (L148) | `RoiPercent` L218; tax/fee defaults L156-157 |
| Resale anchor / outliers | `Core/Services/HomePriceReference.cs` | `WithoutOutliers` (L30) | 0.5 × median cut, needs ≥3 listings |
| Resale pricing / undercut | `Core/Services/PricingStrategyService.cs` | `Evaluate` (L14), `CalculateFloor` (L90), `IsPriceWar` (L100) | Lowest−1, floor, price war |
| Resale floor from cost | `Core/Services/ProcurementPriceSafety.cs` | `MinimumResalePrice` (L5) | `/0.95` gross-up |
| Price sanity / placeholders | `Core/Services/MarketPriceSafety.cs` | `IsSafeAutomaticUnitPrice`, `IsSafetySeedRepresentation` | 1M curated ceiling (L13) |
| Stock / quality / budget helpers | `Core/Services/ResaleStockPolicy.cs` | `ComfortableBagTarget` (L8), `ComfortableItemTarget` (L11), `BufferIsComfortable` (L52), `BuyableQuality` (L67), `SpendableGil` (L77), `BufferSpendableGil` (L85) | |
| Route + per-world item choice | `Core/Services/ShoppingScoutPolicy.cs` | `BuildRoute`, `SelectWorldItems`, `IsExceptional` (L74) | Immediate-buy threshold |
| Excluded worlds / scopes | `Core/Services/ProcurementTravelPolicy.cs` | `ExcludedWorlds` (L5), `CanShopOnWorld` (L11), `ShoppingScope` | 5 congested worlds |
| Discovery proposals | `Core/Services/MarketDiscoveryPolicy.cs` | `Propose` | Food/medicine, 150k (L15), 50/day (L16), ≤12 (L22) |
| Shopping orchestration | `src/SmartUndercutBot/Automation/ProcurementController.cs` | `HoldingForSaleSlots` (L188), `PollListings` (L1233), `PollPurchase` (L1398), `RequiredPurchaseRoi` (L1481), `TryAutomaticStart` (L1700), `AvailablePurchaseSlots` (L1842), `SpendableGil` (L1848), `CollectBagStock` (L1967), `PlannedSaleSlots` (L2019), `ShoppingRules` (L2033) | Trip gates, live guards |
| Circuit / comparison / top-up | `Automation/ProcurementController.Priority.cs` | `ScanPriorityRegionAsync` (L58), `PrepareScoutRoute` (L77), `ScoutKnowledgeIsFresh` (L158), `FinishPriorityScouting` (L195), `TopUpEmptySaleSlots` (L239), `BeginPriorityShopping` (L296), `HomeReferenceMaxAge` (L456), `ObservePriorityItem` (L470) | |
| Repricing loop | `Automation/AutomationController.cs` | `EvaluateCurrentListing` (L1115), `TryReadSellerFeePercent` use (L911), `ObservedHomePrices` | Feeds home anchors |
| Bag listing | `Automation/BagListingController.cs` | `QueuePricedStock` (L314), `BuildStockSummary` (L414) | Universalis-priced |
| Pending stock ledger | `Services/ProcurementLedger.cs` | `RecordPurchase`, `MarkListed`, `PendingSaleSlots` | Slot accounting |
| Universalis client | `Services/UniversalisService.cs` | `FavoriteNames` (L39), `CreateFavoriteRules` (L73), `CreateLiquidationRules` (L100), `CreateBuyableDyeRules` (L114), `CreateTomeMaterialRules` (L130), `CreateTradeableMateriaRules` (L142), `ScanAsync` (L176), `FetchPriceHintsAsync` (L259) | Seeded categories |
| Universalis parsing | `Core/Services/UniversalisResponseParser.cs`, `UniversalisAggregatedParser.cs` | `Parse`, `ParseHints` | Velocity fields |
| Live board (repricing) | `Services/MarketDataService.cs` | `GetSnapshotAsync` | Packet aggregation |
| Live board (buying) | `Services/MarketPurchaseService.cs` | `TrySelectLiveListing`, `SubmitPurchase`, `TryConfirmPurchase` | Only purchase authority |
| Response completeness | `Core/Services/MarketResponseTracker.cs` | `IsReady` | Declared rows fully received |
| Discovery orchestration | `Services/MarketDiscoveryService.cs` | `RefreshIfDue` | Off by default |
| Saddlebag statistics | `Services/SaddlebagStatisticsProvider.cs` | `GetStatisticsAsync` | Optional, discovery only |
| All settings + migrations | `Configuration.cs` | properties L20–L106, `PortfolioGates` (L119), `EnableStockAutomation` (L126), migration v42 (L532–539), `Normalize` clamps (L545–585) | Schema v42 |
| Settings UI | `Windows/DashboardWindow.cs` | Shopping tab; "High-volume ROI %" (L1091) | |

---

## Ambiguities and limits of this analysis

1. **`ProcurementOrder.SaleSlots` is always 1** at every construction site I found, so the `Slots`
   divisor in `ProfitVelocity`, `ResaleValuePerSaleSlot` and `ExpectedProfitPerSaleSlot` is inert. If a
   multi-slot order were ever constructed those metrics would divide; I could not confirm any intended
   path that does so.

2. **Real FFXIV tax rates were not verified against the game.** The code assumes 5% buy and 5% sell.
   Whether that matches current live rates (including city-state discounts) is outside what the
   repository can tell me.

3. **`LiveWorldStockHuntEnabled` reachability is a config-order question.** A user on a *fresh* config
   who never presses "Start all automation" would run the live-tour path. I classify it as effectively
   dead because both the Start button (L126) and the v16 migration set it false, and
   `KeepsRetainersStocked` requires it false.

4. **`MarketStatistic` / `ParseStatistics` and `IMarketDiscoveryProvider`** exist and compile, but I
   traced only one implementation path (`SaddlebagStatisticsProvider` → `MarketDiscoveryService`),
   which is disabled by default. Universalis' `ParseStatistics` has no active caller I found.

5. **Numeric examples in §4 and §9 use plausible market prices, not observed ones.** The formulas are
   taken verbatim from the code; the input prices are illustrative.

6. **I did not execute the code or run the test suite** as part of this analysis. Behaviour is derived
   from reading the implementation, cross-checked against test names in
   `tests/SmartUndercutBot.Core.Tests/` (notably `ProcurementPlannerServiceTests`,
   `PortfolioAllocationTests`, `StockPriorityTests`, `PricingStrategyServiceTests`).

7. **The velocity-tiered ROI gate is untested.** `grep -rn "RequiredRoiPercent\|FastMover" tests/`
   returns nothing, so `PortfolioPolicy.RequiredRoiPercent` — added in commit `ad1fa36` and now sitting
   directly on the buy path and on the resale margin pinned after every purchase — has no test
   coverage at all. That makes it the least-verified piece of economic logic in the repository. [OBS]
