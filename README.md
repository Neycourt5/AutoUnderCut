# Smart Undercutter

Smart Undercutter is a Dalamud SDK 15 plugin for guarded retainer-market repricing and opt-in procurement.

## Keep retainers stocked

1. Enable Lifestream and vnavmesh, and open the summoning-bell retainer list.
2. Open `/sub`. On **Home**, review **Gil per shopping trip**, minimum expected
   return, and the quantity to keep in your bags.
3. Press **Start keeping retainers stocked** (or use `/sub start`). This enables
   automatic price changes, purchases, listing, gil collection, and repeat checks.

The loop checks every retainer, fills empty slots from eligible bag stock, searches
for profitable purchases when capacity remains, travels to buy them, then returns
home to list them. Retainer checks repeat every 5–10 minutes by default, so sold
slots are detected on the next check. Bag refills get priority before shopping.
The normal deal search uses Universalis to choose destinations and rechecks each
purchase against the live in-game listing. Home-world sale history and competing
listings set the resale estimate; the player's own retainers are excluded.

By default **Spend whatever gil is in the wallet** is on, so each trip may spend
the whole balance apart from the travel reserve (5,000 gil, configurable to 0) and
sales compound into the next trip. Turn it off to fall back to the per-trip
maximum, which applies per trip and resets on the next one.

Starting from nothing works: at zero spendable gil no trip begins, and if the
wallet runs dry partway through a route the trip is abandoned and the character
returns to the home-world summoning bell rather than touring with no money. Start
keeps repeating retainer passes there, which collect gil from anything that sold
and notice freed sale slots, so shopping resumes on the next interval once there
is gil to spend.

How much of one item may be held is capped by **maximum stock to hold**, a share of
that item's observed weekly sales (25% by default). The cap counts stacks already
listed on the retainers plus everything held in the bags, so a cheap item is not
re-bought every trip until it crowds out everything else. One full target stack is
always allowed, so an item with no stock can always be restocked. Bag-space
reserves, per-item slot limits, minimum profit, and sale capacity still apply. If
no deal qualifies, slots stay empty and another search runs later; filling every
slot is a goal, not a guarantee.

The default buying list contains HQ Grade 4 gemdraughts and HQ Caramel Popcorn.
Configure additional buying rules in **Shopping**. Dyes and materia are seeded as
**sell-only**: everything held is listed from the bags with nothing kept back, and
they are never bought as stock. The **Stock** tab lists every bag item and marks
each row Sell or Ignored, so it is visible what automatic listing will and will not
touch - gear and anything without a rule is never listed.

Leave the game running and the retainer list open between trips. **Home** shows
the current action, last checked capacity, and next check. **Stock** shows bag
inventory, **Shopping** contains buying rules and manual routes, **Earnings**
shows valuation estimates, and **Advanced** contains pricing, individual switches,
timing, and the activity log. Settings save automatically.

**Stop all automation** or `/sub stop` stops every controller and disarms recurring
work, including after a plugin reload. To resume, return to the bell and press
Start. When a purchase or listing could not be verified, check it in the game
before restarting. Temporary failures while returning home schedule another home
attempt; they do not start another shopping route on the visited world.

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
- Builds diversified purchase plans from home-world Universalis sale history and competing listings, constrained by gil, bag slots, confirmed retainer sale slots, weekly sales, ROI, and per-item limits.
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

Use `/sub guided` to start a fresh guided Universalis deal route, or launch it from **Shopping**. Guided routes wait for manual buying; they are separate from the automatic stocking loop.

## Operational notes

Game structures and UI callbacks can change after an FFXIV patch. Rebuild against the current Dalamud release after patches and test in dry-run mode first. Do not interact with the retainer UI while a run is active. Use `/sub stop` or the dashboard's Stop all automation button to abort. Automated tests simulate game services; a successful build does not verify travel plugins or native UI callbacks in a running game.

The procurement workflow expects Lifestream and vnavmesh to be installed. Its default Lifestream shortcut is `/li mb`; this can be changed in Shopping. Automatic procurement only starts while the character is idle at an open summoning-bell retainer list. A completed retainer pass supplies the current free-slot count; newly empty slots trigger a bag refill first, followed by a guarded deal scan if capacity remains. Shopping skips scans while no unreserved sale slots or bag space are available.

A user stop, an uncertain purchase submission, and an unverified listing stay halted until explicitly started again. Closing and reopening the retainer window does not clear a safety stop. Passing procurement failures such as Lifestream being busy or a failed Universalis scan schedule a retry after the procurement interval. Failed returns home also retry the home journey while automatic procurement remains enabled. Stop all automation and `/sub stop` cancel owned Lifestream travel, vnavmesh movement/pathfinding, and pending bag-listing scans. Retainer, bag-listing, and procurement controllers coordinate access to the game UI. Purchases recheck the destination world, open board, purchase arming, inventory reserve, budget, and profit after live buyer tax immediately before submission.

World transfers wait for market windows to close and allow up to ten minutes for cross-data-center queues. Approach and interaction failures have bounded retries. The live tour includes larger home-world stacks when calculating the resale floor, even when those stacks exceed the configured purchase size.

The live hunt scans all worlds before starting its buying pass. Item searches ignore leftover manual category filters and retry timed-out searches up to three attempts, with 30 seconds per attempt. Completion reports confirmed purchases and skipped orders; an empty plan distinguishes missing home-world resale data from deals rejected by the configured guards. The audit log records loaded listing counts, purchase requests sent, and inventory-confirmed purchases separately.

Automation may be restricted by the game's terms or server rules; the operator is responsible for checking those rules.
