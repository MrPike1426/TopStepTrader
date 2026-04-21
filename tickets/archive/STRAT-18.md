# [STRAT-18] Add Chikou-vs-Cloud Filter to MultiConfluence

**Status:** Open  
**Category:** Strategy Improvements  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:124`, `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:77`

## Problem
The current Chikou (lagging span) condition only checks whether current close is above/below the price 26 bars ago. Textbook Ichimoku requires the Chikou to also be **clear of the cloud** at its plotted position — i.e., the cloud that existed 26 bars ago. Without this check, the Chikou can satisfy its condition while plotted inside a dense cloud zone, which represents ambiguous price memory rather than clear bullish/bearish confirmation.

## Change
At the Chikou lag index (`lagIdx = n - 1 - 26`), also retrieve the cloud spans at that historical position and verify the lagging close clears them.

**`MultiConfluenceStrategy.vb`** — extend the Chikou block:
```vb
' Chikou must also clear the cloud at the lag position
Dim lagSpanA = If(lagIdx >= 0, CDec(ichi.SpanA(lagIdx)), Decimal.MinValue)
Dim lagSpanB = If(lagIdx >= 0, CDec(ichi.SpanB(lagIdx)), Decimal.MinValue)
Dim lagCloudTop    = Math.Max(lagSpanA, lagSpanB)
Dim lagCloudBottom = Math.Min(lagSpanA, lagSpanB)

' lc4: Chikou above price 26 bars ago AND above the cloud at that position
Dim lc4 = (lagIdx >= 0 AndAlso lastClose > lagClose + chikouMinGap AndAlso
           lastClose > lagCloudTop)

' sc4: Chikou below price 26 bars ago AND below the cloud at that position
Dim sc4 = (lagIdx >= 0 AndAlso lastClose < lagClose - chikouMinGap AndAlso
           lastClose < lagCloudBottom)
```

Apply the same pattern in `MultiConfluenceSignalProvider.vb` using the `indicators.IchiSpanA(mcLagIdx)` and `indicators.IchiSpanB(mcLagIdx)` arrays (guard for index < 0 and NaN).

## Acceptance Criteria
- [ ] `lc4` requires Chikou to clear the historical cloud top as well as historical price
- [ ] `sc4` requires Chikou to be below the historical cloud bottom as well as historical price
- [ ] Guard: if lagIdx < 0 or historical cloud values are NaN → condition returns False (no signal)
- [ ] Applied in both live and backtest signal providers
- [ ] Unit test: Chikou above old price but inside old cloud → no long signal
- [ ] Unit test: Chikou above old price AND above old cloud → long signal allowed
- [ ] Build passes; all tests still pass
