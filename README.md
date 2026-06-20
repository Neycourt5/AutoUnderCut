# Smart Undercutter

Smart Undercutter is a Dalamud SDK 15 plugin for guarded, autonomous retainer-market repricing.

## What is implemented

- Starts when the summoning-bell retainer list opens, or manually with `/sub`.
- Visits every available retainer and opens every occupied market listing.
- Uses the in-game Compare Prices request and Dalamud market-board events for live listings and sale history.
- Excludes all of the player's retainers before choosing the lowest competitor.
- Defaults to undercutting the lowest real competitor by one gil with no tolerance band.
- Detects an abnormal low listing against recent sale history and leaves the item unchanged by default.
- Submits through the real Adjust Price numeric control and confirm callback, then verifies the server-updated retainer slot.
- Uses randomized 250-450 ms action delays and immediately stops on movement, logout, unexpected UI state, changed listing, or failed confirmation.
- Supports price floors, cost-basis margins, HQ/NQ filtering, match-lowest mode, optional tolerance bands, and 99/999 rounding.
- Provides an ImGui status dashboard, per-retainer progress, queue with live/target prices, configuration, and audit log.

Dry-run mode remains available. `Arm autonomous price writes` enables server submissions without a confirmation prompt for each listing.

## Projects

- `src/SmartUndercutBot.Core`: pure pricing models and `PricingStrategyService`.
- `src/SmartUndercutBot`: Dalamud plugin, automation controller, live market-data service, game UI adapter, and dashboard.
- `tests/SmartUndercutBot.Core.Tests`: pricing behavior tests.

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

Automation may be restricted by the game's terms or server rules; the operator is responsible for checking those rules.
