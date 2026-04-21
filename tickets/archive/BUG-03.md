# [BUG-03] Add `IDisposable` to `BacktestViewModel`, Clean Up `DispatcherTimer` Handler

**Status:** ✅ Complete  
**Category:** Bugs

## Summary
`BacktestViewModel` implements `IDisposable`. `RemoveHandler` and `CancellationTokenSource.Dispose()` called in `Dispose()` (via `BacktestRunViewModel`).

**Files modified:** `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb`, `BacktestRunViewModel.vb`
