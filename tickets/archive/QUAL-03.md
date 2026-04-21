# [QUAL-03] Extract `BacktestView.xaml` DatePicker Styling to Shared Resource Dictionary

**Status:** ✅ Complete  
**Category:** Code Quality  
**Size:** S  
**Files:**
- `src/TopStepTrader.UI/Views/BacktestView.xaml` (~lines 70–162)
- `src/TopStepTrader.UI/Resources/DatePickerTheme.xaml` (create)
- `src/TopStepTrader.UI/App.xaml`

## Problem
88 lines of `CalendarButton`, `CalendarDayButton`, and `CalendarItem` style overrides are inlined in `BacktestView.xaml`. The same pattern will need to be duplicated if a DatePicker is added to any other view. The styles also conflict with any future global theme update.

## Change
1. Create `src/TopStepTrader.UI/Resources/DatePickerTheme.xaml` as a `ResourceDictionary`.
2. Move all DatePicker-related styles there.
3. Merge into `App.xaml` (`Application.Resources` merged dictionaries) so all views inherit it.
4. Remove the inline styles from `BacktestView.xaml`.

## Acceptance Criteria
- [ ] `DatePickerTheme.xaml` exists and is merged in `App.xaml`
- [ ] `BacktestView.xaml` DatePicker section is ≤ 10 lines (just the control declaration)
- [ ] DatePicker renders identically before and after (visual smoke test)
- [ ] Build passes
