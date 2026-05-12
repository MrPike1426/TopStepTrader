# TopStepTrader — AI-Augmented Futures Trader

A WPF desktop application for live and simulated trading on **TopStepX (CME Micro Futures)**, with AI-augmented signals, automated strategy engines, and multi-asset monitoring.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Getting Started](#getting-started)
4. [Views](#views)
   - [Dashboard](#1-dashboard-)
   - [Hydra](#2-hydra-)
   - [Pump-n-Dump](#3-pump-n-dump-)
   - [CryptoJoe](#4-cryptojoe-)
   - [SuperTrend+ Autopilot](#6-supertrend-autopilot-)
   - [Trade History](#7-trade-history-)
   - [Test Trade](#8-test-trade-)
   - [API Keys](#9-api-keys-)
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
├── TopStepTrader.Services/     # Business logic — engines, market data, workers
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

### TopStepX Account & API Key

This application trades exclusively through **TopStepX** (powered by the ProjectX API).

1. Create a TopStepX account at [https://www.topsteptrader.com](https://www.topsteptrader.com)
2. Once your account is active, log in to the **TopStepX Member Portal**
3. Navigate to **API / Developer Settings** and generate an API key
4. Note down your **account email address** (username) and the generated **API key** — these are the only two credentials the application requires

> **Practice vs Live:** TopStepX provides separate Practice (simulated) and Live (funded) account environments. The application will list all accounts available to your credentials on the Dashboard — select the appropriate one before starting any engine.

### Configuration

1. Copy `src/TopStepTrader.UI/appsettings.template.json` → `src/TopStepTrader.UI/appsettings.json`
2. Open the application and navigate to **API Keys** in the sidebar
3. Enter your TopStepX **email address** and **API key**, then click Save
4. On the **Dashboard**, select the active account — this context propagates to all engines

Credentials are never stored in `appsettings.json`; they are saved to `%LOCALAPPDATA%\TopStepTrader\apikeys.json` on your local machine only.

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

Default settings: **Multi-Confluence strategy · per-asset bar timeframe (see below) · ADX ≥ 25 · ClaudeTrader fixed brackets (50-tick SL / 25-tick TP)**

#### How It Works

1. User selects account and persona (Lewis / Damian / Joe) to pre-fill risk parameters.
2. The strategy is pre-selected as Multi-Confluence on launch. The user can switch via the strategy tile grid.
3. For each strategy-timeframe bar close, a confidence score (0–100%) and direction are calculated per instrument. Signal evaluation always uses completed strategy-timeframe bars.
4. Between strategy-bar evaluations, the indicator grid (Close, Ichimoku, ADX, EMA21/50) refreshes every ~15 seconds from live 15-second bar data without affecting signal scores.
5. New entries are blocked for equity index futures (MES) outside London main session (08:30–13:30 UTC) and US main session (14:00–20:00 UTC).
6. When confidence exceeds the threshold and entry conditions are met, the engine opens a trade automatically.
7. Per-asset cards update in real-time showing confidence, direction, live P&L, and bracket prices.

#### Multi-Confluence Per-Asset Bar Timeframes

| Asset | Instrument | MC Bar Timeframe |
|---|---|---|
| OIL | MCLE (Micro Crude) | 5 min |
| GOLD | MGC (Micro Gold) | 10 min |
| SPX500 | MES (Micro S&P) | 5 min |
| EUR/USD | M6E (Micro Euro FX) | 10 min |
| BTC | MBT (Micro Bitcoin) | 15 min |

#### Strategy Presets

Each button pre-configures indicator periods, TP/SL, and leverage for the whole asset roster.

#### ATR Tier Buttons

ATR tiers control bracket sizing (`SlMultipleOfN` / `TpMultipleOfN`) only. **Live orders always use fixed ClaudeTrader ticks** (`DefaultSlTicks = 50`, `DefaultTpTicks = 25` from `appsettings.json`).

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

### 3. Pump-n-Dump 💥

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

### 4. CryptoJoe ☕

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

### 5. Backtest 🔬

_Removed (ARCH-17). The Backtest tab, Maximum Effort runner and Pinned Trades have been deleted; live SuperTrend+ Autopilot is the supported research/trading workflow._

---

### 6. SuperTrend+ Autopilot 📈

**Purpose:** Multi-asset automated trading engine driven by the SuperTrend indicator, ADX trend-strength, and Directional Indicators. Monitors up to all instruments in `FavouriteContracts` simultaneously, manages up to three concurrent position slots (one trade per instrument at a time), and advances each open position through a six-phase stop management system.

#### How It Works

1. User selects account, bar timeframe (5min / 15min / 1hr), entry mode (Confirmed or Early), TP multiple, and SuperTrend ATR multiplier.
2. Click **Start Monitoring** to begin a 15-second polling loop across all watchlist instruments.
3. For each bar close the engine computes SuperTrend, ADX(14), +DI, and −DI. A signal fires when all three gates pass: SuperTrend direction, DI confirmation (+DI > −DI for long), and ADX ≥ minimum threshold (default 25).
4. When a signal qualifies, the engine allocates an idle slot and places a market order with a bracket stop-loss at the SuperTrend line and (optionally) a take-profit at the configured R multiple.
5. Every monitoring tick the open slot is updated: the SuperTrend stop ratchets in the trade direction, and the degradation scorer evaluates seven exit signals. When the scorer breaches the exit threshold the position is closed.
6. If the Claude AI toggle is enabled, a `PreTradeCheckAsync` call to Claude Haiku gates each order. A VETO suppresses the instrument for 15 minutes.

#### Entry Modes

| Mode | Description |
|---|---|
| **Confirmed** | Signal must align on a fully closed bar — fewer false entries, slightly later timing |
| **Early (Multi-Signal)** | Multiple DI/ADX conditions evaluated mid-bar for earlier entries on fast-moving instruments |

#### Position Slots

Slots are allocated based on ADX band (trend strength):

| ADX Range | Max open slots | Coffee Level |
|---|---|---|
| 25–39 | 1 | ☕ L1 Decaff |
| 40–59 | 2 | ☕☕ L2 Latte |
| 60+ | 3 | ☕☕☕ L3 Espresso |

A new slot only opens on the next bar after ADX rises into the next band. No two slots open on the same bar. Slots are blocked when degradation is Amber (warning) or higher.

#### Six-Phase Stop Management

Each open slot advances through phases as profit grows. R = initial risk distance (entry price minus stop at entry).

| Phase | Profit threshold | Stop level |
|---|---|---|
| Initial | < 0.5R | SuperTrend line (ratchets with each bar) |
| Breakeven | ≥ 0.5R | Exact entry price |
| ProfitLock | ≥ 1R | Entry + 0.3R |
| ProfitTrail | ≥ 1.5R | ATR-based trailing stop |
| Harvest | ≥ 2R | Locked at entry + 1.5R |
| FreeRide | ≥ 3R | Locked at entry + 2R |

The stop never retreats.

#### Nine Degradation Signals

Scored each bar. Combined score at or above the exit threshold triggers immediate close.

| Signal | Condition | Weight |
|---|---|---|
| E1 SuperTrend Flip | Trend reversed | 8 (immediate exit) |
| E2 Momentum Slowing | Price converging toward ST line over 3 bars | 2 |
| E3 ADX Declining | Trend strength fading from entry ADX | 2 |
| E4 DI Compression | +DI / −DI spread narrowing | 2 |
| E5 DI Crossover | DI lines crossing — leading reversal signal | 4 |
| E6 Rejection Bar | Price rejected at extension — large wick | 2 |
| E7 ATR Contraction | ATR < 80% of entry ATR — momentum fading | 1 |
| E8 VWAP Cross | Price crosses below/above session-anchored VWAP | 2 |
| E9 RSI Hidden Divergence | Price higher high but RSI lower high (14-period, 3–5 bar window) | 3 |

Amber (warning) = score building, no new slots opened. Red (exiting) = exit triggered.

#### Watchlist Panel

The top panel shows all monitored instruments with live trend arrow, signal state, ADX strength (coffee scale), +DI/−DI values, and a plain-language explanation of the current signal.

#### Slot Boxes

Three slot boxes (Slot 1 / 2 / 3) show:
- Instrument, direction, and entry time
- Live P&L (flashes white on change)
- Entry price, stop loss, take profit, and current stop phase (in gold)
- Idle monitoring status when no position is open
- Per-slot **AI Sense Check** button (manual mid-trade Claude Haiku review)
- AI verdict panel (PROCEED / CAUTION / VETO with explanation)

#### AI Check History

A scrolling log below the slot boxes records every AI pre-trade and mid-trade check with timestamp, instrument, and colour-coded verdict (green = proceed, yellow = caution, red = veto).

#### Key Controls

| Control | Description |
|---|---|
| Account | TopStepX account selector |
| Timeframe | Bar resolution for SuperTrend/ADX computation (5min, 15min, 1hr) |
| Confirmed / Early | Entry mode toggle |
| TP | Take-profit multiple of initial risk R (or "None / flip only") |
| ST× | SuperTrend ATR multiplier (2.0 / 2.5 / 3.0) |
| Start / Stop Monitoring | Starts or stops the 15-second monitoring loop |
| AI On / Off | Enables Claude Haiku pre-trade gate (resets to Off on restart) |
| AI Sense Check (per slot) | Manually triggers a mid-trade AI review for that slot |

#### Notes

- On start, the engine scans open broker positions and onboards any live positions that match a watchlist instrument into the appropriate slot (orphan recovery).
- Re-entry cooldown: after a slot closes, the instrument is blocked from re-entering until at least one full bar has elapsed.
- **Monday morning HTF gate (FEAT-37):** On Monday before 08:00 UK local time (BST-aware), a new entry is only allowed if the 1-hour SuperTrend direction agrees with the signal direction. This filters gap-driven phantom trends caused by thin liquidity and price gaps from the Sunday open. The gate degrades gracefully — if 1H bar data is unavailable, the entry is allowed. Controlled by `SuperTrendPlusConfig.MondayMorningHtfFilterEnabled` (default `True`).
- AI VETO suppresses the instrument from new checks for 15 minutes to avoid repeatedly calling the API on a blocked setup.
- Scale-in: when a slot advances to ADX L2, the engine adds +1 contract; at L3 it adds +2 contracts on top of the initial position.

---

### 7. Trade History 📋

**Purpose:** Scrollable log of every trade placed by the strategy engines. Mirrors the TopStepX Trades panel with full trade lifecycle data (entry, exit, P&L, commission, fees). Read-only — order placement is not available here (use Test Trade for manual orders).

#### How It Works

1. On load the view recovers any open records from the previous session: it queries the last 48 hours of fills from TopStepX and closes any unresolved `IsOpen` records, computing P&L from the actual exit fill.
2. After recovery, the last 100 trades are loaded and displayed, newest first.
3. Trades are written to the database in real time as engines open and close positions — the Refresh button reloads the grid with the latest data.
4. Filters can be applied at any time; click **Apply** to reload with the active filter set.

#### Filter Bar

| Filter | Input | Behaviour |
|---|---|---|
| Symbol | Text box | Match by instrument name (e.g. `/M6E`, `MGC`) — blank = all |
| Strategy | Text box | Match by strategy name (e.g. `SuperTrend+`) — blank = all |
| Persona | Text box | Match by persona (`Lewis`, `Damian`, or `Joe`) — blank = all |
| P&L | Dropdown | `All` / `Winners` (P&L > 0) / `Losers` (P&L ≤ 0) |

#### Trade Grid Columns

| Column | Source | Notes |
|---|---|---|
| ID | TopStepX Trade ID (fill ID) | Falls back to entry order ID until resolved |
| Symbol | Instrument name | e.g. `/M6E` |
| Dir | Long / Short | Coloured green / red |
| Size | Contract count | |
| Leverage | `MaxScaleIns` from persona | 1× = Lewis, 2× = Damian, 3× = Joe |
| Strategy | Engine strategy name | e.g. `SuperTrend+` |
| Persona | Active persona at entry | |
| Entry Time | UTC → local | Full date + time |
| Exit Time | UTC → local | Time only (or "Open" / "—") |
| Duration | Elapsed from entry to exit | Formatted as Xh Xm / Xm Xs / Xs |
| Entry Px | Fill price | Back-corrected from broker snapshot when available |
| Exit Px | Derived from P&L and tick math | "Live" while open |
| P&L | Realised profit/loss in USD | Green (profit) / red (loss) / grey (open) |
| Commission | TopStepX commission | $0.50 × contracts |
| Fees | Exchange + NFA fees | `FavouriteContracts.RoundTripFee` × contracts |
| Exit | Reason the trade closed | e.g. `ST Flip`, `Exit Signal`, `Recovered` |

#### Crash Recovery

On every app startup, `TradeRecordService.RecoverOpenTradesAsync` scans `TradeHistory.db` for any records still marked `IsOpen = True`. For each one it queries the TopStepX `/api/Trade/search` endpoint for fills in the last 48 hours, finds the exit fill (opposite side, timestamp after entry), computes realised P&L from `FavouriteContracts.PxPointValue`, and closes the record with `ExitReason = "Recovered"`.

#### Notes

- The grid is populated exclusively by strategy engines — no manual order entry.
- Open trades are shown at 70% opacity with `"…"` in the P&L column and `"Live"` in the Exit Price column.
- The status bar at the bottom shows the total trade count and number of open positions.

---

### 8. Test Trade 🧪

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

### 9. API Keys 🔑

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
| `CryptoStrategyExecutionEngine` | `Services/Trading` | CryptoJoe per-asset engine (MBT, GMET) |
| `PumpNDumpExecutionEngine` | `Services/Trading` | 3-bar scalping engine |
| `SuperTrendPlusViewModel` | `UI/ViewModels` | SuperTrend+ Autopilot — multi-asset monitoring, slot allocation, stop phasing, AI gate |
| `SlotManager` | `Core/Trading` | Manages the three concurrent position slots; enforces ADX-band open/close rules |
| `ExitSignalEngine` | `Services/Trading` | Evaluates nine degradation signals per bar and returns a scored exit decision |
| `PositionSlot` | `Core/Trading` | Plain state object for a single open slot (entry, SL, TP, health, stop phase) |
| `SuperTrendPlusConfig` | `Core/Settings` | All configurable thresholds (ADX bands, stop-phase R multiples, degradation weights) |
| `TradeRecordService` | `Services/Trades` | Records every engine-placed trade to `TradeHistory.db`; resolves TopStepX trade IDs and performs crash recovery on startup |
| `BarIngestionWorker` | `Services` | Background worker that polls and persists bar data to SQLite |
| `TokenRefreshWorker` | `Services` | Proactively refreshes the ProjectX JWT token before expiry |

---

## License

Proprietary. All rights reserved.
