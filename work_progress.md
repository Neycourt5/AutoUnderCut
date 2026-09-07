# Work progress log

Running record of what changed and why, so another agent (or a later session) can
pick up without re-deriving the history. Newest first. `PLAN.md` holds the current
task plan; this file holds the trail.

Build: use `C:/Users/Acour/.dotnet/dotnet.exe` (SDK 10.0.301), **not** the PATH
`dotnet`, which is runtime-only and fails with NETSDK1045. Set
`DOTNET_ROOT=C:\Users\Acour\.dotnet`. `gh` is not installed; verify releases with
`curl` against the GitHub REST API. Publishing is pre-authorized (see `AGENTS.md`).

---

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
