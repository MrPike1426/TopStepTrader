# TEST-08 NaN warm-up paths for MC indicators

**Status:** Open  
**Category:** Tests  
**Size:** S  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Tests/Strategies/MultiConfluenceWarmupTests.vb` (new file)

## Problem
Most existing MC tests provide fully warmed-up indicator arrays. BUG-11 and any future asymmetric `IsNaN` guards need dedicated NaN-path coverage.

## Proposed Fix
For each indicator (EMA21, EMA50, ADX, MACD, StochRSI, Ichimoku Span A/B at lag index), produce a series where that single indicator's last value is `Single.NaN`. Assert `Side = Nothing` and no exception thrown.

## Acceptance Criteria
- [ ] One test case per indicator, all returning `Side = Nothing` with no exception
- [ ] Build passes; all tests pass
