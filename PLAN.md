# Retainer automation and UI plan

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
