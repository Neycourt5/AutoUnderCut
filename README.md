# Smart Undercutter

Smart Undercutter is a Dalamud SDK 15 plugin for guarded retainer-market repricing and opt-in procurement.

## What is implemented

- Starts when the summoning-bell retainer list opens, or manually with `/sub`.
- Visits every available retainer and opens every occupied market listing.
- Uses the in-game Compare Prices request and Dalamud market-board events for live listings and sale history.
- Reuses a complete live result for matching same-item rows for 30 seconds; distinct-item requests are throttled and transient failures retry the same row with backoff.
- Resolves the stabilized Adjust Price item before every request and preserves duplicate-stack slot alignment even when market data fails.
- Excludes all of the player's retainers before choosing the lowest competitor.
- Defaults to undercutting the lowest real competitor by one gil with no tolerance band.
- Detects extreme crash listings (60% below recent history by default) and leaves the item unchanged without blocking normal market swings.
- Submits through the real Adjust Price numeric control and confirm callback, then verifies the server-updated retainer slot.
- Uses randomized 250-450 ms action delays and immediately stops on movement, logout, unexpected UI state, changed listing, or failed confirmation.
- Supports price floors, cost-basis margins, HQ/NQ filtering, match-lowest mode, optional tolerance bands, and 99/999 rounding.
- Provides an ImGui status dashboard, per-retainer progress, queue with live/target prices, configuration, and audit log.
- Shows a Portfolio estimate with wallet and retainer gil, gross asking value, live market-aligned value, per-retainer seller tax, estimated net proceeds, markdown risk, and projected total wealth.
- Automatically filters bag stock to HQ Grade 4 gemdraughts and HQ Caramel Popcorn, prices them from the current-world market, and fills free retainer slots with complete 99-stacks while preserving 100 of each item for personal use by default.
- Consolidates inventory stacks before bag filling, skips retainers already at 20/20 during fill-only runs, validates the exact visible safety-seeded row before every write, and returns to the main bell list before idling.
- Builds diversified purchase plans from Universalis sale history and current listings, constrained by gil, bag slots, retainer sale slots, weekly sales, ROI, and per-item limits.
- Scans all North American worlds plus Oceania by default and merges them into one travel-ready procurement plan.
- Uses Lifestream for world and cross-data-center travel and vnavmesh for local approaches, then revalidates every candidate against the live in-game listing before submitting a purchase.
- Offers a guided deal route that prioritizes Universalis opportunities by world, travels to each market board, flashes the FFXIV taskbar icon, shows expected prices and guarded ceilings, and waits for a manual Done / Next command.
- Returns home, opens a summoning bell, distributes purchased stacks into open retainer slots, and feeds them through the normal live repricing pass.
- The curated procurement set is HQ Grade 4 gemdraughts plus HQ Caramel Popcorn; legacy Grade 3 gemdraught and food defaults are removed during migration.
- Persists the landed unit cost of automatic purchases, including the buyer fee, and prevents later repricing below that cost plus the configured margin.

Dry-run mode remains available. Repricing writes, purchases, automatic listing, and recurring procurement each have separate controls. Automatic curated bag refills are enabled during idle bell runs; market-board purchases remain separately armed.

## Projects

- `src/SmartUndercutBot.Core`: pure pricing, procurement, and portfolio valuation models and services.
- `src/SmartUndercutBot`: Dalamud plugin, automation controller, live market-data service, game UI adapter, and dashboard.
- `tests/SmartUndercutBot.Core.Tests`: pricing, procurement, portfolio valuation, Universalis parsing, and automation regression tests. The route tests run the production controller with simulated game services and a controllable clock.

## Build

Install the .NET 10 SDK and set up a normal Dalamud development environment, then run:

```powershell
dotnet restore SmartUndercutBot.sln
dotnet test SmartUndercutBot.sln -c Release
dotnet build SmartUndercutBot.sln -c Release
```

## Install through Dalamud

Add this URL under Dalamud Settings -> Experimental -> Custom Plugin Repositories:

```text
https://github.com/Neycourt5/AutoUnderCut/releases/latest/download/repo.json
```

Save it, then search for **Smart Undercutter** in the plugin installer.

Use `/sub guided` to start a fresh guided Universalis deal route, or launch it from the Procurement tab.

## Operational notes

Game structures and UI callbacks can change after an FFXIV patch. Rebuild against the current Dalamud release after patches and test in dry-run mode first. Do not interact with the retainer UI while a run is active. Use `/sub stop` or the dashboard's Emergency Stop button to abort.

The procurement workflow expects Lifestream and vnavmesh to be installed. Its default Lifestream shortcut is `/li mb`; this can be changed in the Procurement tab. Automatic procurement only starts while the character is idle at an open summoning-bell retainer list. A completed retainer pass supplies the current free-slot count; newly empty slots trigger an immediate guarded scan, with periodic scans as a fallback.

A user Emergency Stop, and a purchase the game accepted but inventory never confirmed, stay halted until explicitly started again. Every other stop - no free sale slot yet, Lifestream busy, a failed Universalis scan - schedules a retry after the procurement interval and resumes only once the character is parked at a summoning bell again, so a single passing failure cannot end unattended shopping for the session. A route that gives up mid-trip returns home to the bell instead of leaving the character on a visited world. Emergency Stop and `/sub stop` also cancel owned Lifestream travel, vnavmesh movement/pathfinding, and pending bag-listing scans. Retainer, bag-listing, and procurement controllers coordinate access to the game UI. Purchases recheck the destination world, open board, purchase arming, inventory reserve, budget, and profit after live buyer tax immediately before submission.

World transfers wait for market windows to close and allow up to ten minutes for cross-data-center queues. Approach and interaction failures have bounded retries. The live tour includes larger home-world stacks when calculating the resale floor, even when those stacks exceed the configured purchase size.

The live hunt scans all worlds before starting its buying pass. Item searches ignore leftover manual category filters and retry timed-out searches up to three attempts, with 30 seconds per attempt. Completion reports confirmed purchases and skipped orders; an empty plan distinguishes missing home-world resale data from deals rejected by the configured guards. The audit log records loaded listing counts, purchase requests sent, and inventory-confirmed purchases separately.

Automation may be restricted by the game's terms or server rules; the operator is responsible for checking those rules.
