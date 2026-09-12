# Smart Undercutter

Smart Undercutter is a Dalamud SDK 15 plugin for guarded retainer-market repricing and opt-in procurement.

## Keep retainers stocked

1. Enable Lifestream and vnavmesh, and open the summoning-bell retainer list.
2. Open `/sub`. On **Home**, review reinvestment, travel gil, spare-stock limits,
   minimum expected return, and the quantity to keep in your bags.
3. Press **Start all automation** (or use `/sub start`). This enables
   automatic price changes, purchases, listing, gil collection, and repeat checks.

The loop checks every retainer, fills empty slots from eligible bag stock, searches
for profitable purchases when capacity remains, travels to buy them, then returns
home to list them. Retainer checks repeat every 5 minutes by default, so sold
slots are detected on the next check. Bag refills get priority before shopping.
The default priority shopper checks recent home-world sales, then live home
prices for every configured buyable flip with sufficient demand. Item priority
comes first, then observed sales volume. It visits Aether, Primal, Crystal and
Dynamis in that order, buying qualifying live deals during each visit. A home
bargain may be bought immediately when recent sales and another competing listing
support the resale price. One purchase per item per world favors variety.

Shopping returns to the bell after four away worlds or a 20-minute checkpoint
between item checks, or sooner when stock, spending or bag limits are reached.
Travel already in progress and a pending purchase finish before returning. Home
checks also have a 20-minute bound; home quotes older than 30 minutes trigger a
refresh trip. The saved next world/item resumes after the retainer pass and next
shopping interval. Retainer checks run while at home, and resume after each trip.

Nearby boards and bells are used first. Local travel defaults to Limsa Lower
Decks, replacing the old `/li mb` shortcut, which goes to Ul'dah. World travel
requests Limsa as Lifestream's gateway through its public IPC; older Lifestream
versions fall back to the world-name chat command and their configured gateway.
Custom local travel commands remain configurable.

At the board the shopper types the query, waits, submits Enter, waits for the exact
item row and opens it once. A visible results window is insufficient: the server
must declare its listing count, matching listing packets must arrive, and the
rows must settle. Every selected row, quantity, tax, budget and profit is checked
again before the purchase packet. Inventory must confirm receipt within 30 seconds;
explicit server rejection skips the order, while an unknown outcome stops buying.
**Shopping > Recent live price comparisons** and the session log record each
world's prices, quantities and decisions. Own retainers are excluded from resale
comparisons. The older cached-deal plan and full live tour remain optional tools.

By default **Reinvest available gil and sale income** is on, so each trip may spend
the whole balance apart from the travel reserve (5,000 gil, configurable to 0) and
sales compound into the next trip. Turn it off to fall back to the per-trip
maximum, which applies per trip and resets on the next one.

Starting from nothing works: at zero spendable gil no trip begins, and if the
wallet runs dry partway through a route the trip is abandoned and the character
returns to the home-world summoning bell rather than touring with no money. Start
keeps repeating retainer passes there, which collect gil from anything that sold
and notice freed sale slots, so shopping wakes as soon as new spendable income is detected.

How much of one item may be held is capped by **maximum stock to hold**, a share of
that item's observed weekly sales (25% by default). The cap counts stacks already
listed on the retainers plus everything held in the bags, so a cheap item is not
re-bought every trip until it crowds out everything else. One full target stack is
always allowed, so an item with no stock can always be restocked. Bag-space
reserves, per-item slot limits, minimum profit, and sale capacity still apply. If
no deal qualifies, slots stay empty and another search runs later.

### The portfolio comes before the slot count

Filling retainer slots is not the objective; holding good stock is. Every
prospective purchase and every holding sits in one of three tiers:

- **Core / preferred** - the curated food and gemdraughts, plus anything automatic
  discovery finds that matches them. Targets **75%** of the portfolio by default.
- **Secondary** - not pinned by hand, but with real trading characteristics:
  meaningful value in the occupied sale slot, meaningful profit, and genuine sales
  velocity. It uses whatever capacity the core stock does not.
- **Opportunistic** - dyes, materia and one-off arbitrage. Capped at **10%** by
  default so a good-looking percentage cannot take over the retainers.

Stock already listed on the retainers counts toward these percentages, so a
backlog of listed dye actively blocks buying more of it and pushes the next
purchases back toward preferred stock. ROI stays a safety guard rather than the
objective: plans are compared by core deficit first, then the opportunistic cap,
then the profit velocity of the good stock, then absolute profit, and only last by
how many slots get filled. A 300% return on a stack worth two thousand gil loses
to a large, fast-moving stack of raid food, and **an empty slot is preferred to a
slot of junk**. The lower fill-up margin reaches preferred and high-liquidity
stock only.

**Shopping > Why each item was chosen or skipped** and the session log show the
tier, units per day, expected profit, ROI, estimated turnover in days, and the
reason for every decision, with a portfolio summary line on Home.

### Optional market discovery

A daily market-statistics feed can suggest additional high-value, high-volume food
and medicine. Candidates are validated against the game's own item data (the item
exists, is tradable, its high-quality capability, and its category) before a rule
is created, and results are cached for 24 hours. This is intelligence, not
authorization: a suggested item still has to pass fresh live market-board prices,
the live buyer tax, ROI, budget and every portfolio gate before anything is bought.
The six curated items stay pinned whether or not discovery is enabled or reachable,
and a discovery failure never interrupts normal shopping.

Scouting uses Universalis' cached aggregate endpoint
(`/api/v2/aggregated/{scope}/{ids}`, up to 100 items per request) to decide which
worlds and items deserve an expensive live scan. Cached observations are a
deliberately separate type from market listings, so they cannot be mistaken for
something the planner may buy. Detailed listings and sale history are still
requested for the home world, which is the resale anchor.

The default buying list contains HQ Grade 4 gemdraughts and HQ Caramel Popcorn.
**Only buy high-quality stock** is on by default: wherever an item exists at both
qualities, only the high-quality form is bought. Categories that have no
high-quality form at all, such as dyes, stay tradeable. This applies to buying only
- sell-only stock is still listed from the bags whatever its quality.

**Keep a comfortable stock in bags** is on by default. It targets about 20% of
checked retainer capacity, bounded between 5 and 20 spare sale stacks: 60 sale
slots means a target of 12. Per-item targets range from 1 to 3 spare sale stacks,
based on how many of that item are listed. Stock gets topped up as it moves onto
retainers. Sales-volume, profit and budget checks can leave the target partly empty.

Spare purchases use up to 20% of available capital by default, counting the saved
acquisition cost of trading stock already in bags. Repeated trips do not reset
that allowance. Sale-only backlog and personal reserves do not count as trading
stock. Real free bag space is always required. Periodic searches continue even
when stocked; purchases and travel resume when suitable restock deals fit the
limits. Disable comfortable-stock mode to use the older fixed spare-stack target.

**Bag slots** means actual occupied inventory slots. **Units** means individual
items. **Sale stacks** means future listings at each rule's selling quantity:
397 HQ potions in one inventory slot, with 100 reserved, make three sale stacks
of 99. Home and Stock show these separately instead of calling every small
future sale lot a physical bag stack.

Configure additional buying rules in **Shopping**. Dyes, materia and ethers are
seeded as **sell-only**: everything held is listed from the bags with nothing kept
back, and they are never bought as stock. The high-volume `General-Purpose` and
`Wide-Spectrum` dye lines are the exception - those are traded like any other stock. Sell-only items are also left out of the
Universalis deal scan - asking about hundreds of items the plugin would never buy
is what made that scan time out with a 504.

Universalis requests retry transient failures (504, 502, 429, timeouts) up to three
times with a growing delay, and a scan keeps whatever batches did answer instead of
discarding all of them. A scan fails only when nothing answered at all, and then it
retries on the procurement interval. The **Stock** tab lists every bag item and marks
each row Sell or Ignored, so it is visible what automatic listing will and will not
touch - gear and anything without a rule is never listed.

Leave the game running and the retainer list open between trips. **Home** shows
the current action, last checked capacity, and next check. **Stock** shows bag
inventory, **Shopping** contains buying rules and manual routes, **Earnings**
shows valuation estimates, and **Advanced** contains pricing, individual switches,
timing, and the activity log. Settings save automatically.

**Stop all automation** or `/sub stop` stops every controller and disarms recurring
work, including after a plugin reload. To resume, return to the bell and press
Start. Initial retainer loading waits for the game data; transient menu failures
return to the bell and retry after a minute. Bag refills preserve the next full
retainer-check deadline. When a purchase or listing could not be verified, check it in the game
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
- Uses randomized 250-450 ms action delays and stops on movement, logout, changed listings, or unknown write outcomes; known menu/read failures have bounded recovery.
- Supports price floors, cost-basis margins, HQ/NQ filtering, match-lowest mode, optional tolerance bands, and 99/999 rounding.
- Provides an ImGui status dashboard, per-retainer progress, queue with live/target prices, configuration, and audit log.
- Shows a Portfolio estimate with wallet and retainer gil, gross asking value, live market-aligned value, per-retainer seller tax, estimated net proceeds, markdown risk, and projected total wealth.
- Automatically filters bag stock to HQ Grade 4 gemdraughts and HQ Caramel Popcorn, prices them from the current-world market, and fills free retainer slots with complete 99-stacks while preserving 100 of each item for personal use by default.
- Consolidates inventory stacks before bag filling, skips retainers already at 20/20 during fill-only runs, validates the exact visible safety-seeded row before every write, and returns to the main bell list before idling.
- Builds diversified purchase plans from home-world Universalis sale history and competing listings, constrained by gil, bag slots, confirmed retainer sale slots, weekly sales, ROI, and per-item limits.
- Searches North America by default. Oceania is excluded from saved search scopes, automatic plans, guided routes, live tours and outgoing shopping travel.
- Full live tours are limited to 8 low-stock items; regular automation uses targeted deal routes.
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
