# TICKET: AI-001 — AI Augmentation, Adaptive Engine & Instrument Expansion
**Status:** Ready for implementation
**Created:** 2026-03-31
**Companion docs:** `AI_AUGMENTATION_PLAN.md`, `ADAPTIVE_ENGINE_PLAN.md`, `TODO.md`

---

## App Context (read before starting)

- **Language:** VB.NET throughout. All 7 projects.
- **Runtime:** .NET 10, x64, Windows. WPF MVVM.
- **DI:** Microsoft.Extensions.DependencyInjection. Composition root: `src/TopStepTrader.UI/Infrastructure/AppBootstrapper.vb`.
- **DI lifetimes:** Singleton = stateless/long-lived services. Scoped = anything touching EF Core DbContext. Transient = execution engines, DiagnosticLogger.
- **Scoped into Transient:** ViewModelLocator creates a per-view `IServiceScope`. Transient engines are resolved inside that scope, so they can receive Scoped repositories. This is intentional and correct.
- **Database:** EF Core + SQLite (`TopStepTrader.db`). DateTimeOffset columns have an EF Core ordering bug — always order by `Id` (INTEGER) not by DateTimeOffset columns. Use `FromSqlInterpolated` for complex queries (see existing `TradeOutcomeRepository` for the pattern).
- **Schema migrations:** No EF migrations. Schema is managed by `EnsureSchemaCurrent()` in `src/TopStepTrader.Data/AppDbContext.vb` using `CREATE TABLE IF NOT EXISTS` and `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`. Add new DDL at the end of that method.
- **Broker:** TopStepX only. `IOrderService` → `ProjectXOrderService`. `TradingSessionContext.ActiveBroker` always returns `BrokerType.TopStepX`.
- **Tick math:** Always use `TickMath.vb` and `contract.GetTickSize(_session.ActiveBroker)` / `contract.GetPointValue(_session.ActiveBroker)`. Never hardcode tick specs.
- **Tests:** xUnit in `src/TopStepTrader.Tests`. Run with `dotnet test --project src/TopStepTrader.Tests/TopStepTrader.Tests.vbproj`. Currently 213 tests, all passing.
- **Build:** `dotnet build` — if Visual Studio has the app open, build the test project only to avoid file lock errors.

---

## Key Existing Fields in `StrategyExecutionEngine` (do not duplicate)

These in-memory fields already exist. Reference them when wiring new features:

```vb
_lastEntryPrice As Decimal          ' entry price
_lastEntrySide As OrderSide         ' Buy / Sell
_lastConfidencePct As Integer       ' signal confidence score
_lastTpPrice As Decimal             ' current TP price (ratchets)
_lastSlPrice As Decimal             ' current SL price (ratchets)
_initialSlPrice As Decimal          ' SL at time of entry (does not change)
_currentAtrValue As Decimal         ' ATR(14) at signal time
_lastAdxValue As Single             ' ADX(14) at signal time
_positionOpenedAt As DateTimeOffset ' when broker confirmed position open
_lastApiPnl As Decimal              ' most recent broker-reported unrealised P&L
_totalDollarPerPoint As Decimal     ' (contracts × tickValue) / tickSize
_strategy As StrategyDefinition     ' deep-copied at StartAsync
_lastBarWasStale As Boolean         ' stale bar guard flag
_openTradeDiagEntry As DiagnosticLogEntry  ' existing diagnostic capture
```

Existing methods to call (do not rewrite):
- `FlattenContractAsync()` — exits the position (cancel brackets + close)
- `ApplyAtrTrailAsync()` — 30-second trail loop (add MAE/MFE tracking here)
- `ResetTrailState()` — called on every position close (add reset of new fields here)
- `Log(message)` — engine log line
- `RaiseEvent ConfidenceUpdated(...)` — fires to ViewModel every tick

---

## Implementation Phases (strict dependency order)

---

## PHASE 0 — Data Foundation
**Must be completed before all other phases. Everything downstream depends on this data.**

### 0.1 Extend `TradeClosedEventArgs` with ExitPrice

**File:** `src/TopStepTrader.Core/Events/SignalGeneratedEventArgs.vb`

Add `ExitPrice As Decimal` to `TradeClosedEventArgs`. Currently only `ExitReason` and `PnL` are carried. Exit price is needed for `TradeLifespanRecords` and R-Multiple calculation.

```vb
Public Class TradeClosedEventArgs
    Inherits EventArgs
    Public ReadOnly Property ExitReason As String
    Public ReadOnly Property PnL As Decimal
    Public ReadOnly Property ExitPrice As Decimal      ' ADD THIS
    Public Sub New(exitReason As String, pnl As Decimal, Optional exitPrice As Decimal = 0D)
        Me.ExitReason = exitReason
        Me.PnL = pnl
        Me.ExitPrice = exitPrice
    End Sub
End Class
```

Update all `RaiseEvent TradeClosed(...)` call sites in `StrategyExecutionEngine` to pass the exit price. The exit price at SL/TP fill can be inferred: `entryPrice ± (pnl / _totalDollarPerPoint)`. At manual close, use `_lastEntryPrice` as fallback if fill price is unavailable.

### 0.2 New Database Tables

**File:** `src/TopStepTrader.Data/AppDbContext.vb` — add to end of `EnsureSchemaCurrent()`

Add these three blocks:

```sql
-- TradeSetupSnapshots: full indicator + context snapshot at signal time
CREATE TABLE IF NOT EXISTS "TradeSetupSnapshots" (
    "Id"                          INTEGER PRIMARY KEY AUTOINCREMENT,
    "TradeOutcomeId"              INTEGER NOT NULL,
    "Tenkan"                      REAL NOT NULL DEFAULT 0,
    "Kijun"                       REAL NOT NULL DEFAULT 0,
    "Cloud1"                      REAL NOT NULL DEFAULT 0,
    "Cloud2"                      REAL NOT NULL DEFAULT 0,
    "Ema21"                       REAL NOT NULL DEFAULT 0,
    "Ema50"                       REAL NOT NULL DEFAULT 0,
    "MacdHist"                    REAL NOT NULL DEFAULT 0,
    "MacdHistPrev"                REAL NOT NULL DEFAULT 0,
    "StochRsiK"                   REAL NOT NULL DEFAULT 0,
    "PlusDI"                      REAL NOT NULL DEFAULT 0,
    "MinusDI"                     REAL NOT NULL DEFAULT 0,
    "AdxValue"                    REAL NOT NULL DEFAULT 0,
    "Rsi14"                       REAL NOT NULL DEFAULT 0,
    "UpPct"                       INTEGER NOT NULL DEFAULT 0,
    "DownPct"                     INTEGER NOT NULL DEFAULT 0,
    "ConditionMask"               INTEGER NOT NULL DEFAULT 0,
    "ConditionCount"              INTEGER NOT NULL DEFAULT 0,
    "ConditionNames"              TEXT NOT NULL DEFAULT '',
    "SignalBarOpen"               REAL NOT NULL DEFAULT 0,
    "SignalBarHigh"               REAL NOT NULL DEFAULT 0,
    "SignalBarLow"                REAL NOT NULL DEFAULT 0,
    "SignalBarClose"              REAL NOT NULL DEFAULT 0,
    "SignalBarRange"              REAL NOT NULL DEFAULT 0,
    "SignalBarIsBullish"          INTEGER NOT NULL DEFAULT 0,
    "AtrValue"                    REAL NOT NULL DEFAULT 0,
    "AtrPercentile"               REAL NOT NULL DEFAULT 0,
    "AtrRegime"                   TEXT NOT NULL DEFAULT '',
    "DayOfWeek"                   INTEGER NOT NULL DEFAULT 0,
    "HourOfDay"                   INTEGER NOT NULL DEFAULT 0,
    "SessionWindow"               TEXT NOT NULL DEFAULT '',
    "NextCalendarEvent"           TEXT NOT NULL DEFAULT '',
    "HoursUntilCalendarEvent"     REAL NOT NULL DEFAULT 0,
    "IsWithinCalendarBlackout"    INTEGER NOT NULL DEFAULT 0,
    "MacroPosture"                TEXT NOT NULL DEFAULT '',
    "MacroSessionQuality"         TEXT NOT NULL DEFAULT '',
    "MacroConfidenceAdjustment"   INTEGER NOT NULL DEFAULT 0,
    "MacroKeyRisk"                TEXT NOT NULL DEFAULT '',
    "AiVerdict"                   TEXT NOT NULL DEFAULT '',
    "AiReasoning"                 TEXT NOT NULL DEFAULT '',
    "EffectiveMinConfidence"      INTEGER NOT NULL DEFAULT 0,
    "StrategyName"                TEXT NOT NULL DEFAULT '',
    "PersonaName"                 TEXT NOT NULL DEFAULT '',
    "AtrTier"                     TEXT NOT NULL DEFAULT '',
    "SlMultiple"                  REAL NOT NULL DEFAULT 0,
    "TpMultiple"                  REAL NOT NULL DEFAULT 0,
    "TimeframeMinutes"            INTEGER NOT NULL DEFAULT 0,
    "InitialSlTicks"              INTEGER NOT NULL DEFAULT 0,
    "InitialTpTicks"              INTEGER NOT NULL DEFAULT 0,
    "InitialSlDollars"            REAL NOT NULL DEFAULT 0,
    "InitialTpDollars"            REAL NOT NULL DEFAULT 0,
    "CreatedAt"                   TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS "IX_TradeSetupSnapshots_TradeOutcomeId"
    ON "TradeSetupSnapshots" ("TradeOutcomeId");
CREATE INDEX IF NOT EXISTS "IX_TradeSetupSnapshots_Strategy_Session"
    ON "TradeSetupSnapshots" ("StrategyName", "SessionWindow", "DayOfWeek");

-- TradeLifespanRecords: what happened during the trade (MAE/MFE, duration, trail counts)
CREATE TABLE IF NOT EXISTS "TradeLifespanRecords" (
    "Id"                              INTEGER PRIMARY KEY AUTOINCREMENT,
    "TradeOutcomeId"                  INTEGER NOT NULL,
    "MaxAdverseExcursionDollars"      REAL NOT NULL DEFAULT 0,
    "MaxAdverseExcursionTicks"        INTEGER NOT NULL DEFAULT 0,
    "MaxFavorableExcursionDollars"    REAL NOT NULL DEFAULT 0,
    "MaxFavorableExcursionTicks"      INTEGER NOT NULL DEFAULT 0,
    "MaeMfRatio"                      REAL NOT NULL DEFAULT 0,
    "MfeTpRatio"                      REAL NOT NULL DEFAULT 0,
    "SlRatchetCount"                  INTEGER NOT NULL DEFAULT 0,
    "TpAdvanceCount"                  INTEGER NOT NULL DEFAULT 0,
    "FreeRideActivated"               INTEGER NOT NULL DEFAULT 0,
    "FreeRideActivatedAtMinutes"      REAL NOT NULL DEFAULT 0,
    "ScaleInCount"                    INTEGER NOT NULL DEFAULT 0,
    "DurationMinutes"                 REAL NOT NULL DEFAULT 0,
    "BarsInTrade"                     INTEGER NOT NULL DEFAULT 0,
    "EntrySessionWindow"              TEXT NOT NULL DEFAULT '',
    "ExitSessionWindow"               TEXT NOT NULL DEFAULT '',
    "CrossedSessionBoundary"          INTEGER NOT NULL DEFAULT 0,
    "ExitBarOpen"                     REAL NOT NULL DEFAULT 0,
    "ExitBarHigh"                     REAL NOT NULL DEFAULT 0,
    "ExitBarLow"                      REAL NOT NULL DEFAULT 0,
    "ExitBarClose"                    REAL NOT NULL DEFAULT 0,
    "ExitAdxValue"                    REAL NOT NULL DEFAULT 0,
    "ExitTenkan"                      REAL NOT NULL DEFAULT 0,
    "ExitKijun"                       REAL NOT NULL DEFAULT 0,
    "ExitMacdHist"                    REAL NOT NULL DEFAULT 0,
    "TrendReversedAtExit"             INTEGER NOT NULL DEFAULT 0,
    "ExitConfidencePct"               INTEGER NOT NULL DEFAULT 0,
    "FinalSlPrice"                    REAL NOT NULL DEFAULT 0,
    "FinalTpPrice"                    REAL NOT NULL DEFAULT 0,
    "PnlDollars"                      REAL NOT NULL DEFAULT 0,
    "PnlTicks"                        INTEGER NOT NULL DEFAULT 0,
    "RMultiple"                       REAL NOT NULL DEFAULT 0,
    "CreatedAt"                       TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS "IX_TradeLifespanRecords_TradeOutcomeId"
    ON "TradeLifespanRecords" ("TradeOutcomeId");

-- AdaptiveParameters: engine's learned adjustments (written by PerformanceTracker + WeeklyDigest)
CREATE TABLE IF NOT EXISTS "AdaptiveParameters" (
    "Id"                INTEGER PRIMARY KEY AUTOINCREMENT,
    "ContractId"        TEXT NOT NULL DEFAULT '',
    "StrategyName"      TEXT NOT NULL DEFAULT '',
    "SessionWindow"     TEXT NOT NULL DEFAULT '',
    "DayOfWeek"         INTEGER NOT NULL DEFAULT 0,
    "AtrRegime"         TEXT NOT NULL DEFAULT '',
    "ParameterType"     TEXT NOT NULL,
    "ParameterValue"    TEXT NOT NULL,
    "TradeCount"        INTEGER NOT NULL DEFAULT 0,
    "WinRate"           REAL NOT NULL DEFAULT 0,
    "SourceType"        TEXT NOT NULL DEFAULT '',
    "ValidFrom"         TEXT NOT NULL,
    "ValidUntil"        TEXT,
    "CreatedAt"         TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS "IX_AdaptiveParameters_Lookup"
    ON "AdaptiveParameters" ("ContractId", "StrategyName", "SessionWindow");
```

Also extend `TradeOutcomes` with new columns:
```sql
-- Extend existing TradeOutcomes table
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "ExitPrice"           REAL NOT NULL DEFAULT 0;
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "RMultiple"           REAL NOT NULL DEFAULT 0;
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiPostMortem"        TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiSetupQuality"      TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiExecutionQuality"  TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiPatternTag"        TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiRecommendation"    TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiPreTradeVerdict"   TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiPreTradeReasoning" TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "MacroPostureAtEntry" TEXT NOT NULL DEFAULT '';
```

Add `DbSet` properties to `AppDbContext`:
```vb
Public Property TradeSetupSnapshots As DbSet(Of TradeSetupSnapshotEntity)
Public Property TradeLifespanRecords As DbSet(Of TradeLifespanRecordEntity)
Public Property AdaptiveParameters As DbSet(Of AdaptiveParametersEntity)
```

### 0.3 New Entity Classes

**Create these files in `src/TopStepTrader.Data/Entities/`:**

- `TradeSetupSnapshotEntity.vb` — mirrors `TradeSetupSnapshots` DDL above, annotated with `<Table("TradeSetupSnapshots")>` and `<Key><DatabaseGenerated(DatabaseGeneratedOption.Identity)>`
- `TradeLifespanRecordEntity.vb` — mirrors `TradeLifespanRecords` DDL
- `AdaptiveParametersEntity.vb` — mirrors `AdaptiveParameters` DDL

### 0.4 New Repository Classes

**Create these files in `src/TopStepTrader.Data/Repositories/`:**

**`TradeSetupSnapshotRepository.vb`:**
```vb
Public Interface ITradeSetupSnapshotRepository
    Function SaveAsync(entity As TradeSetupSnapshotEntity) As Task(Of Integer)
    Function GetByTradeOutcomeIdAsync(tradeOutcomeId As Long) As Task(Of TradeSetupSnapshotEntity)
End Interface
```

**`TradeLifespanRepository.vb`:**
```vb
Public Interface ITradeLifespanRepository
    Function SaveAsync(entity As TradeLifespanRecordEntity) As Task(Of Integer)
    Function UpdateAsync(id As Integer, entity As TradeLifespanRecordEntity) As Task
    Function GetByTradeOutcomeIdAsync(tradeOutcomeId As Long) As Task(Of TradeLifespanRecordEntity)
End Interface
```

**`AdaptiveParametersRepository.vb`:**
```vb
Public Interface IAdaptiveParametersRepository
    Function GetAdjustmentAsync(contractId As String, strategyName As String,
                                sessionWindow As String) As Task(Of Integer)
    Function UpsertAsync(entity As AdaptiveParametersEntity) As Task
    Function GetAllActiveAsync() As Task(Of List(Of AdaptiveParametersEntity))
End Interface
```

Register all three as **Scoped** in `src/TopStepTrader.Data/DataServiceExtensions.vb` (follow existing pattern).

### 0.5 Wire `TradeOutcomeRepository` into `StrategyExecutionEngine` (TODO-001)

**File:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb`

This is the most critical change. The engine needs four new constructor parameters:

```vb
Private ReadOnly _tradeOutcomeRepo As ITradeOutcomeRepository
Private ReadOnly _setupSnapshotRepo As ITradeSetupSnapshotRepository
Private ReadOnly _lifespanRepo As ITradeLifespanRepository
```

**Add to constructor signature** (alongside existing `ILogger`, `IOrderService`, etc.)

**New in-memory fields** (add alongside existing `_lastEntryPrice` etc.):
```vb
Private _openTradeOutcomeId As Long? = Nothing
Private _openLifespanId As Integer? = Nothing
Private _runningMaeDollars As Decimal = 0D
Private _runningMfeDollars As Decimal = 0D
Private _slRatchetCount As Integer = 0
Private _tpAdvanceCount As Integer = 0
Private _freeRideActivatedAt As DateTimeOffset? = Nothing
Private _tradeEntrySessionWindow As String = String.Empty
Private _barsInTrade As Integer = 0
Private _lastSignalArgs As ConfidenceUpdatedEventArgs = Nothing  ' stored at signal time
Private _lastSignalBar As MarketBar = Nothing                    ' bar that triggered signal
```

**On signal (before `PlaceBracketOrdersAsync`):** capture `_lastSignalArgs` from the `ConfidenceUpdatedEventArgs` that triggered the signal. Capture `_lastSignalBar` from the bar collection at that moment.

**On `TradeOpened` path** (after bracket placement confirmed):
```vb
' Save TradeOutcome (open)
Dim outcome = New TradeOutcomeEntity With {
    .ContractId = _strategy.ContractId,
    .Timeframe = _strategy.TimeframeMinutes,
    .SignalType = _lastEntrySide.ToString(),
    .SignalConfidence = _lastConfidencePct,
    .ModelVersion = _strategy.StrategyName,
    .EntryTime = _positionOpenedAt,
    .EntryPrice = _lastEntryPrice,
    .IsOpen = True
}
_openTradeOutcomeId = Await _tradeOutcomeRepo.SaveOutcomeAsync(outcome)

' Save TradeSetupSnapshot from captured _lastSignalArgs + _lastSignalBar
If _openTradeOutcomeId.HasValue AndAlso _lastSignalArgs IsNot Nothing Then
    Dim snap = BuildSetupSnapshot(_openTradeOutcomeId.Value)
    Await _setupSnapshotRepo.SaveAsync(snap)
End If

' Initialise TradeLifespanRecord (open, will be updated throughout)
If _openTradeOutcomeId.HasValue Then
    Dim lifespan = New TradeLifespanRecordEntity With {
        .TradeOutcomeId = _openTradeOutcomeId.Value,
        .EntrySessionWindow = SessionWindowHelper.GetWindow(DateTimeOffset.UtcNow)
    }
    _openLifespanId = Await _lifespanRepo.SaveAsync(lifespan)
End If

_tradeEntrySessionWindow = SessionWindowHelper.GetWindow(DateTimeOffset.UtcNow)
_runningMaeDollars = 0D
_runningMfeDollars = 0D
_barsInTrade = 0
```

**In the 30-second trail loop** (inside `ApplyAtrTrailAsync` or equivalent, after `_lastApiPnl` is updated):
```vb
' MAE/MFE tracking — 4 lines
If _lastApiPnl < _runningMaeDollars Then _runningMaeDollars = _lastApiPnl
If _lastApiPnl > _runningMfeDollars Then _runningMfeDollars = _lastApiPnl
_barsInTrade += 1
' Track free-ride activation
If _freeRideActivatedAt Is Nothing AndAlso _lastSlPrice > _lastEntryPrice Then
    _freeRideActivatedAt = DateTimeOffset.UtcNow
End If
```

Track `_slRatchetCount += 1` wherever SL is ratcheted. Track `_tpAdvanceCount += 1` wherever TP is advanced.

**On `TradeClosed` path** (before `ResetTrailState()`):
```vb
If _openTradeOutcomeId.HasValue Then
    ' Resolve TradeOutcome
    Await _tradeOutcomeRepo.ResolveOutcomeAsync(
        _openTradeOutcomeId.Value,
        DateTimeOffset.UtcNow,
        e.ExitPrice,
        e.PnL,
        e.PnL > 0,
        e.ExitReason)

    ' Compute R-Multiple: PnL / initial dollar risk
    Dim rMultiple As Single = 0
    If _initialSlDollars > 0 Then rMultiple = CSng(e.PnL / _initialSlDollars)

    ' Finalise lifespan record
    If _openLifespanId.HasValue Then
        Dim exitWindow = SessionWindowHelper.GetWindow(DateTimeOffset.UtcNow)
        Dim initialTpDistance = Math.Abs(_lastTpPrice - _lastEntryPrice) * _totalDollarPerPoint / _tickSize
        Dim mfeTpRatio As Single = If(initialTpDistance > 0, CSng(_runningMfeDollars / initialTpDistance), 0)
        Dim initialSlDist = Math.Abs(_initialSlPrice - _lastEntryPrice) * _totalDollarPerPoint / _tickSize
        Dim maeMfRatio As Single = If(initialSlDist > 0, CSng(Math.Abs(_runningMaeDollars) / initialSlDist), 0)
        ' ... build and update TradeLifespanRecordEntity with all fields
        Await _lifespanRepo.UpdateAsync(_openLifespanId.Value, lifespanUpdate)
    End If
End If
```

**In `ResetTrailState()`** — add resets:
```vb
_openTradeOutcomeId = Nothing
_openLifespanId = Nothing
_runningMaeDollars = 0D
_runningMfeDollars = 0D
_slRatchetCount = 0
_tpAdvanceCount = 0
_freeRideActivatedAt = Nothing
_tradeEntrySessionWindow = String.Empty
_barsInTrade = 0
_lastSignalArgs = Nothing
_lastSignalBar = Nothing
```

**Acceptance criteria for Phase 0:**
- After a trade completes, `SELECT * FROM TradeOutcomes WHERE IsOpen=0` returns a row with ExitPrice, PnL, IsWinner populated
- `SELECT * FROM TradeSetupSnapshots` returns a row linked to that trade with all indicator values
- `SELECT * FROM TradeLifespanRecords` returns a row with non-zero MAE/MFE after any trade that moved against position
- All 213 tests still pass

---

## PHASE 1 — Wire Existing AI Infrastructure

### 1.1 Extract `IClaudeReviewService` Interface

**File to create:** `src/TopStepTrader.Core/Interfaces/IClaudeReviewService.vb`

```vb
Public Interface IClaudeReviewService
    Function ReviewStrategyAsync(strategy As StrategyDefinition) As Task(Of String)
    Function ConfidenceCheckAsync(contractId As String) As Task(Of String)
    Function AnalyseBacktestResultsAsync(resultsSummary As String) As Task(Of String)
    Function PreTradeCheckAsync(ctx As PreTradeContext) As Task(Of (Proceed As Boolean, Reasoning As String))
    Function PostTradeAnalysisAsync(ctx As PostTradeContext) As Task(Of PostTradeAnalysisResult)  ' Phase 4
End Interface
```

Have `ClaudeReviewService` implement this interface. Update DI registration in `ServicesExtensions.vb` to register as `IClaudeReviewService`. No call sites need to change yet — they can be updated incrementally.

### 1.2 Confirm `PreTradeCheckAsync` is Wired in `StrategyExecutionEngine`

Read the engine's signal-to-order path. If `PreTradeCheckAsync` is not called before `PlaceBracketOrdersAsync`, wire it. If it is already called, verify it receives a fully populated `PreTradeContext` (including `AtrValue`, `AdxValue`, `ConfidencePct`, `StrategyName`, `PersonaName`).

Add `UseAiPreTradeGate As Boolean = True` to `StrategyDefinition`. Gate call is skipped when False (for PumpNDump/Sniper where sub-minute timing makes an API call impractical). Store the verdict in `_lastAiVerdict As String` and `_lastAiReasoning As String` for later capture into `TradeSetupSnapshot`.

### 1.3 Enrich `PreTradeContext` with Performance Fields

**File:** `src/TopStepTrader.Core/Models/PreTradeContext.vb`

Add:
```vb
Public Property RollingWinRate As Decimal?          ' from TradeOutcomeRepository.GetRollingWinRateAsync(20)
Public Property RecentPnL As Decimal?               ' sum of last 5 closed trades
Public Property ConsecutiveLosses As Integer = 0    ' tracked in engine, reset on win
Public Property TotalTradesThisSession As Integer = 0
Public Property EffectiveMinConfidence As Integer   ' base ± all adjustments (for logging)
```

Track `_consecutiveLosses As Integer` and `_totalTradesThisSession As Integer` as new engine fields. Update on each `TradeClosed` event.

**Acceptance criteria for Phase 1:**
- Pre-trade gate runs before every bracket placement (verify via log line: `↳ AI: PROCEED/VETO — "..."`)
- `IClaudeReviewService` is the registered type; `ClaudeReviewService` is not directly referenced in any ViewModel or engine

---

## PHASE 2 — Instrument Expansion

### 2.1 Add M6B (Micro GBP/USD) to `FavouriteContracts`

**File:** `src/TopStepTrader.Core/Trading/FavouriteContracts.vb`

Add entry to `GetDefaults()`:
```vb
New FavouriteContract With {
    .Name = "GBP/USD",
    .Symbol = "GBPUSD",
    .EToroContractId = "GBPUSD",
    .PxContractId = "CON.F.US.M6B.M26",
    .PxRootSymbol = "M6B",
    .TickSize = 0.0001D,
    .TickValue = 6.25D,
    .PxMinStopDollars = 12.50D,
    .YahooSymbol = "GBPUSD=X",
    .MacroNarrative = "CME Micro GBP/USD. Sensitive to BoE rate decisions, UK inflation (CPI/RPI), and UK political risk. Peak liquidity 07:00–17:00 UTC. Avoid thin market after NY close (21:00 UTC+)."
}
```

**Note:** Contract expiry (M26 = June 2026) will need updating when rolling to next quarterly contract. Follow the same pattern as M6E.

### 2.2 Add `MacroNarrative` to Existing GMET Entry

**File:** `src/TopStepTrader.Core/Trading/FavouriteContracts.vb`

Find the existing GMET entry and add:
```vb
.MacroNarrative = "CME Micro Ethereum. Higher beta than MBT. Same 22:00–23:00 UTC daily CME settlement gap — avoid entries in this window. Sensitive to DeFi protocol events, ETH staking yields, and Layer-2 news. Treat identically to MBT for session blackout."
```

Also add `MacroNarrative` to MYM (already in catalogue):
```vb
.MacroNarrative = "CME Micro Dow Jones. Blue-chip equity index. Lower beta than MES. More sensitive to dividend flows and traditional sector earnings (banks, industrials, energy). Same US session hours as MES (14:30–21:00 UTC). Moderate correlation with MES (~0.85) — distinct enough to monitor independently."
```

### 2.3 Add `MacroNarrative` Property to `FavouriteContract` Model

**File:** `src/TopStepTrader.Core/Trading/FavouriteContracts.vb` (or wherever `FavouriteContract` class is defined)

```vb
''' <summary>
''' Two-sentence instrument macro context injected into every Haiku prompt for this instrument.
''' Describes primary macro drivers, key risk events, and liquid trading hours.
''' </summary>
Public Property MacroNarrative As String = String.Empty
```

### 2.4 Expand Hydra to 2×4 Grid

**File:** `src/TopStepTrader.UI/ViewModels/HydraViewModel.vb`

Replace the current 5-instrument hardcoded roster (lines ~500–526) with an 8-instrument roster:

```vb
' Row 1: Energy / Metals / US Equity Index / Dow
Dim oilEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "OIL")
Dim goldEntry  = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "GOLD.24-7")
Dim spxEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "SPX500")
Dim mymEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "US30")
' Row 2: FX / FX / Crypto / Crypto
Dim fxEurEntry = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "EURUSD")
Dim fxGbpEntry = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "GBPUSD")
Dim btcEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "BTC")
Dim ethEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "ETH")

Dim rosterEntries = {oilEntry, goldEntry, spxEntry, mymEntry, fxEurEntry, fxGbpEntry, btcEntry, ethEntry}
Dim icons = {"🛢️", "🥇", "📈", "🏛️", "💶", "🇬🇧", "₿", "🔷"}

For i = 0 To rosterEntries.Length - 1
    If rosterEntries(i) IsNot Nothing Then
        Assets.Add(New HydraAssetViewModel(rosterEntries(i).Name, icons(i),
                   rosterEntries(i).GetActiveContractId(activeBroker)))
    End If
Next
```

**File:** `src/TopStepTrader.UI/Views/HydraView.xaml`

Change:
```xaml
<UniformGrid Rows="1"/>
```
To:
```xaml
<UniformGrid Rows="2" Columns="4"/>
```

This gives 8 equal tiles in a 2×4 grid. Each tile retains the same physical size as the current 5-tile single-row layout (roughly same area per tile on standard 1920px display).

**File:** `src/TopStepTrader.Services/Background/BarIngestionWorker.vb` (or `appsettings.json` `Ingestion` section)

Add GBPUSD (M6B) to the ingestion symbols list so bars start accumulating immediately.

**Acceptance criteria for Phase 2:**
- Hydra shows 8 tiles in 2 rows without clipping on a 1920×1080 display
- Asset Bassett dropdown includes M6B and GMET
- No compilation errors from missing contract lookups

---

## PHASE 3 — Regime Detection & Session Helpers

### 3.1 New Static Helper: `SessionWindowHelper`

**File:** `src/TopStepTrader.Core/Helpers/SessionWindowHelper.vb`

```vb
Public Module SessionWindowHelper
    Public Function GetWindow(utcNow As DateTimeOffset) As String
        Dim h = utcNow.Hour
        Dim m = utcNow.Minute
        Dim totalMins = h * 60 + m
        Select Case totalMins
            Case 360 To 479  : Return "Pre-London"
            Case 480 To 719  : Return "London"
            Case 720 To 839  : Return "London-US Overlap"
            Case 870 To 1259 : Return "US Session"      ' 14:30–21:00
            Case 1260 To 1319: Return "US Close"        ' 21:00–22:00
            Case 1320 To 1379: Return "CME Crypto Settlement"  ' 22:00–23:00
            Case Else        : Return "Dead Zone"
        End Select
    End Function

    Public Function IsLiquid(utcNow As DateTimeOffset, contractId As String) As Boolean
        Dim window = GetWindow(utcNow)
        ' Crypto: avoid settlement window
        If contractId.Contains("MBT") OrElse contractId.Contains("GMET") Then
            Return window <> "CME Crypto Settlement" AndAlso window <> "Dead Zone"
        End If
        ' FX: avoid dead zone
        If contractId.Contains("M6") OrElse contractId.Contains("MJY") Then
            Return window <> "Dead Zone"
        End If
        ' Equity/commodities: US session only
        Return window = "London-US Overlap" OrElse window = "US Session"
    End Function
End Module
```

### 3.2 New Static Helper: `EconomicCalendar`

**File:** `src/TopStepTrader.Core/Helpers/EconomicCalendar.vb`

Hardcoded weekly schedule. Returns the next upcoming high-impact event and hours until it fires.

```vb
Public Module EconomicCalendar
    ' Each entry: (DayOfWeek, HourUtc, MinuteUtc, EventName)
    Private ReadOnly _weeklySchedule As (Dow As DayOfWeek, H As Integer, M As Integer, Name As String)() = {
        (DayOfWeek.Wednesday, 15, 30, "US Oil Inventories (EIA)"),
        (DayOfWeek.Friday, 13, 30, "US Non-Farm Payrolls"),
        (DayOfWeek.Tuesday, 13, 30, "US CPI"),          ' approx monthly — use as weekly default
        (DayOfWeek.Thursday, 13, 30, "US Jobless Claims"),
        (DayOfWeek.Wednesday, 19, 0, "FOMC Statement")  ' 8× yearly — safe to include weekly
    }

    Public Function GetNextEvent(utcNow As DateTimeOffset) As (Name As String, HoursUntil As Single)
        ' Find the next entry in _weeklySchedule relative to utcNow
        ' Return ("None", 99) if nothing in next 24h
    End Function

    Public Function IsWithinBlackout(utcNow As DateTimeOffset,
                                     blackoutMinutes As Integer = 30) As Boolean
        Dim nextEvent = GetNextEvent(utcNow)
        Return nextEvent.HoursUntil * 60 <= blackoutMinutes
    End Function
End Module
```

### 3.3 New Service: `RegimeDetector`

**File:** `src/TopStepTrader.Services/Market/RegimeDetector.vb`
**Lifetime:** Singleton

```vb
Public Interface IRegimeDetector
    Function GetAtrPercentile(contractId As String, currentAtr As Decimal) As Task(Of Single)
    Function GetAtrRegime(contractId As String, currentAtr As Decimal) As Task(Of String)
End Interface
```

Implementation: queries last 50 bars from the `Bars` SQLite table for `contractId`, computes the percentile of `currentAtr` within the ATR values of those bars (use `(High - Low)` as ATR proxy if pre-computed ATR not stored).

Regime thresholds: < 20th percentile = "Low", 20–80th = "Normal", 80–95th = "Elevated", > 95th = "Spike".

Register as **Singleton** in `ServicesExtensions.vb`.

---

## PHASE 4 — MacroContextService

**File:** `src/TopStepTrader.Services/AI/MacroContextService.vb`
**Lifetime:** Singleton
**Interface:** `IMacroContextService` in `src/TopStepTrader.Core/Interfaces/`

### New Model: `MacroContext`

**File:** `src/TopStepTrader.Core/Models/MacroContext.vb`

```vb
Public Class MacroContext
    Public Property SessionQuality As String = "good"
    Public Property MacroPosture As String = "neutral"
    Public Property RecommendedTier As String = "Standard"
    Public Property ConfidenceAdjustment As Integer = 0
    Public Property KeyRisk As String = String.Empty
    Public Property Notes As String = String.Empty
    Public Property GeneratedAtUtc As DateTimeOffset = DateTimeOffset.UtcNow
End Class
```

### Interface

```vb
Public Interface IMacroContextService
    Function GetContextAsync(contractId As String) As Task(Of MacroContext)
    Sub InvalidateCache(contractId As String)
End Interface
```

### Implementation

- Cache: `Dictionary(Of String, (Context As MacroContext, ExpiresAt As DateTimeOffset))` — Singleton, thread-safe via `SemaphoreSlim`
- TTL: 30 minutes per instrument
- On cache miss: build prompt using `FavouriteContract.MacroNarrative`, `SessionWindowHelper.GetWindow()`, `EconomicCalendar.GetNextEvent()`, and the instrument's current ADX (passed in or use last known value)
- Call Haiku; parse JSON response; store in cache
- On API error: return a default `MacroContext` with all neutral values (fail permissive)

**Haiku prompt (build as a Private method `BuildMacroPrompt`):**

```
System: You are a professional futures trading risk analyst. Return valid JSON only. No prose outside JSON.

User: Assess current trading conditions for {contract.MacroNarrative}
UTC time: {utcNow:yyyy-MM-dd HH:mm} ({dayOfWeek})
Session: {sessionWindow}
Next high-impact event: {nextEvent.Name} in {nextEvent.HoursUntil:F1}h
Within 30-min blackout: {isBlackout}

Return: {"sessionQuality":"good|marginal|avoid","macroPosture":"risk-on|risk-off|neutral",
"recommendedTier":"Tight|Standard|Wide","confidenceAdjustment":<-10 to 10>,
"keyRisk":"<one sentence>","notes":"<one sentence>"}
```

**Acceptance criteria for Phase 4:**
- `MacroContextService` is injected into `StrategyExecutionEngine`; each bar-check logs current `MacroPosture` and `ConfidenceAdjustment`
- Cache is populated; second call within 30 min does not make an API call (verify via log or debug)
- API failure returns neutral context, engine continues normally

---

## PHASE 5 — PerformanceTracker & AiCircuitBreaker

### 5.1 New Service: `PerformanceTracker`

**File:** `src/TopStepTrader.Services/AI/PerformanceTracker.vb`
**Lifetime:** Singleton
**Interface:** `IPerformanceTracker` in `src/TopStepTrader.Core/Interfaces/`

On startup, load recent trade history from `TradeOutcomes` + `TradeSetupSnapshots` to pre-populate rolling windows (last 50 closed trades). On each `RecordOutcome`, update in-memory windows AND persist if a dimension's win rate crosses 40% or 70% threshold by writing/updating an `AdaptiveParameters` row.

```vb
Public Interface IPerformanceTracker
    Sub RecordOutcome(contractId As String, strategyName As String, sessionWindow As String,
                      dayOfWeek As Integer, atrRegime As String,
                      isWinner As Boolean, rMultiple As Single)

    ' Returns integer to add to effective min confidence (positive = raise bar, negative = lower bar).
    ' Returns 0 if insufficient data (< 8 trades in dimension).
    Function GetConfidenceAdjustment(contractId As String, strategyName As String,
                                     sessionWindow As String, dayOfWeek As Integer,
                                     atrRegime As String) As Integer

    Function GetWinRate(contractId As String, strategyName As String,
                        sessionWindow As String) As Single?  ' Nothing if < 8 trades

    Function GetAverageRMultiple(contractId As String, strategyName As String) As Single?
End Interface
```

**Adjustment scale:**
- WR < 30% (n ≥ 10): +20
- WR 30–40% (n ≥ 10): +10
- WR 40–60%: 0
- WR 60–70%: −5
- WR > 70% (n ≥ 10): −8
- n < 8: 0 (insufficient data — no adjustment)
- Sum all applicable dimensions; cap at ±25 total

### 5.2 New Service: `AiCircuitBreaker`

**File:** `src/TopStepTrader.Services/AI/AiCircuitBreaker.vb`
**Lifetime:** Singleton
**Interface:** `IAiCircuitBreaker` in `src/TopStepTrader.Core/Interfaces/`

```vb
Public Interface IAiCircuitBreaker
    Sub RecordOutcome(contractId As String, isWinner As Boolean)
    Function GetConfidenceBoost(contractId As String) As Integer  ' 0 or +15
    Function IsArmed(contractId As String) As Boolean
    Sub Reset(contractId As String)  ' called after winning trade
End Interface
```

Logic: after 3 consecutive losses on a contract, `IsArmed` returns True and `GetConfidenceBoost` returns +15 for 30 minutes. Winning trade resets the counter. Arms again after next 3-loss streak.

### 5.3 Wire Adjustments into Engine

In `StrategyExecutionEngine`, at the point where confidence is compared against `_strategy.MinConfidencePct`:

```vb
Dim baseThreshold = _strategy.MinConfidencePct
Dim macroAdj = If(_macroContext IsNot Nothing, _macroContext.ConfidenceAdjustment, 0)
Dim perfAdj = _perfTracker.GetConfidenceAdjustment(_strategy.ContractId, _strategy.StrategyName,
                  SessionWindowHelper.GetWindow(DateTimeOffset.UtcNow),
                  DateTimeOffset.UtcNow.DayOfWeek, _currentAtrRegime)
Dim circuitAdj = _circuitBreaker.GetConfidenceBoost(_strategy.ContractId)
Dim effectiveThreshold = baseThreshold - macroAdj + perfAdj + circuitAdj

If confidencePct >= effectiveThreshold Then
    ' ... signal passes
End If
```

Log the effective threshold calculation on every signal evaluation:
```
Log($"⚡ Confidence gate: {confidencePct}% vs {effectiveThreshold}% effective (base={baseThreshold}, macro={macroAdj:+0;-0}, perf={perfAdj:+0;-0}, circuit={circuitAdj:+0;-0})")
```

**Acceptance criteria for Phase 5:**
- After a 3-loss streak, log shows `circuit=+15` in the confidence gate line
- After a win, circuit resets, log shows `circuit=0`
- A dimension with 6 trades returns adjustment=0 (insufficient data)

---

## PHASE 6 — Session Briefing

### 6.1 New Service: `SessionBriefingService`

**File:** `src/TopStepTrader.Services/AI/SessionBriefingService.vb`
**Lifetime:** Singleton (caches per contractId+strategyName; invalidates on macro regime change)

Called once during `StrategyExecutionEngine.StartAsync()` after first fresh bar confirmed. Non-blocking (fire-and-forget via `Task.Run`).

**Haiku prompt:**
```
System: You are a professional futures trading analyst. Give a pre-session briefing.
Be concise and actionable. 3–5 sentences maximum. Plain English, no jargon.

User: Pre-session briefing for {contract.Name} ({contract.MacroNarrative})
Strategy: {strategyName} | Persona: {personaName}
Current macro context: {macroContext.Notes} | Session: {sessionWindow}
Indicators: ADX={adxValue:F1}, Tenkan={tenkan:F4}, Kijun={kijun:F4},
            MACD hist={macdHist:F4}, ATR regime={atrRegime}
```

### 6.2 New Event: `SessionBriefingEventArgs`

**File:** `src/TopStepTrader.Core/Events/SignalGeneratedEventArgs.vb`

Add at the end of the file:
```vb
Public Class SessionBriefingEventArgs
    Inherits EventArgs
    Public ReadOnly Property Briefing As String
    Public ReadOnly Property ContractId As String
    Public ReadOnly Property GeneratedAtUtc As DateTimeOffset
    Public Sub New(briefing As String, contractId As String)
        Me.Briefing = briefing
        Me.ContractId = contractId
        Me.GeneratedAtUtc = DateTimeOffset.UtcNow
    End Sub
End Class
```

Engine raises `Public Event SessionBriefing As EventHandler(Of SessionBriefingEventArgs)`.

### 6.3 UI: Collapsible AI Brief Header

**Files:** `HydraView.xaml`, `HydraViewModel.vb`, `HydraAssetViewModel.vb`

Add a collapsible panel above the tile grid in `HydraView.xaml`. Collapsed by default (user clicks to expand). Contains:
- Per-instrument macro regime badge (🟢 / 🟡 / 🔴) bound to `MacroPosture` on each `HydraAssetViewModel`
- `SessionBriefing` text for the most recently started engine
- Last refreshed timestamp

Add to `HydraAssetViewModel`:
```vb
Public Property MacroPosture As String = "neutral"    ' "risk-on" | "risk-off" | "neutral"
Public Property MacroSessionQuality As String = "good"
Public Property SessionBriefing As String = String.Empty
Public Property MacroBadge As String = "🟡"          ' 🟢 risk-on, 🟡 neutral, 🔴 risk-off
```

`HydraAssetViewModel.ApplyMacroContext(ctx As MacroContext)` updates these; `ApplySessionBriefing(briefing As String)` updates `SessionBriefing`.

---

## PHASE 7 — Post-Trade Intelligence

### 7.1 New Model: `PostTradeContext`

**File:** `src/TopStepTrader.Core/Models/PostTradeContext.vb`

```vb
Public Class PostTradeContext
    Public Property ContractId As String
    Public Property ContractDescription As String
    Public Property Side As String
    Public Property EntryTime As DateTimeOffset
    Public Property ExitTime As DateTimeOffset
    Public Property DurationMinutes As Single
    Public Property EntryPrice As Decimal
    Public Property ExitPrice As Decimal
    Public Property PnlDollars As Decimal
    Public Property PnlTicks As Integer
    Public Property RMultiple As Single
    Public Property ExitReason As String
    Public Property InitialSlDollars As Decimal
    Public Property InitialTpDollars As Decimal
    Public Property MaxAdverseExcursionDollars As Decimal
    Public Property MaxFavorableExcursionDollars As Decimal
    Public Property MaeMfRatio As Single
    Public Property MfeTpRatio As Single
    Public Property AiPreTradeVerdict As String
    Public Property AiPreTradeReasoning As String
    Public Property MacroPostureAtEntry As String
    Public Property StrategyName As String
    Public Property SessionWindow As String
    Public Property CrossedSessionBoundary As Boolean
    ' Indicator snapshot at entry
    Public Property EntryAdx As Single
    Public Property EntryTenkan As Decimal
    Public Property EntryKijun As Decimal
    Public Property EntryMacdHist As Single
    Public Property EntryConfidencePct As Integer
    ' Indicator snapshot at exit
    Public Property ExitAdx As Single
    Public Property ExitTenkan As Decimal
    Public Property ExitKijun As Decimal
    Public Property TrendReversedAtExit As Boolean
End Class
```

### 7.2 New Model: `PostTradeAnalysisResult`

**File:** `src/TopStepTrader.Core/Models/PostTradeAnalysisResult.vb`

```vb
Public Class PostTradeAnalysisResult
    Public Property PostMortem As String = String.Empty
    Public Property SetupQuality As String = "B"        ' A / B / C / D
    Public Property ExecutionQuality As String = "Clean" ' Clean / Premature / Late / Overextended
    Public Property PatternTag As String = String.Empty  ' short tag for clustering
    Public Property Recommendation As String = String.Empty
End Class
```

### 7.3 Add `PostTradeAnalysisAsync` to `ClaudeReviewService`

**Haiku prompt (input ~500 tokens, output ~150 tokens):**

```
System: You are a professional futures trading analyst reviewing a completed trade.
Return JSON only. Be concise and specific.

User: Review this completed trade:
Instrument: {contractDescription}
Direction: {side} | Strategy: {strategyName}
Entry: {entryPrice} @ {entryTime:HH:mm UTC} | Exit: {exitPrice} @ {exitTime:HH:mm UTC}
Duration: {durationMinutes:F0} minutes | Session: {sessionWindow}
P&L: {pnlDollars:+$#.00;-$#.00} ({rMultiple:+0.0R;-0.0R})
Exit reason: {exitReason}
MAE: ${maxAdverseExcursionDollars:F2} ({maeMfRatio:P0} of SL distance)
MFE: ${maxFavorableExcursionDollars:F2} ({mfeTpRatio:P0} of TP distance)
Crossed session boundary: {crossedSessionBoundary}
Pre-trade AI verdict: {aiPreTradeVerdict} — "{aiPreTradeReasoning}"
Entry indicators: ADX={entryAdx:F1}, Tenkan {entryKijunRelation}, MACD hist={entryMacdHist:F4}
Exit indicators: ADX={exitAdx:F1}, Trend reversed: {trendReversedAtExit}

Return: {"postMortem":"<2-3 sentences>","setupQuality":"A|B|C|D",
"executionQuality":"Clean|Premature|Late|Overextended",
"patternTag":"<5-word kebab-case tag>","recommendation":"<one actionable sentence>"}
```

Called fire-and-forget from `TradeClosed` handler. Result stored via `_tradeOutcomeRepo.UpdateAiPostMortemAsync()`.

Add `UpdateAiPostMortemAsync(id As Long, result As PostTradeAnalysisResult) As Task` to `ITradeOutcomeRepository` and `TradeOutcomeRepository`.

---

## PHASE 8 — AI Insights View

**New files:**
- `src/TopStepTrader.UI/Views/AiInsightsView.xaml`
- `src/TopStepTrader.UI/ViewModels/AiInsightsViewModel.vb`

Register in `ViewModelLocator.vb`. Add **AI Insights** nav button to `MainWindow.xaml` sidebar under CONFIG section.

**ViewModel exposes:**

```vb
Public Property InstrumentStatuses As ObservableCollection(Of InstrumentStatusRow)
' InstrumentStatusRow: ContractId, SessionQuality, MacroPosture, Badge, LastRefreshed

Public Property CircuitBreakerStatuses As ObservableCollection(Of CircuitBreakerRow)
' CircuitBreakerRow: ContractId, IsArmed, ConsecutiveLosses, NextResetTime

Public Property PerformanceRows As ObservableCollection(Of PerformanceRow)
' PerformanceRow: Strategy, Session, Trades, WinRate, AvgRMultiple

Public Property SuppressedPatterns As ObservableCollection(Of SuppressedPatternRow)
' SuppressedPatternRow: ContractId, Strategy, Session, WinRate, TradeCount, Reason
```

**Refresh command** reloads from `IPerformanceTracker`, `IMacroContextService`, `IAiCircuitBreaker`, and `IAdaptiveParametersRepository`.

---

## PHASE 9 — Weekly Digest Worker

**File:** `src/TopStepTrader.Services/Background/AiWeeklyDigestWorker.vb`
**Lifetime:** Singleton `IHostedService`

Runs Sunday 00:00 UTC. Also triggerable on-demand from AI Insights view via `IWeeklyDigestWorker.RunNowAsync()`.

### 9.1 Digest Data Package Construction

Query these views and pass as structured text to Haiku:

```sql
-- Section 2: Strategy × Session matrix
SELECT s.StrategyName, s.SessionWindow,
       COUNT(*) AS Trades,
       ROUND(AVG(CASE WHEN o.IsWinner=1 THEN 1.0 ELSE 0.0 END)*100,1) AS WinRate,
       ROUND(AVG(l.RMultiple),2) AS AvgR,
       ROUND(AVG(l.MfeTpRatio),2) AS AvgMfeTp
FROM TradeSetupSnapshots s
JOIN TradeOutcomes o ON o.Id = s.TradeOutcomeId
JOIN TradeLifespanRecords l ON l.TradeOutcomeId = s.TradeOutcomeId
WHERE o.IsOpen=0 AND o.CreatedAt >= datetime('now','-7 days')
GROUP BY s.StrategyName, s.SessionWindow HAVING COUNT(*)>=5
ORDER BY WinRate DESC;

-- Section 4: Confluence condition effectiveness
SELECT
  ROUND(AVG(CASE WHEN (s.ConditionMask&1)>0 AND o.IsWinner=1 THEN 1.0
            WHEN (s.ConditionMask&1)>0 THEN 0.0 ELSE NULL END),2) AS C1_WR,
  -- ... repeat for C2–C7
  COUNT(*) AS Trades
FROM TradeSetupSnapshots s
JOIN TradeOutcomes o ON o.Id=s.TradeOutcomeId
WHERE s.StrategyName='Multi-Confluence' AND o.IsOpen=0
  AND o.CreatedAt >= datetime('now','-7 days');

-- Section 5: Session boundary impact
SELECT l.CrossedSessionBoundary,
       COUNT(*) AS Trades,
       ROUND(AVG(CASE WHEN o.IsWinner=1 THEN 1.0 ELSE 0.0 END)*100,1) AS WinRate,
       ROUND(AVG(l.DurationMinutes),0) AS AvgDuration
FROM TradeLifespanRecords l
JOIN TradeOutcomes o ON o.Id=l.TradeOutcomeId
WHERE o.IsOpen=0 AND o.CreatedAt>=datetime('now','-7 days')
GROUP BY l.CrossedSessionBoundary;
```

### 9.2 Haiku Digest Prompt

Uses the full structured format from `ADAPTIVE_ENGINE_PLAN.md` Section 7.1. Returns JSON with: `suppressedPatterns`, `preferredPatterns`, `bracketRecommendations`, `confluenceInsights`, `ruleRecommendations`, `generalNarrative`.

### 9.3 Write Results to `AdaptiveParameters`

For each `suppressedPattern`, upsert an `AdaptiveParameters` row with `ParameterType="ConfidenceBoost"`, `ParameterValue=<boost>`, `SourceType="WeeklyDigest"`.

For each `bracketRecommendation`, store with `ParameterType="AtrTierOverride"`.

**`PerformanceTracker.LoadAdaptiveParameters()`** called on startup to pre-load these into memory.

### 9.4 New `StrategyDefinition` Field

```vb
''' <summary>Maximum trade duration in minutes. 0 = disabled. Applied when cross-session
''' trades consistently underperform (data-driven; set by weekly digest recommendation).</summary>
Public Property MaxTradeDurationMinutes As Integer = 0
```

Engine checks on each tick: if `_openTradeOutcomeId.HasValue AndAlso _strategy.MaxTradeDurationMinutes > 0` and `durationMinutes > _strategy.MaxTradeDurationMinutes`, call `FlattenContractAsync()` with `exitReason="MaxDuration"`.

---

## PHASE 10 — Configurable Hydra Roster (Option B)

*Lower priority. Implement after Phases 0–9 are stable.*

### 10.1 New SQLite Table: `HydraRosterSettings`

```sql
CREATE TABLE IF NOT EXISTS "HydraRosterSettings" (
    "Id"            INTEGER PRIMARY KEY AUTOINCREMENT,
    "Position"      INTEGER NOT NULL,
    "ContractId"    TEXT NOT NULL,
    "IsEnabled"     INTEGER NOT NULL DEFAULT 1
);
```

### 10.2 Dynamic Grid Calculation

Replace fixed `Rows="2" Columns="4"` with calculated values based on `Assets.Count`:

| Count | Rows | Columns |
|---|---|---|
| 1–5 | 1 | 5 |
| 6–8 | 2 | 4 |
| 9–10 | 2 | 5 |

Bind `UniformGrid.Rows` and `UniformGrid.Columns` to `HydraViewModel.GridRows` and `GridColumns` computed properties.

### 10.3 Roster Configurator UI

Add a "Configure Instruments" button to `HydraView.xaml` that opens a small settings panel. Shows all `FavouriteContracts` for TopStepX as a checklist. User selects up to 10. On confirm, saves to `HydraRosterSettings` and rebuilds `Assets`.

---

## Test Requirements

Add tests for each new service in `src/TopStepTrader.Tests/`:

| Test class | What to test |
|---|---|
| `SessionWindowHelperTests` | All UTC hour boundaries; crypto settlement window; FX vs equity liquid hours |
| `EconomicCalendarTests` | Next event lookup; blackout detection; week rollover |
| `RegimeDetectorTests` | ATR percentile with known bar history; regime boundaries |
| `AiCircuitBreakerTests` | Arms after 3 losses; boost = +15 when armed; resets on win; re-arms after next streak |
| `PerformanceTrackerTests` | < 8 trades → 0 adjustment; 10 losses → +20; 10 wins → −8; cap at ±25 |
| `TradeSetupSnapshotRepositoryTests` | Round-trip save/retrieve |
| `TradeLifespanRepositoryTests` | Round-trip save/update |
| `IClaudeReviewServiceTests` | `FakeClaudeReviewService` returns deterministic PROCEED/VETO; post-trade result deserialises correctly |

All new service tests use fake/stub implementations — no live API calls or live database in tests. Follow the `FakeCatalogLocalOnly` pattern already established in `TopStepXTickTests.vb`.

---

## DI Registration Checklist

Add to `ServicesExtensions.vb`:
```vb
' Singletons
services.AddSingleton(Of IRegimeDetector, RegimeDetector)()
services.AddSingleton(Of IMacroContextService, MacroContextService)()
services.AddSingleton(Of ISessionBriefingService, SessionBriefingService)()
services.AddSingleton(Of IPerformanceTracker, PerformanceTracker)()
services.AddSingleton(Of IAiCircuitBreaker, AiCircuitBreaker)()
services.AddHostedService(Of AiWeeklyDigestWorker)()

' Scoped (already registered — verify)
services.AddScoped(Of IClaudeReviewService, ClaudeReviewService)()
```

Add to `DataServiceExtensions.vb`:
```vb
services.AddScoped(Of ITradeSetupSnapshotRepository, TradeSetupSnapshotRepository)()
services.AddScoped(Of ITradeLifespanRepository, TradeLifespanRepository)()
services.AddScoped(Of IAdaptiveParametersRepository, AdaptiveParametersRepository)()
```

---

## Implementation Health Checks

After each phase, verify:

1. **`dotnet test`** — all tests pass (213 baseline + new)
2. **App launches** — no DI resolution exceptions on startup
3. **Engine starts** — log shows no null reference exceptions in first 60 seconds
4. **Phase 0 specific:** After running one trade to completion, run:
   ```sql
   SELECT COUNT(*) FROM TradeOutcomes WHERE IsOpen=0;
   SELECT COUNT(*) FROM TradeSetupSnapshots;
   SELECT COUNT(*) FROM TradeLifespanRecords;
   ```
   All three should return 1 for the first completed trade.
5. **Phase 5 specific:** Start an engine; check log for confidence gate line showing all four components (base, macro, perf, circuit).

---

## Known Gotchas

| Issue | Detail |
|---|---|
| EF Core DateTimeOffset ordering | Always order by `Id` (INTEGER), never by DateTimeOffset columns. Use `FromSqlInterpolated` for raw queries if EF translator fails. |
| StrategyExecutionEngine is Transient | Scoped repos can be injected because ViewModelLocator creates a per-view scope. Do not inject into a Singleton — that would capture a scoped service in a singleton (DI violation). |
| Contract ID rolling | `PxContractId` contains the expiry month (e.g. `M26` = June 2026). When the front-month rolls, update `FavouriteContracts`. `TopStepXInstrumentCatalog` fetches live contract IDs with 15-min TTL — this is the authoritative source at runtime. |
| M6B min stop dollars | Verify `PxMinStopDollars` for M6B from a live ProjectX API call before first trade. The value above ($12.50) is an estimate based on M6E parity. |
| MacroNarrative field | Must not be null. Add null guard in `MacroContextService.BuildMacroPrompt` — use `"Futures instrument."` as fallback if empty. |
| GMET settlement window | Same 22:00–23:00 UTC gap as MBT. `IsInEquitySessionBlackout()` currently only checks equity futures. Verify that crypto settlement blackout is handled by `SessionWindowHelper.IsLiquid()` rather than the equity blackout method. |
| UniformGrid column stretch | With `Rows="2" Columns="4"`, if `Assets.Count < 8`, empty cells may appear. Either always populate exactly 8 tiles (pad with placeholder) or switch to `WrapPanel` with fixed tile width. |

---

*This ticket is self-contained. Read `CLAUDE.md` for build commands and project structure, then start with Phase 0.*
*Last updated: 2026-03-31*
