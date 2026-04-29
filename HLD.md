# TopStepTrader — High-Level Design (HLD)

**Version:** 1.7
**Date:** 2026-04-29
**Status:** Current

---

## 1. Purpose

TopStepTrader is a WPF desktop application for live and automated trading on TopStepX (CME Micro futures) via the ProjectX REST and SignalR API.

| Broker | Account Type | Instrument Class |
|---|---|---|
| **TopStepX** | Funded / Practice futures | CME Micro Futures (MES, MYM, MGC, MCL, SIL, MBT, GMET, M6E) |

---

## 2. System Context

```
┌────────────────────────────────────────────────────────────┐
│                     TopStepTrader (WPF)                    │
│                                                            │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐   │
│  │   Strategy   │  │   Backtest   │  │   Dashboard / │   │
│  │   Engines    │  │              │  │   Order Book  │   │
│  └──────┬───────┘  └──────┬───────┘  └───────┬───────┘   │
│         └─────────────────┴──────────────────┘            │
│                           │                                │
│               ┌──────────────────────┐                    │
│               │    IOrderService      │                    │
│               │  ProjectXOrderService │                    │
│               └──────────┬───────────┘                    │
│                          │                                 │
│              ┌───────────▼────────────┐                   │
│              │  ProjectX REST API     │                    │
│              │  (Futures accounts)    │                    │
│              └────────────────────────┘                   │
│                                                            │
│          ┌───────────────────────────────┐                │
│          │  SQLite (EF Core)             │                │
│          │  bars / orders / backtest     │                │
│          └───────────────────────────────┘                │
└────────────────────────────────────────────────────────────┘
```

**External dependencies:**

| Dependency | Protocol | Purpose |
|---|---|---|
| ProjectX REST | HTTPS | Account, orders, contract metadata (TopStepX broker) |
| ProjectX UserHub | SignalR / WebSocket | Real-time order fills and position events |
| ProjectX MarketHub | SignalR / WebSocket | Real-time tick data |
| Yahoo Finance | HTTPS | Free historical bar data for backtesting |
| Claude API (Anthropic) | HTTPS | AI trade review (ClaudeReviewService) |
| Local SQLite | File | Bar persistence, order history, backtest results |

---

## 3. Architecture Overview

The application follows a **layered MVVM architecture** with dependency injection throughout.

### 3.1 Layer Diagram

```
┌───────────────────────────────────────────────────────────┐
│  TopStepTrader.UI                                         │
│  WPF MVVM — Views (XAML) + ViewModels (VB.NET)           │
│  Infrastructure: AppBootstrapper (DI Root)               │
├───────────────────────────────────────────────────────────┤
│  TopStepTrader.Services                                   │
│  Business logic: Execution Engines, Background Workers,   │
│  Backtest, Market Data Ingestion                         │
├──────────────┬────────────────┬──────────────────────────┤
│  .Core       │  .API          │  .Data          │  .ML   │
│  Domain      │  HTTP/SignalR  │  EF Core+SQLite │  Models│
│  Models,     │  Clients:      │  Repositories   │  Tech  │
│  Interfaces, │  ProjectX,     │                 │  Indica│
│  Enums,      │  Yahoo         │                 │  tors  │
│  TickMath,   │                │                 │        │
│  Favourites  │                │                 │        │
└──────────────┴────────────────┴─────────────────┴────────┘
```

**Dependency rule:** UI → Services → (Core, Data, API, ML). No upward or cross-layer references.

### 3.2 Technology Stack

| Concern | Technology |
|---|---|
| Language | VB.NET (all seven projects) |
| Runtime | .NET 10, x64, Windows |
| UI Framework | WPF |
| Architecture Pattern | MVVM + Microsoft.Extensions.DI |
| Database | Entity Framework Core + SQLite |
| Background services | IHostedService (generic host) |
| Testing | xUnit |

---

## 4. Key Subsystems

### 4.1 Order Service

All order operations flow through a single interface, `IOrderService`, registered directly as `ProjectXOrderService`:

```
IOrderService (interface)
    └── ProjectXOrderService  (TopStepX / ProjectX REST — registered directly)
```

Every execution engine, ViewModel, and service depends only on `IOrderService`. There is no broker dispatcher — TopStepX is the sole trading platform.

### 4.2 Global Session Context

`TradingSessionContext` (Singleton) holds the user's current account selection. It is injected into every engine and service that needs account context. When the user changes account on the Dashboard, an `AccountChanged` event fires and all subscribed components update immediately. `ActiveBroker` always returns `BrokerType.TopStepX`.

### 4.3 Execution Engines

Four independent, transient trading engine types operate on the same `IOrderService` abstraction. Asset Bassett uses up to 9 instances of `StrategyExecutionEngine` concurrently:

| Engine | View | Strategy | SL / TP Management |
|---|---|---|---|
| `StrategyExecutionEngine` | Hydra, Asset Bassett | Multi-confluence confidence scoring, multi-asset or single-asset multi-strategy coordinator | Fixed ClaudeTrader ticks (50 SL / 25 TP from `ProjectXSettings`); adaptive timer (5s open / 60s idle) trails both SL+TP when profitable (ratchet never reverses); Danger Zone snaps SL to breakeven |
| `SniperExecutionEngine` | Sniper | 3-EMA Cascade pyramid (TripleEmaCascade) | Tick-based trail (SL + TP ratchet away) |
| `CryptoStrategyExecutionEngine` | CryptoJoe | Two CME crypto futures (MBT, GMET) | ATR trail (SL + TP ratchet away) |
| `PumpNDumpExecutionEngine` | PumpNDump | 3-bar price-action scalp | TP tightens on fade; free-ride then SL ratchet; trading hours guard (default 06-21 UTC); stale-bar guard (>5 min suppresses entry); 30s re-entry cooldown after close |

Each engine:
- Is resolved as **Transient** — a fresh instance per engine start
- Receives a `StrategyDefinition` deep-copy at startup (isolates from UI mutations mid-trade)
- Raises typed events (`TradeOpened`, `TradeClosed`, `ConfidenceUpdated`, `LogMessage`) consumed by its ViewModel
- Manages its own cancellation token (`CancellationTokenSource`) for orderly shutdown

### 4.4 Risk Management

Risk is applied in two places:

1. **Session-level (Dashboard):** Daily Loss Limit, Max Position Size, Min Confidence, Auto-Execution toggle. Settings live in-memory for the session; they reset to `appsettings.json` defaults on next launch.
2. **Engine-level (StrategyDefinition):** Per-engine `SlMultipleOfN`, `TpMultipleOfN`, ATR-based bracket sizing, proportional TP floor scaling (when the broker's minimum stop forces the SL wider, TP is scaled by the same ratio to preserve R:R).

### 4.5 Instrument Catalogue

Two layers of instrument metadata:

- **`FavouriteContracts`** (static, in-memory): Master list of all tradeable instruments with ProjectX contract IDs, tick sizes, tick values, and minimum stop distances. Source of truth for tick arithmetic.
- **`TopStepXInstrumentCatalog`** (Singleton, 15-min TTL cache): Fetches live front-month contract IDs and API-provided minimum stop tick counts from ProjectX on demand. Falls back to `FavouriteContracts` local defaults if the API is unavailable.

### 4.6 Background Workers

Two hosted services run for the lifetime of the application:

| Worker | Purpose | Behaviour |
|---|---|---|
| `TokenRefreshWorker` | Proactively refreshes the ProjectX JWT before expiry | Runs every N minutes; calculates time-to-expiry from the decoded token |
| `BarIngestionWorker` | Polls market data and persists bars to SQLite | Configurable polling interval per timeframe; sleeps between polls |

### 4.7 Backtesting

`IBacktestService` / `BacktestEngine` (Scoped) runs historical simulations using locally-cached bar data. If bars are missing, `IBarCollectionService` downloads them from Yahoo Finance. Results are persisted to SQLite and recalled by the Backtest and Sniper views.

Key engine behaviours:

- **End-of-day forced close** — detects day boundaries; closes all open legs at the prior bar's `Close` with SL-slippage applied (`ExitReason = "EndOfDay"`). 1-bar re-entry cooldown follows. `BacktestResult.EndOfDayCloseCount` tracks these.
- **Per-contract commission** — `FavouriteContract.RoundTripFee` is deducted on every open and close leg (OIL=$1.04, GOLD=$1.24, SIL=$1.24, MES=$0.74, M6E=$0.74, MBT=$2.34; default=$0.80).
- **Train/test split** — `BacktestConfiguration.TrainTestSplit = 0.6` runs the first 60% of bars as in-sample and the last 40% as out-of-sample. `TestPnL` and `DegradationPct` surface on result rows.
- **Market regime filter** — `RegimeClassifier.Classify(atr, adx, threshold)` can route each bar to a `TrendingStrategyOverride` or `RangingStrategyOverride` condition type before signal evaluation.
- **VolumeGateEnabled** — MC volume condition can be disabled per-run via `BacktestConfiguration.McVolumeGateEnabled = False` for zero-PX-volume instruments.

### 4.8 SuperTrend+ Autopilot

`SuperTrendPlusViewModel` (`UI/ViewModels/SuperTrendPlusViewModel.vb`) is a fully autonomous scan-and-trade engine independent of the Hydra/AssetBassett engines. It polls seven instruments every 15 seconds and manages up to three concurrent **Position Slots** per instrument using a composite degradation-signal exit engine.

**Watchlist instruments:** Oil (MCLE) · Gold (MGC) · Silver (SIL) · S&P 500 (MES) · NQ (MNQ) · EUR/USD (M6E) · Bitcoin (MBT)

**Position Slot model (FEAT-23):**
- `PositionSlot` — identity-free state object per open position (SlotIndex, Instrument, Side, EntryPrice, EntryBarTime, EntryAdx, StopPrice, TakeProfitPrice, SlotHealth)
- `SlotManager` — owns up to 3 slots; enforces ADX-band slot counts, same-bar blocking, counter-trend blocking, and Exiting-state blocking
- `SuperTrendPlusConfig` — single unified config (replaces per-persona profiles)

**ADX band → slot count:**

| ADX Range | Slots Opened |
|---|---|
| < 25 | No trade |
| 25–39 | 1 slot |
| 40–59 | Up to 2 slots |
| 60+ | Up to 3 slots |

**Phased stop management (FEAT-24):** Each slot advances through stop phases — Initial → Breakeven (1R) → Profit Trail (1.5R) → Harvest (2R) → Free Ride (3R). Stop never retreats.

**Exit degradation signals — `ExitSignalEngine` (FEAT-24, FEAT-25):**

| Signal | Weight | Description |
|---|---|---|
| E1 — SuperTrend Flip | Immediate | Trend reversed — close all slots |
| E2 — Momentum Slowing | 2 | Three consecutive contracting bars toward ST line |
| E3 — ADX Declining | 2 | ADX falling for 3 bars AND >10 pts below entry ADX |
| E4 — DI Compression | 2 | +DI/−DI spread narrowing AND below 10 |
| E5 — DI Crossover | 4 | +DI/−DI crossed (leading indicator) |
| E6 — Rejection Bar | 2 | Long wick in trade direction; close in lower half |
| E7 — ATR Contraction | 1 | ATR declining 3 bars AND below 80% of entry ATR |
| E8 — VWAP Cross | 2 | Price crosses below/above session VWAP |
| E9 — RSI Hidden Divergence | 3 | Price higher high but RSI lower high over 3–5 bars |

Score 0–2 → Healthy (green); 3–5 → Warning (amber — blocks new slots); 6+ → Exiting (red — close slot).

**Partial exits (FEAT-25):** At 2R profit Slot 2 reduces; at 3R Slot 1 reduces. Uses `IOrderService.PartialCloseContractAsync` with full-close fallback on API failure.

**Order integrity (BUG-27/28/30):** `FireEntryAsync` computes stop ticks from `|lastClose - stLine| / tickSize` clamped to `PxMinStopDollars` floor. Rejected orders (status ≠ Working) release the lock and do not mark a position as open.

---

## 5. Data Flows

### 5.1 Trade Placement (TopStepX)

```
User clicks "Start"
    → ViewModel deep-copies StrategyDefinition
    → StrategyExecutionEngine.StartAsync()
    → Engine polls bars, computes ATR + confidence score
    → Confidence ≥ threshold → PlaceBracketOrdersAsync()
        → Fixed ClaudeTrader ticks: SL = DefaultSlTicks (50), TP = DefaultTpTicks (25)
        → TopStepXInstrumentCatalog.ClampStopTicksAsync() enforces API minimum
        → ProjectXOrderService.PlaceOrderAsync() — SL + TP sent as brackets
        → 4×750ms retry to resolve PositionId for immediate trail activation
    → TrailBracketAsync() — adaptive timer: 5s while open, 60s idle (independent of bar-check):
        → Only fires when position open, positionId resolved, entryPrice > 0, lastSlPrice > 0
        → Fetches current price via GetLatestPriceAsync (15-second bar)
        → Only acts when profitable (long: price > entry; short: price < entry)
        → SL ratchets to (currentPrice − slTicks × tickSize), snapped to tick grid
        → TP moves with SL: (currentPrice + tpTicks × tickSize)
        → Guards: ≥1 tick improvement required; SL never retreats
        → EditPositionSlTpAsync() updates both brackets atomically
    → FlattenContractAsync() on stop/exit:
        → Cancels both SL (type=4 Stop) and TP (type=1 Limit) brackets
        → Closes position via CloseContractAsync()
```

### 5.2 Bar Ingestion

```
BarIngestionWorker (background, continuous)
    → Reads IngestionSettings (symbols, timeframes, poll interval)
    → BarIngestionService.IngestAsync(contractId, timeframe)
        → Fetches latest bars from ProjectX history API
        → BarRepository.UpsertBarsAsync() → SQLite
    → Engines call GetBarsForMLAsync() → read from SQLite cache
```

---

## 6. Non-Functional Characteristics

| Concern | Approach |
|---|---|
| **Security** | Credentials stored in `ApiKeyStore` (file-backed). Never logged. |
| **Resilience** | Post-fill position-ID retry loop (4×750ms). Token proactive refresh. Fallback tick specs from FavouriteContracts if catalog API fails. |
| **Market-closed detection** | `StrategyExecutionEngine` declares a bar stale when `barAgeMins > TimeframeMinutes × 3`. On the fresh→stale transition it fires one `ConfidenceUpdated(IsMarketClosed=True)` event — tiles show `"⏸ Closed"` — and suppresses all entry signals. Position monitoring (broker sync + adaptive trail) continues. |
| **Observability** | Each engine emits `LogMessage` events bound to a per-view scrolling log. `DiagnosticLogger` (Transient) provides structured `ILogger<T>` output to the host. |
| **Testability** | `FakeCatalogLocalOnly` and `FakeCatalogWithMinStop` test doubles in `TopStepXTickTests.vb` cover tick math and min-stop clamping without hitting the API. |
| **Performance** | In-memory sorting in repositories (EF Core SQLite DateTimeOffset ORDER BY limitation). 15-min TTL catalog cache. Log capped at 500 entries per engine to prevent memory growth. |
| **Broker isolation** | All order logic is fully encapsulated in `ProjectXOrderService`. All engines use `IOrderService` only. |

---

## 7. Views Summary

| View | Engine | Purpose |
|---|---|---|
| Dashboard | — | Account selection, daily P&L, risk settings, auto-execution toggle |
| Hydra | `StrategyExecutionEngine` | Multi-asset confidence monitoring and automated trading. Asset roster: OIL · GOLD · SPX500 (MES) · EUR/USD (M6E) · BTC (MBT). Default strategy: Multi-Confluence, Wide ATR tier (2.5N/5.0N), ADX≥25. Tiles show `"⏸ Closed"` when the market is inactive. Two rows of 5 strategy tiles (10 total): Row 1 — EMA/RSI Combined, Multi-Confluence, LULT Divergence, BB Squeeze Scalper, VIDYA Cross; Row 2 — Connors RSI-2, SuperTrend, Donchian Breakout, BB+RSI Reversion, placeholder. |
| Asset Bassett | `StrategyExecutionEngine` × 9 | Single-asset, multi-strategy coordinator. Up to 9 independent engines monitor one chosen FavouriteContract simultaneously. Option-B blocking (first signal wins; others locked out until position closes). Danger Zone: opposing signal snaps SL to breakeven on the owning engine. Same-direction signal = scale-in candidate (logged; owning engine executes). Tiles are toggles (click to enable/disable); all-off resets on launch. |
| Sniper | `SniperExecutionEngine` | Single-instrument 3-EMA pyramid + backtest tab |
| Pump-n-Dump | `PumpNDumpExecutionEngine` | 3-bar scalp with momentum-fade TP tightening; free-ride activates at configured P&L threshold then SL ratchets behind price until the trade closes |
| CryptoJoe | `CryptoStrategyExecutionEngine` × 2 | Two parallel CME crypto engines (MBT, GMET) |
| Backtest | `BacktestEngine` | Pre-trade strategy validation against historical bars |
| Order Book | — | Manual order placement and cancellation |
| Test Trade | — | Diagnostic order placement with bracket validation; no active management |
| API Keys | — | Credential management for TopStepX, Claude AI, and future platforms |
| SuperTrend+ | `SuperTrendPlusViewModel` | Autonomous 7-instrument scan-and-trade autopilot. Position Slot model (up to 3 slots per instrument), ADX-band entry, phased stop management, 9-signal degradation exit engine, partial exits at 2R/3R. Watchlist: Oil · Gold · Silver · S&P 500 · NQ · EUR/USD · Bitcoin. |

---

## 8. Configuration

Settings are loaded from `appsettings.json` (committed template: `appsettings.template.json`) and bound via `IOptions<T>`:

| Options Class | Section | Key Contents |
|---|---|---|
| `ProjectXSettings` | `"ProjectX"` | TopStepX base URLs, account IDs, `DefaultSlTicks` (50), `DefaultTpTicks` (25) |
| `RiskSettings` | `"Risk"` | Daily loss limit, max position, min confidence |
| `TradingSettings` | `"Trading"` | Default durations, leverage caps |
| `MLSettings` | `"ML"` | Model directory, hot-reload flag |
| `IngestionSettings` | `"Ingestion"` | Bar symbols, timeframes, poll interval |
| `ClaudeSettings` | `"Claude"` | Claude API base URL (key from ApiKeyStore) |

Broker credentials are **never** stored in `appsettings.json`. They are managed by `ApiKeyStore`.

---

## 9. Glossary

| Term | Meaning |
|---|---|
| ATR | Average True Range — volatility measure used for bracket sizing and trail steps |
| Bracket order | A linked SL and/or TP submitted together with the entry order |
| Free-ride | State where SL has advanced to breakeven — position can only profit or break even |
| OCO | One-Cancels-Other — when one bracket fills, the other is cancelled automatically |
| PX | Abbreviation for ProjectX (TopStepX's order-routing platform) |
| R:R | Risk-to-Reward ratio (e.g. 1:2 means TP is twice the distance of SL) |
| Ratchet | Trail mechanism that only moves in the profitable direction — never reverses |
| Tick | Minimum price increment for a futures contract |
| N-multiple | Multiplier applied to ATR to size SL or TP (e.g. 1× ATR for SL, 2× ATR for TP) |
