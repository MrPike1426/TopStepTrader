# [STRAT-27] Make MACD histogram minimum ATR fraction persona-configurable

**Status:** Open  
**Category:** Strategy  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:189`, `src/TopStepTrader.Core/Settings/PersonasSettings.vb`, `src/TopStepTrader.Core/Models/PersonaProfile.vb`  
**Depends on:** STRAT-24 (externalise all MultiConfluence thresholds)

## Problem

The MACD condition inside `MultiConfluenceStrategy.Evaluate` applies a minimum histogram magnitude gate:

```vb
' MultiConfluenceStrategy.vb:189
Dim macdMinMag = AtrValue * 0.05D
If Math.Abs(macdHist) < macdMinMag Then Return False   ' condition fails
```

`0.05` is a hardcoded literal. There is no way to tune it per persona without a source change and redeployment.

**Why this matters across personas:**

| Persona | Risk appetite | Effect of 0.05 |
|---------|--------------|----------------|
| **Lewis** (aggressive) | Higher bar for entries | 0.05 may be too low — weak MACD surges trigger entries on noisy bars. Prefer 0.07–0.10. |
| **Damian** (balanced) | Neutral — current default is acceptable | 0.05 is the baseline. |
| **Joe** (conservative) | Lower bar — wants more signals | 0.02–0.03 to participate in early momentum moves. |

In the UAT run (Damian, OIL, WIDE), a 0.05 threshold filtered out no signals on its own — but there is no observability on how many bars *narrowly failed* the gate. With a configurable value, threshold sensitivity can be tuned and diagnosed per persona.

This ticket is narrower than STRAT-24 (which covers all MC thresholds). STRAT-27 implements only the MACD magnitude fraction and adds it to `PersonaProfileSettings` so it can be adjusted in `appsettings.json` without touching strategy code.

## Change

**1. Add to `PersonaProfileSettings`:**

```vb
''' <summary>Minimum MACD histogram magnitude as a fraction of ATR. Default 0.05.</summary>
Public Property MacdHistMinAtrFraction As Double = 0.05
```

**2. Populate persona defaults in `PersonasSettings.vb`:**

```vb
' Lewis
MacdHistMinAtrFraction = 0.07

' Damian (keep baseline)
MacdHistMinAtrFraction = 0.05

' Joe
MacdHistMinAtrFraction = 0.03
```

**3. Pass into `MultiConfluenceStrategy.Evaluate`:**

`Evaluate` already receives `adxThreshold As Integer` as a parameter. Add a second persona parameter:

```vb
Public Function Evaluate(
    bars As List(Of OhlcvBar),
    adxThreshold As Integer,
    macdHistMinAtrFraction As Double
) As MultiConfluenceResult
```

**4. Replace the hardcoded literal:**

```vb
' Before
Dim macdMinMag = AtrValue * 0.05D

' After
Dim macdMinMag = AtrValue * macdHistMinAtrFraction
```

**5. Update `StrategyExecutionEngine` call site** (the `mcResult = _mcStrategy.Evaluate(bars, adxThreshold)` line) to pass `_persona.MacdHistMinAtrFraction`.

## Acceptance Criteria

- [ ] `PersonaProfileSettings` has `MacdHistMinAtrFraction` property, default 0.05
- [ ] Lewis=0.07, Damian=0.05, Joe=0.03 set in `PersonasSettings.vb`
- [ ] `MultiConfluenceStrategy.Evaluate` accepts `macdHistMinAtrFraction As Double` parameter; the `0.05D` literal is removed
- [ ] Call site in `StrategyExecutionEngine` passes `_persona.MacdHistMinAtrFraction`
- [ ] Existing `MultiConfluenceStrategy` unit tests updated to pass an explicit fraction (0.05) so behaviour is unchanged
- [ ] `appsettings.json` sample updated with the new property under each persona section
- [ ] Build passes; all tests still pass
