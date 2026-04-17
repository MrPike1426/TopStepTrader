# TopStepTrader — Data Collection & Adaptive Engine Plan
## Building a Trading Engine That Learns From Its Own History

**Version:** 1.0
**Date:** 2026-03-31
**Status:** Approved for implementation
**Companion document:** `AI_AUGMENTATION_PLAN.md`

---

## 1. Executive Summary

The existing `AI_AUGMENTATION_PLAN.md` defines *what* Haiku should do. This document defines *what data must be collected* to make those decisions meaningful, and how the engine can adapt its own behaviour based on patterns discovered in that data.

The core insight from reviewing the codebase: **the infrastructure is almost entirely built but critically under-connected.** The `TradeOutcomeEntity`, `TradeOutcomeRepository`, and `DiagnosticLogger` exist and are ready. What is missing is:

1. The engine never writes to the database on trade open or close (TODO-001)
2. The data that *is* captured is too thin — entry/exit only, no lifespan, no indicator breakdown, no MAE/MFE
3. There is no feedback path from outcomes back into engine behaviour

Fix these three things and you have the foundation of a genuinely adaptive system.

---

## 2. Current Data Capture — Honest Assessment

### 2.1 What Exists Today

| Data Point | Where | Queryable? |
|---|---|---|
| Entry price, side, confidence | `_lastEntryPrice` etc. (in-memory) | ❌ Lost on session end |
| Full indicator snapshot at signal | `DiagnosticLogger` JSONL files | ❌ Flat files only |
| SL/TP levels at entry | `_lastSlPrice`, `_lastTpPrice` (in-memory) | ❌ Lost on session end |
| ATR value at entry | `_currentAtrValue` (in-memory) | ❌ Lost on session end |
| ADX value at signal | `_lastAdxValue` (in-memory) | ❌ Lost on session end |
| Exit reason | `TradeClosedEventArgs.ExitReason` | ❌ Never persisted |
| P&L | `TradeClosedEventArgs.PnL` | ❌ Never persisted |
| Every bar ever polled | `Bars` SQLite table | ✅ Queryable |
| Every order placed | `Orders` SQLite table | ✅ Queryable |
| Strategy configuration | `StrategyDefinition` (in-memory) | ❌ Not persisted with trade |

### 2.2 What the Trade Outcome Table Stores Today

**Answer: Nothing.** `TradeOutcomeRepository.SaveOutcomeAsync()` and `ResolveOutcomeAsync()` are never called by `StrategyExecutionEngine`. The `TradeOutcomes` table is empty in every live session.

### 2.3 The Consequence

Without persisted trade data, the AI augmentation plan is operating blind. Haiku's weekly digest has nothing to analyse. The circuit breaker has no loss history. The rolling win rate returns `Nothing`. Pattern suppression cannot build a suppression list. The system is making AI calls with zero institutional memory.

**Every trade that closes without being recorded is a lost data point that can never be recovered.**

---

## 3. The Complete Data Taxonomy

Everything the adaptive engine needs, organised by when it is collected.

### 3.1 At Signal Generation (Pre-Entry)

The moment confluence scoring passes the minimum threshold — before the AI gate, before order placement.

**Category A: Technical Indicator Snapshot**

| Field | Source | Notes |
|---|---|---|
| `Tenkan` | `ConfidenceUpdatedEventArgs` | Ichimoku conversion line |
| `Kijun` | `ConfidenceUpdatedEventArgs` | Ichimoku base line |
| `Cloud1`, `Cloud2` | `ConfidenceUpdatedEventArgs` | Senkou Span A/B |
| `Ema21`, `Ema50` | `ConfidenceUpdatedEventArgs` | Trend EMAs |
| `MacdHist`, `MacdHistPrev` | `ConfidenceUpdatedEventArgs` | MACD momentum |
| `StochRsiK` | `ConfidenceUpdatedEventArgs` | Momentum oscillator |
| `PlusDI`, `MinusDI` | `ConfidenceUpdatedEventArgs` | Directional movement |
| `AdxValue` | `ConfidenceUpdatedEventArgs` | Trend strength |
| `Rsi14` | `ConfidenceUpdatedEventArgs` | RSI (EmaRsi strategy) |
| `VidyaValue`, `CmoValue`, `DeltaVol` | `ConfidenceUpdatedEventArgs` | VIDYA strategy |
| `LongCount`, `ShortCount` | `ConfidenceUpdatedEventArgs` | Conditions passed |
| `TotalConditions` | `ConfidenceUpdatedEventArgs` | e.g. 7 for MultiConfluence |
| `UpPct`, `DownPct` | `ConfidenceUpdatedEventArgs` | Final confidence score |

**Category B: Confluence Condition Breakdown**

*This is new — not currently captured anywhere.* For Multi-Confluence (7 conditions), store which individual conditions passed as a bitmask. This enables analysis of which conditions actually predict success.

| Field | Type | Notes |
|---|---|---|
| `ConditionMask` | Integer | Bit flags: bit 0=C1, bit 1=C2, ... bit 6=C7 |
| `ConditionCount` | Integer | Number of conditions that passed (LongCount or ShortCount) |
| `ConditionNames` | String | Comma-separated list of which specific conditions passed |

**Category C: Signal Bar (the bar that triggered the signal)**

| Field | Type | Notes |
|---|---|---|
| `SignalBarOpen` | Decimal | Open price of the bar that fired the signal |
| `SignalBarHigh` | Decimal | |
| `SignalBarLow` | Decimal | |
| `SignalBarClose` | Decimal | Entry price reference |
| `SignalBarVolume` | Long | Volume at signal (where available) |
| `SignalBarRange` | Decimal | High − Low |
| `SignalBarBodySize` | Decimal | |Cl − Op| |
| `SignalBarIsBullish` | Boolean | Close > Open |

**Category D: Volatility Regime**

| Field | Type | Notes |
|---|---|---|
| `AtrValue` | Decimal | ATR(14) at signal time |
| `AtrPercentile` | Single | ATR relative to last 50 bars (0–100). 80+ = elevated, 95+ = spike |
| `AtrRegime` | String | "Low" / "Normal" / "Elevated" / "Spike" |

**Category E: Session and Calendar Context**

| Field | Type | Notes |
|---|---|---|
| `SignalUtcTime` | DateTimeOffset | When the signal fired |
| `DayOfWeek` | Integer | 1=Monday … 5=Friday |
| `HourOfDay` | Integer | UTC hour 0–23 |
| `SessionWindow` | String | "London" / "London-US Overlap" / "US Session" / etc. |
| `NextCalendarEvent` | String | e.g. "US NFP" |
| `HoursUntilCalendarEvent` | Single | |
| `IsWithinCalendarBlackout` | Boolean | Within 30 min of high-impact event |

**Category F: Macro Context (from MacroContextService)**

| Field | Type | Notes |
|---|---|---|
| `MacroPosture` | String | "risk-on" / "risk-off" / "neutral" |
| `MacroSessionQuality` | String | "good" / "marginal" / "avoid" |
| `MacroConfidenceAdjustment` | Integer | ±10 applied at this signal |
| `MacroKeyRisk` | String | AI-generated risk note |

**Category G: AI Pre-Trade Gate**

| Field | Type | Notes |
|---|---|---|
| `AiVerdict` | String | "PROCEED" / "VETO" / "SKIPPED" (gate disabled) |
| `AiReasoning` | String | Haiku's 1–2 sentence rationale |
| `EffectiveMinConfidence` | Integer | Actual threshold applied (base ± macro adjustment ± circuit breaker) |

**Category H: Strategy Identity**

| Field | Type | Notes |
|---|---|---|
| `StrategyName` | String | e.g. "Multi-Confluence" |
| `PersonaName` | String | "Lewis" / "Damian" / "Joe" |
| `AtrTier` | String | "Tight" / "Standard" / "Wide" |
| `SlMultiple` | Single | SlMultipleOfN at signal time |
| `TpMultiple` | Single | TpMultipleOfN at signal time |
| `TimeframeMinutes` | Integer | Bar timeframe |
| `InitialSlTicks` | Integer | Calculated SL in ticks |
| `InitialTpTicks` | Integer | Calculated TP in ticks |
| `InitialSlDollars` | Decimal | Dollar risk at entry |
| `InitialTpDollars` | Decimal | Dollar target at entry |

---

### 3.2 During the Trade (Lifespan)

Data about what happens between entry and exit. This is the most under-appreciated dataset — it tells you not just whether a trade won but *how* it won and *how close* it came to losing.

| Field | Type | Notes |
|---|---|---|
| `MaxAdverseExcursion` | Decimal | MAE: worst unrealised loss in dollars during the trade |
| `MaxAdverseExcursionTicks` | Integer | MAE expressed in ticks |
| `MaxFavorableExcursion` | Decimal | MFE: best unrealised profit in dollars during the trade |
| `MaxFavorableExcursionTicks` | Integer | MFE expressed in ticks |
| `MaeMfRatio` | Single | MAE / initial SL distance. >0.8 = nearly stopped out |
| `MfeTpRatio` | Single | MFE / initial TP distance. 1.0 = price fully reached TP |
| `SlRatchetCount` | Integer | How many times the SL trailed forward |
| `TpAdvanceCount` | Integer | How many times TP was extended (ExtendTpOnClose) |
| `FreeRideActivated` | Boolean | Did SL advance to breakeven? |
| `FreeRideActivatedAtMinutes` | Single | How many minutes into the trade free-ride activated |
| `ScaleInCount` | Integer | Number of scale-ins added |
| `DurationMinutes` | Single | Total trade duration |
| `EntrySessionWindow` | String | Session at entry |
| `ExitSessionWindow` | String | Session at exit (may differ from entry) |
| `CrossedSessionBoundary` | Boolean | Entry and exit in different session windows |
| `BarsInTrade` | Integer | Number of bars from entry bar to exit bar |

**How to collect MAE/MFE:** In the existing 30-second trail loop (`ApplyAtrTrailAsync` or equivalent), after retrieving the latest bar, update running min/max against the open position. This is a simple two-line addition — track `_runningMae` and `_runningMfe` alongside `_lastSlPrice` and `_lastTpPrice`.

---

### 3.3 At Trade Close

Conditions at the moment of exit — the mirror image of the entry snapshot.

| Field | Type | Notes |
|---|---|---|
| `ExitPrice` | Decimal | Actual fill price (inferred from entry + PnL if not directly available) |
| `ExitTime` | DateTimeOffset | UTC timestamp of close |
| `ExitReason` | String | "TP" / "SL" / "Reversal" / "Manual" / "Closed" |
| `ExitBarOpen` | Decimal | Bar that closed the trade |
| `ExitBarHigh` | Decimal | |
| `ExitBarLow` | Decimal | |
| `ExitBarClose` | Decimal | |
| `ExitAdxValue` | Single | ADX at close — was trend still valid? |
| `ExitTenkan` | Decimal | |
| `ExitKijun` | Decimal | |
| `ExitMacdHist` | Single | Momentum at exit |
| `TrendReversedAtExit` | Boolean | Was trend direction opposite at exit vs entry? |
| `ExitConfidencePct` | Integer | What was the confidence score on the exit bar? |
| `FinalSlPrice` | Decimal | Where the SL actually rested at close |
| `FinalTpPrice` | Decimal | Where the TP actually rested at close |
| `PnlDollars` | Decimal | Final P&L in dollars |
| `PnlTicks` | Integer | Final P&L in ticks |
| `IsWinner` | Boolean | PnL > 0 |
| `RMultiple` | Single | PnL / initial dollar risk. 1.0R = made exactly what was risked |

**The R-Multiple** is the single most important derived metric. A trade that wins $30 with a $15 SL is a +2R winner. Consistent +2R outcomes confirm the strategy is working as designed. Consistent +0.3R winners (scraping small profits, losing big) reveal a broken TP strategy. This number belongs in every post-trade analysis.

---

### 3.4 Post-Trade AI Analysis

Generated by Haiku after close, stored permanently with the trade record.

| Field | Type | Notes |
|---|---|---|
| `AiPostMortem` | String | 2–3 sentence Haiku explanation of the trade outcome |
| `AiSetupQuality` | String | "A" / "B" / "C" / "D" — Haiku grades the original setup |
| `AiExecutionQuality` | String | "Clean" / "Premature" / "Late" / "Overextended" |
| `AiPatternTag` | String | Short tag for clustering (e.g. "strong-trend-clean-tp", "reversal-stopped-thin-session") |
| `AiRecommendation` | String | One-line actionable suggestion for next similar setup |

The `AiPatternTag` becomes the key clustering field. Over 50–100 trades, distinct pattern clusters emerge. Haiku's weekly digest can then say: *"You have 23 trades tagged 'thin-session-marginal-exit'. Win rate: 31%. Suppressing this pattern adds +12% to overall win rate."*

---

## 4. New Database Schema

### 4.1 `TradeSetupSnapshots` Table (new)

One row per trade. Stores the complete signal-time context.

```sql
CREATE TABLE IF NOT EXISTS "TradeSetupSnapshots" (
    "Id"                          INTEGER PRIMARY KEY AUTOINCREMENT,
    "TradeOutcomeId"              INTEGER NOT NULL,           -- FK to TradeOutcomes

    -- Indicators
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

    -- Confluence breakdown
    "ConditionMask"               INTEGER NOT NULL DEFAULT 0,
    "ConditionCount"              INTEGER NOT NULL DEFAULT 0,
    "ConditionNames"              TEXT NOT NULL DEFAULT '',

    -- Signal bar
    "SignalBarOpen"               REAL NOT NULL DEFAULT 0,
    "SignalBarHigh"               REAL NOT NULL DEFAULT 0,
    "SignalBarLow"                REAL NOT NULL DEFAULT 0,
    "SignalBarClose"              REAL NOT NULL DEFAULT 0,
    "SignalBarRange"              REAL NOT NULL DEFAULT 0,
    "SignalBarIsBullish"          INTEGER NOT NULL DEFAULT 0,

    -- Volatility regime
    "AtrValue"                    REAL NOT NULL DEFAULT 0,
    "AtrPercentile"               REAL NOT NULL DEFAULT 0,
    "AtrRegime"                   TEXT NOT NULL DEFAULT '',

    -- Session and calendar
    "DayOfWeek"                   INTEGER NOT NULL DEFAULT 0,
    "HourOfDay"                   INTEGER NOT NULL DEFAULT 0,
    "SessionWindow"               TEXT NOT NULL DEFAULT '',
    "NextCalendarEvent"           TEXT NOT NULL DEFAULT '',
    "HoursUntilCalendarEvent"     REAL NOT NULL DEFAULT 0,
    "IsWithinCalendarBlackout"    INTEGER NOT NULL DEFAULT 0,

    -- Macro context
    "MacroPosture"                TEXT NOT NULL DEFAULT '',
    "MacroSessionQuality"         TEXT NOT NULL DEFAULT '',
    "MacroConfidenceAdjustment"   INTEGER NOT NULL DEFAULT 0,
    "MacroKeyRisk"                TEXT NOT NULL DEFAULT '',

    -- AI pre-trade gate
    "AiVerdict"                   TEXT NOT NULL DEFAULT '',
    "AiReasoning"                 TEXT NOT NULL DEFAULT '',
    "EffectiveMinConfidence"      INTEGER NOT NULL DEFAULT 0,

    -- Strategy identity
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
```

### 4.2 `TradeLifespanRecords` Table (new)

One row per trade. Populated incrementally during the trade's life, finalised at close.

```sql
CREATE TABLE IF NOT EXISTS "TradeLifespanRecords" (
    "Id"                          INTEGER PRIMARY KEY AUTOINCREMENT,
    "TradeOutcomeId"              INTEGER NOT NULL,

    -- Excursion analysis
    "MaxAdverseExcursionDollars"  REAL NOT NULL DEFAULT 0,
    "MaxAdverseExcursionTicks"    INTEGER NOT NULL DEFAULT 0,
    "MaxFavorableExcursionDollars" REAL NOT NULL DEFAULT 0,
    "MaxFavorableExcursionTicks"  INTEGER NOT NULL DEFAULT 0,
    "MaeMfRatio"                  REAL NOT NULL DEFAULT 0,
    "MfeTpRatio"                  REAL NOT NULL DEFAULT 0,

    -- Trail behaviour
    "SlRatchetCount"              INTEGER NOT NULL DEFAULT 0,
    "TpAdvanceCount"              INTEGER NOT NULL DEFAULT 0,
    "FreeRideActivated"           INTEGER NOT NULL DEFAULT 0,
    "FreeRideActivatedAtMinutes"  REAL NOT NULL DEFAULT 0,
    "ScaleInCount"                INTEGER NOT NULL DEFAULT 0,

    -- Duration and session
    "DurationMinutes"             REAL NOT NULL DEFAULT 0,
    "BarsInTrade"                 INTEGER NOT NULL DEFAULT 0,
    "EntrySessionWindow"          TEXT NOT NULL DEFAULT '',
    "ExitSessionWindow"           TEXT NOT NULL DEFAULT '',
    "CrossedSessionBoundary"      INTEGER NOT NULL DEFAULT 0,

    -- Exit conditions
    "ExitBarOpen"                 REAL NOT NULL DEFAULT 0,
    "ExitBarHigh"                 REAL NOT NULL DEFAULT 0,
    "ExitBarLow"                  REAL NOT NULL DEFAULT 0,
    "ExitBarClose"                REAL NOT NULL DEFAULT 0,
    "ExitAdxValue"                REAL NOT NULL DEFAULT 0,
    "ExitTenkan"                  REAL NOT NULL DEFAULT 0,
    "ExitKijun"                   REAL NOT NULL DEFAULT 0,
    "ExitMacdHist"                REAL NOT NULL DEFAULT 0,
    "TrendReversedAtExit"         INTEGER NOT NULL DEFAULT 0,
    "ExitConfidencePct"           INTEGER NOT NULL DEFAULT 0,
    "FinalSlPrice"                REAL NOT NULL DEFAULT 0,
    "FinalTpPrice"                REAL NOT NULL DEFAULT 0,

    -- Outcome
    "PnlDollars"                  REAL NOT NULL DEFAULT 0,
    "PnlTicks"                    INTEGER NOT NULL DEFAULT 0,
    "RMultiple"                   REAL NOT NULL DEFAULT 0,

    "CreatedAt"                   TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS "IX_TradeLifespanRecords_TradeOutcomeId"
    ON "TradeLifespanRecords" ("TradeOutcomeId");
```

### 4.3 `TradeOutcomeEntity` — Additional Columns (extend existing)

Add to the existing `TradeOutcomes` table via `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`:

```sql
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiPostMortem"        TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiSetupQuality"      TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiExecutionQuality"  TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiPatternTag"        TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "AiRecommendation"    TEXT NOT NULL DEFAULT '';
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "RMultiple"           REAL NOT NULL DEFAULT 0;
ALTER TABLE "TradeOutcomes" ADD COLUMN IF NOT EXISTS "ExitPrice"           REAL NOT NULL DEFAULT 0;
```

### 4.4 `AdaptiveParameters` Table (new)

The engine's learned adjustments. Written by the weekly digest worker and the rolling performance tracker.

```sql
CREATE TABLE IF NOT EXISTS "AdaptiveParameters" (
    "Id"                    INTEGER PRIMARY KEY AUTOINCREMENT,
    "ContractId"            TEXT NOT NULL DEFAULT '',      -- '' = applies to all
    "StrategyName"          TEXT NOT NULL DEFAULT '',      -- '' = applies to all
    "SessionWindow"         TEXT NOT NULL DEFAULT '',      -- '' = applies to all
    "DayOfWeek"             INTEGER NOT NULL DEFAULT 0,   -- 0 = applies to all
    "AtrRegime"             TEXT NOT NULL DEFAULT '',      -- '' = applies to all
    "ParameterType"         TEXT NOT NULL,                 -- "ConfidenceBoost" | "SessionSuppress" | "AtrTierOverride" | "PatternSuppress"
    "ParameterValue"        TEXT NOT NULL,                 -- JSON or scalar string
    "TradeCount"            INTEGER NOT NULL DEFAULT 0,   -- how many trades support this
    "WinRate"               REAL NOT NULL DEFAULT 0,      -- win rate that produced this
    "SourceType"            TEXT NOT NULL DEFAULT '',      -- "WeeklyDigest" | "RollingStats" | "Manual"
    "ValidFrom"             TEXT NOT NULL,
    "ValidUntil"            TEXT,                          -- null = no expiry
    "CreatedAt"             TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS "IX_AdaptiveParameters_Lookup"
    ON "AdaptiveParameters" ("ContractId", "StrategyName", "SessionWindow");
```

---

## 5. The Adaptive Engine Architecture

### 5.1 Overview — The Learning Loop

```
┌─────────────────────────────────────────────────────────────────┐
│                     THE LEARNING LOOP                           │
│                                                                 │
│   Signal fires                                                  │
│       │                                                         │
│       ▼                                                         │
│   [1] COLLECT — TradeSetupSnapshot saved to SQLite              │
│       │                                                         │
│       ▼                                                         │
│   [2] CONSULT — PerformanceTracker looks up historical          │
│                 win rate for this exact (strategy × instrument  │
│                 × session × day × ATR regime) combination       │
│       │                                                         │
│       ▼                                                         │
│   [3] GATE — Effective confidence threshold adjusted by:        │
│                 • MacroContext adjustment (±10)                  │
│                 • Historical win rate adjustment (±15)          │
│                 • AiCircuitBreaker boost (0 or +15)             │
│                 • PatternSuppression boost (0 or +20)           │
│       │                                                         │
│       ▼                                                         │
│   [4] PROCEED or VETO                                           │
│       │ PROCEED                                                  │
│       ▼                                                         │
│   [5] TRADE RUNS — MAE/MFE tracked every 30s tick              │
│       │                                                         │
│       ▼                                                         │
│   [6] CLOSE — TradeLifespanRecord finalised, TradeOutcome       │
│               resolved in database                              │
│       │                                                         │
│       ▼                                                         │
│   [7] ANALYSE — PostTradeAnalysisAsync (async, non-blocking)    │
│                 Haiku grades setup, assigns pattern tag         │
│       │                                                         │
│       ▼                                                         │
│   [8] UPDATE — PerformanceTracker rolling window updated        │
│                AdaptiveParameters table updated if threshold    │
│                crossed (< 40% WR or > 70% WR in last 10)       │
│       │                                                         │
│       ▼                                                         │
│   [9] WEEKLY — AiWeeklyDigestWorker: deeper pattern analysis,  │
│                suppressedPatterns list refreshed, preferred     │
│                patterns reinforced                              │
│       │                                                         │
│       └──────────────── feeds back into step [3] ──────────────┘
```

### 5.2 New Service: `PerformanceTracker`

**File:** `src/TopStepTrader.Services/AI/PerformanceTracker.vb`
**Lifetime:** Singleton
**Interface:** `IPerformanceTracker`

This is the core adaptive intelligence. It maintains rolling performance windows across every relevant dimension and computes the effective confidence adjustment for any given setup.

```vb
Public Interface IPerformanceTracker
    ' Called at trade close. Updates rolling windows.
    Sub RecordOutcome(snapshot As TradeSetupSnapshot, lifespan As TradeLifespanRecord,
                      isWinner As Boolean, rMultiple As Single)

    ' Called at signal time. Returns suggested confidence boost (positive = raise bar, negative = lower bar).
    Function GetConfidenceAdjustment(contractId As String, strategyName As String,
                                     sessionWindow As String, dayOfWeek As Integer,
                                     atrRegime As String) As Integer

    ' Returns the current performance matrix for the AI Insights view.
    Function GetPerformanceMatrix() As PerformanceMatrix

    ' Returns win rate for a specific dimension combination.
    Function GetWinRate(contractId As String, strategyName As String,
                        sessionWindow As String) As Single?

    ' Returns average R-Multiple for a dimension combination.
    Function GetAverageRMultiple(contractId As String, strategyName As String) As Single?
End Interface
```

**Performance dimensions tracked** (rolling window of last 20 trades per cell):

```
Dimensions:
  ContractId    × StrategyName           = 60 cells  (12 instruments × 5 strategies)
  StrategyName  × SessionWindow          = 25 cells  (5 strategies × 5 sessions)
  ContractId    × DayOfWeek             = 60 cells  (12 instruments × 5 days)
  StrategyName  × AtrRegime             = 20 cells  (5 strategies × 4 regimes)
  SessionWindow × DayOfWeek             = 25 cells  (5 sessions × 5 days)
```

**Adjustment logic:**

| Dimension win rate | Adjustment |
|---|---|
| < 30% (last 10+ trades) | +20 points (near-suppression) |
| 30–40% (last 10+ trades) | +10 points |
| 40–60% | ±0 (neutral) |
| 60–70% | −5 points (slight encouragement) |
| > 70% (last 10+ trades) | −8 points (strong edge confirmed) |

Adjustments from multiple dimensions are summed but capped at ±25 to prevent over-correction.

Minimum trade count before a dimension influences the threshold: **8 trades** (avoids noise from small samples). Below 8 trades, the dimension returns ±0.

### 5.3 New Service: `RegimeDetector`

**File:** `src/TopStepTrader.Services/Market/RegimeDetector.vb`
**Lifetime:** Singleton

Pure statistics, no AI. Classifies the current volatility and trend regime from bar history.

```vb
Public Interface IRegimeDetector
    Function GetAtrRegime(contractId As String, currentAtr As Decimal) As String
    Function GetAtrPercentile(contractId As String, currentAtr As Decimal) As Single
    Function GetTrendRegime(adxValue As Single) As String   ' "Ranging" | "Weak" | "Trending" | "Strong"
End Interface
```

**ATR Regime Classification** (computed against the last 50 bars stored in SQLite):

| ATR Percentile | Regime | Recommended Action |
|---|---|---|
| 0–20th | Low | Tight bracket; small moves expected |
| 20–80th | Normal | Standard bracket; normal operation |
| 80–95th | Elevated | Wide bracket; increased volatility |
| 95th+ | Spike | Consider skipping; abnormal conditions |

The ATR percentile is passed to `MacroContextService` as input and stored in `TradeSetupSnapshot`. Over time this becomes a key predictor — if 80% of losses occur in "Spike" regime on MES, the engine learns to skip or raise the bar during those conditions.

### 5.4 MAE/MFE Tracking — Engine Changes

In `StrategyExecutionEngine`, add two running fields updated on every 30-second tick while a position is open:

```vb
' New in-memory fields (reset on position close)
Private _runningMaeDollars As Decimal = 0D    ' worst point against position
Private _runningMfeDollars As Decimal = 0D    ' best point in favour of position
```

On each tick, after retrieving the broker's unrealised P&L:

```vb
If _lastApiPnl < _runningMaeDollars Then _runningMaeDollars = _lastApiPnl   ' MAE tracks minimum (negative)
If _lastApiPnl > _runningMfeDollars Then _runningMfeDollars = _lastApiPnl   ' MFE tracks maximum (positive)
```

At trade close, these two values are stored in `TradeLifespanRecord`. This is a 4-line change to the existing trail loop.

### 5.5 Pattern Analysis Queries

Once data is accumulating, these SQL queries drive the adaptive parameters. They are run by `AiWeeklyDigestWorker` and fed to Haiku for interpretation.

**Win rate by (strategy × session) — the primary pattern view:**
```sql
SELECT s.StrategyName, s.SessionWindow,
       COUNT(*) AS Trades,
       ROUND(AVG(CASE WHEN o.IsWinner = 1 THEN 1.0 ELSE 0.0 END) * 100, 1) AS WinRate,
       ROUND(AVG(l.RMultiple), 2) AS AvgRMultiple,
       ROUND(AVG(l.MfeTpRatio), 2) AS AvgMfeTpRatio
FROM TradeSetupSnapshots s
JOIN TradeOutcomes o ON o.Id = s.TradeOutcomeId
JOIN TradeLifespanRecords l ON l.TradeOutcomeId = s.TradeOutcomeId
WHERE o.IsOpen = 0
GROUP BY s.StrategyName, s.SessionWindow
HAVING COUNT(*) >= 8
ORDER BY WinRate DESC;
```

**Near-miss analysis — trades that came close to SL but recovered:**
```sql
SELECT s.StrategyName, s.ContractId, s.AtrRegime,
       COUNT(*) AS Trades,
       AVG(l.MaeMfRatio) AS AvgMAE_SL_Ratio,
       AVG(CASE WHEN o.IsWinner = 1 THEN 1.0 ELSE 0.0 END) AS WinRate
FROM TradeSetupSnapshots s
JOIN TradeLifespanRecords l ON l.TradeOutcomeId = s.TradeOutcomeId
JOIN TradeOutcomes o ON o.Id = s.TradeOutcomeId
WHERE l.MaeMfRatio > 0.7   -- came within 30% of SL
  AND o.IsOpen = 0
GROUP BY s.StrategyName, s.ContractId, s.AtrRegime;
```
*High MAE/SL ratio with a high win rate = SL is appropriately placed but tight. Low win rate = SL is too tight.*

**Exit efficiency — are TPs being captured?:**
```sql
SELECT s.StrategyName, s.ContractId,
       ROUND(AVG(l.MfeTpRatio), 2) AS AvgMfeTpRatio,
       ROUND(AVG(l.RMultiple), 2) AS AvgRMultiple,
       COUNT(*) AS Trades
FROM TradeSetupSnapshots s
JOIN TradeLifespanRecords l ON l.TradeOutcomeId = s.TradeOutcomeId
JOIN TradeOutcomes o ON o.Id = s.TradeOutcomeId
WHERE o.IsOpen = 0
GROUP BY s.StrategyName, s.ContractId
ORDER BY AvgMfeTpRatio ASC;
```
*MfeTpRatio < 0.5 consistently = price is reaching the target area but TP is set too conservatively, or trails are giving back too much. Extend TP feature may need to be more aggressive.*

**Session boundary performance:**
```sql
SELECT l.CrossedSessionBoundary,
       COUNT(*) AS Trades,
       ROUND(AVG(CASE WHEN o.IsWinner = 1 THEN 1.0 ELSE 0.0 END) * 100, 1) AS WinRate,
       ROUND(AVG(l.DurationMinutes), 0) AS AvgDurationMins
FROM TradeLifespanRecords l
JOIN TradeOutcomes o ON o.Id = l.TradeOutcomeId
WHERE o.IsOpen = 0
GROUP BY l.CrossedSessionBoundary;
```
*If CrossedSessionBoundary=1 win rate < 40%, consider setting a max trade duration or adding session-exit logic.*

**Confluence condition effectiveness (Multi-Confluence only):**
```sql
SELECT
    ROUND(AVG(CASE WHEN (s.ConditionMask & 1) > 0 THEN 1.0 ELSE 0.0 END), 2) AS Cond1_PassRate,
    ROUND(AVG(CASE WHEN (s.ConditionMask & 2) > 0 AND o.IsWinner = 1 THEN 1.0 ELSE 0.0 END) /
          NULLIF(AVG(CASE WHEN (s.ConditionMask & 2) > 0 THEN 1.0 ELSE 0.0 END), 0), 2) AS Cond2_WinRateWhenPassed,
    -- ... repeat for all 7 conditions
    COUNT(*) AS Trades
FROM TradeSetupSnapshots s
JOIN TradeOutcomes o ON o.Id = s.TradeOutcomeId
WHERE s.StrategyName = 'Multi-Confluence' AND o.IsOpen = 0;
```
*This reveals which individual confluence conditions actually predict outcomes. A condition with 90% pass rate but same win rate as baseline adds no signal — it can be relaxed. A condition with 40% pass rate and 80% win rate when passed is your golden filter.*

---

## 6. The Adaptive Engine — Behavioural Changes Over Time

### 6.1 What the Engine Learns to Do Differently

The following are examples of adaptations that emerge organically from data — no manual tuning required.

**Pattern: BB Squeeze Scalper on MBT during US Session is 1/9**
→ `AdaptiveParameters` row: ConfidenceBoost +20 for (MBT × BB Squeeze × US Session)
→ Effective min confidence becomes 90 + 20 = 110% — never passes
→ In practice: pattern is suppressed without any code change
→ Haiku weekly digest note: *"BB Squeeze on Bitcoin during US session has been suppressed. Last 9 trades: 1 win. Pattern conflicts with post-settlement distribution."*

**Pattern: Multi-Confluence on MES during London-US Overlap is 8/11 (73%)**
→ `AdaptiveParameters` row: ConfidenceBoost −8 for (MES × Multi-Confluence × London-US Overlap)
→ Effective min confidence becomes 80 − 8 = 72% — slightly lower bar for this high-probability setup
→ More signals pass through during the application's best-performing window

**Pattern: MfeTpRatio averaging 0.38 for OIL × Multi-Confluence (Wide tier)**
→ Price is regularly reaching 38% of TP before reversing
→ Haiku weekly note: *"OIL trades are being stopped at 38% of TP. Consider switching from Wide to Standard tier on OIL — TP distance is too ambitious for this instrument's average daily range."*
→ User acts on recommendation or enables `AllowAiTierOverride`

**Pattern: Trades that cross session boundaries have 35% win rate vs 58% in-session**
→ Haiku recommends: add a max-duration exit of 4 hours (8 × 30-min bars at 30-min timeframe)
→ `StrategyDefinition` gains `MaxTradeDurationMinutes As Integer` field
→ Engine exits if `DurationMinutes > MaxTradeDurationMinutes`

**Pattern: MAE/SL ratio > 0.8 on winning trades (nearly stopped before recovering)**
→ These trades win but only because the SL had enough room
→ If the same trades had a tighter SL (say Tight tier) they would have been stopped out
→ Confirms Wide bracket is appropriate for this instrument in elevated ATR regime

### 6.2 R-Multiple Targets — What Healthy Looks Like

Use these benchmarks to evaluate whether the engine is performing as designed:

| Metric | Target | Concern Threshold | Action |
|---|---|---|---|
| Win rate | > 55% | < 40% | Review confluence conditions |
| Average R-Multiple (all trades) | > 1.0 | < 0.5 | TP too conservative or SL too tight |
| MfeTpRatio (avg) | > 0.65 | < 0.40 | TP too aggressive or trails giving back gains |
| MaeMfRatio on winners (avg) | < 0.50 | > 0.80 | SL too tight — winners nearly stopped out |
| Duration on losers vs winners | Losers shorter | Losers longer | Consider time-based exits |
| CrossedSessionBoundary WR | > 50% | < 40% | Add max duration / session-exit rule |

---

## 7. Haiku as Pattern Interpreter — Enhanced Weekly Digest

### 7.1 Full Data Package Sent to Haiku

The weekly digest is no longer a simple win/loss summary. It is a structured performance report:

```
SECTION 1 — SUMMARY (last 7 days)
Total trades: 47 | Winners: 28 | Win rate: 59.6% | Avg R: 0.87
Net P&L: $423 | Best day: Wednesday $210 | Worst day: Monday −$89

SECTION 2 — PERFORMANCE MATRIX
Strategy × Session (min 5 trades):
  Multi-Confluence × London-US Overlap: 8/11 (73%) — Avg R: 1.24
  Multi-Confluence × US Session: 6/10 (60%) — Avg R: 0.91
  BB Squeeze × US Session: 1/9 (11%) — Avg R: −0.43
  VIDYA Cross × London: 3/6 (50%) — Avg R: 0.72

SECTION 3 — EXCURSION ANALYSIS
Avg MAE/SL: 0.44 | Avg MFE/TP: 0.61
Trades where MAE > 0.8 (nearly stopped): 7/47 (15%)
  → Of those 7: 4 eventually won, 3 lost
Avg MFE/TP by instrument:
  MES: 0.71 | OIL: 0.38 | MBT: 0.55 | GOLD: 0.82 | M6E: 0.44

SECTION 4 — CONFLUENCE CONDITIONS (Multi-Confluence only, 21 trades)
Condition pass rates: C1=95%, C2=90%, C3=71%, C4=86%, C5=62%, C6=48%, C7=91%
Win rate when C6 passed vs not passed: 44% vs 67%
Win rate when C5 passed vs not passed: 52% vs 61%

SECTION 5 — SESSION BOUNDARY
In-session trades: 38 @ 63% WR | Cross-session trades: 9 @ 33% WR
Avg cross-session duration: 187 min

SECTION 6 — PATTERN TAGS (from AiPostMortem)
"strong-trend-clean-tp": 12 trades, 83% WR
"marginal-session-thin-volume": 8 trades, 25% WR
"reversal-stopped-early": 6 trades, 17% WR
"momentum-burst-quick-tp": 9 trades, 78% WR
```

### 7.2 What Haiku Returns

```json
{
  "suppressedPatterns": [
    {
      "contractId": "",
      "strategyName": "BB Squeeze Scalper",
      "sessionWindow": "US Session",
      "reason": "1/9 win rate. No edge in this combination — suppress until 20+ trade sample shows improvement.",
      "confidenceBoost": 25,
      "minimumTradesBeforeReview": 20
    }
  ],
  "preferredPatterns": [
    {
      "contractId": "CON.F.US.MES.M26",
      "strategyName": "Multi-Confluence",
      "sessionWindow": "London-US Overlap",
      "reason": "73% WR, Avg R 1.24. Strong edge. Consider reducing min confidence by 5 for this combination.",
      "confidenceBoost": -5
    }
  ],
  "bracketRecommendations": [
    {
      "contractId": "CON.F.US.MCL.M26",
      "currentTier": "Wide",
      "recommendedTier": "Standard",
      "reason": "OIL MfeTpRatio averaging 0.38 — Wide bracket TP is too ambitious. Switch to Standard."
    }
  ],
  "confluenceInsights": [
    {
      "strategyName": "Multi-Confluence",
      "insight": "Condition 6 (C6) shows inverse correlation — trades lose more often when C6 passes. Consider inverting or removing C6 for MES.",
      "affectedInstruments": ["MES"]
    }
  ],
  "ruleRecommendations": [
    {
      "rule": "MaxTradeDuration",
      "value": 240,
      "unit": "minutes",
      "reason": "Cross-session trades: 33% WR vs 63% in-session. A 4-hour max exit would have avoided 7 of 9 cross-session losers."
    }
  ],
  "generalNarrative": "Your strongest edge remains Multi-Confluence during London-US Overlap on MES and GOLD. BB Squeeze Scalper is not performing outside London session — consider restricting it. OIL needs tighter brackets. The 9 cross-session trades are a structural drag — a time-exit rule would meaningfully improve overall statistics."
}
```

---

## 8. New Rule: Max Trade Duration

One of the most actionable findings from session-boundary analysis will be that cross-session trades underperform. This motivates a new engine parameter:

**`StrategyDefinition.MaxTradeDurationMinutes As Integer`** (default 0 = disabled)

When enabled, the engine checks on each 30-second tick whether `DurationMinutes > MaxTradeDurationMinutes`. If so, it exits the trade with `ExitReason = "MaxDuration"` using the existing `FlattenContractAsync` flow.

This is not an AI feature — it is a rule driven by data. Haiku recommends it; the user enables it; the engine enforces it.

---

## 9. Implementation Additions to AI_AUGMENTATION_PLAN.md

The following phases are added to or modify the existing plan:

### Phase 0 (new — runs before everything else)

**Data Foundation Sprint.** Before any AI feature is meaningful, TODO-001 must be complete AND the new tables must be created. This sprint:
- Creates `TradeSetupSnapshots` and `TradeLifespanRecords` tables in `EnsureSchemaCurrent`
- Extends `TradeOutcomes` with new AI columns
- Creates `AdaptiveParameters` table
- Wires `TradeOutcomeRepository` into `StrategyExecutionEngine` (TODO-001)
- Adds MAE/MFE tracking to the 30-second trail loop (4 lines)
- Captures `TradeSetupSnapshot` from `ConfidenceUpdatedEventArgs` at signal time
- Captures `TradeLifespanRecord` at trade close

**Everything downstream depends on this.** No live data = no patterns = no adaptation.

### Phase 2b (new — inserts between existing Phase 2 and Phase 3)

**RegimeDetector service.** ATR percentile computation from SQLite bar history. Feeds into `MacroContextService` and `TradeSetupSnapshot`. No AI cost — pure statistics.

### Phase 5b (new — after existing Phase 5)

**PerformanceTracker service.** Rolling win rate windows across all pattern dimensions. Feeds `GetConfidenceAdjustment` into the engine's effective threshold calculation. Reads from `TradeSetupSnapshots` + `TradeOutcomes`. No AI cost.

### Phase 7b (new — enhances existing Phase 7)

**Enhanced weekly digest.** Replaces the simple win/loss summary with the full structured performance report (Sections 1–6 above). Haiku now returns confluence insights, bracket recommendations, and rule recommendations in addition to suppressed/preferred patterns. All outputs written to `AdaptiveParameters` table and to a new `WeeklyDigestHistory` table for audit trail.

---

## 10. Data Volume and Retention

At typical trading volumes (5–15 trades/day, 5 days/week):

| Table | Growth rate | 1-year size |
|---|---|---|
| `TradeOutcomes` | ~10 rows/day | ~2,500 rows |
| `TradeSetupSnapshots` | ~10 rows/day | ~2,500 rows |
| `TradeLifespanRecords` | ~10 rows/day | ~2,500 rows |
| `AdaptiveParameters` | ~50 rows/week | ~2,600 rows |
| `Bars` | ~1,000 rows/day | ~260,000 rows |

All tables remain small. No partitioning or archiving strategy required at this scale. SQLite handles this comfortably.

**Minimum data before adaptation is meaningful:**

| Adaptation type | Minimum trades |
|---|---|
| Single dimension adjustment (1 strategy × 1 session) | 8 |
| Pattern suppression (weekly digest) | 15 |
| Bracket recommendation (MfeTpRatio) | 10 |
| Confluence condition analysis | 20 |
| Cross-session rule (MaxTradeDuration) | 15 cross-session trades |

At 10 trades/day, the first meaningful weekly digest can run after **week 2**. Pattern suppression becomes reliable after **3–4 weeks**. Full confluence condition analysis after **6–8 weeks**.

**The engine gets meaningfully smarter every week it runs.**

---

## 11. File Summary — New Files

| File | Layer | Type |
|---|---|---|
| `Core/Interfaces/IPerformanceTracker.vb` | Core | Interface |
| `Core/Interfaces/IRegimeDetector.vb` | Core | Interface |
| `Core/Models/TradeSetupSnapshot.vb` | Core | Model |
| `Core/Models/TradeLifespanRecord.vb` | Core | Model |
| `Core/Models/PerformanceMatrix.vb` | Core | Model |
| `Core/Models/WeeklyDigestResult.vb` | Core | Model |
| `Data/Entities/TradeSetupSnapshotEntity.vb` | Data | Entity |
| `Data/Entities/TradeLifespanRecordEntity.vb` | Data | Entity |
| `Data/Entities/AdaptiveParametersEntity.vb` | Data | Entity |
| `Data/Repositories/TradeSetupSnapshotRepository.vb` | Data | Repository |
| `Data/Repositories/TradeLifespanRepository.vb` | Data | Repository |
| `Data/Repositories/AdaptiveParametersRepository.vb` | Data | Repository |
| `Services/AI/PerformanceTracker.vb` | Services | Singleton |
| `Services/Market/RegimeDetector.vb` | Services | Singleton |

---

*Document owner: TopStepTrader project*
*Last updated: 2026-03-31*
*Read in conjunction with: `AI_AUGMENTATION_PLAN.md`, `TODO.md`*
