# Combine Sim Promotion Protocol (OBS-08)

**Status:** Active protocol
**Applies to:** promotion from TopStepX practice account to the real $50k combine
**Tooling:** `py tools/combine_report.py` after every session (read-only over
`Diagnostics/TradeHistory.db` and `Diagnostics/TopStepTrader.db`)
**Related:** FEAT-73 (combine guard), FEAT-74 (trailing MLL), STRAT-45 (strategy profile),
STRAT-44 (May audit — evidence base for the parameters)

## Purpose

Promotion to the real combine is a measured decision, not a feel-based one. The
combine may only be attempted after **10 consecutive practice sessions on the
final combine configuration** pass every gate below. `tools/combine_report.py`
evaluates the gates automatically after each session.

A **session** is a TopStep trading day (17:00 CT → 17:00 CT, keyed by the CT
date the day ends on) with at least one closed trade, run with
`Combine:Enabled = true` on the practice account with the locked parameters:

| Parameter | Value |
|---|---|
| Soft halt (entries blocked) | −$600 combined daily P&L |
| Hard flatten + halt | −$750 combined daily P&L |
| Profit lock | arms at +$220, floors at +$170 (STRAT-46/47) |
| Max trades / day | 4 |
| Max consecutive losers | 2 |
| Trailing max drawdown (MLL) | −$2,000 from peak equity, frozen at start balance, +$100 app buffer |
| Sizing | micros only, max 5 contracts, SlipStream risk ≤ 0.4% |

## Step 0 — Kill-switch drill (run BEFORE session 1)

Prove the guard's teeth on the practice account before any counted session.
Do not count a session until the drill has passed.

1. In `appsettings.json` set temporary tiny thresholds, e.g.
   `"DailyLossSoftDollars": -20, "DailyLossHardDollars": -30,
   "ProfitLockTriggerDollars": 15, "ProfitLockFloorDollars": 10`.
2. **Soft-halt check:** take a small losing position past −$20 combined P&L.
   Verify new entries are blocked and the dashboard strip shows the soft halt.
3. **Hard-flatten check:** let combined P&L reach −$30. Verify: all positions
   flattened, all working orders (including pre-staged stop entries) cancelled,
   a `CombineHardLoss` row written to `RiskEvents`, and the halt persists even
   after P&L would recover.
4. **Profit-lock check:** on a fresh trading day (or after reset), get combined
   P&L ≥ +$15 then let it retrace to ≤ +$10. Verify flatten + `CombineProfitLock`
   row and that the day banked green.
5. **Reconciliation check:** confirm in the logs that every flatten reported
   complete and `TradeReconciliationWorker` found no orphan positions or orders.
6. Restore the locked parameters (table above) exactly, restart the app, and
   confirm the effective config in the dashboard combine strip before session 1.

Any failure in the drill is a bug: fix it, and the fix itself resets the
session counter (see below), so re-run the drill afterwards.

## The 10-session run

- Trade the practice account exactly as the combine would be traded — same
  strategies (STRAT-45 profile), same hours, same config. No manual overrides
  of guard decisions.
- After every session run:

  ```
  py tools/combine_report.py
  ```

  and file the D4 gate table. Optionally `--out Docs/research/combine_report_latest.md`.
- **Counter reset rule:** any change to guard code (`DailyLossGuardService`,
  flattener, trading-day clock, trail persistence) or to any parameter in the
  table above resets the counter to 0/10. Strategy-parameter tuning that does
  not touch the guard or the combine profile does not reset it, but should be
  avoided mid-run — the 10 sessions are supposed to measure one configuration.
- Sessions must be **consecutive** (no cherry-picking: every practice session
  on the config counts, including red ones).

## Promotion gates (all must PASS over the 10 sessions)

| Gate | Requirement | How measured |
|---|---|---|
| G1 | ≥ 7 of 10 green days | `combine_report.py` D4 (green = net after fees > $0) |
| G2 | Median green-day P&L ≥ +$150 | `combine_report.py` D4 |
| G3 | Zero days past −$750 app-side; the guard fired first on every losing day that approached it | `combine_report.py` D4 (max adverse excursion of cumulative day P&L) |
| G4 | Zero guard malfunctions: every flatten completed, no orphan positions or orders | `combine_report.py` D4 automated proxy (no entries after a hard halt) **plus manual cross-check of `TradeReconciliationWorker` logs and any "flatten INCOMPLETE" log lines** |
| G5 | Profit lock banked ≥ +$170 on every day it armed (peak ≥ +$220) | `combine_report.py` D4 |

The $150 figure in G2 is TopStep's payout **winning-day threshold**: the 2026
payout policy requires 5 winning days of ≥ $150 net P&L per payout cycle, so a
locked-in day below $150 is green for the combine but useless for withdrawals.
The lock floor sits $20 above that line so flatten slippage cannot drop a
banked day under it (STRAT-46/47; previously $150 arm / $100 floor, which
systematically banked non-qualifying $100–149 days).

G4 is the only gate with a manual component: the report cannot see broker-side
state, so the reconciliation-log check is mandatory before declaring it passed.

## On pass

The owner flips `Combine:Enabled` (and points the app at the combine account)
**manually**. There is no automated promotion. Before the first real session,
re-run the config confirmation from drill step 6 against the combine account.

## On fail

- Any FAIL gate ⇒ no promotion. Diagnose with `combine_report.py` D1–D3 and the
  per-strategy expectancy table; apply the
  validate-before-expand rule (fix or remove what is demonstrably losing before
  adding anything new).
- Changes made in response reset the counter per the reset rule.
