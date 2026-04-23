# STRAT-22 Yahoo intraday volume unreliable for futures — replace for live MC vol gate

**Status:** Open  
**Category:** Strategy  
**Size:** M  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.API/BarCollectionService.vb`

## Problem
Yahoo aggregates futures volume across exchanges/sessions inconsistently and often returns 0 on micro contracts (MES/MNQ/M6E). Confluence condition #8 (volume gate) becomes noise. The ProjectX/TopStepX bar feed has authoritative volume for the live path.

## Proposed Fix
For live evaluation, source `volumes` from ProjectX (`PXHistoryClient.RetrieveBarsAsync`) instead of Yahoo. Backtest stays on Yahoo for history depth — accept the divergence and document it.

## Acceptance Criteria
- [ ] Capture 1 trading session of MGC and M6E volumes from both Yahoo and ProjectX; tabulate `volAvg = 0` bar counts
- [ ] Live MC volume gate uses ProjectX-sourced volumes
- [ ] Build passes; all tests pass

## Open Questions
- Does ProjectX history go back the 60 days the backtest needs? If not, dual-source is required.
