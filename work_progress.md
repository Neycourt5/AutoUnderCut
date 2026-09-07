# Work progress log

Running record of what changed and why, so another agent (or a later session) can
pick up without re-deriving the history. Newest first. `PLAN.md` holds the current
task plan; this file holds the trail.

Build: use `C:/Users/Acour/.dotnet/dotnet.exe` (SDK 10.0.301), **not** the PATH
`dotnet`, which is runtime-only and fails with NETSDK1045. Set
`DOTNET_ROOT=C:\Users\Acour\.dotnet`. `gh` is not installed; verify releases with
`curl` against the GitHub REST API. Publishing is pre-authorized (see `AGENTS.md`).

---

## v1.0.0.54 - faster regional scouting and compare before buying (published)

Latest request: reach other data centers sooner, skim quickly, compare normal
deals and only buy immediately when the margin is exceptional. Preserve the
now-working .50-.53 native search/purchase fixes and Limsa travel.

- Fetch North America price hints alongside home demand. Rank useful stops, then
  scout two worlds per DC in Aether -> Primal -> Crystal -> Dynamis waves. Save
  the complete route and cursor across retainer checkpoints; all 31 away worlds
  still get a turn. If regional hints fail, use rotating live scouts.
- Home checks still cover configured high-volume flips, reusing fresh repricing
  observations. Away stops check at most 8 items (adjustable), half from promising
  hints and the remainder from rotating priorities so missing hints cannot hide
  everything. A 51-item fixture checks 64 away items across 8 worlds, previously
  408, without waiting for all Aether worlds before reaching the other DCs.
- Record live offers while scouting. After the configured world/time checkpoint,
  allocate normal purchases against all observed prices and home sales, then
  revisit the selected offers with fresh reads. Prices may improve, but cannot
  exceed the winning observed price. The buying pass is bounded at 20 minutes.
  Net ROI of at least 100% permits an immediate guarded purchase during scouting.
  Retain budget, tax, exposure, bag-space and inventory-receipt checks. Preserve
  already-spent gil and confirmed purchases between scouting and normal buying.
- Complete non-empty packet/native row counts advance immediately, removing the
  extra 3-second result delay, 750ms quiet delay and 3-second between-item pause.
  Keep separate typing/Enter ticks, exact-row matching and empty-response settling.
  Unanswered scouting reads retry after 6/12/18 seconds with 1/2-second backoff.
  A submitted purchase is never blindly retried.
- Home quote reuse and buying now share one 5-30 minute setting. .53 reused quotes
  for 24 hours then rejected them after 30 minutes, causing a return/reuse loop.
- Retainer editor names are decoded as SeStrings and whitespace/control formatting
  is normalized. Unmapped visible rows request the actual reply item, rather than
  filtering it against a guessed inventory order. Require item/name/quantity/HQ
  agreement and recheck the editor before writing. This fixes the reproduced
  wrapped Sky Blue name + reversed inventory order case; the exact native text
  behind the user's original skip was not captured, so confirm in the next run.

Validation: 209 tests pass. Final 1.0.0.54 release build passed with zero warnings
or errors. Tests cover all four DCs, ordinary/exceptional purchases,
changed offers, quick bounded retries, expired references, budgets across both
phases, repeated trips and wrapped dye repricing. These are simulated game
services; .54 has not been exercised in FFXIV by this session.
Published from source commit 750c62b with annotated v1.0.0.54. Release workflow
34135916011 and Build 34135916091 succeeded. Downloaded the public latest
installer JSON and update ZIP: installer, packaged manifest and plugin assembly
all report 1.0.0.54, API 15. Both DLLs present; ZIP 465,743 bytes.
Release: https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.54

## v1.0.0.49 - priority shopping, fresh responses and Limsa (published)

User asked for a paced, repeatable home-first shopping loop, buying profitable
live stock during visits in Aether -> Primal -> Crystal -> Dynamis order.

### Evidence and correction to .48
The newest Dalamud log declares prices ready 10-60 ms after row selection, then
often reports zero listings. `MarketSearchSession` bypassed its three-second
delay as soon as the window appeared, and treated the proxy's false Waiting flag
as a completed response. The same Metallic Red listing was submitted twice in
different runs without inventory receipt. Dark Brown Dye did buy successfully.
The latter run reports zero confirmation prompts; .48's claim that the missing
prompt caused the failure was not established. The native path sends a purchase
packet directly and does not open a prompt. Removed its unrelated SelectYesno
acceptance. Do not claim the precise reason for the earlier server rejection is
known; new response diagnostics capture that information.

### Implementation
- Type and Enter on separate ticks; select the matching row once. Wait for the
  native server result count, matching Dalamud offering packets, all native rows,
  a 750 ms quiet period and the three-second window settle. An unanswered window
  cannot be mistaken for an empty market. Retired request IDs reject late packets.
- Observe native ProcessPurchaseResponse; an explicit error skips, an unknown
  outcome still stops. Wait up to 30 seconds for inventory; no blind write retry.
- Nearby objects before local travel; migrate the default to `/li tp Limsa
  Lominsa Lower Decks`. Lifestream TPAndChangeWorld requests gateway 8 (Limsa),
  including the return gateway. If that IPC is absent, use the existing chat
  route; its gateway is then controlled by Lifestream. Custom local commands stay.
- Default priority shopping uses home sales to select qualifying flips, sorts by
  rule priority then volume, checks live home prices, and compares every visited
  world's actual listings. It buys immediately through the existing tax/ROI,
  demand, stock, bag and wallet guards. Cached order quantities do not restrict
  this path. Home bargains need another real competing listing and sales history.
- One buy per item per world; four away worlds or 20 minutes between item checks
  triggers return/list/collect. World and next item are saved to configuration.
  Home checks are bounded at 20 minutes; home quotes expire at 30 minutes. Existing
  stock and buffer capital budgets still bound purchases. Stocked/zero-gil loops
  wait and retry rather than travelling without a purchasing reason.
- Shopping shows recent price comparisons, also written to session logs.

### Validation / resume
185 tests pass, including fresh/delayed/empty/late responses, early-home buying,
live quantities, all 31 away worlds across eight return trips, three automatically
scheduled trips, mid-world resume, filled-buffer scouting, configuration migration
and Limsa gateway arguments. Release build passes with no warnings/errors.
Tests use simulated services; the new native hooks and travel have not been
exercised in FFXIV by this session. Published from 44e0583 on main, annotated
v1.0.0.49. Release workflow 34090050056 and Build 34090049939 succeeded. Downloaded
the public latest installer JSON and update ZIP: installer, packaged manifest and
plugin DLL all report 1.0.0.49, API 15; both DLLs present, ZIP 439,757 bytes.
Release: https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.49

Primary source checks: [FFXIVClientStructs market proxy](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyItemSearch.cs),
[Lifestream market shortcut](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/Tasks/Shortcuts/TaskMBShortcut.cs),
[Lifestream public IPC](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/IPC/IPCProvider.cs).

## v1.0.0.56 - full retainers are the objective, and price knowledge persists

User: keeping retainers stocked is the priority. After scanning all servers, if
the slots are not filled, go buy the best-ROI item available, preferring Caramel
Popcorn and potions since they are the highest volume. Also: hold scanned price
knowledge for about a day and only shop when it is fuzzy.

**Volume beats margin.** `Allocate` gained a strategy ordered by `TourPriority`
(raid food/potions 0, dyes 1, materia 2), and plan selection now prefers, in
order: more slots filled, then lower total priority, then profit. A dye with a
slightly richer margin no longer outranks the popcorn that actually turns over.

**Empty slots get topped up.** `TopUpEmptySaleSlots` runs after the scout
comparison: if slots remain, it re-plans the leftover listings at
`ProcurementFillRoiPercent` (10% default, must be below the normal bar) and
appends. Still a real profit after fees - never a loss - and it excludes listings
already taken and counts the pending buys as owned stock so limits still hold.

**Knowledge persists.** `scoutObservedAt` timestamps every world/item observation.
`scoutListings` is no longer cleared per trip; stale entries are pruned against
`ScoutKnowledgeMaxAgeHours` (24), and `ShouldScoutItem` skips a pair already seen
inside that window. `ScoutKnowledgeCoverage` reports how much is already known.

Note this is a **separate** knob from `HomePriceMaxAgeMinutes` (30). The home
price is the resale anchor that decides whether a deal is profitable, and a
retainer pass re-reads it for free, so it stays tight. Away-world observations
only decide where to look, so a day is fine.

## v1.0.0.55 - retainer recovery no longer latches after a shopping return

The screenshot's "could not recover the retainer interface" was terminal: after a
shopping trip returned to the bell, the next retainer selection never completed
and recovery **halted permanently** 60 seconds later. The log for that session
(session-20260907-100244) shows the trip itself went well - all four data centers
scouted, items purchased, Sky Blue Dye repriced - and no unverified write was
involved, so latching was pure loss.

- That `Halt` is now `ScheduleRetainerRetry`, with cooldowns escalating 15s to
  300s instead of stopping.
- Selection waits for a retainer list that is **ready and settled** for a second
  with no child window open, rather than firing at a list still initialising.
- A dropped selection callback is retried once; a menu that vanishes reopens the
  nearby bell.
- `IsRetainerListReady` was added to the retainer service so "visible" and
  "usable" are no longer conflated.
- Failures log a full `Retainer UI:` state summary, so the next one is
  diagnosable from the log rather than a screenshot.

Uncertain writes still latch: a pending auto-listing, an in-flight commit or gil
verification, or a submitted-but-unconfirmed row still halts rather than retrying,
which is the one case where repeating is worse than stopping.

Seven regression tests cover dropped selections, a vanished menu, a bell that is
visible but uninitialised, a prolonged outage that backs off then recovers, a
late frame after the deadline, and automation being disabled mid-recovery.

## v1.0.0.53 - stock health by value, longer trips, no redundant home sweep

The loop is working now: it reached Adamantoise and Cactuar and kept the retainers
stocked. Three refinements from that run.

**Stock health is value, not stacks.** `PriorityTripShouldReturn` only asked
`AvailablePurchaseSlots() > 0`, so twelve stacks of cheap dye satisfied the target
and the trip came home with gil idle. `ResaleStockPolicy.BufferIsComfortable`
requires both the stack target and a value target
(`ProcurementBufferValueTarget`, 1M default), and `ResaleBagValue` prices the
buffer from the home reference. Items with no known home price contribute nothing
rather than a guess.

**Two worlds was the cap, not the appetite.** 4 worlds / 20 minutes per trip are
now `PriorityWorldsPerTrip` (8) and `PriorityMinutesPerTrip` (45).

**The home sweep was redundant.** Every retainer pass already reads the live home
board for each listing it reprices, so `AutomationController.ObservedHomePrices`
records them and shopping seeds `homePrices` from that. Anything fresher than
`HomePriceMaxAgeHours` (24) is skipped on the home leg instead of re-read, which
removes most of the 51-item home scan.

Three trip tests pinned the old 4/20 caps; they now set them explicitly so they
test the checkpoint mechanism rather than the shipped defaults.

## v1.0.0.52 - every live read returned empty, and the route never left home

### WaitingForListings, a fourth and fifth time
v1.0.0.51 fixed the readiness check but three *reads* still gated on the same
flag. Log:

    Live prices loaded for Craftsman's Cunning Materia XI
    PRICE CHECK Siren: ... NQ: 0 listings / 0 units, lowest 0.
                        No confirmed home resale listings; skip buying.

Ready, then zero rows. `ReadLiveListings`, `TrySelectLiveListing` and
`SubmitPurchase` all began with `proxy->WaitingForListings`, which stays set on
this client, so every read returned empty with the rows in memory. That is why no
home prices were ever recorded and nothing could qualify. All three now use
`MarketResponseTracker.IsReady`, which is strictly stronger.

Running total for this one flag: submission (.36), reading rows (.47), readiness
(.51), and now the three live reads. **Do not gate anything on it.**

### The route never travelled
`PriorityTripShouldReturn` is called before every item, and its home branch used a
flat 20-minute budget measured from the start of the home scan. With 51 flips at
roughly ten seconds each the budget expired mid-scan, `FinishShopping` threw the
gathered prices away, and the next pass started over and expired at the same
point - "it just went back to refreshing stock prices".

The home scan happens at home, so it is not time away from the retainers. It now
gets a budget scaled to the list (30s per rule, floor 20 minutes), and on overrun
it travels with the prices already gathered instead of discarding them.

### Home reference prices (user request)
`HomePriceReference` in Core, six tests. A listing below half the median is
treated as someone's mistake and excluded from the resale anchor, but only with
three or more listings, and never if it would empty the board. Shopping gains a
"Home reference prices" table: item, quality, listings, lowest, median, and the
reference actually used, flagging how many were ignored.

## v1.0.0.51 - WaitingForListings stalls a response that already arrived

Screenshot, Grade 4 Gemdraught of Intelligence, Search Results fully populated:

    Waiting for live prices for Grade 4 Gemdraught of Intelligence.
    (server rows 29, received 29, error 0)

The server declared 29 rows, all 29 arrived, no error - and the plugin was still
waiting. The readiness test was:

    result.ResponseReceived && !result.Waiting && now >= nextActionAt

`Waiting` is `InfoProxyItemSearch.WaitingForListings`, and on this client it stays
set after a complete response. So every item sat until its route timeout with the
data already in hand. That is the reported "hangs on this screen, very slow".

This is the **third** stall caused by that flag: v1.0.0.36 gated submission on it,
v1.0.0.47 gated reading search rows on it, and now readiness. Treat it as
unreliable for "done" - it is only useful for "do not clobber something in
flight", which is all it still guards (closing a stale results window, and the
purchase-time check in `SubmitPurchase`).

`MarketResponseTracker.IsReady` is strictly stronger and is now the sole readiness
signal: right item, server row count declared, every row received, no error,
visible count matching, and settled for 750ms.

Two tests: a complete response is accepted while the flag is still set, and an
incomplete one is still refused.

## v1.0.0.50 - the tour was walking only materia, and empty boards were written off

### The tour skipped every item being flipped
Read the live config at
`%AppData%\XIVLauncher\pluginConfigs\SmartUndercutBot.json` - do this, it is
authoritative:

    buyable rules by (TourPriority, HuntOnTour): {(5, False): 25, (2, True): 26}

All 26 materia were on the tour at priority 2. All 25 flips - four gemdraughts,
Caramel Popcorn and twenty General-purpose dyes - were at the **default priority 5
with HuntOnTour false**, so the all-world tour walked materia and nothing else.

Cause: v1.0.0.38 added `HuntOnTour`/`TourPriority` and set them in the seeding
methods, but seeding only touches rules the config does not already have
(`ProcurementRules.All(x => x.ItemId != rule.ItemId)`). Existing rules were never
backfilled; only materia, added fresh in that same version, ever got the fields.
Config version 31 backfills them: curated consumables 0, mass-market dyes 1,
current materia 2. Dye names are matched with and without the hyphen because the
game uses both ("General-purpose", "Wide Spectrum #1 Dye").

### v1.0.0.46's "no live market" conclusion was wrong - retracted
The log disproves it directly:

    01:19:17  row 6  Pastel Purple Dye      15 live listings      OK
    01:19:29  row 7  Pastel Purple Dye      0 packets, skipped after 1 attempt
    01:19:41  row 8  Savage Might Materia XI 0 packets, skipped after 1 attempt
    01:19:50  row 11 Savage Might Materia XI 18 live listings     OK

The same items return full listings seconds later, so an empty board never meant
"this item has no market". It means the request was made too soon - exactly what
the user described. Worse, .46 turned that into "skipped after **1** attempt",
removing the retries that would have recovered it.

Retries restored. Timeout 10s -> 6s and retry backoff 2s -> 1.2s (config 30): a
real response arrives in about a second, so the old settings only made a stalled
row cost ~90s.

### Still open
Row 9 (#13721, Metallic Sky Blue Dye) still fails to map to a retainer slot. The
skip message now reports what the queue row expected and how many slots were
already claimed, which is the missing piece for diagnosing it.

## v1.0.0.48 - the purchase confirmation prompt was never answered (historical hypothesis)

v1.0.0.47 fixed the search completely. The log now runs the whole chain:

    MARKET SEARCH Waiting for search rows ...; no results are visible yet
    MARKET SEARCH Opened the General-purpose Metallic Red Dye row
    MARKET SEARCH Live prices loaded
    00:30:06 MARKET BUY request sent for item 13717, ... quantity 8, unit price 1,148
    00:30:16 PURCHASE OUTCOME UNKNOWN ... inventory did not confirm it

So search, row activation, live prices and submission all work; only the last
step failed. The market board asks for confirmation before taking gil and nothing
answered it, so the request sat unanswered and inventory never moved.

This was flagged as an open risk at the very start of these sessions
("SendPurchaseRequestPacket with no handling for a confirmation dialog") and never
closed. `TryConfirmPurchase` now finds the SelectYesno prompt, and **only accepts
it when the prompt text names the item being bought** - any other yes/no dialog in
front of the player is left alone, and a non-matching prompt is logged so a wrong
guess about the text is visible rather than silent. Answering the prompt extends
the confirmation deadline by 10s, and the timeout message reports how many prompts
were answered.

`APurchaseIsCompletedByAnsweringTheConfirmationPrompt` models a board that takes
nothing until the prompt is answered; it fails without the fix.

### Also
The most frequent skip, "the Universalis deal was gone or exceeded the live
ceiling", now reports what the board actually held: the price ceiling and quantity
wanted, how many matching-quality listings were present, and the cheapest one.
That skip is correct safety behaviour - the plan comes from Universalis and the
live board disagrees - but it was undiagnosable.

## v1.0.0.47 - shopping search never read its results

Session log, shopping on another world:

    00:09:45.035 MARKET SEARCH submitted 'Grade 4 Gemdraught of Strength';
                 mode=Normal, filter=-1, callback=None.
    00:09:45.035 MARKET SEARCH Search submitted ...; waiting for the matching item row.
    00:10:14.682 MARKET SEARCH No item search is active.        <- 30s timeout, reset

The status **never advanced** past the line set immediately after submission. It
never reached either row-reading status ("no results are visible yet" / "N
other/disabled row(s) are visible"), which proves `Poll` returned early every tick
before `ReadRows`.

The guard was `if (result.Waiting || now < nextActionAt) return false;`.
`result.Waiting` is `InfoProxyItemSearch.WaitingForListings`, which describes an
in-flight **listing** request and says nothing about whether the name-search rows
have populated. With that flag set - and `SubmitSearch` logs "ended a stale
listing request", so it was set - the rows were never read at all. This is the
same confusion fixed for *submission* in v1.0.0.37, left behind in the read path.

Removed from that guard; the flag still governs the results window after a row is
opened. `SearchRowsAreReadEvenWhileAListingRequestIsInFlight` covers it and fails
against the old guard.

### Still to establish
`callback=None` from the Enter callback means the submission may not be running
the search at all. Until now that was invisible behind the read block. If the next
log shows "Waiting for search rows ...; no results are visible yet" then the
search genuinely is not running and the fallback belongs in `SubmitSearch`
(`RunSearch` after Enter). Deliberately not layered in speculatively.

## v1.0.0.46 - the blue dye, the throttle theory retracted, wealth graph

### v1.0.0.44's throttle theory was wrong - retracted
The session log disproves it. Rows 1-8 all succeeded at ~1.5s apart with no
backoff. Requests only start failing *after* this line:

    00:03:10 [E] Visible row 9: live item #13721 could not be mapped to an
                 unused retainer market slot; skipped.

and after that only **materia** fail. Every dye and pelt returned data. So it is
not a rate limit; those materia have no live market at all, and the escalating
global cooldown from .44 was punishing healthy items for it. Removed.

Replaced with per-item handling: an item that returns nothing is recorded in
`itemsWithNoLiveMarket` and gets one attempt instead of three for the rest of the
run. `MarketRequestCooldownMs` stays at the safer 3s from .44.

### The blue dye (item #13721)
`TryMapCurrentPriceEditor` runs once *before* the live item id is known, matching
on name, quantity and price with a `?? candidates[0]` fallback - so it can claim
the wrong backing slot when a retainer holds similar listings (this account has
five Diatryma Pelt and two Metallic Silver rows). When the id arrived and
disagreed, the old code returned false and **skipped the row**, permanently, every
pass. That is why one specific item never repriced.

It now releases the wrongly-claimed slot and remaps with the known id, logging the
correction at Debug. The user's "it's the 4th item in line" was a good lead but the
position was incidental - what matters is a same-name/similar-price neighbour
earlier in the queue.

### Wealth graph (user request)
`WealthHistory` in Core: samples, 15-minute coalescing, out-of-order rejection,
thinning at 720 points that halves the oldest half rather than dropping the start.
Six tests. `WealthHistoryService` persists to `<config>/wealth-history.json` and
records one point per completed all-retainer valuation. Earnings shows an
`ImGui.PlotLines` graph with 24h/7d/30d/All ranges, low/high, change over the
window, and a per-day rate.

## v1.0.0.45 - a deep stack counted as a whole trading buffer

Panel showed shopping permanently parked on "comfortable trading stock is ready
(238/12 sale stacks)" with 18 empty sale slots and 35 bag slots of marketable
items. 238 buffer stacks out of 35 bag slots is impossible, and that inflated
count is what stopped shopping ever starting after a retainer pass.

`CollectBagStock` computed `slots = ceil(quantity / TargetStackSize)` with no cap.
Materia XI/XII became **buyable** in v1.0.0.38, so they stopped being excluded as
sell-only and started counting: a single bag slot holding 999 materia at stack 20
counted as 50 buffer stacks. A few deep stacks reached 238 and swamped a target
of 12.

Buffer slots are now capped per item by that rule's `MaximumSaleSlots`. Extra
quantity beyond what an item may occupy is concentration, not readiness to fill
diverse sale slots.

This also required updating `FullRetainersSearchOnScheduleEvenWhenComfortableStockIsReady`,
whose fixture reached 12 stacks from one item with 8 allowed slots - impossible
under the corrected rule, so the fixture now grants that item enough slots.

## v1.0.0.44 - the market board is throttling us

The v1.0.0.43 diagnostics answered it on the first run:

    Quickarm Materia XII ... [item 41781: 0 offering packet(s), 0 row(s),
                              history not received, request id none]
    Savage Might Materia XI ... [item 41760: 0 offering packet(s), ... id none]
    General-purpose Pastel Purple Dye ... [item 13715: 0 offering packet(s), ...]

**Nothing arrives at all** - no offerings, no history, no request id. The query is
not slow, it is dropped. Two facts confirm the cause is rate limiting rather than
anything item-specific:

- Pastel Purple Dye *succeeded* at 23:43:01 and *failed three times* at 23:44:33-59.
  Same item, same session. Not an item property.
- The first three rows of every pass succeed, then everything fails. That is a
  burst allowance followed by a throttle.

`RequestComparePrices` fires callback 4 and returns true unconditionally, so a
dropped query is invisible to the caller; the code then waited out the full 10s
timeout and retried twice more at 1.6s cadence, which kept it throttled.

Fix: `MarketDataService.LastRequestSawAnyPacket` distinguishes "nothing arrived"
from a genuine failure. On that, `marketThrottleLevel` doubles the request cooldown
(capped at 45s) and resets the moment data flows again. Base cooldown 1.6s -> 3s,
migrated in config version 28.

Note: the "N copied to clipboard" chat spam is **not this plugin**. 6,598 is the
undercut target and Penny Pincher copies it when the Adjust Price window opens; we
open that window a lot, so it copies a lot. Red herring.

## v1.0.0.43 - session log files, and the real cause found in dalamud.log

### Read the user's log directly - do this first, always
`%AppData%\XIVLauncher\dalamud.log` is readable from this workspace and contains
every `[SmartUndercutBot]` line. Four releases were spent guessing at symptoms that
this file answers in one grep:

    grep -a "SmartUndercutBot\]" "$APPDATA/XIVLauncher/dalamud.log" | tail -40

### What it actually showed
Not a commit failure, and not a loop. **133 live market-request timeouts**, spread
across many items:

    61 Savage Might Materia XI      27 General-purpose Pastel Purple Dye
    15 Wide Spectrum #1 Dye         15 Quickarm Materia XII       (and others)

Each row costs three attempts with backoff, so the pass crawls and never reaches
the listing the user was watching. The dyes it *did* reach were priced correctly:
"kept 6,999 gil; live lowest 7,000" - already undercutting. The "opening the price
page over and over and never doing anything" is this: opening a row, waiting out
the Compare Prices timeout, retrying, moving on.

Everything blamed in v1.0.0.40-42 (the seed-repair path, recovery replays, the
commit refusal) was real hardening but not the cause of the reported symptom.

### Session log files (user request)
`AutomationLog` now writes every entry, Debug included, to
`<plugin config>/logs/session-yyyyMMdd-HHmmss.log`, keeping the last 10. The path
is shown on the Activity log tab with a copy button. The in-game view holds 500
entries, which is far too few for a run of this length.

Market timeouts now log what the request actually observed - offering packet
count, row count, whether history arrived, request id - so the next log says
whether the packet never arrived or arrived and was rejected.

### Next
Find why Compare Prices requests time out. `OnOfferingsReceived` drops packets
whose first row's item id differs from the request, and completes only after a
350 ms quiet period following at least one packet; an item with **no offerings at
all** may produce no event, which would time out exactly like this. That is the
leading hypothesis and the new diagnostics will confirm or kill it.

## v1.0.0.42 - the price write is refused, and now says why
Screenshot evidence: General-purpose Metallic Sky Blue Dye, own listing 6,699 x5,
lowest competitor 6,599. Chat shows "6,598 copied to clipboard" four times. So the
undercut target is computed **correctly** (6,599 - 1) and the write is what fails;
the pricing logic is fine.

Ruled out on the evidence: the 1,000,000 curated ceiling. At 6,699 the listing is
nowhere near it, so `IsUnresolvedCuratedPrice` is false and the placeholder-repair
branch is not involved. (That ceiling **is** still applied to every item rather
than only curated consumables, which is a latent bug for anything trading above 1M
- worth fixing separately, but it is not this.)

`CommitPrice` can refuse for three reasons and all three were reported as bare
sentences with no observed values. They now include them: the value the field
actually holds versus the target, the addon value slot, whether the confirm button
exists, and for a changed listing, what was expected versus what was found.

A refused write also left the price unchanged, so the next pass evaluated the same
listing and was refused identically - reopening the price page every pass forever.
`NoteListingWriteFailure` drops the row after a second rejection for the rest of
the run, and `LastWriteFailure` puts the reason on the Home screen instead of only
in the log.

### Still open
The commit path has no test coverage: the fake would need the price editor,
compare-prices, market snapshot, `TryReadListing` and `CommitPrice`. That is the
next thing to build, and it is where the remaining diagnosis has to happen - the
in-game reason string is needed to know which of the three refusals it is.

## v1.0.0.41 - the actual reopen loop, reproduced in a test
v1.0.0.40 did not fix it. That change addressed the fill-only safety-seed path;
the real loop was `RecoverInterface`, and it is reached from ten call sites
including "Could not select Adjust Price" and "Could not click Compare Prices" -
which is precisely the "opening it and checking the price" the user described.

`RecoverInterface` returns to the bell and replays the **entire pass**. There was
no attempt counter anywhere in the controller, so a row that fails the same step
every time is reopened, fails, recovers, and replays - once a minute, forever.

- `NoteRecoveryFailure` counts failures per (retainer, slot). After the second the
  row is skipped for the rest of the run and logged at Error level.
- Both the reprice queue and the fill-run seed selector skip those rows.
- `consecutiveRecoveries` halts the run after four recoveries without finishing a
  retainer, rather than cycling indefinitely.
- The skip list is cleared only by a deliberate Start. This mattered: the first
  attempt cleared it in `ResetSession`, which recovery also calls, so the record
  was wiped on exactly the retry it existed to protect. The test caught that.

### Testing gap closed
`FakeRetainerService.OpenListingContextMenu` and `SelectAdjustPrice` are now
virtual, and the `Game` fake implements listings, the context menu and Adjust
Price. `AListingThatAlwaysFailsIsDroppedInsteadOfBeingReopenedForever` drives a
listing whose Adjust Price never opens and asserts the pass still completes with a
bounded number of reopens; it fails (stuck in `RecoveringRetainerInterface`) when
the guard is removed. `AHealthyListingIsStillOpenedNormally` pins the normal path.
This is the first automated coverage of the repricing path.

Still uncovered there: price editor mapping, compare-prices, market snapshots and
commit/verification. Worth extending the same fake next.

## v1.0.0.40 - the reprice pass reopened one listing forever
User: "it keeps opening the same auction over and over checking the price and
never actually doing anything."

`ReadListings` in a fill-only run picks the first curated listing whose price is
still an unresolved safety seed and calls `BeginSafetySeedPricing`, which sets
`returnToAutoListingAfterCurrent` and returns to `ReadingListings` afterwards.
Two faults made that a closed loop:

1. `MoveToNextListing` only added the slot to `freshlyRepricedAutoListingSlots`
   when the outcome was a verified write, or NoChange/WithinTolerance below the
   maximum. **Every other outcome left it unmarked** - no safe adjustment, write
   disarmed, commit failed, stale market data rejected, row could not be mapped.
2. The `unresolvedSeed` selector never consulted that set anyway.

So the listing's price never changed, it matched the selector again on the next
pass, and the run reopened it indefinitely. Now the slot is always recorded, with
a warning naming the unsettled status, and the selector skips attempted slots.
The set is cleared per run, so the next run retries.

### Testing gap (important)
This path has **no automated coverage**. `FakeRetainerService` throws
`NotSupportedException` for `OpenListingContextMenu`, `SelectAdjustPrice`,
`TryReadListing`, `TryResolveOpenPriceEditor`, `IsOpenPriceEditorFor`,
`RequestComparePrices` and `CommitPrice`, and several are not virtual, so no test
can drive a listing through context menu -> price editor -> market data ->
evaluate -> commit. The 154 passing tests never touch it. Building that fake
surface is the highest-value next task: the repricing loop is where the live bugs
keep appearing and it is the only major controller path with zero coverage.

## v1.0.0.39 - tomestone materials are listed from the bags
User pasted a market analysis of tome materials on Siren and asked to "include
these if they are in my bag". These are bought with tomestones, not gil, so they
are sell-only: listed from the bags with nothing kept back, never purchased, and
never walked on the tour.

Seeded: Diatryma Pelt, Hydrophobic Preservative, Double Duracoat, Everkeep Resin,
Turali Pigment, Mastodon Pelt, Shaaloani Coke, Yollal Extract. Stack 20, up to 5
sale slots each, `AllowHighQuality` read from the sheet's `CanBeHq`.

Matching is by **exact sheet name**, so a rename or typo would silently seed
nothing. Startup logs how many of the eight names matched; check the Shopping item
table if one is missing. Config version 27.

Also made `ExistingSettingsEnableComfortableStockOnUpgrade` compare against a fresh
`Configuration().Version` instead of a literal, so it stops breaking on every
migration.

## v1.0.0.38 - the all-world tour becomes an explicit allowlist
User: limit the tour to current materia (XI and XII, low priority), Caramel
Popcorn and other raid food such as Popoto Potage, Wide-Spectrum dyes,
General-Purpose dyes that sell well, and potions.

- `ProcurementRule.HuntOnTour` and `TourPriority`; `ResaleStockPolicy.SelectTourRules`
  filters to marked, non-sell-only, below-target rules and orders by priority.
  Previously the tour took any buyable rule, `.Take(8)` in declaration order.
- Priorities: raid food and potions 0, General-Purpose/Wide-Spectrum dyes 1,
  materia XI/XII 2. The user called materia low priority, so it is dropped first
  when the item cap bites.
- "Potions" is already covered: the seeded Grade 4 Gemdraughts are the current
  tinctures. Popoto Potage added to the curated consumables, so it also gets the
  99-stack listing and bag-reserve treatment.
- Materia XI/XII move off the sell-off list into buyable rules
  (`MinimumWeeklyUnitsSold = 50`, one sale slot). Every other grade stays sell-only.
- `LiveWorldStockHuntMaximumItems` (default 8) replaces the hardcoded cap and is
  editable. This closes the "tour has no cap on rule count" gap noted below.
- Config version 26 re-seeds.

Also, on "not sure if this is being calculated correctly": the trip figures were
right but unexplained. With sale slots covered, "Available for the next trip" is
the 20% spare-stock allowance rather than the wallet, and "room for 0 stack(s)"
means the bag buffer is already at target. Home and Shopping now print the
`ShoppingWaitReason` next to those numbers instead of leaving them bare.

## v1.0.0.37 - the search deadlock v1.0.0.36 introduced
Screenshot after .36: the Item Search box is focused and **empty**, the board shows
the category pane, and Home reads "Waiting to submit the item search for
General-purpose Pastel Purple Dye". That status is .36's own message, so
PrepareSearch succeeded (the box is focused) and SubmitSearch was refusing.

The only preconditions in SubmitSearch that are not also in PrepareSearch were
`proxy == null` and `proxy->WaitingForListings`. **Self-inflicted deadlock:**
`WaitingForListings` describes an in-flight request for one item's listings, has
nothing to do with typing a name, and nothing clears it on its own - so once set,
the search box sat focused and empty for the rest of the route.

- SubmitSearch now calls `EndRequest()` on a stale listing request instead of
  waiting on it.
- `CanUseInput` no longer requires a non-null input callback; a missing callback
  falls back to `RunSearch` rather than stalling the route.
- `IMarketSearchUi.LastBlocker` names the exact failing precondition, and the
  session puts it in the on-screen status: "Waiting to submit ...: the Item Search
  window is still initialising", etc. No more guessing which check refused.
- The submit log line records `mode` and `filter`, which will show whether the
  addon is still in category mode when a search runs.

Still unverified in game. If it stalls again the status now names the blocker; if
the search submits but no rows match, compare the logged `mode`/`filter` against a
manual name search.

## v1.0.0.36 - submit the market search before waiting for prices
User screenshot: General-purpose Pastel Pink Dye appears in the input box, but
ItemSearch still shows the empty wishlist while Home says revalidating prices.
Read the local Dalamud log: 20:06:38 and 20:07:10 on 2026-09-06 show attempts 2/3
and 3/3 for that dye on Behemoth, with no purchase submitted.

- The old code typed text and called RunSearch directly. Submit now focuses the
  native input and emits clear/TextChanged, set/TextChanged, then Enter through
  its callback, after a 400 ms mode/focus settle. Does not write cached addon
  search strings: working native drivers document that as a no-op submit path.
- Name-search matches live in AgentItemSearch.ItemBuffer/ItemCount. The old code
  read ListingPageItemIds/ListingPageItemCount (category-page data), which can
  remain empty/stale after a text search. Now read the actual match buffer with
  null/count guards, check the rendered list and wait for in-progress searches.
- A native-free MarketSearchSession coordinates preparation, submission, match
  selection and fresh-result readiness. Revalidate the exact enabled row before
  selecting and dispatching one ListItemClick; remove the old second double click.
- Reject stale result windows unless our matching row was selected; wait for
  in-flight responses. Every completed purchase still resets the search before
  another order, even for the same item. Purchase submission and inventory
  verification are unchanged.
- Home reports the actual search stage. Information-level MARKET SEARCH log
  transitions replace debug-only diagnostics, including submitted mode/filter.
- 146 tests pass; Release plugin build has 0 warnings/errors. Search tests cover
  settling, exact/disabled rows, stale results, in-flight responses, repeat polling,
  item changes, fresh same-item retries and missing-result status messages.
- Native contracts and working input-event drivers checked against sources and
  installed API 15:
  https://github.com/barnicskolaci/Conduit/blob/ccb4926eddd8a39b56676c94b525e6ca02161475/Conduit/Reflection/MarketBuyer.cs
  https://github.com/FranFkntastic/MarketMafioso/blob/6dd67c3394dfd52a8c7ada131867388b49d1e2cf/src/MarketMafioso/Automation/MarketBoard/MarketBoardItemSearchDriver.cs
  https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/AddonItemSearch.cs
  https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentItemSearch.cs
- Publication verification pending. Native behavior still needs an in-game check;
  these sessions read logs but did not submit a purchase in the running client.

## v1.0.0.35 - comfortable bag stock and accurate stack labels
User: keep visiting servers and buying until there is comfortable bag stock; the
Home display of 932 stacks looked wrong.

- Root cause: .34 added sale-only stock to the trading buffer using each rule's
  sale lot size. Thousands of ethers/materia/dyes divided into lots of 5 became
  hundreds of "stacks", exhausting the 5-stack shopping quota.
- Separate physical bag slots from quantities and planned sale stacks. Home shows
  physical marketable bag slots plus trading sale lots/target; Stock shows each
  item's physical slots, units, personal reserve and intended sale quantities.
- Sale-only stock and its pending queue do not consume trading buffer capacity or
  budget. It still occupies real bag space and remains eligible for automatic listing.
- New default: comfortable stock = 20% of checked retainer capacity, bounded 5-20
  planned sale stacks (60 slots -> 12 spares). Refill after stock moves onto retainers.
- Per-item spare targets = 25% of listed slots, bounded 1-3. Permit those bag
  replacements alongside existing listings even when old listed-slot caps are full;
  preserve original configured rules and apply total owned-quantity weekly demand
  limits. This prevents one cheap item taking the entire spare-stock allocation.
- Existing 20% acquisition-cost budget remains when sale slots are covered, plus
  travel reserve, ROI, live revalidation and real free-bag-slot checks. The fixed
  five-stack mode remains available by disabling comfortable-stock mode.
- Periodic Universalis searches continue at the bell when stocked. A full buffer
  prevents unnecessary purchases/travel, but does not switch off future searches.
  Income/new capacity can trigger an earlier search; zero wallet avoids trips.
- Config 25 enables comfortable stock for existing installations. Oceania excluded.
- 136 tests pass. Includes a fixture with 4,660 sale-only units in 5 physical slots
  (932 future lots), unaffected automatic buying/return, one physical pile vs sale
  lots, three spare buys beside full retainer exposure, scout cadence and migration.
- Published v1.0.0.35 from 3a5b473. Release run 34070840381 and build run
  34070840362 succeeded. Downloaded latest installer JSON and its ZIP: manifest,
  ZIP manifest and DLL assembly all report 1.0.0.35, API 15; both DLLs included.
  ZIP 382,179 bytes. https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.35

## v1.0.0.34 - continuous loop and North America shopping
User reported travel to Oceania and a false "No retainers were available" stop
while the screenshot showed three active retainers. User asked to finish and publish.

- Removed the guessed RetainerList AtkValue row/availability offsets. Read the
  game's sorted RetainerManager records with IsReady and Available instead, keeping
  unsubscribed retainers excluded. Wait 30 seconds for initial data, then retry.
  Reference: https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/RetainerManager.cs
- Safe menu/read failures unwind owned retainer windows for up to 60 seconds, then
  retry a full pass after one minute. A failed unwind, movement, explicit Stop,
  exceptions and unverified writes stay stopped. A route retry at the bell releases
  only its own repricing pause.
- Bag fills preserve the next full price/gil check deadline instead of pushing it
  back on every refill. The production retainer controller now has an injectable
  clock and tests through the same native-service contract used by the plugin.
- Actual four-bag resale stock counts toward spare capacity even with no in-memory
  ledger after reload; ledger and inventory are merged, not added. Personal stock
  reserves are excluded. Scan actual bag items instead of querying hundreds of
  empty dye/materia rules each frame.
- Fill real vacancies first. Once covered, spare stock defaults to 5 stacks and
  20% of available capital, counting saved acquisition costs already held. This
  allowance does not reset each trip. Wallet reinvestment still fills real vacancies.
- Zero spendable gil causes no automatic scans/trips. New income triggers a scan
  without waiting for the normal interval. Recheck item eligibility, owned exposure
  and capacity immediately before submitting a purchase.
- North-America is the default scope. Remove Oceania/Materia/world scopes when
  normalizing settings and filter Oceanic listings/plans/routes. The final outgoing
  travel guard rejects Oceanic shopping; returning to the actual home world remains
  possible. Live tours cover NA and at most 8 low-stock rules.
- Home and Shopping share real spending controls. Explain ready bag stock, buffer
  limits, no-gil waits, and retainer pauses during travel. Correct HQ-only dye text.
- Config version 24. 125 tests pass; Release plugin build has 0 warnings/errors.
  Tests include a simulated three-day retainer loop, late loading, recovery, Stop,
  unknown listing, zero-wallet/income, buffer bounds, and all five excluded worlds.
- Published v1.0.0.34 from f0c49f6; release workflow 34069792116 succeeded.
  Downloaded latest repo.json and its ZIP (378,614 bytes): installer version, ZIP
  manifest and DLL assembly version are all 1.0.0.34; API 15; both required DLLs
  are at the ZIP root. https://github.com/Neycourt5/AutoUnderCut/releases/tag/v1.0.0.34

## v1.0.0.33 - one button, bag buffer, buyable dyes
User asked why a shopping trip was not running while "Keep retainers stocked" was
ON, and wanted one button for all automation, a stock buffer in the bags,
preemptive price scouting, and high-volume dyes bought rather than dumped.

- **Shopping was already part of Start.** It was idle because retainers were at
  60/60. Nothing was switched off. The UI simply never said so - the Home screen
  even hid the "next deal search" line when free slots were 0, which made shopping
  look disabled. Added an "Everything Start runs" table showing all four stages and
  why each is waiting. Renamed the button to **Start all automation**.
- **Bag buffer.** `ProcurementBagBufferStacks` (default 5) is added on top of free
  sale slots in `PlannedSaleSlots`, so full retainers keep buying a small reserve
  that is ready to list the instant something sells.
- **Removed the hard zero-slot gate.** `TryAutomaticStart` refused to run whenever
  `LastKnownFreeSaleSlots is not > 0`, which conflated "no pass has completed yet"
  (correct to refuse) with "a pass completed and found zero free" (now handled by
  the buffer). It now checks for `null` and lets `AvailablePurchaseSlots()` decide.
- **HQ-only semantics corrected.** v1.0.0.32 made `BuyHighQualityOnly` skip any
  item without an HQ form. That would have silently excluded dyes, which have no HQ
  form at all. `ResaleStockPolicy.BuyableQuality` now means "prefer HQ wherever both
  forms exist", so NQ-only categories stay tradeable.
- **High-volume dyes are buyable.** `General-Purpose *` and `Wide-Spectrum *` dyes
  are seeded as normal buy rules (`MinimumWeeklyUnitsSold = 50`) and removed from
  the sell-off list. `AllowHighQuality` is read from the sheet's `CanBeHq`.
- Config version 23. 101 tests.

## v1.0.0.32 - buy high quality only
User: "only HQ items sell tbh". Added `BuyHighQualityOnly` (default on) rather than
hardcoding it. Superseded in part by the .33 semantics fix above.

## v1.0.0.31 - the Universalis 504
Reported in game: the scan sat on "504 (Gateway Timeout)" and nothing was queued to
list. **Self-inflicted.** Seeding every dye and materia as a sell-only rule was
correct for selling, but those rules were still handed to the *buy* scan, turning a
five-item request into hundreds of ids across two scopes. Bag listing prices through
the same service, so it died too and showed "0 stacks queued to list".

- Sell-only stock is excluded from the deal scan.
- Universalis retries 504/502/429/timeouts three times with backoff, keeps partial
  results, and fails only when no batch answered. Concurrency 3 -> 2, per-try
  timeout 30s -> 20s, scan deadline 90s -> 150s.
- Ethers joined dyes and materia in the sell-off list; per-item sell cap 2 -> 5.

## v1.0.0.30 - spend the wallet, cap by demand, sell off dyes and materia
The prior session defined the reinvestment settings and planner inputs but never
connected them: `ResaleStockPolicy.SpendableGil` and
`ProcurementWeeklySalesSharePercent` were dead code and `OwnedStock` was never
passed. Wired all three.

- Buying spends the live wallet minus a travel reserve, so sales compound.
- Holdings capped as a share of observed weekly sales, counting listed stacks plus
  bag contents. **One full target stack is always allowed** - with a 20/week minimum
  and a 25% share the cap would otherwise never admit a 99-stack and nothing would
  ever be bought.
- `ProcurementRule.LiquidateOnly`: sell from bags, never buy.
- Running dry mid-route abandons the trip and returns to the home bell, where the
  repeating retainer pass collects sale proceeds.

## v1.0.0.28 - stopped routes stayed stopped
`Halted`/`Faulted` returned from `Tick` unconditionally, so any passing failure
ended unattended shopping for the session. Split user-facing stops (Emergency Stop,
unverified purchase) from retryable ones; the latter resume at the bell after a
backoff. In-route failures now return home instead of stranding the character.
Also unified the sale-slot maths - `PollScan` stored a pending-adjusted count while
`TryAutomaticStart` compared an unadjusted one, so any pending stock made the
"capacity changed" trigger fire forever and rescan Universalis continuously.

---

## Known gaps / not verified
- **No in-game verification of native calls has ever been done from these sessions.**
  Purchase submission, listing writes, and travel are exercised only against
  simulated game services. Do not report them as tested.
- The market-board purchase path uses `SendPurchaseRequestPacket` with no handling
  for a confirmation dialog. Whether one appears is unconfirmed. If a trip reaches a
  board and buys nothing, check the log for `MARKET BUY request sent` followed by
  `PURCHASE OUTCOME UNKNOWN` - that pairing would implicate a dialog.
- Quieter travel destinations are deferred at the user's request. Only the
  non-breaking half is in: an optional separate Lifestream command for the bell leg,
  defaulting to empty.
- Live tours are now capped at 8 rules and exclude Oceania. Regular Start continues
  to use targeted Universalis routes; native travel still needs an in-game smoke test.
