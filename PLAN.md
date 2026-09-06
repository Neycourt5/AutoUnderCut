# Retainer automation and UI plan

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
- Preparing v1.0.0.29 from main. Existing remote main matches the starting commit
  b18c8e6 and v1.0.0.28 is the previous release tag.
- Added release workflow checks to reject failed tests/builds and mismatched tags.
- [ ] Validate versioned build and manifest, commit, and push main + release tag.
- [ ] Confirm GitHub Actions success and the public installer manifest / ZIP.

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
