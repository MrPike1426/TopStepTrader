# [STRAT-19] Replace Hardcoded Confidence=1.0 with Graduated Score in MultiConfluence

**Status:** Open  
**Category:** Strategy Improvements  
**Size:** M  
**Files:** `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:129`, `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`

## Problem
`MultiConfluenceSignalProvider` always returns `.Confidence = 1.0F` regardless of how marginal the indicator values are. This bypasses the `MinConfidence` persona threshold (90% for Lewis, 80% for Damian, 70% for Joe), making the threshold meaningless for this strategy. An ADX of 20.1 on a barely-trending instrument and an ADX of 50 in a strong trend both produce identical signals.

## Change
After the 8/8 long or short detection, compute a graduated confidence score based on indicator strength:

```vb
' Confidence = weighted average of continuous indicator strengths
' ADX strength: normalise to [0.5, 1.0] over range [threshold, threshold+30]
Dim adxStrength = CSng(Math.Min(1.0F, 0.5F + (adxNow - adxThreshold) / 60.0F))
' DI spread strength: normalise MinDiSpread to MinDiSpread+20
Dim diStrength  = CSng(Math.Min(1.0F, (Math.Abs(plusDINow - minusDINow) - MinDiSpread) / 20.0F + 0.5F))
' MACD histogram strength: normalise by ATR
Dim macdStrength = CSng(Math.Min(1.0F, Math.Abs(histNow) / (macdMinMag * 10.0F + 0.001F)))
' StochRSI distance from boundary (K < 0.7 for long: 1.0 at K=0, 0.5 at K=0.7)
Dim stochStrength = If(isLong, CSng((0.7F - stochKNow) / 0.7F), CSng((stochKNow - 0.3F) / 0.7F))
stochStrength = CSng(Math.Max(0.0F, Math.Min(1.0F, stochStrength)))

Dim confidence = CSng((adxStrength * 0.35F) + (diStrength * 0.25F) +
                      (macdStrength * 0.25F) + (stochStrength * 0.15F))
```

Return `confidence` (clamped 0–1) in `SignalResult.Confidence`. The backtest engine and live engine already filter on `MinConfidence`, so no engine changes are required.

Apply graduated scoring in both the backtest signal provider and the live `MultiConfluenceStrategy.Evaluate` result (via a helper or shared module).

## Acceptance Criteria
- [ ] `SignalResult.Confidence` reflects graduated strength, not hardcoded 1.0F
- [ ] A marginal signal (ADX barely above threshold, tiny DI spread) scores < 0.80
- [ ] A strong signal (ADX > 40, large DI spread, large MACD histogram) scores > 0.90
- [ ] Lewis (MinConfidence=0.90) rejects marginal signals that Damian (0.80) would take
- [ ] Unit test: marginal parameter set → confidence < 0.85
- [ ] Unit test: strong parameter set → confidence > 0.90
- [ ] Build passes; all tests still pass
