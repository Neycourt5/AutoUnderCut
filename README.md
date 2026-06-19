# Smart Undercut Bot

Smart Undercut Bot is a Dalamud SDK 15 plugin scaffold for guarded, autonomous retainer-market repricing. It keeps pricing policy in a dependency-free core library and confines all game-memory interaction to a small adapter executed from Dalamud's framework thread.

## What is implemented

- Activation when `RetainerSellList` opens, plus `/sub` and `/sub stop`.
- Framework-tick state machine with randomized 800–1500 ms delays.
- Reads the active retainer's loaded `InventoryType.RetainerMarket` slots.
- Uses the generated `InventoryManager.SetRetainerMarketPrice` client function after re-validating retainer, slot, item, quantity, quality, and old price.
- Cached Universalis market snapshots with timeout, age validation, sale-history median, and stale fallback limits.
- Undercut/match modes, absolute floors, configurable cost-basis margin floors, HQ/NQ filtering, tolerance bands, price-war handling, and 99/999 rounding.
- Immediate halt on movement, logout, closed UI, invalid inventory, changed listing, or rejected update.
- ImGui status, queue, rule editor, safety/data settings, and a 500-entry audit log.
- Dry-run mode is the default. `Arm autonomous price writes` is a single persistent arming control; there is no per-listing approval prompt.

## Projects

- `src/SmartUndercutBot.Core`: pure pricing models and `PricingStrategyService`.
- `src/SmartUndercutBot`: Dalamud plugin, automation controller, data/cache services, game adapter, and UI.
- `tests/SmartUndercutBot.Core.Tests`: pricing behavior tests.

## Build

Install the .NET 10 SDK and set up a normal Dalamud development environment, then run:

```powershell
dotnet restore SmartUndercutBot.sln
dotnet test SmartUndercutBot.sln -c Debug
dotnet build SmartUndercutBot.sln -c Debug
```

The plugin project follows the current official SamplePlugin SDK declaration: `Dalamud.NET.Sdk/15.0.0`.

## Operational notes

Game client structures and generated member signatures can change after a patch. The implementation deliberately uses current named FFXIVClientStructs members and validates every input before invoking the update function, but you should rebuild against the current Dalamud release after each FFXIV patch and run in dry-run mode first.

The market API endpoint is editable in the UI. External data may lag the in-game market board; age limits and price-war protection reduce that risk but cannot eliminate it. Use of automation may also be restricted by the game's terms or server rules; the operator is responsible for checking those rules.
