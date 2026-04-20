# REFACTOR_TRACKER.md
> Last updated: 2026-04-19 | Build target: `net10.0-windows` x64 | Language: VB.NET | Test count baseline: 312 passed, 0 failed | ARCH-01a–01f, ARCH-02a–02e, ARCH-03, BUG-01–BUG-06, TEST-01–TEST-06 complete
> ARCH-01 and ARCH-02 are split into sub-tickets (a–e/f) so each step fits a single session.

---

## Project Context

Seven-project WPF desktop trading application targeting TopStepX (ProjectX REST/SignalR API).

| Project | Role |
|---|---|
| `TopStepTrader.Core` | Domain models, interfaces, enums, tick math, FavouriteContracts |
| `TopStepTrader.API` | HTTP/SignalR clients, ProjectX token manager |
| `TopStepTrader.Data` | EF Core + SQLite repositories |
| `TopStepTrader.ML` | Technical indicators (pure math, no ML runtime currently) |
| `TopStepTrader.Services` | Business logic — backtest engine, bar collection, trading engines |
| `TopStepTrader.UI` | WPF MVVM — ViewModels, Views, ViewModelLocator, AppBootstrapper |
| `TopStepTrader.Tests` | xUnit, 221 tests across 10 files |

**Key architectural facts to remember:**
- `BacktestEngine.vb` — 1,584 lines. Single `RunBacktestAsync` method handles 15+ strategy types in one `If/ElseIf` chain. All dynamic exit state (`dynStop`, `dynTp`, etc.) is held in local variables within this procedure.
- `BacktestViewModel.vb` — 1,933 lines. Handles strategy selection, persona application, orchestration, Max Effort (840 runs), Claude AI calls, CSV export, elapsed timer, pinned results, and previous runs.
- `BacktestMetrics.vb` — well-extracted pure functions, 30 test facts. Do not disturb without updating tests.
- Slippage in the engine (lines 629–636) is **intentional** — it degrades SL fill price in the adverse direction to model real-world market-order fill. Not a bug.
- `FavouriteContracts.vb` holds the master instrument list. Always call `contract.GetTickSize(_session.ActiveBroker)` / `contract.GetPointValue(_session.ActiveBroker)` — never hardcode specs.
- Persona system: `IPersonaService` → `PersonaService` (Singleton) → SQLite persistence. Never call `RiskProfile.Lewis/Damian/Joe` directly in ViewModels.
- `StrategyConditionType` enum integer values are DB discriminators — must not be changed once data is stored.

**Self-verification rule (must run after every file edit):**
```bash
dotnet build src/TopStepTrader.Services/TopStepTrader.Services.vbproj --no-restore -v q
dotnet build --no-restore -v q
dotnet test --no-build -v q
# Expected: 286 passed, 0 failed (update this number when new tests are added)
```

---

## Priority Queue

Execute these first, in order. ARCH-01 and ARCH-02 are split into sub-steps — complete each lettered sub-ticket before moving to the next.

1. **[ARCH-01a]** — Define scaffold: `IStrategySignalProvider`, `SignalResult`, `StrategyIndicators`, factory skeleton
2. **[ARCH-01b]** — Extract `EmaRsiSignalProvider` and `MultiConfluenceSignalProvider`
3. **[ARCH-01c]** — Extract remaining live-trading providers (BbSqueeze, Lult, Vidya, NakedTrader, DoubleBubbleButt)
4. **[ARCH-01d]** — Extract QuantLab providers (ConnorsRsi2, SuperTrend, Donchian, BbRsiReversion)
5. **[ARCH-01e]** — Gut `BacktestEngine` main loop: replace If/ElseIf chain with `provider.Evaluate()` calls
6. **[ARCH-01f]** — Add one unit test per signal provider
7. **[BUG-01]** — Replace 11 pending-state locals with a `PendingEntry` record class
8. **[BUG-02]** — Cap `LogEntries` in `SniperViewModel` at 1,000 entries
9. **[BUG-03]** — Add `IDisposable` to `BacktestViewModel`, remove `DispatcherTimer` handler on disposal
10. **[BUG-04]** — Add `TickSize > 0` guard in `BacktestMetrics.CalculatePnL` and `MaxScaleIns ≥ 0` guard in `BacktestConfiguration`
11. **[ARCH-02a]** — Extract `BacktestRunViewModel`
12. **[ARCH-02b]** — Extract `MaxEffortViewModel`
13. **[ARCH-02c]** — Extract `PinnedResultsViewModel`
14. **[ARCH-02d]** — Extract `PreviousRunsViewModel`
15. **[ARCH-02e]** — Wire shell `BacktestViewModel`, update `BacktestView.xaml` bindings

---

## Ticket Backlog

---

### ARCHITECTURE

**ARCH-01 sub-tickets (BacktestEngine extraction):**
- [x] **[ARCH-01a]** Define scaffold — interface, SignalResult, StrategyIndicators, factory skeleton
- [x] **[ARCH-01b]** Extract `EmaRsiSignalProvider` + `MultiConfluenceSignalProvider`
- [x] **[ARCH-01c]** Extract `BbSqueezeSignalProvider`, `LultDivergenceSignalProvider`, `VidyaCrossSignalProvider`, `NakedTraderSignalProvider`, `DoubleBubbleButtSignalProvider`
- [x] **[ARCH-01d]** Extract QuantLab providers — `ConnorsRsi2`, `SuperTrend`, `DonchianBreakout`, `BbRsiReversion`
- [x] **[ARCH-01e]** Gut `BacktestEngine` main loop — replace If/ElseIf chain with `provider.Evaluate()` calls
- [x] **[ARCH-01f]** Add one unit test per signal provider (11 tests minimum)

**ARCH-02 sub-tickets (BacktestViewModel split):**
- [x] **[ARCH-02a]** Extract `BacktestRunViewModel`
- [x] **[ARCH-02b]** Extract `MaxEffortViewModel`
- [x] **[ARCH-02c]** Extract `PinnedResultsViewModel`
- [x] **[ARCH-02d]** Extract `PreviousRunsViewModel`
- [x] **[ARCH-02e]** Wire shell `BacktestViewModel`, update `BacktestView.xaml` tab bindings

**Other architecture:**
- [x] **[ARCH-03]** Replace `IsWorking` three-boolean pattern with `WorkPhase` enum

---

#### [ARCH-01a] Define Scaffold — Interface, SignalResult, StrategyIndicators, Factory Skeleton

**Files to create:**
- `src/TopStepTrader.Core/Interfaces/IStrategySignalProvider.vb`
- `src/TopStepTrader.Core/Models/SignalResult.vb`
- `src/TopStepTrader.Services/Backtest/StrategyIndicators.vb`
- `src/TopStepTrader.Services/Backtest/StrategySignalProviderFactory.vb` (skeleton — no implementations yet)

**Files to read first:** `BacktestEngine.vb` (full file, to understand what indicators are pre-calculated and what fields SignalResult must carry)

**Problem:**
Before any strategy can be extracted, the shared contracts must exist. This step creates the types that all subsequent sub-tickets depend on. No changes to `BacktestEngine.vb` in this step — the engine is not touched yet.

**Change:**

`IStrategySignalProvider`:
```vb
Public Interface IStrategySignalProvider
    Function Evaluate(bar As HistoricalBar, indicators As StrategyIndicators,
                      config As BacktestConfiguration, barIndex As Integer) As SignalResult
End Interface
```

`SignalResult` — carries everything the engine needs to fill an entry:
```vb
Public Class SignalResult
    Public Property Side As String          ' "Buy", "Sell", or Nothing = no signal
    Public Property Confidence As Single
    Public Property StopDelta As Decimal    ' ATR-relative entry→SL distance
    Public Property TpDelta As Decimal      ' ATR-relative entry→TP distance
    ' Indicator-anchored absolute exit levels (strategy-specific; 0 = not used)
    Public Property AbsoluteSlPrice As Decimal
    Public Property AbsoluteTpPrice As Decimal
    Public Property IndicatorExitLevel As Decimal   ' DonchianMid, BbMid, SuperTrend line, etc.
    Public Property IsLong As Boolean
End Class
```

`StrategyIndicators` — holds all pre-calculated series arrays so providers don't recompute:
```vb
Public Class StrategyIndicators
    ' EMA series
    Public Property Ema8 As Single()
    Public Property Ema21 As Single()
    Public Property Ema50 As Single()
    ' Momentum
    Public Property Rsi As Single()
    Public Property MacdLine As Single()
    Public Property MacdSignal As Single()
    Public Property MacdHistogram As Single()
    ' Trend / volatility
    Public Property Atr As Single()
    Public Property PlusDi As Single()
    Public Property MinusDi As Single()
    Public Property Adx As Single()
    ' Ichimoku
    Public Property IchiTenkan As Single()
    Public Property IchiKijun As Single()
    Public Property IchiSpanA As Single()
    Public Property IchiSpanB As Single()
    ' Oscillators
    Public Property StochRsiK As Single()
    Public Property StochRsiD As Single()
    Public Property WaveTrend1 As Single()
    Public Property WaveTrend2 As Single()
    ' Bands
    Public Property BbUpper As Single()
    Public Property BbMiddle As Single()
    Public Property BbLower As Single()
    Public Property BbWidth As Single()
    Public Property BbPctB As Single()
    ' Other
    Public Property SuperTrendLine As Single()
    Public Property SuperTrendDir As Single()
    Public Property DonchianUpper As Single()
    Public Property DonchianLower As Single()
    Public Property DonchianMid As Single()
    Public Property Vidya As Single()
    Public Property DeltaVolume As Single()
    Public Property Vwap As Single()
    Public Property ConnorsRsi As Single()
End Class
```

`StrategySignalProviderFactory` — skeleton with a `TODO` for each strategy:
```vb
Public Class StrategySignalProviderFactory
    Public Shared Function Create(condition As StrategyConditionType) As IStrategySignalProvider
        Select Case condition
            Case StrategyConditionType.EmaRsiWeightedScore  : Return New EmaRsiSignalProvider()
            ' TODO ARCH-01b: remaining live strategies
            ' TODO ARCH-01c: ...
            ' TODO ARCH-01d: QuantLab strategies
            Case Else
                Throw New NotSupportedException($"No signal provider for {condition}")
        End Select
    End Function
End Class
```
(Initially only `EmaRsiWeightedScore` resolves; others throw until their sub-ticket is done.)

**Acceptance Criteria:**
- [ ] All 4 files created and compile cleanly
- [ ] `BacktestEngine.vb` is NOT modified in this step
- [ ] `TopStepTrader.Services.vbproj` includes the new files
- [ ] All 221 existing tests still pass

---

#### [ARCH-01b] Extract `EmaRsiSignalProvider` and `MultiConfluenceSignalProvider`

**Files to create:**
- `src/TopStepTrader.Services/Backtest/Strategies/EmaRsiSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb`

**Files to read first:** `BacktestEngine.vb` lines 700–900 (EmaRsi and MultiConfluence signal blocks), `ARCH-01a` scaffold files

**Problem:**
These are the two most critical strategies — EmaRsi is the default live-trading strategy; MultiConfluence produced the backtest winner config. Extract their signal evaluation logic into the provider classes, update the factory, and verify the engine produces identical results when routed through the provider (engine still uses old If/ElseIf for all other strategies in this step).

**Approach:**
- Copy the `If condition = EmaRsiWeightedScore Then ... End If` signal block verbatim into `EmaRsiSignalProvider.Evaluate()`.
- Map the existing local variables to `indicators.Ema21`, `indicators.Rsi`, etc.
- Return a populated `SignalResult` instead of setting `pendingSide` / `pendingConf` directly.
- Do NOT change the engine loop yet — the factory is not called from the engine in this step. The providers are standalone and tested in isolation.

**Acceptance Criteria:**
- [ ] `EmaRsiSignalProvider` and `MultiConfluenceSignalProvider` exist and implement `IStrategySignalProvider`
- [ ] Factory resolves both strategies without throwing
- [ ] Providers are unit-tested (at minimum: buy signal, sell signal, no-signal scenarios using synthetic bars)
- [ ] `BacktestEngine.vb` is NOT modified in this step
- [ ] All 221 existing tests still pass

---

#### [ARCH-01c] Extract Remaining Live-Trading Signal Providers

**Files to create:**
- `src/TopStepTrader.Services/Backtest/Strategies/BbSqueezeSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/LultDivergenceSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/VidyaCrossSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/NakedTraderSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/DoubleBubbleButtSignalProvider.vb`

**Files to read first:** `BacktestEngine.vb` — the signal blocks for each of these 5 strategies

**Approach:** Same as ARCH-01b — copy each strategy's signal block into its provider class, update the factory, keep the engine unchanged.

**Acceptance Criteria:**
- [ ] All 5 provider classes created and implement `IStrategySignalProvider`
- [ ] Factory resolves all 7 live strategies (5 new + 2 from ARCH-01b) without throwing
- [ ] Each provider has at least one unit test
- [ ] `BacktestEngine.vb` is NOT modified in this step
- [ ] All 221 existing tests still pass

---

#### [ARCH-01d] Extract QuantLab Signal Providers

**Files to create:**
- `src/TopStepTrader.Services/Backtest/Strategies/ConnorsRsi2SignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/SuperTrendSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/DonchianBreakoutSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/BbRsiReversionSignalProvider.vb`

**Files to read first:** `BacktestEngine.vb` — QuantLab strategy signal blocks (ConnorsRsi2, SuperTrend, Donchian, BbRsiReversion)

**Note:** `DoubleBubbleButtSignalProvider` already exists from ARCH-01c (it is used in both live trading and QuantLab). Do not duplicate it.

**Acceptance Criteria:**
- [ ] All 4 QuantLab provider classes created
- [ ] Factory now resolves all 11 strategies without throwing
- [ ] Each has at least one unit test
- [ ] `BacktestEngine.vb` is NOT modified in this step
- [ ] All 221 existing tests still pass

---

#### [ARCH-01e] Gut `BacktestEngine` Main Loop

**File to modify:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`
**Files to read first:** All 11 provider files, the scaffold files, current `BacktestEngine.vb`

**Problem:**
All 11 providers are now implemented and tested. This step removes the ~900-line `If/ElseIf` strategy chain from `RunBacktestAsync` and replaces it with:

```vb
' Resolve provider once before the loop (outside For i = warmUp...)
Dim provider = StrategySignalProviderFactory.Create(config.StrategyCondition)

' Inside the loop, replace the entire If/ElseIf strategy block with:
Dim signal = provider.Evaluate(bar, indicators, config, i)
If signal IsNot Nothing AndAlso signal.Side IsNot Nothing Then
    pendingSide      = signal.Side
    pendingConf      = signal.Confidence
    pendingStopDelta = signal.StopDelta
    pendingTpDelta   = signal.TpDelta
    ' ... map remaining SignalResult fields to pending state
End If
```

The fill, exit, bookkeeping, and dynamic-exit sections of the loop are **not changed** in this step — only the signal evaluation block is replaced.

After this step `BacktestEngine.RunBacktestAsync` should be ≤ 500 lines.

**Acceptance Criteria:**
- [x] The `If/ElseIf` strategy chain is fully removed from `BacktestEngine`
- [x] `StrategySignalProviderFactory.Create()` is called from the engine
- [x] `BacktestEngine.RunBacktestAsync` is 525 lines (≈500 target; pre-calc extracted to `BuildIndicators`)
- [x] All 281 existing tests still pass
- [ ] Run a manual backtest for at least one strategy and confirm P&L matches a pre-refactor baseline

---

#### [ARCH-01f] Add One Unit Test Per Signal Provider

**File:** `src/TopStepTrader.Tests/Backtest/` — add `SignalProviderTests.vb` (or per-provider files)

**Problem:**
ARCH-01b through ARCH-01d asked for "at least one" test per provider as a minimum gate. This dedicated step adds the full test suite: buy signal, sell signal, and no-signal for each of the 11 providers using synthetic bar series. All tests must be deterministic and require no API calls.

**Acceptance Criteria:**
- [x] ≥ 3 tests per provider × 11 providers = ≥ 33 new `<Fact>` tests
- [x] Tests use synthetic `HistoricalBar` lists built in-memory
- [x] `StrategyIndicators` is populated with known values so signal output is predictable
- [x] All tests pass; update expected count in `CLAUDE.md`

---

#### [ARCH-02a] Extract `BacktestRunViewModel`

**Files to create:** `src/TopStepTrader.UI/ViewModels/BacktestRunViewModel.vb`
**Files to read first:** `BacktestViewModel.vb` (full file), `BacktestView.xaml` (Tab 1 content)

**What moves here:**
All properties and logic for the **Run Backtest tab** (Tab 1):
- Contract selection, date range pickers, strategy selector, timeframe display
- Capital, quantity, SL/TP inputs, ATR-mode toggle, ATR multiples
- Persona selector + `ApplyPersona()` method
- `ExecuteRun` command and its entire implementation
- `TimeframeResultRowVm` inner class (or move to `Core/Models/`)
- Results DataGrid (`TimeframeResults` collection)
- Elapsed timer (`_elapsedTimer`, `StartElapsedTimer`, `StopElapsedTimerIfIdle`)
- `IsWorking`, `IsIndeterminateProgress`, `CanRun`, `ShowDescription` computed properties
- `IDisposable` implementation (from BUG-03, if already done — otherwise include it here)

**What stays in `BacktestViewModel` (shell):** Navigation between tabs, references to each sub-VM.

**Acceptance Criteria:**
- [ ] `BacktestRunViewModel` exists and contains all Tab 1 logic
- [ ] `BacktestViewModel` delegates to `BacktestRunViewModel` for Tab 1
- [ ] Tab 1 renders and runs correctly (manual smoke test)
- [ ] All 221 existing tests still pass

---

#### [ARCH-02b] Extract `MaxEffortViewModel`

**Files to create:** `src/TopStepTrader.UI/ViewModels/MaxEffortViewModel.vb`
**Files to read first:** `BacktestViewModel.vb` — `ExecuteMaximumEffort` and related members, `BacktestView.xaml` Tab 2 content

**What moves here:**
- `ExecuteMaximumEffort` command and full implementation
- `MaxEffortResults` collection (`ObservableCollection(Of MaxEffortRowVm)`)
- `MaxEffortRowVm` inner class (or move to `Core/Models/`)
- `PinResultCommand`
- Claude AI analysis panel (`ClaudeAnalysis` text, `IsClaudeAnalysisVisible`)
- All persona-iteration logic (`_personaService.GetAllProfiles()`)
- Cancel command scoped to Max Effort runs

**Acceptance Criteria:**
- [ ] `MaxEffortViewModel` exists and contains all Tab 2 logic
- [ ] Tab 2 runs, populates results, and Claude analysis fires correctly
- [ ] Cancellation resets UI state (addresses UX-04 implicitly)
- [ ] All 221 existing tests still pass

---

#### [ARCH-02c] Extract `PinnedResultsViewModel`

**Files to create:** `src/TopStepTrader.UI/ViewModels/PinnedResultsViewModel.vb`
**Files to read first:** `BacktestViewModel.vb` — `PinnedResults` and related members, `BacktestView.xaml` Tab 3 content

**What moves here:**
- `PinnedResults` collection (`ObservableCollection(Of MaxEffortRowVm)`)
- Seed logic (the 2025-07 Gold/Multi-Confluence/1hr baseline row added in constructor)
- Any clear/export commands on the Pinned tab

**Note:** `PinResultCommand` lives in `MaxEffortViewModel` (ARCH-02b) and calls `PinnedResultsViewModel.Add()` — the two VMs need a shared reference or an event bus. Prefer a direct reference injected via constructor.

**Acceptance Criteria:**
- [ ] `PinnedResultsViewModel` exists with `PinnedResults` collection and seed row
- [ ] Pin button in Tab 2 correctly appends to Tab 3
- [ ] Tab 3 renders correctly
- [ ] All 221 existing tests still pass

---

#### [ARCH-02d] Extract `PreviousRunsViewModel`

**Files to create:** `src/TopStepTrader.UI/ViewModels/PreviousRunsViewModel.vb`
**Files to read first:** `BacktestViewModel.vb` — previous runs grid members, `BacktestView.xaml` Tab 4 content

**What moves here:**
- `PreviousRuns` collection (`ObservableCollection(Of BacktestRunEntity)`)
- Load-on-navigate logic (typically triggered when Tab 4 is selected)
- Any delete/clear commands on the Previous Runs tab

**Acceptance Criteria:**
- [ ] `PreviousRunsViewModel` exists and loads history from SQLite on activation
- [ ] Tab 4 renders and populates correctly
- [ ] All 221 existing tests still pass

---

#### [ARCH-02e] Wire Shell `BacktestViewModel` + Update `BacktestView.xaml`

**Files to modify:**
- `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb` (now becomes the thin shell)
- `src/TopStepTrader.UI/ViewModels/ViewModelLocator.vb` (register sub-VMs)
- `src/TopStepTrader.UI/Views/BacktestView.xaml` (update tab DataContext bindings)

**What remains in `BacktestViewModel`:**
- Constructor that instantiates/injects the 4 sub-VMs
- Properties exposing each sub-VM (`RunVm`, `MaxEffortVm`, `PinnedVm`, `PreviousVm`)
- Active-tab tracking if needed for navigation

**`BacktestView.xaml` changes:**
Each `TabItem` `DataContext` is bound to the corresponding sub-VM property:
```xml
<TabItem Header="Run Backtest" DataContext="{Binding RunVm}">
<TabItem Header="Maximum Effort!" DataContext="{Binding MaxEffortVm}">
<TabItem Header="Pinned" DataContext="{Binding PinnedVm}">
<TabItem Header="Previous Runs" DataContext="{Binding PreviousVm}">
```

**Acceptance Criteria:**
- [ ] `BacktestViewModel.vb` is ≤ 80 lines
- [ ] No individual sub-VM file exceeds 600 lines
- [ ] All four tabs render and function correctly (manual smoke test of each tab)
- [ ] `ViewModelLocator` resolves sub-VMs correctly (scoped lifetime preserved)
- [ ] All 221 existing tests still pass

---

#### [ARCH-03] Replace `IsWorking` Three-Boolean Pattern with `WorkPhase` Enum

**File:** `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb:442–446`

**Problem:**
`IsWorking` is computed from `_isRunning OrElse _isTraining OrElse _isBarsDownloading`. If any flag gets stuck `True`, the UI shows the progress bar indefinitely. Three independent booleans can produce 8 states; only 4 are valid. Adding a 4th phase (e.g. "Saving") requires adding a 4th boolean and remembering to include it in every computed property.

**Change:**
```vb
Public Enum WorkPhase
    Idle
    DownloadingBars
    Training
    Running
End Enum

Private _workPhase As WorkPhase = WorkPhase.Idle
```
Replace all `_isRunning / _isTraining / _isBarsDownloading` reads and writes with `_workPhase`. Update `IsWorking`, `IsIndeterminateProgress`, `CanRun`, `ShowDescription` computed properties accordingly.

**Acceptance Criteria:**
- [x] `WorkPhase` enum defined (in `Core/Enums/` or within the ViewModel file)
- [x] `_isRunning`, `_isTraining`, `_isBarsDownloading` fields removed
- [x] `IsWorking`, `IsIndeterminateProgress` derived from `_workPhase`
- [x] UI behaviour unchanged (build + manual smoke test)
- [x] All 292 existing tests still pass

---

### BUGS

- [x] **[BUG-01]** Replace 11 Pending-State Locals with a `PendingEntry` Record
- [x] **[BUG-02]** Cap `LogEntries` in `SniperViewModel` at 1,000 Entries
- [x] **[BUG-03]** Add `IDisposable` to `BacktestViewModel`, Clean Up `DispatcherTimer` Handler
- [x] **[BUG-04]** Add `TickSize > 0` Guard in `CalculatePnL` and `MaxScaleIns ≥ 0` Guard in `BacktestConfiguration`
- [x] **[BUG-05]** NaN Propagation Guard Before Every Indicator Access in `BacktestEngine`
- [x] **[BUG-06]** `DonchianBreakout` Exit De-bounce (Oscillation Around Mid)

---

#### [BUG-01] Replace 11 Pending-State Locals with a `PendingEntry` Record

**File:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb:351–367`

**Problem:**
The next-bar entry fill mechanism uses 11 separate local variables:
`pendingSide`, `pendingGroupId`, `pendingConf`, `pendingIsScaleIn`, `pendingStopDelta`, `pendingTpDelta`, `pendingAbsStSl`, `pendingAbsStTp`, `pendingStIsLong`, `pendingDonMid`, `pendingDonIsLong`, `pendingBbMid`, `pendingBbIsLong`, `pendingDbbInner`, `pendingDbbIsLong`.

Each strategy branch sets a different subset. Leaving a stale value from a previous bar's signal in an unused field is trivially easy — the type system cannot catch it.

**Change:**
Define a `PendingEntry` structure (or `Class` with `Nothing` as sentinel) in `Services/Backtest/` or `Core/`:
```vb
Friend Class PendingEntry
    Public Property Side As String          ' "Buy", "Sell"
    Public Property GroupId As Integer
    Public Property Confidence As Single
    Public Property IsScaleIn As Boolean
    Public Property StopDelta As Decimal
    Public Property TpDelta As Decimal
    ' Indicator-anchored levels (Nothing = not set for this strategy)
    Public Property AbsStSl As Decimal?
    Public Property AbsStTp As Decimal?
    Public Property StIsLong As Boolean
    Public Property DonMid As Decimal?
    Public Property DonIsLong As Boolean
    Public Property BbMid As Decimal?
    Public Property BbIsLong As Boolean
    Public Property DbbInner As Decimal?
    Public Property DbbIsLong As Boolean
End Class
```
Replace all 15 local variables with `Dim pending As PendingEntry = Nothing`. Clear by setting `pending = Nothing`.

**Acceptance Criteria:**
- [x] `PendingEntry` class/record defined and used in `BacktestEngine`
- [x] All 15 pending-state local variables removed
- [x] All 286 existing tests still pass
- [x] No behaviour change in backtest output for any strategy

---

#### [BUG-02] Cap `LogEntries` in `SniperViewModel` at 1,000 Entries

**File:** `src/TopStepTrader.UI/ViewModels/SniperViewModel.vb:343`

**Problem:**
`LogEntries` is an unbounded `ObservableCollection(Of String)`. An 8-hour live session at 30-second poll intervals generates at minimum ~960 raw entries, and potentially 5–10k if signal evaluation is logged per bar. The WPF ListView virtualises rendering but holds all strings in memory.

**Change:**
Add a helper that enforces the cap on every `Add` call:
```vb
Private Sub AppendLog(message As String)
    If LogEntries.Count >= 1000 Then LogEntries.RemoveAt(0)
    LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}")
End Sub
```
Replace all direct `LogEntries.Add(...)` calls with `AppendLog(...)`.

**Acceptance Criteria:**
- [x] `LogEntries` never exceeds 1,000 items during a running session
- [x] Oldest entries are removed first (FIFO)
- [x] Existing log-related tests (if any) still pass
- [x] All 286 existing tests still pass

---

#### [BUG-03] Add `IDisposable` to `BacktestViewModel`, Clean Up `DispatcherTimer` Handler

**File:** `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb:790–793`

**Problem:**
`_elapsedTimer` is a `DispatcherTimer` with `AddHandler _elapsedTimer.Tick, AddressOf OnElapsedTick` wired in the constructor. There is no `IDisposable` implementation and no `RemoveHandler`. If the `ViewModelLocator` ever disposes a scope and creates a new one, the handler closure keeps the old ViewModel instance alive, leaking it and all its injected services.

**Change:**
1. Implement `IDisposable` on `BacktestViewModel`.
2. In `Dispose()`:
   ```vb
   _elapsedTimer.Stop()
   RemoveHandler _elapsedTimer.Tick, AddressOf OnElapsedTick
   _cancellationSource?.Cancel()
   _cancellationSource?.Dispose()
   ```
3. Update `ViewModelLocator` to call `Dispose()` on the ViewModel when the scope is disposed (if not already doing so).

**Acceptance Criteria:**
- [x] `BacktestViewModel` implements `IDisposable` (delegates to `BacktestRunViewModel.Dispose`)
- [x] `RemoveHandler` is called in `Dispose()` (via `BacktestRunViewModel`)
- [x] `CancellationTokenSource` is cancelled and disposed in `Dispose()` (via `BacktestRunViewModel`)
- [x] All 286 existing tests still pass

---

#### [BUG-04] Add `TickSize > 0` Guard in `CalculatePnL` and `MaxScaleIns ≥ 0` Guard in `BacktestConfiguration`

**Files:**
- `src/TopStepTrader.Services/Backtest/BacktestMetrics.vb` — `CalculatePnL`
- `src/TopStepTrader.Core/Interfaces/IBacktestService.vb` — `BacktestConfiguration.MaxScaleIns`

**Acceptance Criteria:**
- [x] `CalculatePnL` throws `InvalidOperationException` (not `DivideByZeroException`) when `TickSize = 0`
- [x] `BacktestConfiguration` rejects negative `MaxScaleIns`
- [x] New tests cover both validation paths (6 new facts)
- [x] All 292 tests pass

---

#### [BUG-05] NaN Propagation Guard Before Every Indicator Access in `BacktestEngine`

**File:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb` — multiple sites (~lines 704, 760, 832+)

**Problem:**
Indicator functions return `Single.NaN` for warm-up bars. NaN checks (`If Not Single.IsNaN(...)`) are scattered inconsistently. A missing guard causes NaN to propagate into `dynStop`/`dynTp` price calculations, producing `Decimal.Parse(NaN)` exceptions or silent zero-initialisation of brackets.

**Change:**
Establish a consistent pattern: before any strategy branch uses an indicator value, assert it is not NaN with a short-circuit `Continue For` if any required indicator is warm-up NaN. Example:
```vb
If Single.IsNaN(ema21(i)) OrElse Single.IsNaN(ema50(i)) OrElse Single.IsNaN(rsi(i)) Then Continue For
```
Audit every strategy branch in `RunBacktestAsync` and add the guard at the top of the branch.

**Acceptance Criteria:**
- [x] Every strategy branch has a NaN guard at its entry point that skips the bar if any required indicator is NaN
- [x] No strategy branch reads an indicator value without a prior NaN check
- [x] Add a test that feeds a bar series shorter than the warm-up period and verifies no exception is thrown and no trades are generated
- [x] All 298 existing tests still pass

---

#### [BUG-06] `DonchianBreakout` Exit De-bounce

**File:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb` — DonchianBreakout exit block (~lines 540–545)

**Problem:**
The Donchian strategy exits when `bar.Close` crosses the 10-bar mid-channel (`pendingDonMid`). If price oscillates around the mid on consecutive bars, the strategy exits and immediately re-enters on the same or next bar, generating artificial churn and inflating trade count.

**Change:**
After a Donchian mid-cross exit, apply the same `lastForceCloseBarIndex` cooldown (or a dedicated `lastDonchianExitBarIndex`) to suppress re-entry for at least 3 bars:
```vb
If exitReason = "MidCross" Then lastDonchianExitBarIndex = i
' ...at entry:
If (i - lastDonchianExitBarIndex) <= 3 Then Continue For  ' de-bounce
```

**Acceptance Criteria:**
- [x] After a Donchian mid-cross exit, no new Donchian entry is allowed for the next 3 bars
- [x] Add a test with a synthetic oscillating-around-mid price series that verifies trade count does not grow unboundedly
- [x] All 221 existing tests still pass

---

### TEST COVERAGE

- [x] **[TEST-01]** `UpdateDynamicExits` Unit Tests (Trailing Stop, Break-Even, Extend TP)
- [x] **[TEST-02]** Scale-In Multi-Leg Exit Tests
- [x] **[TEST-03]** `BarCollectionService` — Non-Native Timeframe Aggregation Tests
- [x] **[TEST-04]** `BarCollectionService` — Staleness and Dedup Tests
- [x] **[TEST-05]** NaN Warm-Up Propagation Tests
- [x] **[TEST-06]** ViewModel Input Validation Tests (`SniperViewModel`, `BacktestViewModel`)

---

#### [TEST-01] `UpdateDynamicExits` Unit Tests

**File:** `src/TopStepTrader.Tests/Backtest/BacktestMetricsTests.vb` (add here)
**Covers:** `BacktestMetrics.UpdateDynamicExits` (or equivalent in engine)

**Problem:**
Trailing stop ratchet, break-even-on-half-TP, and extend-TP-on-close logic are not tested. Any rounding error in ATR mode is silent. The backtest winner config (Multi-Confluence · Damian · OIL · 5-min · Extend TP ON) relies on all three mechanisms.

**Tests to add:**
- Trailing stop advances SL when price moves in favour by > 1 ATR
- Trailing stop never moves SL against the trade
- Break-even triggers exactly at half-TP distance
- Break-even only fires once per position
- Extend TP fires when close ≥ TP price, advances by `TpAtrMultiple × ATR`
- Extend TP fires at most 3 times per trade (`_tpAdvanceCount` cap)

**Acceptance Criteria:**
- [x] ≥ 8 new `<Fact>` tests covering all three dynamic-exit mechanisms
- [x] Each test uses a synthetic bar series, no real API calls
- [x] All tests pass; total test count updated in `CLAUDE.md`

---

#### [TEST-02] Scale-In Multi-Leg Exit Tests

**File:** `src/TopStepTrader.Tests/Backtest/BacktestMetricsTests.vb`

**Problem:**
Scale-in creates multiple `BacktestTrade` legs in `openLegs`. When exit fires, all legs receive the same `exitPrice`. There are no tests confirming that:
- P&L is summed correctly across legs with different entry prices
- A 2-leg position hitting SL produces the correct aggregate loss
- `PositionGroupId` is consistent across all legs of the same group

**Acceptance Criteria:**
- [ ] ≥ 3 new tests for 2-leg scale-in scenarios (profitable, losing, break-even)
- [ ] Tests verify aggregate P&L and individual leg P&L
- [ ] All tests pass

---

#### [TEST-03] `BarCollectionService` — Non-Native Timeframe Aggregation Tests

**File:** `src/TopStepTrader.Tests/Backtest/BarCollectionServiceTests.vb`

**Problem:**
`BarCollectionService` aggregates 5-min bars → 10-min, 1-hr bars → 2-hr/4-hr in memory. The current 5 test facts do not cover aggregation correctness. The timestamp used for aggregated bars (first bar of the window) may not align to UTC midnight boundaries.

**Tests to add:**
- 5-min source → 10-min: verify 2 consecutive 5-min bars merge into 1 correct 10-min bar (OHLCV, timestamp = first bar)
- 1-hr source → 2-hr: verify pairing logic, including odd-count source bars
- 1-hr source → 4-hr: verify grouping into 4-bar windows

**Acceptance Criteria:**
- [ ] ≥ 3 new aggregation tests using synthetic in-memory bar lists
- [ ] Tests assert OHLCV values and timestamps explicitly
- [ ] All tests pass

---

#### [TEST-04] `BarCollectionService` — Staleness and Dedup Tests

**File:** `src/TopStepTrader.Tests/Backtest/BarCollectionServiceTests.vb`

**Problem:**
The 24-hour staleness check and `INSERT OR IGNORE` deduplication behaviour are not tested. If the cache returns stale bars silently, backtests run on outdated data without any indication.

**Tests to add:**
- Cache hit when latest bar < 24 hours old and span ≥ 80%
- Cache miss (re-download triggered) when latest bar ≥ 24 hours old
- Duplicate bars on re-download do not double-count in the result set

**Acceptance Criteria:**
- [x] ≥ 3 new tests using a fake repository (in-memory or mock)
- [x] All tests pass

---

#### [TEST-05] NaN Warm-Up Propagation Tests

**File:** `src/TopStepTrader.Tests/Backtest/BacktestMetricsTests.vb` or new `BacktestEngineTests.vb`

**Problem:**
No test verifies that feeding a bar series shorter than the indicator warm-up period produces zero trades and no exception. A regression here could silently generate trades with NaN-derived bracket prices.

**Tests to add:**
- Run `EmaRsiWeightedScore` backtest on a 5-bar series (below EMA50 warm-up): expect 0 trades, no exception
- Run `MultiConfluence` on a 25-bar series (below Ichimoku warm-up): expect 0 trades, no exception
- Run any ATR-mode strategy with ATR(14) on a 10-bar series: expect 0 trades, no exception

**Acceptance Criteria:**
- [x] ≥ 3 new tests for under-warm-up scenarios
- [x] All return 0 trades and do not throw
- [x] All tests pass

---

#### [TEST-06] ViewModel Input Validation Tests

**Files:**
- `src/TopStepTrader.Tests/ViewModels/SniperViewModelTests.vb` (4 existing facts)
- `src/TopStepTrader.Tests/ViewModels/BacktestViewModelTests.vb` (create if not present)

**Problem:**
Current ViewModel tests cover basic command wiring. Missing:
- `CanRun` is `False` while `IsWorking` is `True` (prevents double-submission)
- Negative capital input is rejected or clamped
- Invalid contract ID surface a validation message rather than an exception
- Cancellation during a run returns the UI to `Idle` state correctly

**Acceptance Criteria:**
- [x] ≥ 4 new tests across the two ViewModel test files
- [x] Tests do not require WPF dispatcher (use `Application.Current = Nothing` pattern or pure-VM layer)
- [x] All tests pass

---

### CODE QUALITY

- [x] **[QUAL-01]** Extract Duplicated Long/Short Exit Check to Helper Function
- [ ] **[QUAL-02]** Rename `InitialSlAmount` / `InitialTpAmount` to Clarify Semantics
- [ ] **[QUAL-03]** Extract `BacktestView.xaml` DatePicker Styling to Shared Resource Dictionary

---

#### [QUAL-01] Extract Duplicated Long/Short Exit Check to Helper Function

**File:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`

**Problem:**
The `"TakeProfit"` / `"StopLoss"` exit-reason assignment block appears ~10 times — once per strategy type — for both fixed-price and ATR-mode checks. Any change to exit logic (e.g. adding a new exit reason) must be applied in 10 places.

**Change:**
Extract to `BacktestMetrics` (already the home for exit logic):
```vb
Public Shared Function CheckFixedExit(side As String, close As Decimal, sl As Decimal, tp As Decimal) As String
    If side = "Buy" Then
        If close <= sl Then Return "StopLoss"
        If tp > 0D AndAlso close >= tp Then Return "TakeProfit"
    Else
        If close >= sl Then Return "StopLoss"
        If tp > 0D AndAlso close <= tp Then Return "TakeProfit"
    End If
    Return Nothing
End Function
```
Replace all duplicated exit-check blocks in the engine with calls to this helper.

**Acceptance Criteria:**
- [x] `CheckFixedExit` (or equivalent) exists in `BacktestMetrics`
- [x] Duplicate exit-check blocks in `BacktestEngine` replaced with single callsite
- [x] Unit tests added for `CheckFixedExit` (buy SL, buy TP, sell SL, sell TP, no-exit)
- [x] All 221 existing tests still pass

---

#### [QUAL-02] Rename `InitialSlAmount` / `InitialTpAmount` in `BacktestConfiguration`

**File:** Wherever `BacktestConfiguration` is defined (likely `Core/Models/` or `Services/Backtest/`)

**Problem:**
`InitialSlAmount` and `InitialTpAmount` are dollar-delta brackets, not absolute prices. The name "Amount" is ambiguous — it could mean price, dollars, or ticks. Callers reading the engine must track the naming through 3 layers to confirm semantics.

**Change:**
Rename to `SlDollarBracket` and `TpDollarBracket`. Update all references.

**Acceptance Criteria:**
- [ ] `InitialSlAmount` and `InitialTpAmount` no longer exist
- [ ] `SlDollarBracket` and `TpDollarBracket` used consistently in engine, ViewModels, and tests
- [ ] Build passes; all 221 tests still pass
- [ ] CLAUDE.md `StrategyDefaults` table updated to reflect new names if referenced there

---

#### [QUAL-03] Extract `BacktestView.xaml` DatePicker Styling to Shared Resource Dictionary

**File:** `src/TopStepTrader.UI/Views/BacktestView.xaml` (~lines 70–162)

**Problem:**
88 lines of `CalendarButton`, `CalendarDayButton`, and `CalendarItem` style overrides are inlined in `BacktestView.xaml`. The same pattern will need to be duplicated if a DatePicker is added to any other view. The styles also conflict with any future global theme update.

**Change:**
1. Create `src/TopStepTrader.UI/Resources/DatePickerTheme.xaml` as a `ResourceDictionary`.
2. Move all DatePicker-related styles there.
3. Merge into `App.xaml` (`Application.Resources` merged dictionaries) so all views inherit it.
4. Remove the inline styles from `BacktestView.xaml`.

**Acceptance Criteria:**
- [ ] `DatePickerTheme.xaml` exists and is merged in `App.xaml`
- [ ] `BacktestView.xaml` DatePicker section is ≤ 10 lines (just the control declaration)
- [ ] DatePicker renders identically before and after (visual smoke test)
- [ ] Build passes

---

### UI/UX

- [ ] **[UX-01]** Add "(cached)" / "(fresh)" Indicator to Bar Download Status Messages
- [ ] **[UX-02]** Add Visual Feedback / Progress for Bar Download Phase
- [ ] **[UX-03]** Run CSV Export on Background Thread in `QuantLabViewModel`
- [ ] **[UX-04]** Verify Max Effort Cancellation Path Resets UI State Correctly

---

#### [UX-01] Add Cache Status to Bar Download Status Messages

**File:** `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb` — bar download status update path

**Problem:**
When `EnsureBarsAsync` returns a cache hit, the UI shows the same "Downloading bars..." or "Ready" message as a fresh download. Users cannot tell whether they're running against stale cached data or freshly fetched bars.

**Change:**
`BarEnsureResult` (or equivalent return type from `BarCollectionService`) should expose a `WasCacheHit As Boolean` field. The ViewModel uses this to set the status message:
- Cache hit: `"Bars ready (cached {count} bars)"`
- Fresh download: `"Bars ready ({count} bars downloaded)"`

**Acceptance Criteria:**
- [ ] `BarEnsureResult` (or equivalent) exposes `WasCacheHit`
- [ ] Status message reflects cache vs fresh in the UI
- [ ] No change to bar download logic itself
- [ ] All 221 tests still pass

---

#### [UX-02] Add Indeterminate Progress During Bar Download Phase

**File:** `src/TopStepTrader.UI/Views/BacktestView.xaml` and `BacktestViewModel.vb`

**Problem:**
During bar download, the progress bar is indeterminate (`IsIndeterminateProgress = True`) but the status text does not update until download completes. Users see a spinner with no message and may think the app has hung.

**Change:**
Set `StatusText = "Downloading bars for {timeframe}…"` (or similar) at the start of each timeframe's download, updating as each timeframe completes. The existing `ProgressUpdated` event from `BacktestEngine` only fires during engine execution — add a parallel status update mechanism for the download phase.

**Acceptance Criteria:**
- [ ] Status text updates at least once during the download phase (before engine starts)
- [ ] Text is human-readable (e.g. "Downloading 5-min bars for MGC…")
- [ ] No UI freeze during download
- [ ] All 221 tests still pass

---

#### [UX-03] Run CSV Export on Background Thread in `QuantLabViewModel`

**File:** `src/TopStepTrader.UI/ViewModels/QuantLabViewModel.vb` — `ExportCsv()` method

**Problem:**
`ExportCsv()` opens a `SaveFileDialog` and writes the file synchronously on the UI thread. If the user selects a network drive with high latency, the WPF window freezes for the duration of the write.

**Change:**
```vb
Private Async Sub ExportCsvAsync()
    Dim dialog = New SaveFileDialog() With { .Filter = "CSV Files|*.csv" }
    If dialog.ShowDialog() <> True Then Return
    Dim path = dialog.FileName
    Await Task.Run(Sub() File.WriteAllText(path, BuildCsvContent()))
End Sub
```
Replace synchronous `ExportCsv` with `ExportCsvAsync`. Bind the command to the async version.

**Acceptance Criteria:**
- [ ] File write executes off the UI thread
- [ ] UI remains responsive during export
- [ ] `SaveFileDialog` still opens on UI thread (required by WPF)
- [ ] All 221 tests still pass

---

#### [UX-04] Verify Max Effort Cancellation Path Resets UI State Correctly

**File:** `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb` — `ExecuteMaximumEffort` cancellation handling

**Problem:**
If the user cancels a Max Effort run mid-execution, the `CancellationToken` triggers `OperationCanceledException`. It is not confirmed that this path:
- Sets `_isRunning = False` (or `_workPhase = Idle` after [ARCH-03])
- Calls `StopElapsedTimerIfIdle()`
- Leaves the results grid in a consistent (partial) state
- Re-enables the "Maximum Effort!" button

**Change:**
Audit the `Catch ex As OperationCanceledException` block in `ExecuteMaximumEffort`. Add `Finally` clause to guarantee state reset:
```vb
Finally
    _isRunning = False
    NotifyMultiple(...)
    StopElapsedTimerIfIdle()
End Try
```

**Acceptance Criteria:**
- [ ] Cancelling Mid-run returns button to enabled state within 2 seconds
- [ ] ElapsedTimeText clears after cancellation
- [ ] Partial results remain visible in the grid (not cleared)
- [ ] All 221 tests still pass

---

## Completion Summary

| Category | Tickets | Done |
|---|---|---|
| Architecture — ARCH-01 sub-tickets | 6 (01a–01f) | 6 |
| Architecture — ARCH-02 sub-tickets | 5 (02a–02e) | 5 |
| Architecture — other | 1 (ARCH-03) | 1 |
| Bugs | 6 | 6 |
| Test Coverage | 6 | 3 |
| Code Quality | 3 | 1 |
| UI/UX | 4 | 0 |
| **Total** | **31** | **19** |

---

*To start a ticket: load this file + the relevant source file(s) into a fresh session and say "Execute [TICKET-ID]".*
*To close a ticket: change `- [ ]` to `- [x]` and update the Completion Summary count.*
