# [STRAT-03] Promote EMA50 to Active Gate in MultiConfluenceStrategy

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:175–192`

## Problem
`ema50Arr` is computed at line 131 and stored in `result.Ema50` for the grid display, but it is **not referenced in any of lc1–lc7 or sc1–sc7**. EMA50 — the big-picture trend anchor — is purely decorative. Adding it as a required condition raises signal quality at negligible computation cost.

## Change
Add as `lc2b` / `sc2b`:
```vb
' Long: price must also be above EMA50 (big-picture trend)
Dim lc2b = (Not Single.IsNaN(ema50Now) AndAlso lastClose > CDec(ema50Now))
' Short: price must also be below EMA50
Dim sc2b = (Not Single.IsNaN(ema50Now) AndAlso lastClose < CDec(ema50Now))
```
Include in `longCount` / `shortCount` arrays, making this an 8-condition strategy. Update grid header from `Long x/7` / `Short x/7` to `Long x/8` / `Short x/8`.

## Acceptance Criteria
- [ ] `lc2b` / `sc2b` added and included in `longCount` / `shortCount`
- [ ] Strategy now requires 8/8 conditions for a signal
- [ ] `result.LongCount` and `result.ShortCount` max is 8; grid headers updated in HydraView.xaml
- [ ] Unit tests updated for 8-condition scoring
- [ ] Build passes; all tests still pass
