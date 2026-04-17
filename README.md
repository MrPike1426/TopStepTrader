# TopStepTrader — AI-Augmented Futures Trader

A WPF desktop application for live and simulated trading on **TopStepX (CME Micro Futures)**, with AI-augmented signals, automated strategy engines, multi-asset monitoring, and historical backtesting.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Getting Started](#getting-started)
4. [Views](#views)
   - [Dashboard](#1-dashboard-)
   - [Hydra](#2-hydra-)
   - [Sniper](#3-sniper-)
   - [Pump-n-Dump](#4-pump-n-dump-)
   - [CryptoJoe](#5-cryptojoe-)
   - [Backtest](#6-backtest-)
   - [QuantLab](#7-quantlab-)
   - [Order Book](#8-order-book-)
   - [Test Trade](#9-test-trade-)
   - [API Keys](#10-api-keys-)
5. [Services Reference](#services-reference)

---

## Overview

TopStepTrader connects to **TopStepX** via the ProjectX REST and SignalR API:

| Broker | Account Type | Instruments | Order Routing |
|---|---|---|---|
| **TopStepX** | Funded / Practice futures accounts | CME Micro futures (MES, MYM, MGC, MCLE, MBT, GMET, M6E) | ProjectX REST + SignalR WebSocket |

---

## Architecture

### Project Structure

```
src/
├── TopStepTrader.Core/         # Domain models, interfaces, enums, TickMath, FavouriteContracts
├── TopStepTrader.API/          # HTTP/SignalR clients for TopStepX/ProjectX and Yahoo Finance
├── TopStepTrader.Data/         # EF Core + SQLite; order and bar repositories
├── TopStepTrader.ML/           # ML model loading, technical indicators
├── TopStepTrader.Services/     # Business logic — engines, market data, backtest, workers
├── TopStepTrader.UI/           # WPF MVVM desktop app
│   ├── Views/                  # XAML views (one per page)
│   ├── ViewModels/             # MVVM ViewModels
│   ├── Styles/                 # XAML resource dictionaries
│   └── Infrastructure/         # AppBootstrapper (DI composition root)
└── TopStepTrader.Tests/        # xUnit tests
```

### Technology Stack

| Layer | Technology |
|---|---|
| Language | VB.NET (all projects) |
| UI Framework | WPF (.NET 10, Windows) |
| Architecture | MVVM + DI (Microsoft.Extensions.DI) |
| Database | Entity Framework Core + SQLite |
| Test Framework | xUnit |

### Order Routing

`IOrderService` is the unified order interface. `ProjectXOrderService` is registered directly as `IOrderService` — all engines, ViewModels, and services use this single implementation.

`FavouriteContracts.vb` is the master instrument list with full TopStepX specs (ProjectX contract IDs, tick sizes, tick values, minimum stop distances).

---

## Getting Started

### Prerequisites

- .NET 10 SDK (x64)
- Visual Studio 2022 or later
- Windows 10/11

### Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project src/TopStepTrader.UI/TopStepTrader.UI.vbproj
```

### Configuration

1. Copy `appsettings.template.json` → `appsettings.json`
2. Open **API Keys** (sidebar) and enter your TopStepX credentials
3. On the **Dashboard**, select the active account — this context propagates to all engines

Credentials are never stored in `appsettings.json`; they are managed by `ApiKeyStore`.

---

## Views

---

### 1. Dashboard 📊

**Purpose:** Application home screen. Shows account health, daily P&L, drawdown, 5-day balance history, and global risk settings. The account selected here propagates to all strategy views.

#### How It Works

1. On load, fetches all TopStepX accounts from `IAccountService` and pre-selects the first Practice account.
2. Selecting an account calls `TradingSessionContext.SelectAccount()` — this immediately affects all other open views.
3. Balance history shows the last 5 days of account value snapshots stored in the local SQLite database.
4. Daily P&L and drawdown are populated by the trading engines as trades close.
5. Risk settings (Daily Loss Limit, Max Position, Min Confidence) are editable and applied in-memory for the session — they are not persisted between restarts.

#### Key Controls

| Control | Description |
|---|---|
| Account ComboBox | Global account selector |
| Daily Loss Limit | Maximum drawdown allowed today before engines halt |
| Max Position Size | Maximum open contracts across all engines |
| Min Signal Confidence | Minimum score (%) required before any engine places a trade |
| Auto-Execution Toggle | Enables/disables AI autonomous order placement (shown with red warning border) |
| Apply Risk | Applies in-memory risk settings to all running services |
| Reconnect | Re-authenticates with the ProjectX API if the connection is stale |

#### Notes

- Risk settings are **session-only** — they reset to `appsettings.json` defaults on next launch.
- The Auto-Execution toggle controls all strategy engines globally.

---

### 2. Hydra 🐙

**Purpose:** Multi-asset confidence monitoring. Runs a single strategy simultaneously across five instruments (OIL, GOLD, SPX500, EUR/USD, BTC) and displays a card-based confidence dashboard. Suitable for surveying the market before committing to a specific engine.

Default roster: OIL · GOLD · SPX500 (MES) · EUR/USD (M6E) · BTC (MBT)

Default settings: **Multi-Confluence strategy (5-min timeframe) · ADX ≥ 25 · ClaudeTrader fixed brackets (50-tick SL / 25-tick TP)**

#### How It Works

1. User selects account and persona (Lewis / Damian / Joe) to pre-fill risk parameters.
2. The strategy is pre-selected as Multi-Confluence on launch. The user can switch via the strategy tile grid.
3. For each bar, a confidence score (0–100%) and direction are calculated per instrument.
4. New entries are blocked for equity index futures (MES) outside London main session (08:30–13:30 UTC) and US main session (14:00–20:00 UTC).
5. When confidence exceeds the threshold and entry conditions are met, the engine opens a trade automatically.
6. Per-asset cards update in real-time showing confidence, direction, live P&L, and bracket prices.

#### Strategy Presets

Each button pre-configures indicator periods, TP/SL, and leverage for the whole asset roster.

#### ATR Tier Buttons

ATR tiers control backtest bracket sizing (`SlMultipleOfN` / `TpMultipleOfN`) only. **Live orders always use fixed ClaudeTrader ticks** (`DefaultSlTicks = 50`, `DefaultTpTicks = 25` from `appsettings.json`).

| Tier | SL multiple | TP multiple | Use case |
|---|---|---|---|
| Tight | 0.75 × N | 1.5 × N | Scalping / low-volatility instruments |
| Standard | 1.5 × N | 3.0 × N | Balanced — commodities |
| **Wide** (default) | **2.5 × N** | **5.0 × N** | **Survives intrabar noise on index futures** |

#### Per-Asset Card Shows

- Confidence score (colour-coded: red < 30%, yellow 30–70%, green > 70%)
- Direction: Long / Short / Neutral
- If a position is open: entry price, unrealised P&L, SL and TP levels
- Free-Ride badge when SL has advanced to breakeven (guaranteed profit locked)

---

### 3. Sniper 🎯

**Purpose:** 3-EMA Cascade momentum trading engine with pyramiding (scaled-in entries), free-ride risk management, and a built-in backtest tab. Best suited for directional trend-following on a single instrument.

#### Strategy Logic

Entry triggers when EMA8 > EMA21 > EMA50 (long) or EMA8 < EMA21 < EMA50 (short). The engine builds a position in tiers:

- **Core entries**: Split the initial position across `CoreAddsCount` orders, each separated by an ATR multiple.
- **Momentum adds**: Additional contracts when momentum continues.
- **Extension adds**: Optional single-contract adds for extended trends (gated by a checkbox).
- **Free-ride lock**: When enough contracts are profitable, all SLs advance to average entry.
- **Structure-fail exit** (optional): If price breaks below EMA21 (long) by more than a configurable buffer, all positions flatten.

#### Live Tab — Key Parameters

| Parameter | Description |
|---|---|
| Initial TP / SL ($) | Dollar values converted to ticks using the instrument's tick value |
| Scale-In Trigger (k) | ATR multiplier that determines how far price must move before adding the next tier |
| Max Risk Heat Ticks | Cumulative tick risk cap across all open contracts; no new adds if breached |
| Target Total Size | Maximum contracts to accumulate across all pyramid tiers |
| Core Size Fraction | Fraction of total size reserved for the initial core entries (0.0–1.0) |
| Core Adds Count | Number of sub-entries the core is split across |
| Duration (hours) | Auto-flattens all positions and stops the engine after this many hours |

#### Backtest Tab

- Select contract, date range, TP/SL, then click "Run Backtest".
- Downloads 1-min bars from the history API if not already cached locally.
- Returns: Total Trades, Win Rate, Net P&L, Max Drawdown, Average P&L/Trade, Sharpe Ratio.
- Trade list (up to 500 rows) shows entry/exit times, prices, reasons, and P&L.

#### Notes

- Win rate colour thresholds: green ≥ 55%, yellow ≥ 40%, red < 40%.
- Win/Loss counters reset when "Start Sniper" is clicked.
- Log entries accumulate for the session; no auto-clear.

---

### 4. Pump-n-Dump 💥

**Purpose:** 3-bar price-action scalping engine. Enters on three consecutive bullish or bearish bars, then scales in as momentum continues. Dynamically tightens TP as momentum fades to lock in profits before the reversal.

#### Strategy Logic

- **Entry trigger**: Three consecutive bars in the same direction (3 green = Long, 3 red = Short).
- **Scale-in**: Each time price moves `ScaleInTicks` further in the trade's direction, add 1 contract (up to `TargetTotalSize`).
- **Free-ride activation**: When unrealised P&L exceeds `FreeRidePnlThreshold` (dollars), move all SLs to average entry price.
- **Momentum fade detection**: Each bar's range is compared to ATR. If `barRange < FadeAtrFraction × ATR`, the move is considered fading — TP is tightened by `TightenTicksPerBar` to exit before reversal.

#### Key Parameters

| Parameter | Description |
|---|---|
| Take-Profit Ticks | Initial TP distance from entry |
| Stop-Loss Ticks | Initial SL distance from entry |
| Free-Ride P&L Threshold ($) | Dollar P&L level that triggers breakeven SL move |
| Scale-In Ticks | Distance moved before adding next contract |
| Max Risk Heat Ticks | Cumulative tick risk cap |
| Target Total Size | Maximum contracts to build up to |
| Momentum Fade ATR Fraction | Fraction of ATR below which a bar is deemed "fading" (0.0–1.0) |
| Tighten Ticks Per Poll | How many ticks to move TP closer each poll when momentum is fading |
| Duration Hours | Engine lifetime before auto-stop |

#### Notes

- The Free-Ride threshold is P&L-based (dollar amount), not tick-based. This means it accounts for the number of contracts currently open.
- TP tightening only occurs while momentum is detected as fading; it does not tighten in a strong move.

---

### 5. CryptoJoe ☕

**Purpose:** Two-asset CME crypto futures monitoring and trading. Runs two independent execution engines (MBT — Micro Bitcoin, GMET — Micro Ether) in isolated DI scopes, sharing the same strategy template. Designed to run 24/7 without expiry.

#### How It Works

1. User selects account and sets shared risk parameters (capital, leverage, TP/SL, min confidence %).
2. User clicks a strategy preset button.
3. The ViewModel creates **one DI scope and one execution engine per asset** — fully isolated bar data, order routing, and state.
4. The strategy template is deep-copied twice (one per asset, each with the correct contract ID and account ID).
5. Both engines start in parallel and run independently.
6. Each engine emits events (`ConfidenceUpdated`, `TradeOpened`, `TradeClosed`, `LogMessage`) bound to its asset card in the UI.

#### Strategy Presets

| Button | Bars | Key Indicators | Notes |
|---|---|---|---|
| EMA/RSI Combined | 5-min | EMA21, EMA50, RSI14 weighted scoring | Balanced trend + momentum |
| Multi-Confluence | 15-min | Ichimoku + EMA + MACD + StochRSI + ADX | All 7 signals must align |
| LULT Divergence | 5-min | WaveTrend anchor/trigger divergence | 11:00–17:00 UTC time filter, 2R TP |
| BB Squeeze Scalper | 1-min | %B + RSI7, dual-mode (breakout + bounce) | 5× leverage, 15-sec polling |
| VIDYA Cross | 5-min | VIDYA(14) CMO(9) adaptive EMA crossover | Low-noise trend follower |

#### Per-Asset Card Shows

- Large crypto icon (₿ Ξ), symbol name, OPEN / CLOSED market badge
- Confidence score with colour coding (red / yellow / green)
- If a position is open: direction, entry price, unrealised P&L
- SL / TP bracket prices
- Free-Ride badge (SL advanced to breakeven)
- ADX validation status (✓ / ✗)

#### Notes

- Engine duration is set to 8760 hours (~1 year) — effectively runs until manually stopped.
- "Stop All" cancels both engines and flattens any open positions.
- Log entries are prefixed `[MBT]`, `[GMET]` and capped at 500 to prevent memory growth.

---

### 6. Backtest 🔬

**Purpose:** General-purpose strategy backtesting against downloaded historical bar data. Supports four pre-configured strategy templates with customisable contract, date range, capital, and timeframe.

#### How It Works

1. Click a strategy card to select the strategy.
2. Set the contract, date range, initial capital, quantity, and bar timeframe.
3. Click "Run Backtest". The engine downloads any missing bars from Yahoo Finance.
4. Results are displayed once complete: win rate, total P&L, max drawdown, average P&L per trade, Sharpe ratio, and a scrollable trade list.
5. Click "Export CSV" to save the trade list to a file.

#### Strategy Cards

| Card | Type | Typical Win Rate | Sharpe |
|---|---|---|---|
| Connors RSI-2 | Mean Reversion | 67–72% | 1.0–1.5 |
| SuperTrend | Trend-Following | 40–52% | 0.7–1.05 |
| Donchian Breakout | Turtle/Breakout | 30–40% | 0.4–0.8 |
| BB + RSI Reversion | Mean Reversion | 55–65% | 0.6–1.2 |

Win-rate and Sharpe ranges on the cards are reference figures from historical research, not live calculations.

#### Notes

- Selecting a new strategy card or changing the contract clears previous results.
- Backtests can be cancelled mid-run.
- Max 500 trade rows are displayed in the grid; the CSV export matches this cap.
- Win rate colour: green ≥ 55%, yellow ≥ 40%, red < 40%. Max Drawdown is always shown in red.

---

### 7. QuantLab 🧫

**Purpose:** Academic strategy research tool. Same four strategies as Backtest but presented for analysis and export — focused on metrics and trade-by-trade inspection rather than live execution.

**QuantLab and Backtest share the same underlying `IBacktestService`** and the same four strategy templates. The key difference is that QuantLab is oriented towards research (all trades displayed, CSV export always available) whereas Backtest is oriented towards pre-trade validation.

#### How It Works

Identical workflow to Backtest (select card → configure → run). Results panel shows the same seven metrics. The trade list displays all trades with no row cap, making it more suitable for detailed analysis.

#### Metrics Panel

- Total Trades, Win Rate (%, coloured), Total P&L (coloured), Sharpe Ratio
- Max Drawdown, Average P&L per Trade, Final Capital

Sharpe Ratio colouring: green if > 1.0, yellow otherwise, grey if N/A.

---

### 8. Order Book 📋

**Purpose:** Manual order management. View open and today's filled orders, place new market orders, and cancel existing working orders.

#### How It Works

1. On load, fetches the active account's open orders and today's filled orders.
2. Open orders grid shows pending/working orders with contract, side (Buy = blue, Sell = red), quantity, status, and placement time.
3. Filled orders grid shows today's completed trades with fill price and time.
4. To place a new order: enter contract ID, quantity, select side, click "Place Buy" or "Place Sell".
5. Orders are always market type — no price entry.
6. To cancel a working order: select it in the grid, click "Cancel Order".
7. Refresh button manually reloads both grids.

#### Notes

- Filled orders are filtered to **today only** (from midnight to now).
- Account is auto-selected from the first available active account (follows Dashboard selection).
- No limit order support — manual order placement is market-only.

---

### 9. Test Trade 🧪

**Purpose:** Diagnostic test page for validating that order placement, bracket submission, and API connectivity work correctly. **This is not a live trading management tool** — it does not monitor or adjust the position after entry.

#### What It Does

1. **Trend analysis**: Fetches 1-hour bars from the ProjectX history API and runs a weighted EMA/RSI scoring model (EMA21/EMA50 crossover, RSI14 momentum, candle pattern). Displays Up/Down probability percentages.
2. **Test order placement**: Clicking "Test BUY" or "Test SELL" places a single market order with SL and TP brackets attached.
3. **Bracket submission**: SL and TP are converted from dollars to ticks and submitted as bracket orders **at the time of the entry order** (server-side brackets).
4. **Fill confirmation**: Fill confirmation arrives via the UserHub WebSocket. A 15-second safety window resets the "order pending" flag if the WebSocket event is delayed.

#### What It Does NOT Do

- **No active SL management**: Once the order is placed, the stop loss is held by the ProjectX server. The view does not trail, adjust, or monitor the SL after entry.
- **No position monitoring**: There is no live P&L display or position status panel.
- **No OCO management**: The SL and TP are bracket orders submitted with the entry. Use the **Order Book** view to cancel them.

#### TopStepX Bracket Behaviour (UAT-confirmed)

| Parameter | Behaviour |
|---|---|
| SL bracket type | `4` (Stop Market) — only type supported by ProjectX |
| TP bracket type | `1` (Limit) |
| Long SL ticks | Negative (below entry price) |
| Short SL ticks | Positive (above entry price) |
| Long TP ticks | Positive (above entry price) |
| Short TP ticks | Negative (below entry price) |

#### Inputs

| Control | Description |
|---|---|
| Contract | Select from favourite instruments (ProjectX futures code) |
| Contracts | Number of contracts |
| SL | Stop-loss in **dollars** (converted to ticks at placement) |
| TP | Take-profit in **dollars** (converted to ticks at placement) |
| Test BUY / Test SELL | Places the order immediately |

#### Notes

- Default values: 1 contract, SL = $100, TP = $1,000 (10:1 R:R).
- Dollar-to-ticks conversion: `ticks = Ceiling(dollars ÷ (tickValue × contracts))`.
- The trend analysis is deterministic (EMA/RSI) — no AI token cost.
- The trend analysis score is **informational only** — clicking BUY or SELL places the order regardless of the score.

---

### 10. API Keys 🔑

**Purpose:** Credential management for all connected services. Stores keys for TopStepX, Claude AI, Binance, and future broker slots.

#### How It Works

1. On load, credentials are read from `ApiKeyStore` (file-backed, never written to `appsettings.json`).
2. User edits credentials for any service.
3. Click "Save" to persist all credentials back to the store.
4. The eye icon toggles visibility of all key fields (show / hide).

#### Credential Sections

| Section | Fields |
|---|---|
| TopStepX | Username, API Key |
| Claude AI | Org ID, API Key |
| Binance | API Key, Secret Key |
| Future 1–4 | Label, Username, API Key (reserved for future platforms) |

#### Notes

- Credentials are **never logged** to the debug console.
- No validation is performed on save — the user is responsible for key correctness.
- Future slots 1–4 are reserved for planned integrations (e.g., Binance live trading — BKL-001).

---

## Services Reference

| Service | Location | Purpose |
|---|---|---|
| `TradingSessionContext` | `Core` | Global account context propagated to all engines |
| `TopStepXInstrumentCatalog` | `Services/Trading` | Caches live tick specs and front-month contract IDs from ProjectX API (15-min TTL) |
| `FavouriteContracts` | `Core/Trading` | Master instrument list with TopStepX specs (tick sizes, tick values, minimum stops) |
| `TickMath` | `Core/Trading` | Tick↔price conversions, min-stop clamping |
| `StrategyExecutionEngine` | `Services/Trading` | Hydra / Asset Bassett engine |
| `SniperExecutionEngine` | `Services/Trading` | Sniper engine (3-EMA Cascade + pyramiding) |
| `CryptoStrategyExecutionEngine` | `Services/Trading` | CryptoJoe per-asset engine (MBT, GMET) |
| `PumpNDumpExecutionEngine` | `Services/Trading` | 3-bar scalping engine |
| `BacktestService` | `Services` | Runs historical strategy simulations |
| `BarIngestionWorker` | `Services` | Background worker that polls and persists bar data to SQLite |
| `TokenRefreshWorker` | `Services` | Proactively refreshes the ProjectX JWT token before expiry |

---

## License

Proprietary. All rights reserved.
