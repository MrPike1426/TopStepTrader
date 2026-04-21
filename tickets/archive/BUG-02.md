# [BUG-02] Cap `LogEntries` in `SniperViewModel` at 1,000 Entries

**Status:** ✅ Complete  
**Category:** Bugs

## Summary
`AppendLog` helper added; `LogEntries` capped at 1,000 with FIFO eviction.

**Files modified:** `src/TopStepTrader.UI/ViewModels/SniperViewModel.vb`
