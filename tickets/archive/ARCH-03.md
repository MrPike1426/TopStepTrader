# [ARCH-03] Replace `IsWorking` Three-Boolean Pattern with `WorkPhase` Enum

**Status:** ✅ Complete  
**Category:** Architecture

## Summary
`WorkPhase` enum (Idle/DownloadingBars/Training/Running) replaces `_isRunning`, `_isTraining`, `_isBarsDownloading`. `IsWorking` and `IsIndeterminateProgress` now derived from `_workPhase`. All 292 tests passed.

**Files modified:**
- `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb`
- `src/TopStepTrader.Core/Enums/WorkPhase.vb` (created or inline)
