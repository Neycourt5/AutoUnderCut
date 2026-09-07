# Work progress log

Running record of what changed and why, so another agent (or a later session) can
pick up without re-deriving the history. Newest first. `PLAN.md` holds the current
task plan; this file holds the trail.

Build: use `C:/Users/Acour/.dotnet/dotnet.exe` (SDK 10.0.301), **not** the PATH
`dotnet`, which is runtime-only and fails with NETSDK1045. Set
`DOTNET_ROOT=C:\Users\Acour\.dotnet`. `gh` is not installed; verify releases with
`curl` against the GitHub REST API. Publishing is pre-authorized (see `AGENTS.md`).

---

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
- Publication and final artifact verification pending.

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
