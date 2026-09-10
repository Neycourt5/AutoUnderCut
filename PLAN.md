# Active: v1.0.0.67 marginal-allocation economics release

Publishing requested by the user; AGENTS.md standing authorization applies.

- [x] Recover HEAD 47498d0 and inspect inherited implementation, tests and notes.
- [x] Marginal rescoring and bounded market-cap improvement implemented.
- [x] Adversarial regressions added (marginal allocation, sell-through observer).
- [x] Release solution build clean (0 warnings) and full suite green on SDK 10.0.302.
- [x] PROFIT_OPTIMIZATION_IMPLEMENTATION.md and PURCHASING_RESALE_LOGIC.md updated,
      with the 15M example reproduced from code by a test.
- [x] Version bumped 1.0.0.66 -> 1.0.0.67 (1.0.0.66 was never released; live was .65).
- [ ] Push main + annotated tag v1.0.0.67; verify the release workflow, published
      repo.json AssemblyVersion and SmartUndercutBot.zip.

Personal sell-through forecasting remains deferred: inventory differences are not
verified sales, and every confound biases the estimate toward buying more. The
estimator and its tests are retained but nothing calls them. Native game operations
have not been exercised.

## Superseded publication checklist from the earlier session

- [x] Read the requested implementation and existing audit; reviewed unfinished changes.
- [ ] Finish planner/controller/configuration integration, fee and inventory safeguards.
- [ ] Add economic regression tests and implementation documentation.
- [ ] Pass complete Release build and tests; review diff.
- [ ] Commit and push main and unused annotated v1.0.0.66 tag.
- [ ] Verify successful release workflow, public repo.json and ZIP.

Native game actions have not been exercised. Earlier entries below are historical.

# Current: v1.0.0.65 skip congested worlds, and tour one data center at a time

User report against v1.0.0.64: the circuit still seemed to reach only a couple of
servers per data center, "obviously sometimes a world can be congested, just skip
it if that's the case, otherwise visit every world to check prices". Then, once
coverage looked right: it makes "weird data center jumps rather than just do the
whole data center one at a time".

## What was actually wrong

Every travel failure was funnelled into `SkipStockHuntWorld`, which counts toward
`StopUnproductiveTour`. Two worlds in a row that could not be reached - exactly
what congestion looks like, and congestion clusters - ended the entire circuit,
however many worlds were left. Worse, that stop happened *before* the world index
advanced, so `PriorityNextWorld` still pointed at the world that had just failed:
the next trip resumed onto it, failed there again, and stalled at the same wall
for good. A refused visit also burned the full ten-minute travel deadline first.

The data center jumps were the wave interleave in `BuildRoute` (two stops per
data center, four waves). It was deliberate - a trip that ends early then still
compares deals region-wide - but a crossing every second world goes out through
character selection each time, and that is most of the trip's clock.

## Work checklist

- [x] New `SkipUnreachableWorld`: congestion, a full world, a refused visit and an
      excluded world skip the stop and carry on. They never count toward
      `StopUnproductiveTour`, which now only measures worlds that were *reached*
      and returned no completed item search.
- [x] Each unreachable world is queued once behind the rest of the circuit (in the
      saved route as well), so a world that was busy at 8pm is still priced later
      in the same circuit rather than waiting for the next one.
- [x] A guard against the other failure: `UnreachableWorldLimit` = 6 in a row means
      world travel itself is unavailable. In priority mode that still finishes
      through `FinishPriorityScouting`, so the trip buys what it did find.
- [x] Recognise a refused visit in about a minute - Lifestream idle, character
      loaded, still on the world we left - instead of waiting out the 600s travel
      deadline for every congested destination.
- [x] The cursor now advances past a failed world *before* the tour may stop, in
      both `SkipStockHuntWorld` and `FinishStockHuntWorld`. That is the stall fix.
- [x] `BuildRoute` groups the circuit by data center: the character's own first
      (no transfer), then the rest in cached-hint score order, worlds inside each
      ordered the same way. Three crossings per circuit instead of ~15.
- [x] Config migration 41 clears the saved route and cursor, because a stored
      route is only rebuilt when its set of worlds changes and would otherwise
      keep the old interleave for the rest of the circuit.
- [x] 301 Release tests pass; the API 15 plugin builds with 0 warnings, 0 errors.
- [ ] Commit and push v1.0.0.65, publish the annotated tag, wait for the release
      workflow, and verify public `repo.json` and `SmartUndercutBot.zip`.

## Trade-off accepted

Grouping by data center is what the user asked for and it buys back most of the
travel time, but a trip that ends early on bag space now compares deals from the
data centers it reached rather than a sample of all four. Ordering the away data
centers by hint score keeps the early stops on the worlds worth visiting.

## Validation boundary

No native game actions were exercised this session; congestion is simulated in
the fixtures (travel accepted, character never leaves) rather than observed.

# Completed: v1.0.0.64 sweep every world, and snipe the rare dyes

User report against v1.0.0.63: only a couple of Primal and Crystal worlds were
visited. Every server should be checked so a far-world deal can be sniped, and
Jet Black and Pure White dye should be searched because they occasionally list
far below the home price.

## What was actually wrong

`BuildRoute` was never the problem: its wave interleave already covers all 31
away worlds exactly once per circuit. `PriorityWorldsPerTrip` was 8, so a trip
stopped after two stops per data center and the saved cursor resumed there next
time. Four trips covered the region, which with the new trip holds could be a
very long time.

Both dyes were already configured, enabled and on the tour. They are low volume,
so the volume ordering and the rotation buried them: `MinimumWeeklyUnitsSold`
could also drop them from the hunt list entirely on a thin week.

## Work checklist

- [x] `PriorityWorldsPerTrip` 8 -> 31 and `PriorityMinutesPerTrip` 45 -> 180, so
      one circuit sweeps every away world. The trip still returns early when the
      bags or the wallet run out, and the cursor still resumes.
- [x] New `AlwaysScout` rule flag: priced on every world of the circuit and never
      dropped for a thin sales week. Unlike `PreferredStock` it says nothing about
      portfolio tiering or the spending cap.
- [x] Migration seeds it for the curated consumables and for Jet Black and Pure
      White dye, and turns their tour flag on.
- [x] `PriorityItemsPerWorld` 12 -> 14, because the block is now eight items.
- [x] Considered grouping the route by data center to save cross-region travel,
      and rejected it: a trip that ends early on bag space would then compare
      deals from only the data center it started in. The interleave is kept.
- [x] 298 Release tests pass; the API 15 plugin builds with 0 warnings, 0 errors.
- [x] Commit and push v1.0.0.64, publish the annotated tag, wait for both
      workflows, and verify public `repo.json` and `SmartUndercutBot.zip`.

## Validation boundary

A full 31-world circuit has not been run in game. The fake board answers far
slower per search than the live client, so the circuit test lowers the per-stop
width to finish inside the trip clock; live timing is ~1.5-4 minutes per world,
which fits 180 minutes with headroom but has not been observed end to end.

# Completed: v1.0.0.63 hold for a worthwhile trip, and fix the net worth graph

User direction while watching v1.0.0.62: diversification matters less now that
undercutting runs every 5-10 minutes, so do not take a trip for a couple of
slots. Wait until 10-20 slots open and there is real gil, then shop the busiest
lines across all data centers. Also: the net worth graph does not work.

## What the saved history actually shows

`wealth-history.json` alternated between real valuations and gil-only ones:
1,832,189 -> 4,217,559 -> 2,510,308 -> 6,869,448, with `ListedNet` exactly 0 on
the low points. A fill-only bag-listing pass never reads the existing listings,
but `ResetPortfolio` still marked it `isFullBellRun`, so it was recorded as a
complete valuation worth retainer gil alone. The graph drew a cliff to bare gil
and back on every bag pass.

## Work checklist

- [x] `ShoppingTripMinimumFreeSaleSlots` (default 10): below this the route stays
      home and keeps undercutting, which is what frees the slots. 0 disables.
- [x] `ShoppingTripMinimumGil` (default 1,000,000) on spendable gil, so a trip is
      not spent buying one cheap stack. 0 disables.
- [x] Both holds skip the periodic Universalis scan, because no trip can result
      from it; the capacity and income triggers still wake it when a hold lifts.
- [x] Half the non-preferred capacity on every world goes to the highest average
      volume lines; cached hints drop from half the stop to a quarter; the tail
      still rotates so quiet lines are not starved.
- [x] A fill-only pass is no longer a full-bell valuation, so the net worth graph
      stops sawtoothing, and the graph gained a Clear history button because an
      existing history keeps its bad points.
- [x] 295 Release tests pass; the API 15 plugin builds with 0 warnings, 0 errors.
- [x] Commit d3c642c pushed to main with annotated v1.0.0.63. Release Actions
      34389308832 and Build 34389302721 both succeeded. Public latest repo.json
      and ZIP verified at 1.0.0.63, API 15, both DLLs present, ZIP 548,144 bytes.

## Validation boundary

The holds and the scan skip are covered by controller tests; the graph fix is
covered by a fill-only pass assertion. No in-game circuit has been run on this
build, so the hold has not been observed live.

# Completed: v1.0.0.62 price food and potions on every world

User report against 1.0.0.61, watching a live circuit: the bot travelled to a
world and never checked food or potion prices.

## What the session log actually shows

`session-20260909-125235.log`, items priced per away world:

- Midgardsormr: 3 gemdraughts, popcorn, potage, then 2 materia.
- Gilgamesh: 1 gemdraught, popcorn, potage, then 5 materia.
- Lamia, Exodus, Malboro: no food or potions at all. 8 materia and dye lines
  each, home sales of 14-140 units/day against gemdraughts at 840-1000.
- Mateus: 1 gemdraught, then 7 materia and dye.

## Cause

`PrepareScoutRoute` rotated the scout window across the whole rule list:
`offset = i * (limit / 2) % stockHuntRules.Count`. With 52 rules and 8 items per
stop the window had walked past every gemdraught by the third world. The
preferred flag existed and the planner already ranked core stock first, but the
items were never priced, so no core candidate ever reached the planner.

## Work checklist

- [x] Reproduce from the live log: which items were priced on which world.
- [x] `ShoppingScoutPolicy.SelectWorldItems` reserves the preferred food and
      potion block on every world; only the secondary lines rotate.
- [x] Preferred stock leads the scan order, so a trip cut short by the clock or a
      bad board has still priced the food.
- [x] Preferred stock is never dropped from the hunt list for a thin sales week.
- [x] Preferred observations are always re-read, so no world is skipped as
      "already known" and every world in the circuit is visited.
- [x] Preferred stock is exempt from the bag-buffer gil cap, so gil can keep
      going into food and potions while the cap still restrains everything else.
- [x] `PriorityItemsPerWorld` 8 -> 12 with a migration, so the 6-item block does
      not squeeze out the rotation.
- [x] 289 Release tests pass; the API 15 plugin builds with 0 warnings, 0 errors.
- [x] Commit 2d264dc..b179b51 pushed to main with annotated v1.0.0.62. Release
      Actions 34388157409 and Build 34388152931 both succeeded.

## Validation boundary

Oceania was already excluded and needed no change: the 32-world route is North
America only, and `ProcurementTravelPolicy` strips the Oceania/Materia scope and
the five Oceanian worlds. Scan composition is covered by unit tests against the
real controller; no in-game circuit has been run on this build.

# Completed: v1.0.0.61 root cause and design notes

User report against v1.0.0.60: Item Search types each configured name, visibly
renders the exact **First Letter Match** row, retries three times, then advances
without ever opening the price/listings window. The laptop is not using a
coordinate path, so display resolution and UI scale are not direct inputs.

## Root cause established from the live client

The v1.0.0.60 diagnostics added for the previous report captured the decisive
state on every attempt in `session-20260908-222704.log` and
`session-20260908-223547.log`:

    agent rows 0, list rows 1, mode Normal

The visible row exists, but `ReadRows()` bounded the rendered list by the
transient `AgentItemSearch.ItemCount`. That makes the count `min(0, 1) = 0`, so
`ActivateRow()` is never called. Commit `d40e302` / v1.0.0.36 introduced this by
replacing the durable visible-page mapping (`ListingPageItemIds` and
`ListingPageItemCount`) with the transient `ItemBuffer` and `ItemCount`. The
v1.0.0.36 native path was not exercised in game, and the new v1.0.0.60 evidence
disproves that change's assumption. A laptop's different frame cadence can make
the transient buffer drain before the fixed 500 ms sample more consistently.

## Repair

- Reconcile the rendered rows through `ListingPageItemIds` first, bounded to the
  actual list count, and accept only the exact expected item id on an enabled,
  settled row. Use the transient buffer only if the durable mapping has no ids.
- If both native id sources are empty, permit only one fail-closed fallback: one
  rendered enabled row, normal mode, a settled search, and an exact full item-name
  query. Every later result, listing read, and purchase guard still independently
  requires the expected item id.
- Pass the expected id and name through both row reads so the identity is
  revalidated immediately before the native click.
- Treat row activation as dispatched rather than acknowledged. If neither the
  target proxy nor the results window acknowledges it within 1.2 seconds,
  revalidate and retry the click once inside the same search. Never click again
  after target acknowledgement.
- Expand the stall diagnostic with durable/transient counts and first ids, query,
  pending flags, and list interaction state.

# Completed: v1.0.0.61 open the market-search row the game rendered

## Work checklist

- [x] Read the v1.0.0.60 session and Dalamud logs and reproduce the zero-transient /
      one-rendered-row contradiction.
- [x] Trace the regression to `d40e302` / v1.0.0.36.
- [x] Add the durable row resolver, fail-closed singleton fallback, native adapter
      integration, and bounded activation acknowledgement retry.
- [x] Add regression and safety tests for durable/transient precedence, conflicting
      ids, disabled and unsettled rows, singleton guards, bounds, and dropped click.
- [x] 276 Release tests pass; the API 15 plugin builds with 0 warnings and errors.
- [x] Commit 2d264dc pushed to main with annotated v1.0.0.61. Release Actions
      34309714817 and Build 34309712068 both succeeded. Public latest repo.json
      and ZIP verified at 1.0.0.61, API 15, both DLLs present, ZIP 544,212 bytes.
      Installer URL unchanged.

## Validation boundary

The exact live failure is evidenced by the v1.0.0.60 game logs, and the corrected
native adapter compiles against the installed API 15 structs. This work session
has not exercised the new click in FFXIV, so native success must not be claimed
until the user runs the published build.

# Completed: v1.0.0.60 restore market-board search confidence

User report against 1.0.0.58: the market board opened the Gemdraught of Mind
search repeatedly and never showed the price listings.

## What the session log actually shows

`session-20260908-220912.log` and `dalamud.log` for 22:09-22:12:

- Repricing worked: 13 price updates submitted and server-verified across three
  retainers. Bag listing worked: 11 stacks listed.
- Shopping failed at the name search only. `MARKET SEARCH submitted 'Grade 4
  Gemdraught of Mind'` then `Waiting for search rows; no results are visible yet`
  for three attempts, then the same for Gemdraught of Dexterity, then the user
  stopped it.
- No exceptions. Only two hitches from this plugin all session, 161.9 ms and
  83.4 ms, neither during a search. Thread starvation is not the cause.
- `git diff --name-only v1.0.0.57..v1.0.0.58` does not include
  `MarketPurchaseService.cs`, `MarketSearchSession.cs`, `MarketResponseTracker.cs`
  or `RetainerListingService.cs`. The native search path is unchanged from .57.

So the evidence neither convicts nor clears .58. `ReadRows()` returns an empty
list for six different reasons and the log cannot distinguish them.

## Changes

1. **Name the cause.** `IMarketSearchUi.RowDiagnostics` reports which precondition
   is refusing - window not loaded/visible/ready, missing results list, agent
   unavailable, no result buffer, partial search still running, rows still being
   pushed - and otherwise the actual counts (agent rows, list rows, mode, filter,
   proxy item, awaiting-listings). The next stalled search names its own cause.
2. **Remove the new runtime surface.** `MarketDiscoveryEnabled` now defaults off
   and config version 37 disarms it on upgrade and retires its suggested rules.
   It was the only part of .58 that added behaviour outside the planner: it
   downloaded a 16,822-item region dataset on the first scan after every load.
   The portfolio objective is unaffected - it is pure planner logic.
3. **Make discovery safe for when it is switched back on.** The configured rule
   ids and region are snapshotted on the framework thread instead of being
   enumerated from the background task while the controller mutates them.
   `MarketDiscoveryPolicy` ranks numerically first and looks up at most 500 items
   in the game's data instead of all 16,822. `LookupItem` resolves the Excel sheet
   handle once.
4. **Stop the per-frame portfolio recomputation.** `PortfolioSummary` read the
   bags and priced every holding on every dashboard frame and every purchase tick.
   It is now cached for two seconds and invalidated after a confirmed purchase.

## Work checklist
- [x] Read the session and Dalamud logs; establish what is and is not evidenced.
- [x] Confirm the native search path is unchanged since .57.
- [x] Add row diagnostics to the stalled-search path.
- [x] Default discovery off, migrate 36 -> 37, retire discovered rules.
- [x] Fix the cross-thread config read and the 16,822 off-thread item lookups.
- [x] Cache the portfolio summary.
- [x] 260 tests pass; Release build 1.0.0.59 clean.
- [x] v1.0.0.59 published, but its Build run failed on a flaky test of mine:
      MarketDiscoveryService sets LastSuccessAt inside RefreshAsync, before the
      task completes, so a test that waited on it could race the next
      RefreshIfDue into the "already running" guard. Added IsRefreshing and
      waited on that; 12 consecutive Release runs of the discovery tests pass.
- [x] Build workflow now writes a TRX and uploads it even on failure, because
      the checks API reports only "exit code 1".
- [x] Commit 87d0189 pushed to main with annotated v1.0.0.60. Release Actions
      34307467926 and Build 34307465424 both succeeded. Public latest repo.json
      and ZIP verified at 1.0.0.60, API 15, both DLLs present, ZIP 541,078 bytes.
      Installer URL unchanged.

## What the user should do
Update to .59 and run one shopping pass. If the search still stalls, the log line
now says why, and that names the actual defect. Market discovery can be switched
back on afterwards under Shopping > Shopping limits.

## Validation boundary
No FFXIV client session was exercised by this work. The diagnostics and the
opt-in change are compile- and unit-tested only; whether they resolve the
in-game search stall is not established and must not be claimed.

# Completed: v1.0.0.58 portfolio-quality procurement

The economic objective changes from "fill as many retainer slots as possible" to
"maintain a high-quality trading portfolio first, then use remaining capacity for
secondary opportunities". An empty slot is better than a slot full of junk.

## Why the current code buys junk

- `ProcurementPlannerService.Allocate()` builds five candidate plans and then
  picks `OrderByDescending(x => x.Orders.Count)` first. Slot occupancy is the
  primary objective, so a cheap dye that fits one more slot beats a richer plan.
- `ProcurementController.Priority.TopUpEmptySaleSlots()` re-runs the whole plan at
  `ProcurementFillRoiPercent` (10%) over *every* buyable rule, so any dye or
  materia with a technically positive margin can occupy an empty slot.
- ROI and `MinimumProfitPerUnit` are the only quality gates. 300% ROI on a
  2,100-gil dye stack outranks nothing, because absolute value is never compared.

## Design

### 1. Portfolio tiers (`SmartUndercutBot.Core/Services/PortfolioPolicy.cs`)

`PortfolioTier` = `Core` | `Secondary` | `Opportunistic`.

- **Core**: `ProcurementRule.PreferredStock` is set. Seeded true for the six
  curated consumables (`ResaleStockPolicy.IsCuratedConsumable`,
  `UniversalisService.CreateFavoriteRules`) and settable by discovery for
  objectively high-value/high-volume food and medicine.
- **Secondary**: not pinned, but the live metrics show real trading stock -
  resale value per sale slot, sales velocity and turnover all clear the gates.
- **Opportunistic**: everything else (dyes, materia, one-off arbitrage).

`PortfolioGates` carries the thresholds. Only three come from configuration; the
secondary liquidity floors are documented constants so the settings screen does
not become a knob pile.

### 2. Metrics beyond ROI

Per candidate, all measured *per occupied sale slot*:

- `ExpectedResaleValue` = target sale price x quantity
- `ExpectedProfit` (already computed, after buyer fee and market tax)
- `SalesPerDay` from `SalesVelocityPolicy` (home-world units/day, HQ/NQ separate)
- `EstimatedDaysToSell` = quantity / salesPerDay, clamped to [0.25, 30] days so a
  one-unit listing cannot manufacture an absurd score and an unsold item is not
  treated as instant
- `ProfitVelocity` = expectedProfit / estimatedDaysToSell (gil per day of slot
  occupancy)

ROI stays as a safety/profitability guard, not the objective.

### 3. Lexicographic allocation

`Allocate` still generates several greedy fills (a single ordering cannot solve
the budget knapsack), but every ordering is tier-major, and the resulting plans
are compared lexicographically:

1. maximise core progress = `min(core slots selected, core deficit)`
2. opportunistic slots (existing + selected) must stay within the cap - enforced
   as a hard constraint while filling, and re-checked when comparing
3. maximise core+secondary profit velocity (high-quality opportunity value)
4. maximise total expected absolute profit
5. maximise slot occupancy
6. tie-break: fewer worlds, then lower cost

A cheap low-value item can therefore never win purely by filling one more slot.

### 4. Existing inventory counts

`StockExposure` gains a `Tier`. `ProcurementController.CollectOwnedStock()`
classifies listed retainer stacks and bag holdings with the same policy, so:

- already-listed curated consumables count toward the preferred target;
- already-listed dyes/materia (including the liquidation backlog) consume the
  opportunistic cap and stop further opportunistic buying;
- the denominator is `PortfolioCapacitySlots` (the configured target sale slot
  count), not just this run's free slots, so percentages describe the whole
  portfolio rather than one shopping trip.

### 5. Top-up pass

`TopUpEmptySaleSlots()` keeps the lower fill margin but runs with
`OpportunisticMaximumPercent = 0`, so it can only reach Core and genuinely
high-liquidity Secondary stock. `PollListings()` additionally refuses to buy an
opportunistic order flagged `IsFillOrder`, so the cheaper margin cannot be
reached through a stale plan.

### 6. Market intelligence vs purchase authorization

New Core abstractions keep cached statistics away from purchase decisions:

- `IMarketStatisticsProvider` - per-item aggregates (median price, units/day).
  `SaddlebagStatisticsProvider` implements it (optional, daily raw stats).
  `UniversalisAggregatedParser.ParseStatistics` produces the same shape from the
  aggregate endpoint, which `UniversalisService` reads for scout hints.
- `MarketDiscoveryService` - turns statistics into candidate rules, validated
  against local Lumina data (exists, tradable, HQ capability, food/medicine
  category), cached for `MarketDiscoveryCacheHours` (24h).
- `MarketPriceHint` - a deliberately separate type from
  `ProcurementMarketListing`. Cached/aggregated data can only produce hints, so
  it is structurally incapable of becoming a purchase candidate. Purchases still
  require a fresh live board read, live buyer tax, the home resale anchor inside
  `HomePriceMaxAgeMinutes`, and every existing guard.

Universalis: `GET /api/v2/aggregated/{scope}/{ids}` (100 ids per request) replaces
the `listings=100&entries=100` regional sweep used only for scout routing. The
home scan still uses the full endpoint because it needs real competing listings
and sale history. This removes requests and payload rather than adding retries.
Aggregate velocity is deliberately *not* merged into home demand: it is the whole
region's rate, and shopping priority is judged on home-world sales.

Saddlebag Exchange (`POST https://docs.saddlebagexchange.com/api/ffxivrawstats`,
verified 2026-09-08: returns `medianNQ/HQ`, `averageNQ/HQ`, `quantitySoldNQ/HQ`,
`mainCategory`, `subCategory`, `lastUpdateTimeUnix`, `itemName`, keyed by item id)
is optional discovery only. If it fails, curated rules and Universalis/live
systems continue unchanged. Live queries are never routed through it.

### 7. Configuration (version 35 -> 36)

- `PreferredPortfolioTargetPercent` = 75
- `OpportunisticPortfolioMaximumPercent` = 10
- `ProcurementMinimumProfitPerSaleSlot` = 2,500 (value gate, tiers 2 and 3)
- `MarketDiscoveryEnabled` = true
- `MarketDiscoveryCacheHours` = 24
- migration pins `PreferredStock` on existing curated consumable rules

### 8. Observability

`ProcurementPlan` carries a `PortfolioAllocationSummary` and per-item
`PortfolioDecision` rows, logged and shown compactly in the dashboard:

```
Caramel Popcorn  CORE  420 units/day  41,000 profit  52% ROI  0.24d  selected: core portfolio below target
Yellow Dye  OPPORTUNISTIC  340% ROI  2,100 profit  skipped: opportunistic cap reached
Preferred 41/45 target | Secondary 8 | Opportunistic 5/6 cap
```

## Work checklist
- [x] Core: portfolio policy, models, planner rewrite.
- [x] Plugin: configuration + migration, controller wiring, top-up rework.
- [x] Market intelligence: aggregated Universalis, optional Saddlebag discovery.
- [x] Observability: logs and compact dashboard summary.
- [x] 258 tests pass, including the ten required behaviours.
- [x] Release build 1.0.0.58 passes with zero warnings and errors.
- [x] Commit cfd52f6 pushed to main with annotated v1.0.0.58.
- [x] Release Actions 34305093661 and Build 34305091103 succeeded.
- [x] Downloaded public latest repo.json and update ZIP: installer, packaged
      manifest and plugin assembly all 1.0.0.58, API 15; both DLLs present.
      ZIP 538,800 bytes. Existing in-game installer URL is unchanged.

## Realistic planner review
A twelve-slot run with four curated consumables, one unpinned high-value tincture,
two dye lines and grade XII materia, on a 60-slot portfolio already holding two
core and two opportunistic slots, selects nine core stacks, two tinctures and one
dye: 19,763,100 gil spent for 8,206,641 expected profit. The 180-212% ROI dyes
lose to 37-53% ROI consumables, and total expected profit is higher than the old
slot-filling objective produced on the same board (7,455,699), because the slots
went to 400,000-gil flips instead of 25,000-gil ones.

The first draft used a 20,000 gil secondary value floor, which let a 40,000 gil
dye stack reach Secondary and take three slots. Curated stacks carry 1.2-4.4
million gil per slot, so the floor is 150,000: ordinary dyes stay opportunistic
while genuine medicine and grade XII materia still qualify.

## Validation boundary
Tests use simulated market/game services. No FFXIV client session is exercised by
this work; do not describe native purchase behaviour as tested. The Universalis
aggregate and Saddlebag raw-statistics response shapes were confirmed against the
live endpoints on 2026-09-08; the in-game purchase path was not exercised.

# Completed: v1.0.0.57 sales-per-day priority and cached-loop validation

The .55 recovery changes and .56 cached-price/top-up changes were committed by
another active workspace session while this task was running. Preserve them.
Latest user screenshots request actual sales/day as a higher shopping priority.

- [x] Confirm .54 native purchases, all-four-DC travel and Sky Blue repricing.
- [x] Confirm .55 contains this task's tested retainer recovery fixes.
- [x] Parse home-world HQ/NQ sales velocity and preserve it through data merges;
      use actual demand before category priority in scouting and purchase plans.
- [x] Show the sales/day number used in shopping comparisons and logs.
- [x] Correct Universalis statsWithin from 604800ms to 604800000ms (seven days).
- [x] Skip fully known worlds before travel; refresh repriced/sold offers during
      buying revisits, invalidate failed reads, and count reused home evidence.
- [x] Apply the fill margin through live tax checks and resale cost protection;
      reserve normal-plan fees and inventory, and allow fill buys only for actual
      uncovered retainer slots (not the spare bag buffer).
- [x] 236 tests pass, including repeated cached circuits, cache expiry, changed
      winners, quality-specific demand, live fill buying and stock changes.
- [x] Final 1.0.0.57 release build passes without warnings/errors; diff check clean.
- [x] Remote main matches f1ffee7; v1.0.0.57 is unused; increment version.
- [x] Commit 327027d pushed to main with annotated v1.0.0.57.
- [x] Release Actions 34159676341 and Build 34159676210 succeeded.
- [x] Downloaded public latest repo.json and update ZIP: installer, packaged
      manifest and plugin assembly all 1.0.0.57, API 15; both DLLs present.
      ZIP 480,303 bytes. Existing in-game installer URL is unchanged.

# Completed: v1.0.0.55 retainer handoff and automatic menu recovery

Screenshot: retainer recovery latched after a shopping return. Latest native log
session-20260907-100244 confirms successful cross-DC scouting, purchased items,
and Sky Blue Dye repricing. At 11:05:53 return-to-bell began a retainer pass;
11:06:04 selection timed out; 11:07:04 recovery halted. No unverified write was
logged for that failure. Exact addon state at the timeout was not recorded.

- [x] Inspect current source, screenshot, and native shopping/retainer log.
- [x] Wait for usable, settled retainer list after travel; retry missed selection.
- [x] Reopen a nearby bell during menu-only recovery; bounded attempts and
      increasing cooldowns, retaining manual stops and uncertain-write holds.
- [x] Report recovery accurately instead of implying an unknown purchase.
- [x] Regressions for dropped selections, closed bell recovery, long outages,
      disabled automation and unverified writes; build/test and publish.
- [x] Commit/main/tag push, Actions and public installer/ZIP verification.

# Completed: v1.0.0.54 faster scouting and compared purchases

Latest user steering: proceed as soon as real prices arrive; retry an unanswered
empty screen with backoff. Scout across data centers and compare normal deals;
buy immediately only for exceptional margins. Publish the finished update.

- [x] Inspect current game/session logs and saved rule priorities.
- [x] Normalize formatted/wrapped editor names; resolve unmapped rows from the
      actual returned item ID instead of requesting a guessed inventory-row ID.
- [x] Preserve .50-.53's now-working native response hooks; completed packet/row
      evidence is authoritative. Remove extra three-second waits and the quiet
      delay on complete non-empty responses; retain empty-response settling.
- [x] Short bounded retries on missing prices, with diagnostics and visible
      comparison/skip reasons; never treat a timeout as an empty market.
- [x] Preserve .50's migrated default priorities; North America cached shortlist,
      two worlds per DC per wave, 8 items per away world, persisted full circuit.
      Compare live offers after scouting, revisit normal winners at the observed
      price or better, buy exceptional 100%+ net ROI immediately. Keep all guards.
- [x] Fix inconsistent home quote lifetimes (24h reuse vs 30min buy rejection).
      Use one configurable 5-30 minute lifetime for reuse and purchase approval.
- [x] 209 tests pass: wrapped dye mapped and repriced, counted response speed,
      empty retries, 31-world circuit, short 51-item scouting, Crystal beating
      Aether, immediate exceptional deals, stale/changed prices, spending across
      both phases, automatic repeat trips and home quote expiry.
- [x] Final 1.0.0.54 build passed with zero warnings/errors; 209 tests pass.
- [x] Commit 750c62b pushed to main with annotated v1.0.0.54.
- [x] Release Actions 34135916011 and Build 34135916091 succeeded. Downloaded
      public latest installer and ZIP: installer/manifest/plugin DLL = 1.0.0.54,
      API 15, both DLLs present, ZIP 465,743 bytes. Installer URL unchanged.

Evidence: .49 log at 01:19 skips Sky Blue #13721 because it cannot map the editor.
Other long dye rows produce requests for the wrong provisional inventory item.
Shopping from 01:21 to 01:38 never completes one comparison, spending about 90s
per item. Saved buyable rules all have legacy TourPriority=5 / HuntOnTour=false.
Do not describe .49's native shopping run as successful.
The repository advanced through .53 while this task was active; those changes
were preserved. User now reports shopping working and asks for faster broader
scouting. This session has not exercised .54 in FFXIV. Installer URL unchanged.

# Completed: v1.0.0.49 reliable priority shopping

Standing authorization: implement, test, commit, push and publish (AGENTS.md).

Evidence: latest log accepts results 10-60 ms after row selection, before listings
arrive. Dark Brown Dye bought successfully; Metallic Red timed out twice with no
confirmation prompt. Packet submission is not server acceptance. Lifestream's
TaskMBShortcut hardcodes Ul'dah and our controller always invokes it.

- [x] Require a matching fresh server response and settled rows before buying.
- [x] Pace search, row selection and purchase; never retry unknown purchases.
- [x] Nearby boards/bells first; migrate default `/li mb` to Limsa; gateway IPC uses Limsa.
- [x] Home demand/prices first, then prioritized flips; buy live profitable deals
      during each visit with tax, demand, exposure, stock and spending checks.
- [x] Aether -> Primal -> Crystal -> Dynamis; periodic return/list/collect with
      saved progress so the next trip continues the route. No Oceania.
- [x] Price comparison log and clear Shopping/Home status.
- [x] 185 tests pass: delayed/empty/stale responses, immediate buys, full circuit,
      automatic repeat trips, checkpoints, stock budgets and Limsa gateway calls.
- [x] Release build passed; version bumped to unused 1.0.0.49.
- [x] Published main commit 44e0583 and annotated v1.0.0.49. Release workflow
      34090050056 and Build 34090049939 succeeded. Public latest repo.json,
      ZIP manifest and plugin assembly all verified at 1.0.0.49, API 15.
      ZIP: 439,757 bytes; plugin and Core DLLs present.
      https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.49

No native game actions have been exercised by this session. Use local .NET 10 SDK
as documented below. Historical plans follow; the .36 unchecked release entry is
obsolete: the repository has since published .48. See work_progress.md.

# Historical retainer automation and UI plan

## Active release: v1.0.0.36 market-search handoff
Screenshot shows an item typed while the wishlist remains visible. Local Dalamud
log confirms search timeouts on Behemoth before any purchase (20:06-20:07).
- [x] Prepare normal search mode and focus; wait 400 ms, then drive editable
  text through clear/change, set/change and Enter callbacks. Do not mirror cached
  search strings or call RunSearch directly.
- [x] Read name-search matches from ItemBuffer/ItemCount (not category-page IDs),
  wait for the exact enabled row, dispatch one click, and accept
  only that item's fresh listing response after our selection.
- [x] Show the current search stage in Home and information-level game logs.
- [x] 146 tests pass and Release build has no warnings/errors. Existing purchase
  verification is preserved; no unverified write is retried.
- [ ] Publish v1.0.0.36 and verify the actual public installer JSON and ZIP.

Native calls have not been exercised in game. Existing local game log lines prove
that .35 timed out before buying; the new state-machine tests simulate UI responses.
Resume point: implementation is committed only after validation, then tag/push
and record actual artifact verification here and in work_progress.md.

## Active release: v1.0.0.35 comfortable bag stock
User wants a comfortable bag reserve and says the 932-stack count is wrong.
- [x] Exclude sale-only backlog from trading buffer capacity and acquisition cost.
- [x] Default to comfortable stock: 20% of checked sale capacity (5-20 sale stacks),
  so 60 retainer slots target 12 spares; cap each item's bag target at 1-3 stacks.
- [x] Permit spare replacements beside already-full listed item exposure while
  keeping total-quantity sales-volume limits, ROI, budget and bag space checks.
- [x] Keep periodic searches running when stocked, without repeated immediate scans.
- [x] Distinguish physical bag slots, units, personal reserves and planned sale lots.
- [x] 136 tests pass, including the 932-sale-lot regression, automatic trip/return,
  reserve buying beside full listings, periodic scouting, and config migration.
- [x] Published v1.0.0.35 from 3a5b473. Release 34070840381 and build
  34070840362 succeeded. Public installer JSON, ZIP manifest and DLL all verified
  at 1.0.0.35, API 15, both required DLLs present (ZIP 382,179 bytes).
  https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.35

No in-game bag contents or native actions were directly read/exercised here.
The screenshot's count was theoretical sale lots and included sell-off stock;
regression fixtures demonstrate the corrected distinction, not actual bag contents.

## Current release: v1.0.0.34 continuous-loop fixes
- [x] Count existing resale bags plus pending purchases without counting them twice.
- [x] Limit buffer acquisition cost to 20% of available capital when sale slots are
  covered; preserve wallet reinvestment for vacancies and wake when income arrives.
- [x] Preserve full check deadlines across bag refills; recover safe menu failures.
- [x] Fix the reported empty-retainer false stop: read sorted RetainerManager records
  and availability, wait up to 30 seconds for data, then use a timed retry.
- [x] Show actual spending mode, bag stock, capacity and waiting reasons on Home.
- [x] Exclude Oceania from saved scopes, plans, guided routes and live shopping travel.
- [x] Run 125 regression tests, including actual retainer-controller tests with a
  simulated three-day repeat run, and build Release with 0 warnings / errors.
- [x] Published v1.0.0.34 from f0c49f6. Release workflow 34069792116 succeeded.
  Downloaded the public latest installer JSON and update ZIP: installer, ZIP
  manifest and plugin DLL all report 1.0.0.34, API 15, with both DLLs included.
  Release: https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.34

Native game operations still require an in-game smoke test. Unknown purchase or
listing outcomes and explicit Stop stay stopped. The screenshot proves the old
UI reported zero available retainers despite visible active rows; it does not
verify native behavior of the new adapter. RetainerManager fields were checked
against upstream FFXIVClientStructs and the installed Dalamud 15 build.

The sections below are historical implementation notes; current behavior is
recorded above and in the newest entry in `work_progress.md`.

## Active update: reinvestment, diversification, and bag awareness
User requests: use available gil for profitable purchases (starting from 0), avoid
overbuying when retainers are saturated, diversify into fast-selling dyes, scan
bags, and publish the update for in-game installation.

- [x] Add wallet-based reinvestment with optional cap and visible travel reserve;
  wait at 0 gil and recheck when income arrives.
- [x] Count owned listings and resale bag stock before planning purchases; apply
  per-item exposure limits across trips and observed sales-volume limits.
- [x] Improve budget allocation for small wallets and limited sale slots.
- [x] Dyes and materia are sell-only, not stock to buy (user revised this: they
  carry no dye or materia inventory and want existing holdings cleared).
- [x] Scan bag inventory, show stock, and use eligible configured stock before
  shopping without listing unrelated equipment or other unconfigured items.
- [x] Update UI/docs, add regression tests, build, and publish next version.

### Wiring completed this session
The previous session defined the settings and planner inputs but never connected
them; `ResaleStockPolicy.SpendableGil` and `ProcurementWeeklySalesSharePercent`
were dead code and `OwnedStock` was never passed. Now:
- `SpendableGil()` replaces the fixed `Math.Min(budget, gil)` in both planners, the
  live-tour capacity guard, and the per-purchase budget check.
- `CollectOwnedStock()` feeds listed retainer stacks plus bag holdings to the
  planner so per-item exposure spans trips.
- The weekly-sales share always permits one full target stack. Without that floor a
  20-per-week minimum and a 25% share can never admit a 99-stack, so the planner
  would have bought nothing at all.
- `ProcurementRule.LiquidateOnly` marks sell-only stock. Dyes and materia are
  seeded with it, are listed from bags with no reserve, and are excluded from every
  purchase plan. Config migrates to version 19 and re-seeds both.

- Running dry mid-route abandons the trip and returns to the home bell, where the
  repeating retainer pass collects sale proceeds. Start already sets RepeatBellRuns
  and AutomaticallyCollectRetainerGil, so the zero-gil cycle closes on its own.

### v1.0.0.31 - Universalis 504 and the sell-off list
Reported in game: the scan sat on "504 (Gateway Timeout)" and nothing was queued to
list. Root cause was this session's own dye/materia seeding - every sell-only rule
was still being sent to the buy scan, turning a five-item request into hundreds of
ids across two scopes. Bag listing prices through the same service, so it failed too
and left "0 stacks queued to list".
- Sell-only rules are excluded from the deal scan.
- Universalis retries 504/502/429/timeouts three times with backoff, keeps partial
  results, and fails only if no batch answered. Concurrency 3 -> 2, timeout 30s ->
  20s per try, scan deadline 90s -> 150s.
- Ethers joined dyes and materia; per-item sell cap 2 -> 5 slots. Curated
  consumables can never be swept into the sell-off list. Config version 20 re-seeds.
- The all-world tour now covers every buyable rule and both qualities, not just
  HQ-required ones, so normal-quality food and potions are included.

### v1.0.0.32 - buy high quality only
User: "only HQ items sell tbh". The previous version had broadened the all-world
tour to both qualities, which was wrong for this account. Added
`BuyHighQualityOnly` (default on, config version 21) rather than hardcoding it:
- The planner yields no normal-quality candidates, and an item with no HQ form
  produces nothing at all, so it is skipped instead of stocked unsellably.
- The tour does not visit worlds for items it would not buy, and reads only HQ rows.
- Owned-stock exposure and the low-stock threshold count HQ only, so an NQ pile
  never suppresses restocking of the HQ form.
- Selling is untouched: dyes, materia and ethers are normal quality and are still
  listed from the bags.

### v1.0.0.33 - one button, bag buffer, buyable dyes
See `work_progress.md` for the full trail. Shopping was always part of Start; it was
idle only because retainers were full, and the UI never said so. Added the loop
readout, the bag buffer, and buyable high-volume dyes, and corrected the HQ-only
rule so it no longer excludes categories that have no HQ form.

### Deferred (user: low priority, do not break anything)
- Quieter travel destinations. Only the safe half is in: the summoning-bell leg now
  has its own optional Lifestream command, defaulting to empty, which reuses the
  market-board command and so changes nothing. Choosing older-expansion cities or
  ranking market boards by how busy they are is not implemented.

No running FFXIV inventory has been inspected by this session. Bag scanning will
run through the plugin's game adapter; do not claim actual bag contents were read
from the workspace.

## Goal
Make the everyday workflow easy to understand: check every retainer, refill empty
sale slots from eligible bag stock, travel to buy profitable stock when needed,
return home and list purchases, then repeat as items sell.

## Approach
- Preserve the existing game adapters, live purchase validation, cost floors,
  inventory reserves, and unknown-outcome stops.
- Add an explicit Keep retainers stocked mode that coordinates the existing
  controllers and enables the settings needed for the complete loop.
- Put Start, Stop, current activity, next action, capacity, and spending limits on
  a simple home screen; retain detailed controls under clearly named tabs.
- Prefer the existing Universalis plan plus live purchase validation for regular
  restocking. Keep the slow all-world live hunt and guided route as manual tools.
- Do not promise full retainers when no qualifying profitable stock is available.

## Work checklist
- [x] Inspect repository, UI entry points, controller scheduling, and test setup.
- [x] Trace refill/purchase handoffs and fix capacity or scheduling gaps.
- [x] Implement coordinated stock mode, explicit restart, and shared stop.
- [x] Simplify dashboard and explain prerequisites / blocked states.
- [x] Add regression coverage for continuous restocking and stop behavior.
- [x] Run tests and plugin build; inspect final diff.
- [x] Update README and this file with results and remaining limitations.

## Initial findings / resume notes
- Working tree was clean at start. No AGENTS.md found in the repository.
- Plugin.cs wires AutomationController, BagListingController, and
  ProcurementController via IsStartBlocked callbacks.
- Existing controls require separate arming for writes, purchases, listing,
  repeated bell runs, and automatic procurement. There is no unified start.
- Procurement can use a six-hour live-hunt cooldown instead of regular scans.
- Emergency stop suspends procurement and bag listing; a unified restart must
  explicitly resume those controllers without bypassing purchase verification.
- Bag listing retries every five minutes; retainer passes supply free slots.
- `dotnet --version` reports no SDK installed. Resolve build tooling if possible;
  do not report tests or live game validation as passed without evidence.

## Implementation checkpoint
- Added StockAutomationController: unified Start arms the required controls,
  resumes shopping and bag schedulers, and starts a full retainer check. Stop
  disarms all recurring work and active writes, then stops each controller.
- Added `/sub start`; `/sub stop` now uses the shared stop.
- Reorganized dashboard into Home, Stock, Shopping, Earnings, Advanced. Start /
  Stop stay visible. Home explains activity, capacity, cadence, and per-trip
  spending. Settings save automatically; detailed controls remain available.
- Retainer safety stops stay latched across window close/reopen. Bag fills react
  to increased empty-slot counts. Purchases require confirmed unreserved sale
  capacity and bag space.
- Regular deal planning now fetches home-world sales and competing listings in
  addition to the buying scope. Home competition caps the resale estimate and
  the player's own retainers are excluded.
- Failed home journeys retry returning home on the procurement interval.
  Uncertain purchase submissions and lost verification state stay stopped.
- 88 tests currently pass, including two restock cycles, sold-slot triggers,
  shared start/stop, home recovery, uncertain purchases, and local resale pricing.
- Final Release solution build passed against installed Dalamud 15 with zero
  warnings and errors. `git diff --check` passed. README includes the new workflow,
  per-trip budget behavior, recovery behavior, and validation limits.

## Build tooling
Use `C:/Users/Acour/.dotnet/dotnet.exe` (SDK 10.0.301), not the PATH dotnet.
Set DOTNET_CLI_HOME to `$env:TEMP/AutoUnderCut-dotnet-home`. NuGet restore was
completed with approved network access. Tests can now run with `--no-restore`.
Use `-p:UseSharedCompilation=false` to avoid sandbox compiler-server pipe access.

## Final handoff
Implementation and local validation are complete. The plugin build is at
`src/SmartUndercutBot/bin/x64/Release/SmartUndercutBot.dll` with its companion
`SmartUndercutBot.Core.dll`. Tests use simulated game services; visual UI behavior and native game
operations have not been exercised in FFXIV.

## GitHub publication (user requested)
- Standing instruction recorded in AGENTS.md: publish completed plugin updates
  so the user can update in game.
- Published v1.0.0.29 from commit 2c5082e on main; v1.0.0.28 was the previous release.
- Added release workflow checks to reject failed tests/builds and mismatched tags.
- [x] Validate versioned build and manifest, commit, and push main + release tag.
- [x] Confirm GitHub Actions success and the public installer manifest / ZIP.
- GitHub Build run 34054881383 and release run 34054881342 both succeeded.
- Downloaded the public latest installer manifest and its update ZIP. Confirmed
  installer version, ZIP manifest version, and DLL assembly version all equal
  1.0.0.29, with Dalamud API 15 and both required DLLs at the ZIP root.
- Release: https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.29

For an in-game smoke test: open the bell with Lifestream and vnavmesh ready, review
the per-trip budget and item rules, start the stock loop, observe a bag refill and
one purchase/return/list cycle, then verify that Stop prevents a later restart.
Confirm the next scheduled retainer check discovers sold slots. No profitable
deals, insufficient inventory space, and unknown purchase/listing outcomes should
remain visible instead of forcing a purchase or retrying an uncertain write.

## Validation boundary
Controller regression tests can simulate route completion and failures. Actual
FFXIV UI callbacks, travel plugins, and listing writes need an in-game smoke test.
No in-game session has been verified in this workspace.
