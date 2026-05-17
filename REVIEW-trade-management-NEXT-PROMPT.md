# Prompt for the post-fix trade management review

Use this **in a fresh Claude Code session** once tickets `DOC-07`, `FEAT-57`, `FEAT-58`, `FEAT-59`, `BUG-86`, and `BUG-87` have all landed on `main`. The goal: produce a second review document that is directly comparable to `REVIEW-trade-management-2026-05-17.md` so we can measure whether the fixes worked and whether new gaps emerged.

---

## The prompt (copy from here)

I have a baseline trade-management review at `REVIEW-trade-management-2026-05-17.md` and six tickets opened from it: `DOC-07`, `FEAT-57`, `FEAT-58`, `FEAT-59`, `BUG-86`, `BUG-87`. They should now all be Closed and have moved to `tickets/archive/`. If any of them are still Open, flag it and stop — don't generate a comparison review against unfinished work.

Do a fresh audit of the trade management system, **same scope as the baseline**:

- (a) Entry detection — 15-min default timeframe, SuperTrend+ approach across favourites
- (b) Trade management — SL placed correctly, SL ratchet moves recorded, exit when conditions degrade
- (c) Exit paths and postmortem persistence — SL hit / app-close / TopStepX force-close all update the DB correctly
- Replay sufficiency — can we step through any closed trade bar-by-bar from the persisted data
- ML training pipeline — does the engine see real win/loss outcomes from live trades

Method: **source-code audit only**. Read tests if needed but don't execute `dotnet build` or `dotnet test`. Per the project workflow, Claude plans / Copilot implements — your deliverable is a markdown document, not code changes.

After the fresh audit, **explicitly reconcile against the baseline**:

- For each verdict in the baseline TL;DR table (✅ / ⚠️ / ❌), is the same statement still accurate?
- For each ticket: did the implementation match its Acceptance Criteria, and did closing it actually resolve the underlying problem?
- Strategy drift — any change (intentional or not) to SL ladder thresholds, exit cadence, entry rules between v1 and v2.

Also call out:

- **New gaps** — things you'd open a ticket for now that weren't in the baseline.
- **Regressions** — anything that was working at v1 but no longer works.

### Output format

Match the baseline structure section-by-section so a side-by-side read is tractable:

- Same TL;DR table at the top, with the same row order
- Same `(a)`, `(b)`, `(c)`, Replay, ML section headers
- **New section: "Reconciliation against v1 review"** — one row per baseline finding with current status (Closed / Partially closed / Still open / Drifted / N/A) plus a one-line note
- **New section: "New gaps surfaced since v1"** — proposes ticket IDs to open for each
- **"What I did NOT verify"** section at the end, same shape as v1

Save as `REVIEW-trade-management-<YYYY-MM-DD>.md` at the repo root (today's date).

Open one ticket per gap in the "New gaps" section using the standalone-ticket rules from `MEMORY.md` (no line numbers, no "pick one of N", explicit acceptance criteria, manual verification steps, declared dependencies).

Ask for clarification ONLY if:
- Any of the six baseline tickets cannot be located in `tickets/` or `tickets/archive/`
- The scope has changed in a way the baseline doesn't anticipate (e.g. a new strategy or asset class added since)
- The user wants a deliverable shape different from the baseline

(Copy ends here)

---

## How to compare v1 and v2

Once the new review exists:

### 1. Side-by-side TL;DR
Open both files. Read the TL;DR table in v1 next to the TL;DR table in v2.
- Any row that moved ❌/⚠️ → ✅ is a confirmed improvement.
- Any row that moved the other direction is a regression — investigate before doing anything else.
- Any row that stayed ❌/⚠️ in v2 means the matching ticket either wasn't closed or didn't fully resolve the issue.

### 2. The Reconciliation section is the audit log
v2's "Reconciliation against v1 review" is the explicit pass/fail per baseline finding. Keep this — it's the trading engine's improvement log over time. If you run this loop quarterly you'll have a real longitudinal view.

### 3. Spot-check the two highest-stakes tickets
For `FEAT-57` (TradeOutcome persistence) and `FEAT-58` (snapshot + lifespan): open the archived ticket and walk its Acceptance Criteria list against current code yourself, with `git log` to find the implementing commit. The point isn't to second-guess Claude's audit — it's to catch implementation-vs-intent drift that an audit might gloss over. The other four tickets are smaller and lower-risk; the AC-as-audit treatment from v2 is usually enough.

### 4. New gaps → next iteration's input
v2's "New gaps surfaced since v1" becomes the backlog for the next round. Open the tickets, then queue the next post-fix review with the same prompt above, substituting the new ticket IDs.

### 5. Trend over time
Keep every `REVIEW-trade-management-<date>.md` file. After three or four iterations you'll see which areas of the system actually improve under attention and which regress whenever you look away. That pattern is itself signal — it tells you where to invest engineering time vs where to leave a working system alone.

---

## Quick sanity checks before running the prompt

Before generating v2, confirm:
- All six tickets are in `tickets/archive/` (not still in `tickets/` root) — i.e. they have actually been closed via the tickets CLI.
- The branch you're auditing has all six implementing commits merged. `git log --oneline --grep="FEAT-57\|FEAT-58\|FEAT-59\|DOC-07\|BUG-86\|BUG-87"` should show six (or more) commits.
- The current branch is the one you intend to ship from. Auditing a feature branch produces a misleading review if a parallel branch is what will actually merge.
