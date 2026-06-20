# Smart Undercutter

Smart Undercutter is a Dalamud SDK 15 plugin for guarded retainer-market repricing and opt-in procurement.

## What is implemented

- Starts when the summoning-bell retainer list opens, or manually with `/sub`.
- Visits every available retainer and opens every occupied market listing.
- Uses the in-game Compare Prices request and Dalamud market-board events for live listings and sale history.
- Resolves the visible Adjust Price item before every request and rejects delayed packets from a previous row instead of skipping or mismatching the listing.
- Excludes all of the player's retainers before choosing the lowest competitor.
- Defaults to undercutting the lowest real competitor by one gil with no tolerance band.
- Detects extreme crash listings (60% below recent history by default) and leaves the item unchanged without blocking normal market swings.
- Submits through the real Adjust Price numeric control and confirm callback, then verifies the server-updated retainer slot.
- Uses randomized 250-450 ms action delays and immediately stops on movement, logout, unexpected UI state, changed listing, or failed confirmation.
- Supports price floors, cost-basis margins, HQ/NQ filtering, match-lowest mode, optional tolerance bands, and 99/999 rounding.
- Provides an ImGui status dashboard, per-retainer progress, queue with live/target prices, configuration, and audit log.
- Shows a Portfolio estimate with wallet and retainer gil, gross asking value, live market-aligned value, per-retainer seller tax, estimated net proceeds, markdown risk, and projected total wealth.
- Builds diversified purchase plans from Universalis sale history and current listings, constrained by gil, bag slots, retainer sale slots, weekly sales, ROI, and per-item limits.
- Uses Lifestream and vnavmesh to visit same-data-center worlds, then revalidates every candidate against the live in-game listing before submitting a purchase.
- Returns home, opens a summoning bell, distributes purchased stacks into open retainer slots, and feeds them through the normal live repricing pass.
- Persists the landed unit cost of automatic purchases, including the buyer fee, and prevents later repricing below that cost plus the configured margin.

Dry-run mode remains available. Repricing writes, purchases, automatic listing, and recurring procurement each have separate opt-in controls. Purchase and listing controls ship disarmed.

## Projects

- `src/SmartUndercutBot.Core`: pure pricing, procurement, and portfolio valuation models and services.
- `src/SmartUndercutBot`: Dalamud plugin, automation controller, live market-data service, game UI adapter, and dashboard.
- `tests/SmartUndercutBot.Core.Tests`: pricing, procurement, and portfolio valuation behavior tests.

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

## Operational notes

Game structures and UI callbacks can change after an FFXIV patch. Rebuild against the current Dalamud release after patches and test in dry-run mode first. Do not interact with the retainer UI while a run is active. Use `/sub stop` or the dashboard's Emergency Stop button to abort.

The procurement workflow expects Lifestream and vnavmesh to be installed. Its default Lifestream shortcut is `/li mb`; this can be changed in the Procurement tab. Automatic procurement only starts while the character is idle at an open summoning-bell retainer list. Run a normal retainer pass first so the plugin has a current count of free sale slots.

Automation may be restricted by the game's terms or server rules; the operator is responsible for checking those rules.
