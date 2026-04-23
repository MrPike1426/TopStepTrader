# REFACTOR_TRACKER.md
> Last updated: 2026-04-26 | Build: `net10.0-windows` x64 | Tests: 360 passed, 0 failed
> Session 2026-04-26: Pump-n-Dump code review vs new live TP/SL system. Raised BUG-15–21, STRAT-28–29, OBS-06, TEST-10.
> Session 2026-04-25: UAT — OIL/Damian/WIDE ATR, 1 trade/$57, missed ~09:00 and ~13:30 uptrends. Raised BUG-12, BUG-13, BUG-14, STRAT-27, OBS-02–05, TEST-09.
> Session 2026-04-24: Multi-Confluence code review. Raised ARCH-04, BUG-07–11, STRAT-20–26, TEST-07, TEST-08, OBS-01.

---

## Priority Queue

1. **BUG-12** — `fetchCount` (70) < `MinBarsRequired` (80) — perpetual MC warm-up (High)
2. **BUG-13** — MC same-direction scale-in blocked by `_positionOpen` guard (High)
3. **BUG-14** — `TrailHardStopBracketAsync` resets TP to `DefaultTpTicks`, discards WIDE-tier TP (High)
4. **BUG-07** — Live partial-signal (8/9) missing hard-gate guard (Blocker)
5. **BUG-08** — StochRSI long threshold mismatch live(0.7) vs backtest(0.8) (Blocker)
6. **BUG-09** — DI-spread / Chikou-gap / MACD-mag filters absent in backtest (High)
7. **ARCH-04** — Consolidate MC live + backtest evaluators (High)
8. **BUG-10** — `Confidence` computed when `Side = Nothing` in live evaluator (High)
9. **STRAT-21** — Volume gate fail-open in backtest, fail-closed in live (High)
10. **STRAT-26** — Backtest does not clamp SL to broker minimum ticks (High)
11. **STRAT-20** — `min(1.5xATR, cloud edge)` picks the tighter SL — clarify or invert (High)
12. **TEST-07** — Live vs backtest MC evaluator parity tests (High)
13. **STRAT-22** — Yahoo intraday volume unreliable for futures — replace for live vol gate (Med)
14. **STRAT-25** — Add spread/slippage/commission to backtest fills (Med)
15. **STRAT-23** — Verify MC respects TOD / news / contract-roll no-trade windows (Med)
16. **STRAT-24** — Externalise MC hard-coded thresholds to config/persona (Med)
17. **OBS-01** — Enrich MC StatusLine with diagnostic signal components (Med)
18. **BUG-11** — lc2 missing Single.IsNaN(ema21Now) guard (Med)
19. **TEST-08** — NaN warm-up paths for MC indicators (Med)
20. **STRAT-27** — Make MACD histogram minimum ATR fraction persona-configurable (Med)
21. **OBS-02** — Emit FailedConditions list from MultiConfluenceStrategy.Evaluate (Med)
22. **OBS-03** — Log StrategyDefinition config snapshot at engine Start() (Med)
23. **OBS-04** — Write AI pre-trade veto outcome to DiagnosticLogger (Med)
24. **OBS-05** — Emit bracket state-transition entries to DiagnosticLogger (Med)
25. **TEST-09** — Verify ATR-tier bracket ticks applied in PlaceBracketOrdersAsync (Med)
26. **BUG-15** — PumpNDump silently overrides user-provided TP/SL ticks (Blocker)
27. **BUG-17** — PumpNDump ViewModel passes eToro BrokerType to TopStepX engine (Blocker)
28. **BUG-16** — PumpNDump bracket close detection has no miss tolerance (High)
29. **BUG-18** — PumpNDump EmergencyCloseAsync uses stale internal qty (High)
30. **BUG-19** — PumpNDump average entry derived from bar close not fill price (High)
31. **BUG-20** — PumpNDump TP tighten + SL trail can produce SL/TP inversion (High)
32. **BUG-21** — PumpNDump OnOrderFilled re-triggers DoCheckAsync — immediate re-entry after TP fill (High)
33. **STRAT-28** — PumpNDump missing trading hours and stale-bar entry suppression (Med)
34. **STRAT-29** — PumpNDump free-ride drops heat to zero — scale-in cap bypass (Med)
35. **OBS-06** — PumpNDump missing DiagnosticLogger integration (Med)
36. **TEST-10** — PumpNDump has no backtest signal provider (Med)

---

## Open Tickets

| ID | Title | Category | Size | Source |
|---|---|---|---|---|
| ARCH-04 | Consolidate MC live + backtest evaluators | Architecture | L | Code-Review |
| ~~BUG-07~~ | ~~Live partial-signal (8/9) missing hard-gate guard~~ | Bugs | S | ✅ Done |
| BUG-08 | StochRSI long threshold mismatch live(0.7)/backtest(0.8) | Bugs | XS | Code-Review |
| BUG-09 | MC DI-spread / Chikou-gap / MACD-mag filters absent in backtest | Bugs | M | Code-Review |
| BUG-10 | MC live Confidence computed when Side = Nothing | Bugs | XS | Code-Review |
| BUG-11 | MC lc2 missing Single.IsNaN(ema21Now) guard | Bugs | XS | Code-Review |
| BUG-12 | fetchCount (70) < MinBarsRequired (80) — perpetual MC warm-up | Bugs | S | UAT |
| BUG-13 | MC same-direction scale-in blocked by _positionOpen guard | Bugs | M | UAT |
| BUG-14 | TrailHardStopBracketAsync resets TP to DefaultTpTicks — discards WIDE-tier TP | Bugs | S | UAT |
| STRAT-20 | Clarify/rename min(1.5xATR, cloud edge) SL selection | Strategy | S | Code-Review |
| STRAT-21 | Volume gate fail-open in backtest, fail-closed in live | Strategy | S | Code-Review |
| STRAT-22 | Replace Yahoo intraday volume with ProjectX source for live vol gate | Strategy | M | Code-Review |
| STRAT-23 | Verify MC respects TOD / news / contract-roll no-trade windows | Strategy | S | Code-Review |
| STRAT-24 | Externalise MC hard-coded thresholds to config/persona | Strategy | M | Code-Review |
| STRAT-25 | Add spread/slippage/commission to backtest fills | Strategy | M | Code-Review |
| STRAT-26 | Clamp backtest SL to broker minimum ticks (parity with live) | Strategy | S | Code-Review |
| STRAT-27 | Make MACD histogram minimum ATR fraction persona-configurable | Strategy | S | UAT |
| TEST-07 | Live vs backtest MC evaluator parity tests | Tests | M | Code-Review |
| TEST-08 | NaN warm-up paths for MC indicators | Tests | S | Code-Review |
| TEST-09 | Verify ATR-tier bracket ticks applied in PlaceBracketOrdersAsync | Tests | S | UAT |
| OBS-01 | Enrich MC StatusLine with diagnostic signal components | Observability | XS | Code-Review |
| OBS-02 | Emit FailedConditions list from MultiConfluenceStrategy.Evaluate | Observability | S | UAT |
| OBS-03 | Log StrategyDefinition config snapshot at engine Start() | Observability | XS | UAT |
| OBS-04 | Write AI pre-trade veto outcome to DiagnosticLogger | Observability | XS | UAT |
| OBS-05 | Emit bracket state-transition entries to DiagnosticLogger | Observability | S | UAT |
| BUG-15 | PumpNDump silently overrides user-provided TP/SL ticks | Bugs | S | Code-Review |
| BUG-16 | PumpNDump bracket close detection has no miss tolerance | Bugs | S | Code-Review |
| BUG-17 | PumpNDump ViewModel passes eToro BrokerType to TopStepX engine | Bugs | XS | Code-Review |
| BUG-18 | PumpNDump EmergencyCloseAsync uses stale internal qty | Bugs | S | Code-Review |
| BUG-19 | PumpNDump average entry derived from bar close not fill price | Bugs | S | Code-Review |
| BUG-20 | PumpNDump TP tighten and SL trail can produce SL/TP inversion | Bugs | S | Code-Review |
| BUG-21 | PumpNDump OnOrderFilled re-triggers DoCheckAsync — immediate re-entry after TP fill | Bugs | M | Code-Review |
| STRAT-28 | PumpNDump missing trading hours and stale-bar entry suppression | Strategy | M | Code-Review |
| STRAT-29 | PumpNDump free-ride drops heat to zero — scale-in cap bypass after activation | Strategy | S | Code-Review |
| OBS-06 | PumpNDump missing DiagnosticLogger integration | Observability | L | Code-Review |
| TEST-10 | PumpNDump has no backtest signal provider | Tests | M | Code-Review |

---

## Completion Summary

| Category | Done | IDs |
|---|---|---|
| Architecture | 12 | ARCH-01a–f, ARCH-02a–e, ARCH-03 |
| Bugs | 7 | BUG-01–07 |
| Strategy | 19 | STRAT-01–19 |
| Tests | 6 | TEST-01–06 |
| Code Quality | 3 | QUAL-01–03 |
| UI/UX | 4 | UX-01–04 |
| Observability | 0 | — |
| **Total** | **50** | |

---

*To execute a ticket manually: tell Claude or Copilot `Execute [ID]` — they will load this file + `tickets/[ID].md` + the referenced source files.*

*To run tickets automatically: start the **Ticket Handler** agent. It reads this file, picks the highest-priority open ticket, implements the fix, commits and pushes directly to the current branch (`origin HEAD`), marks the ticket resolved here, and runs `/clear` before moving on. See `CLAUDE.md → Automated Agent Workflow` for the full procedure.*
