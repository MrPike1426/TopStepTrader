# [TEST-09] Verify ATR-tier bracket ticks correctly applied in `PlaceBracketOrdersAsync`

**Status:** Open  
**Category:** Tests  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:1847-1854`, `tests/TopStepTrader.Tests/Trading/StrategyExecutionEngineTests.vb`

## Problem

`PlaceBracketOrdersAsync` computes SL and TP tick distances from `SlMultipleOfN × ATR / tickSize` and `TpMultipleOfN × ATR / tickSize`. These values drive `_initialSlTicks` and `_initialTpTicks`, which in turn control every subsequent trail and Free Roll calculation. Despite being the single most critical numerical path in the engine, there are **no unit tests** that assert the computed tick distances for each ATR bracket tier.

The UAT finding (BUG-14: `TrailHardStopBracketAsync` uses `DefaultTpTicks` instead of `_initialTpTicks`) survived undetected partly because no test ever verified that `_initialTpTicks` was set to the WIDE-tier value after entry. A regression suite for this path would have caught BUG-14 before UAT.

Additionally, the absence of tier coverage means a misconfiguration (e.g., `SlMultipleOfN = 0`, `AtrBracketTier` enum mismatch) silently produces a 1-tick bracket — the `Math.Max(1, …)` clamp masks it at runtime.

## Change

Add a parameterised test class `AtrBracketTickTests` in the existing test project:

```vb
<Theory>
<InlineData("NARROW", 1.0, 2.0, 0.50, 10, 20)>   ' ATR=0.50, tick=0.01 → SL=50, TP=100  (?)
<InlineData("STANDARD", 1.5, 3.0, 0.50, 10, 75)>  ' ATR=0.50, tick=0.01 → SL=75, TP=150
<InlineData("WIDE",    2.5, 5.0, 0.50, 10, 250)>   ' ATR=0.50, tick=0.01 → SL=125, TP=250
Public Async Function PlaceBracket_CorrectTicksStoredFor_AtrTier(
    tierName As String,
    slMult As Decimal, tpMult As Decimal,
    atrValue As Decimal, tickSize As Decimal,
    expectedTpTicks As Integer) As Task
```

Each test should:
1. Build a `StrategyDefinition` with the given `SlMultipleOfN`, `TpMultipleOfN`, and `AtrBracketTier`
2. Mock `IBarRepository.GetBarsForMLAsync` to return a minimal bar list with the given `atrValue` pre-calculated (or use a bar list that produces it)
3. Mock `IBrokerClient.PlaceBracketOrderAsync` to capture the SL/TP arguments
4. Call `PlaceBracketOrdersAsync` on the engine
5. Assert:
   - `_initialTpTicks == expectedTpTicks`
   - `_initialSlTicks == expectedSlTicks`
   - The broker was called with SL price = `entryPrice − slTicks × tickSize` (Long) or `entryPrice + slTicks × tickSize` (Short)
   - The broker was called with TP price = `entryPrice + tpTicks × tickSize` (Long)

Also add a regression test specifically for BUG-14:

```vb
<Fact>
Public Async Function TrailHardStop_UsesInitialTpTicks_NotDefaultTpTicks() As Task
    ' Arrange: WIDE tier entry → _initialTpTicks = 80, DefaultTpTicks = 10
    ' Act: advance price 1 tick into profit → TrailHardStopBracketAsync fires
    ' Assert: EditPositionSlTpAsync called with TP derived from 80 ticks, not 10
```

## Acceptance Criteria

- [ ] `AtrBracketTickTests` covers NARROW, STANDARD, and WIDE tiers (at minimum)
- [ ] Each test asserts both `_initialSlTicks` and `_initialTpTicks` after `PlaceBracketOrdersAsync`
- [ ] Each test asserts the broker-call SL and TP prices match the computed tick distances
- [ ] BUG-14 regression test: `TrailHardStopBracketAsync` uses `_initialTpTicks`, not `DefaultTpTicks`
- [ ] Tests are parameterised (no copy-paste per tier)
- [ ] Tests use the existing mock/stub patterns in the test project (no new test infrastructure)
- [ ] All new tests pass; existing 360 tests remain passing
- [ ] Build passes
