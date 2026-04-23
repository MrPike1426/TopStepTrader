# STRAT-23 Verify MC respects TOD / news / contract-roll no-trade windows

**Status:** Open  
**Category:** Strategy  
**Size:** S  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`, `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb`

## Problem
Multi-Confluence `Evaluate(...)` has no session check inside it. The TOD gate from STRAT-02 likely lives upstream in `StrategyExecutionEngine` but was not verified during review. A pure-rules signal will fire during CME 17:00–18:00 ET maintenance, around CPI/FOMC releases, and across quarterly contract rolls (CLAUDE.md notes M6E U26→M26 quote-freeze).

## Proposed Fix
(a) Document where the TOD gate sits in `StrategyExecutionEngine` with an inline cross-reference comment in `MultiConfluenceStrategy.vb`. (b) Add a configurable news-blackout list (date-time ranges in `appsettings.json`). (c) Add a contract-roll blackout (≥ 2 sessions before front-month rollover).

## Acceptance Criteria
- [ ] Unit test: synthetic clock at 17:30 ET → expect no entry signal
- [ ] TOD gate location documented with a comment
- [ ] Build passes; all tests pass

## Open Questions
- Where is the source of truth for "current front-month" — `FavouriteContracts` only, or a roll-detection service?
