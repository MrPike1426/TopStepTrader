# TopStepTrader — AI Augmentation Plan
## From Technical Engine to AI-Augmented Trading System

**Version:** 1.0
**Date:** 2026-03-31
**Status:** Approved for implementation
**Model:** Claude Haiku (`claude-haiku-4-5-20251001`) throughout

---

## 1. Vision

TopStepTrader currently identifies trades through pure technical analysis — confluence scoring, ATR brackets, and ADX gating. The goal of this plan is to layer a Haiku-powered AI co-pilot on top of that foundation, creating a system where:

- **Technicals identify** the opportunity
- **Macro context qualifies** the environment
- **Performance history modulates** the confidence threshold
- **AI explains every decision** — PROCEED with reasoning, VETO with reasoning, post-mortem on close
- **The system improves over time** — weekly digest builds a pattern-suppression list from real outcomes
- **The trader stays informed** — plain-English briefings replacing raw numbers

This is the difference between a technical screener and a professional-grade trading co-pilot.

---

## 2. Current State Assessment

The infrastructure is more complete than it appears. The following components are built but not yet fully wired:

| Component | Status | Notes |
|---|---|---|
| `ClaudeReviewService.PreTradeCheckAsync` | ✅ Built | Unclear if called by `StrategyExecutionEngine` |
| `PreTradeContext` model | ✅ Built | Rich context payload, missing performance fields |
| `TradeOutcomeRepository` | ✅ Built | Never written to by engine (see TODO-001) |
| `ConfidenceUpdatedEventArgs` | ✅ Full snapshot every 30s | Tenkan, Kijun, EMA, MACD, StochRSI, ADX |
| `AnalyseBacktestResultsAsync` | ✅ Wired | Maximum Effort tab only |
| `IClaudeReviewService` interface | ❌ Missing | Concrete class only — cannot mock in tests |
| Macro / session awareness | ❌ Missing | No concept of time-of-day, news risk, regime |
| Post-trade AI explanation | ❌ Missing | No Haiku call on `TradeClosed` |
| AI learning / feedback loop | ❌ Missing | No performance data fed back into pre-trade gate |
| Session briefing | ❌ Missing | No instrument narrative at engine start |
| Dynamic risk adjustment | ❌ Missing | AI can veto but cannot modulate SL/TP/confidence |

**The gap is not infrastructure — it is context depth and feedback loops.**

---

## 3. Design Principles

1. **AI augments, never replaces** — technicals identify the signal; AI decides if conditions are right to act on it
2. **Fail permissive** — any Haiku timeout or API error = PROCEED; trading never stops because of AI
3. **Cache aggressively** — macro context has a 30-minute TTL per instrument; session briefings run once at engine start
4. **Every AI decision is logged** — reasoning stored with every trade outcome and visible in the UI
5. **Token budget discipline** — Haiku is cheap (~$0.0008/1K input tokens); target < 400 tokens per pre-trade call
6. **User settings always win** — AI recommendations are advisory; explicit user configuration overrides AI suggestions unless the user opts in to AI overrides

---

## 4. Architecture — The AI Augmentation Layer

```
┌─────────────────────────────────────────────────────────────────┐
│  StrategyExecutionEngine  (30-second tick)                      │
│                                                                 │
│  1. Poll bars → compute indicators                (existing)    │
│  2. Score confluence → ConfidencePct             (existing)    │
│  3. ADX gate                                      (existing)    │
│       │                                                         │
│       ▼  confidence ≥ threshold?                                │
│  ┌──────────────────────────────────────────┐                   │
│  │   AI AUGMENTATION GATE                   │  ← NEW           │
│  │                                          │                   │
│  │  MacroContextService (30-min TTL cache)  │                   │
│  │  └─ regime / session quality / bias      │                   │
│  │  └─ confidence adjustment (±10 pts)      │                   │
│  │  └─ recommended bracket tier             │                   │
│  │                                          │                   │
│  │  TradeHistoryContext                     │                   │
│  │  └─ rolling win rate (last 20 trades)    │                   │
│  │  └─ recent P&L (last 5 trades)           │                   │
│  │  └─ consecutive losses counter           │                   │
│  │                                          │                   │
│  │  AiCircuitBreaker (Singleton)            │                   │
│  │  └─ arms after 3 consecutive losses      │                   │
│  │  └─ raises effective min confidence      │                   │
│  │                                          │                   │
│  │  PreTradeCheckAsync()                    │                   │
│  │  └─ PROCEED / VETO                       │                   │
│  │  └─ reasoning stored with TradeOutcome   │                   │
│  └──────────────────────────────────────────┘                   │
│       │ PROCEED                                                 │
│       ▼                                                         │
│  PlaceBracketOrdersAsync()                        (existing)    │
│       │                                                         │
│       ▼                                                         │
│  TradeOutcomeRepository.SaveAsync()               (TODO-001)    │
│       │                                                         │
│       ▼  on TradeClosed                                         │
│  PostTradeAnalysisAsync()                         ← NEW         │
│  └─ stores Haiku post-mortem with TradeOutcome                  │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  MacroContextService  (Singleton, 30-min TTL per instrument)    │
│                                                                 │
│  Returns per instrument:                                        │
│  - sessionQuality: "good" | "marginal" | "avoid"               │
│  - macroPosture:   "risk-on" | "risk-off" | "neutral"          │
│  - recommendedTier: "Tight" | "Standard" | "Wide"              │
│  - confidenceAdjustment: -10 to +10 (offset to min threshold)  │
│  - keyRisk: plain-English e.g. "US CPI at 13:30 UTC"           │
│  - notes: instrument-specific narrative                        │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  SessionBriefingService  (fires once per engine Start())        │
│                                                                 │
│  3–5 sentence plain-English briefing per instrument/strategy:   │
│  "BTC: Settlement window risk until 23:00 UTC. ADX at 18       │
│   suggests low trend strength. Prefer Short signals only with   │
│   Tight bracket today. Settlement risk resumes 22:00 UTC."     │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  AiWeeklyDigestWorker  (background, runs Sunday 00:00 UTC)      │
│                                                                 │
│  Queries TradeOutcomes for last 7 days → sends to Haiku        │
│  Haiku returns suppressedPatterns + preferredPatterns           │
│  Stored in SQLite → loaded into MacroContextService on startup  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 5. Instrument Macro Narratives

Each `FavouriteContract` gains a `MacroNarrative` string injected into every Haiku prompt. These are static and maintained in `FavouriteContracts.vb`:

| Instrument | Narrative |
|---|---|
| OIL (MCL) | "CME Micro WTI. Driven by OPEC supply decisions, US inventory data (Wed 15:30 UTC), and geopolitical risk premium. Avoid entries within 30 min of inventory release." |
| GOLD (MGC) | "CME Micro Gold. Safe-haven asset. Inversely correlated with USD and real yields. Peaks on risk-off events, Fed pivots, and geopolitical shocks." |
| MES | "CME Micro S&P 500. Risk-on asset. Sensitive to Fed rate expectations, earnings season, and VIX spikes. Strongest directional moves during US session (14:30–21:00 UTC)." |
| M6E | "CME Micro EUR/USD. FX futures. Sensitive to ECB/Fed policy divergence, Eurozone PMI, and US CPI. Avoid low-liquidity windows (22:00–07:00 UTC)." |
| MBT | "CME Micro Bitcoin. Daily settlement gap 22:00–23:00 UTC — avoid entries in this window. Crypto-specific volatility spikes on exchange news, ETF flow data, and on-chain events." |
| GMET | "CME Micro Ether. Correlated with MBT but higher beta. Same 22:00–23:00 UTC settlement risk. More reactive to DeFi/staking protocol news." |

---

## 6. Implementation Phases

---

### Phase 1 — Close Existing Gaps
**Prerequisite for all other phases.**

#### 1.1 Wire `PreTradeCheckAsync` into `StrategyExecutionEngine`

The method exists in `ClaudeReviewService`. The `PreTradeContext` model exists. Confirm the engine calls it before `PlaceBracketOrdersAsync`. If not, wire it.

Add `UseAiPreTradeGate As Boolean` to `StrategyDefinition` (default `True`). Allows per-engine opt-out (e.g. always off for PumpNDump scalps which operate on sub-minute timing where an API call would be too slow).

#### 1.2 Wire `TradeOutcomeRepository` into `StrategyExecutionEngine`

See **TODO-001** in `TODO.md` for full implementation steps. This is the data foundation for Phases 4, 5, and 7.

#### 1.3 Extract `IClaudeReviewService` Interface

`ClaudeReviewService` has no interface. Extract one. Existing call sites continue unchanged. Enables:
- Mocking in xUnit tests
- Future swap to a different model or local LLM without changing call sites

#### 1.4 Enrich `PreTradeContext` with Performance Fields

Add the following fields to `PreTradeContext.vb`:

```vb
''' <summary>Rolling win rate across last N trades for this contract. Nothing if insufficient data.</summary>
Public Property RollingWinRate As Decimal?

''' <summary>Cumulative P&L of last 5 closed trades. Nothing if insufficient data.</summary>
Public Property RecentPnL As Decimal?

''' <summary>Number of consecutive losing trades on this contract in the current session.</summary>
Public Property ConsecutiveLosses As Integer = 0

''' <summary>Total trades taken by this engine instance in the current session.</summary>
Public Property TotalTradesThisSession As Integer = 0
```

Populated from `TradeOutcomeRepository` immediately before the Haiku call.

---

### Phase 2 — Macro Context Service

#### 2.1 New Service: `MacroContextService`

**File:** `src/TopStepTrader.Services/AI/MacroContextService.vb`
**Lifetime:** Singleton
**Interface:** `IMacroContextService`

```vb
Public Interface IMacroContextService
    Function GetContextAsync(contractId As String) As Task(Of MacroContext)
    Sub InvalidateCache(contractId As String)
End Interface
```

**Cache:** `Dictionary(Of String, (Context As MacroContext, ExpiresAt As DateTimeOffset))` — 30-minute TTL per instrument.

**New model:** `MacroContext.vb` in `TopStepTrader.Core/Models/`:

```vb
Public Class MacroContext
    Public Property SessionQuality As String     ' "good" | "marginal" | "avoid"
    Public Property MacroPosture As String       ' "risk-on" | "risk-off" | "neutral"
    Public Property RecommendedTier As String    ' "Tight" | "Standard" | "Wide"
    Public Property ConfidenceAdjustment As Integer  ' -10 to +10
    Public Property KeyRisk As String
    Public Property Notes As String
    Public Property GeneratedAtUtc As DateTimeOffset
End Class
```

**Haiku prompt structure (input ~250 tokens):**

```
System: You are a professional futures trading risk analyst. Return a JSON object only. No prose.

User: Assess trading conditions for {contractDescription}.
Instrument macro context: {macroNarrative}
UTC time: {utcNow:yyyy-MM-dd HH:mm} ({dayOfWeek})
Session window: {sessionLabel}  (e.g. "London-US Overlap", "US Session", "Dead Zone")
Next high-impact event: {nextEvent} in {hoursUntil:F1} hours
Current ADX: {adxValue:F1}  Direction: {trendDirection}

Return JSON: { "sessionQuality": "good|marginal|avoid", "macroPosture": "risk-on|risk-off|neutral",
"recommendedTier": "Tight|Standard|Wide", "confidenceAdjustment": <integer -10 to 10>,
"keyRisk": "<one sentence>", "notes": "<one sentence>" }
```

#### 2.2 Session Window Classification

New static helper `SessionWindowHelper.vb` in `TopStepTrader.Core`:

| Window | UTC Range | Label |
|---|---|---|
| Pre-London | 06:00–08:00 | "Pre-London" |
| London | 08:00–12:00 | "London Session" |
| London-US Overlap | 12:00–16:00 | "London-US Overlap (highest liquidity)" |
| US Session | 14:30–21:00 | "US Session" |
| US Close | 21:00–22:00 | "US Close / Low Liquidity" |
| CME Crypto Settlement | 22:00–23:00 | "CME Crypto Settlement (MBT/GMET — avoid)" |
| Dead Zone | 22:00–06:00 | "Dead Zone / Low Liquidity" |

#### 2.3 Economic Calendar (Static)

New `EconomicCalendar.vb` in `TopStepTrader.Core`. Hardcoded weekly schedule of highest-impact events with UTC time and day-of-week. Returns `NextHighImpactEvent` and `HoursUntilEvent` for the next upcoming release. No external API needed — Haiku already knows what these events mean.

Key events hardcoded:
- US NFP (Friday ~13:30 UTC)
- US CPI (monthly, mid-month ~13:30 UTC)
- FOMC (8× yearly, 19:00 UTC)
- US Oil Inventories (Wednesday 15:30 UTC)
- ECB Rate Decision (8× yearly, 13:15 UTC)
- US GDP (quarterly)

---

### Phase 3 — Session Briefing

#### 3.1 New Service: `SessionBriefingService`

**File:** `src/TopStepTrader.Services/AI/SessionBriefingService.vb`
**Lifetime:** Singleton (caches briefings; invalidates on macro context change)

Called once during `StrategyExecutionEngine.StartAsync()` after first fresh bar confirmed.

**Haiku prompt (input ~300 tokens, output ~200 tokens):**

```
System: You are a professional futures trading analyst giving a pre-session briefing.
Be concise, specific, and actionable. 3–5 sentences maximum.

User: Produce a pre-session briefing for {contractDescription} using {strategyName} strategy.
Persona: {personaName} ({personaDescription})
{macroContext}
Current indicators: ADX={adxValue}, Tenkan={tenkan}, Kijun={kijun}, MACD hist={macdHist}
```

#### 3.2 New Event: `SessionBriefingEventArgs`

Add to `SignalGeneratedEventArgs.vb`:

```vb
Public Class SessionBriefingEventArgs
    Inherits EventArgs
    Public ReadOnly Property Briefing As String
    Public ReadOnly Property GeneratedAtUtc As DateTimeOffset
    Public Sub New(briefing As String)
        Me.Briefing = briefing
        Me.GeneratedAtUtc = DateTimeOffset.UtcNow
    End Sub
End Class
```

Engine raises `SessionBriefing` event. Consumed by ViewModel. Displayed in collapsible "AI Brief" header above strategy tiles in Hydra and Asset Bassett views.

---

### Phase 4 — Post-Trade Intelligence

#### 4.1 `PostTradeAnalysisAsync` in `ClaudeReviewService`

New method on `IClaudeReviewService`. Called on `TradeClosed` — async, fire-and-forget, does not block the engine.

**Input (~500 tokens):**
- Entry/exit time, duration, P&L in dollars and ticks
- Exit reason (TP / SL / Reversal / Closed)
- Full indicator snapshot at entry (from stored `TradeOutcomeEntity`)
- MacroContext active at entry (stored at entry)
- Pre-trade AI verdict and reasoning (stored at entry)

**Output (~150 tokens):** 2–3 sentence post-mortem stored in new `AiPostMortem` column on `TradeOutcomeEntity`.

**Example outputs:**

*TP win:* `"Confluence was genuine — MACD histogram expanding and ADX at 31 confirmed trending conditions. Wide bracket absorbed the initial pullback cleanly. No concerns with this entry."`

*SL loss:* `"Entry was technically valid but macro context was marginal — London-US overlap had just ended and volume was thinning. ADX at 26 was borderline. Consider avoiding late-overlap entries on MES with Standard bracket — switch to Tight or skip."`

#### 4.2 Schema Addition: `AiPostMortem` Column

Add to `TradeOutcomeEntity.vb`:
```vb
Public Property AiPostMortem As String = String.Empty
Public Property AiPreTradeVerdict As String = String.Empty    ' "PROCEED" or "VETO"
Public Property AiPreTradeReasoning As String = String.Empty  ' stored at entry
Public Property MacroPostureAtEntry As String = String.Empty  ' from MacroContext
```

Add to `EnsureSchemaCurrent()` in `AppDbContext.vb` via `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` pattern (existing pattern used for other column additions).

---

### Phase 5 — Adaptive Risk Modulation

#### 5.1 AI Confidence Threshold Adjustment

`MacroContextService.ConfidenceAdjustment` (−10 to +10) applied as offset to `MinConfidencePct` at bar-check time. Applied ephemerally — never modifies stored `StrategyDefinition`:

```vb
Dim effectiveMinConfidence = _strategy.MinConfidencePct - macroContext.ConfidenceAdjustment
```

A −5 adjustment in a macro tailwind lowers the bar slightly. A +10 adjustment in elevated macro risk requires near-perfect technical confluence.

#### 5.2 AI Bracket Tier Advisory

`MacroContextService.RecommendedTier` surfaced in the UI as a non-blocking advisory:
- Displayed in the AI Brief header: *"AI recommends: Wide bracket (elevated volatility)"*
- If `AllowAiTierOverride As Boolean = True` (new user toggle), the engine applies the recommendation automatically with a log entry

User settings always win unless opt-in override is enabled.

#### 5.3 AI Circuit Breaker

**New service:** `AiCircuitBreaker.vb` — Singleton.

Tracks consecutive losses per contract across all engines. After **3 consecutive losses** on a single contract in a session:

1. Engine logs: `⚠ AI circuit breaker armed — 3 consecutive losses on MES`
2. Effective `MinConfidencePct` raised by **+15 points** for that contract for **30 minutes**
3. Next `PreTradeCheckAsync` call includes the streak count — Haiku may extend cool-down further
4. Resets on the next winning trade for that contract

```vb
Public Interface IAiCircuitBreaker
    Sub RecordOutcome(contractId As String, isWinner As Boolean)
    Function GetEffectiveConfidenceBoost(contractId As String) As Integer
    Function IsArmed(contractId As String) As Boolean
End Interface
```

---

### Phase 6 — UI: The AI Co-Pilot Panel

#### 6.1 Session Briefing Header (Hydra + Asset Bassett)

Collapsible panel above strategy tiles. Collapsed by default, expands on click.

Contains:
- Macro regime badge per instrument: 🟢 Risk-On / 🟡 Neutral / 🔴 Risk-Off
- Session quality indicator
- Last AI refresh timestamp
- Plain-English session briefing text
- Circuit breaker status (if armed, shown in amber)

Updates every 30 minutes with MacroContext refresh.

#### 6.2 AI Verdict in Trade Log

Every log entry for an opened trade includes the AI verdict inline:

```
[MES] 🟢 Trade opened — Buy @ 5,621.50 | 1ct
      ↳ AI: PROCEED — "ADX strong at 31, macro confirms risk-on US session, no calendar risk for 6h"

[MES] ⚡ Signal blocked by AI gate — Buy @ 5,618.25 | 87% confidence
      ↳ AI: VETO — "NFP in 45 minutes. Wait for post-data clarity."
```

#### 6.3 Trade Log Tab with Post-Mortems

New sub-tab on Order Book (or standalone view) showing `TradeOutcomes` table with `AiPostMortem` visible. Clicking a row expands the full Haiku explanation. Turns the application into a genuine learning and review tool.

#### 6.4 New "AI Insights" View

New sidebar entry under CONFIG section (alongside Personas). Contains:

| Section | Content |
|---|---|
| Instrument Status | MacroContext state per instrument — session quality, macro posture, last refreshed |
| Circuit Breaker | Per-contract armed/disarmed status, consecutive loss count |
| Rolling Win Rate | Last 20 trades win rate per contract (sparkline if feasible) |
| AI Accuracy | How often PROCEED led to a win vs how often VETO would have lost — Haiku's own track record |
| Suppressed Patterns | Patterns identified by weekly digest as consistently underperforming |

---

### Phase 7 — Pattern Memory (Weekly Digest)

#### 7.1 New Worker: `AiWeeklyDigestWorker`

**File:** `src/TopStepTrader.Services/Background/AiWeeklyDigestWorker.vb`
**Lifetime:** Singleton `IHostedService`
**Schedule:** Sunday 00:00 UTC (or on demand from AI Insights view)

Queries `TradeOutcomes` for the past 7 days and sends a structured performance summary to Haiku:

```
Win rate by instrument: MES 67%, OIL 45%, MBT 38%
Win rate by session window: London 72%, US 58%, Overlap 41%, Dead Zone 20%
Win rate by strategy: Multi-Confluence 71%, BB Squeeze 33%, VIDYA 55%
Win rate by day of week: Mon 35%, Tue 65%, Wed 70%, Thu 58%, Fri 42%
Worst 3 patterns (contract + strategy + session, min 5 trades):
  BB Squeeze / MBT / US Session — 1 win in 9 trades
  VIDYA Cross / OIL / Dead Zone — 0 wins in 5 trades
  BB+RSI Reversion / MES / Monday — 2 wins in 8 trades
```

**Haiku returns structured JSON:**

```json
{
  "suppressedPatterns": [
    {
      "contractId": "CON.F.US.MBT.M26",
      "strategy": "BB Squeeze Scalper",
      "session": "US Session",
      "reason": "1/9 win rate — insufficient edge in this combination",
      "confidenceBoost": 20
    }
  ],
  "preferredPatterns": [
    {
      "contractId": "CON.F.US.MES.M26",
      "strategy": "Multi-Confluence",
      "session": "London Session",
      "reason": "72% win rate — strong edge, maintain current settings"
    }
  ],
  "generalAdvice": "Reduce Friday exposure across all instruments. Mon–Wed remain highest quality days for this account. Consider restricting BB Squeeze to London session only."
}
```

**Storage:** New `AiInsightsEntity` SQLite table. One row per weekly digest. Loaded into `MacroContextService` on startup.

**Effect:** Suppressed patterns apply a `confidenceBoost` to the effective minimum confidence threshold for that contract/strategy/session combination. Effectively, a pattern with a 1/9 win rate requires near-100% technical confidence before it is permitted — in practice it is functionally suppressed without hard-coding a ban.

#### 7.2 Multi-Instrument Correlation Awareness

When MES fires a VETO for a macro reason (risk-off, volatility spike, news event), automatically check all other engines running simultaneously. If the macro veto is instrument-class-wide (not MES-specific), propagate the VETO to correlated instruments for 15 minutes:

- MES VETO on risk-off → check M6E (USD correlated)
- MBT VETO on crypto event → check GMET
- OIL VETO on geopolitical → no automatic propagation (oil-specific)

Implemented as a new method on `IMacroContextService`: `PropagateVetoAsync(sourceContractId, reason, durationMinutes)`.

---

## 7. New Files Summary

| File | Layer | Type | Phase |
|---|---|---|---|
| `Core/Models/MacroContext.vb` | Core | Model | 2 |
| `Core/Models/WeeklyInsights.vb` | Core | Model | 7 |
| `Core/Interfaces/IClaudeReviewService.vb` | Core | Interface | 1 |
| `Core/Interfaces/IMacroContextService.vb` | Core | Interface | 2 |
| `Core/Interfaces/IAiCircuitBreaker.vb` | Core | Interface | 5 |
| `Core/Helpers/SessionWindowHelper.vb` | Core | Helper | 2 |
| `Core/Helpers/EconomicCalendar.vb` | Core | Helper | 2 |
| `Services/AI/MacroContextService.vb` | Services | Singleton | 2 |
| `Services/AI/SessionBriefingService.vb` | Services | Singleton | 3 |
| `Services/AI/AiCircuitBreaker.vb` | Services | Singleton | 5 |
| `Services/Background/AiWeeklyDigestWorker.vb` | Services | Hosted Service | 7 |
| `Data/Entities/AiInsightsEntity.vb` | Data | Entity | 7 |
| `UI/Views/AiInsightsView.xaml` | UI | View | 6 |
| `UI/ViewModels/AiInsightsViewModel.vb` | UI | ViewModel | 6 |

---

## 8. Modified Files Summary

| File | Change | Phase |
|---|---|---|
| `Core/Models/PreTradeContext.vb` | Add performance fields (RollingWinRate, ConsecutiveLosses, etc.) | 1 |
| `Core/Models/TradeOutcome.vb` | Add AiPreTradeVerdict, AiPreTradeReasoning, MacroPostureAtEntry, AiPostMortem | 4 |
| `Core/Events/SignalGeneratedEventArgs.vb` | Add `SessionBriefingEventArgs` class | 3 |
| `Core/Trading/FavouriteContracts.vb` | Add `MacroNarrative As String` field per contract | 2 |
| `Data/Entities/TradeOutcomeEntity.vb` | Add AI columns (AiPostMortem, AiPreTradeVerdict, etc.) | 4 |
| `Data/AppDbContext.vb` | Add `AiInsights` DbSet; `EnsureSchemaCurrent` additions | 7 |
| `Data/Repositories/TradeOutcomeRepository.vb` | Add `UpdateAiPostMortemAsync` method | 4 |
| `Services/AI/ClaudeReviewService.vb` | Add `PostTradeAnalysisAsync`; implement `IClaudeReviewService` | 1, 4 |
| `Services/Trading/StrategyExecutionEngine.vb` | Wire PreTradeCheck, MacroContext, CircuitBreaker, PostTradeAnalysis | 1, 2, 5 |
| `Services/ServicesExtensions.vb` | Register new Singletons and Workers | 2–7 |
| `Core/Settings/ClaudeSettings.vb` | Add `MacroContextTtlMinutes`, `EnablePostTradeAnalysis` | 2, 4 |
| `UI/ViewModels/HydraViewModel.vb` | Consume SessionBriefing event; surface MacroContext in header | 3, 6 |
| `UI/ViewModels/HydraAssetViewModel.vb` | Surface AI veto log lines | 6 |
| `UI/ViewModels/AssetBassettViewModel.vb` | Same as Hydra | 6 |
| `UI/ViewModels/ViewModelLocator.vb` | Register AiInsightsViewModel | 6 |
| `UI/Views/HydraView.xaml` | Add collapsible AI Brief header panel | 6 |
| `UI/Views/AssetBassettView.xaml` | Add collapsible AI Brief header panel | 6 |
| `UI/MainWindow.xaml` | Add AI Insights nav button | 6 |
| `appsettings.template.json` | Add `Claude.MacroContextTtlMinutes`, `Claude.EnablePostTradeAnalysis` | 2, 4 |

---

## 9. Token Cost Estimate

| Call | Frequency | Avg tokens (in + out) | Est. daily cost |
|---|---|---|---|
| `PreTradeCheckAsync` | ~5–15 signals/day | 350 + 80 | ~$0.010 |
| `MacroContextService` refresh | Every 30 min × 5 instruments | 250 + 150 | ~$0.020 |
| `SessionBriefingService` | Once per engine start | 300 + 200 | ~$0.003 |
| `PostTradeAnalysisAsync` | ~5–15 trades/day | 500 + 150 | ~$0.010 |
| `AiWeeklyDigestWorker` | Weekly | 800 + 300 | ~$0.001/day |
| **Total** | | | **< $0.05/day** |

Haiku is cost-effective enough that token budget is not a meaningful constraint at typical trading volumes.

---

## 10. Implementation Sequence

Phases are ordered by dependency. Phase 1 is the hard prerequisite — without `TradeOutcomeRepository` being written to, Phases 4, 5, and 7 have no data to operate on.

```
Phase 1  ──  Wire existing gaps         ──  IClaudeReviewService, TODO-001, PreTradeContext enrichment
Phase 2  ──  MacroContextService        ──  MacroContext model, session classifier, economic calendar
Phase 3  ──  Session Briefing           ──  SessionBriefingService, AI Brief header in Hydra/AssetBassett
Phase 4  ──  Post-Trade Intelligence    ──  PostTradeAnalysisAsync, AiPostMortem column, Trade Log tab
Phase 5  ──  Adaptive Risk              ──  ConfidenceAdjustment, AiCircuitBreaker, tier advisory
Phase 6  ──  AI Insights UI             ──  AiInsightsView, full UI integration across all views
Phase 7  ──  Pattern Memory             ──  AiWeeklyDigestWorker, suppressedPatterns, correlation veto
```

Each phase is independently deployable and testable. The application remains fully functional between phases — each phase adds capability without breaking the previous state.

---

## 11. Testing Strategy

| Phase | Test Coverage |
|---|---|
| 1 | Unit tests for `IClaudeReviewService` using a `FakeClaudeReviewService` that returns deterministic PROCEED/VETO responses. Integration test for `TradeOutcomeRepository` round-trip (open → resolve). |
| 2 | Unit tests for `SessionWindowHelper` covering all UTC time boundaries. Unit tests for `EconomicCalendar` next-event lookup. `MacroContextService` tested with fake Haiku response. |
| 3 | `SessionBriefingService` tested with fake context — confirms briefing is cached and not re-fetched within TTL. |
| 5 | `AiCircuitBreaker` unit tests: 3 losses arms, win disarms, boost applied correctly. |
| 7 | `AiWeeklyDigestWorker` tested with synthetic `TradeOutcomes` dataset — confirms correct performance summary construction before Haiku call. |

All new services implement interfaces. All tests use fake/stub implementations — no live Haiku calls in the test suite.

---

*Document owner: TopStepTrader project*
*Last updated: 2026-03-31*
