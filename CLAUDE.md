# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Restore and build
dotnet restore
dotnet build

# Run all tests
dotnet test

# Run a specific test class
dotnet test --filter "FullyQualifiedName~TopStepXTick"
dotnet test --filter "FullyQualifiedName~CryptoStrategyEngine"

# Run the UI application
dotnet run --project src/TopStepTrader.UI/TopStepTrader.UI.vbproj
```

The solution targets `net10.0-windows` and requires x64. All projects are VB.NET. The test framework is xUnit.

## Self-Verification — Required After Every Code Change

After **every** file edit, run these two commands and fix any failures before responding "done":

```bash
# Step 1 — if the app is running and DLLs are locked, build Services only:
dotnet build src/TopStepTrader.Services/TopStepTrader.Services.vbproj --no-restore -v q

# Step 2 — otherwise build the full solution:
dotnet build --no-restore -v q

# Step 3 — always run the full test suite:
dotnet test --no-build -v q
```

**Expected output:** `320 passed, 0 failed` (as of 2026-04-20). If the count changes after adding new tests, update this number.

**Rules:**
- Never leave the project in a broken build state.
- Never report a task complete until both build and test pass.
- If a test failure is pre-existing (not caused by the current change), flag it explicitly — do not silently ignore it.

## Ticket & Issue Tracking

All open work is tracked in two artefacts — no separate Priority Queue:

- **`Open_TICKETS.md`** — single source of truth: one row per open ticket, with explicit Priority (P0–P3) and Status. Replaces the old `REFACTOR_TRACKER.md`.
- **`tickets/<ID>.md`** — full description, problem, proposed fix, and acceptance criteria for each open ticket.
- **`Closed_Tickets.md`** — append-only log of every completed ticket.
- **`tickets/archive/<ID>.md`** — completed ticket files (moved here on close, never deleted).

These files are local-only (`Open_TICKETS.md`, `Closed_Tickets.md`, `tickets/`) and excluded from the GitHub remote via `.gitignore`.

### Priority Tags

| Tag | Meaning |
|---|---|
| P0 | Blocker — fix before next live session |
| P1 | High — fix this sprint |
| P2 | Medium — scheduled backlog |
| P3 | Low — nice-to-have / future |

### Ticket ID Prefixes

| Prefix | Use |
|---|---|
| `BUG-XX` | Defects in existing behaviour |
| `STRAT-XX` | Strategy logic changes / improvements |
| `TEST-XX` | Missing or insufficient test coverage |
| `QUAL-XX` | Code quality, naming, refactoring |
| `UX-XX` | UI/UX improvements |
| `ARCH-XX` | Architectural changes (DI, patterns, decomposition) |
| `OBS-XX` | Observability — logging, diagnostics, status lines |
| `FEAT-XX` | New features that don't fit another prefix |

Use the next sequential number within each prefix (check `Open_TICKETS.md` + `Closed_Tickets.md` for the current high-water mark).

### T-Shirt Size Estimates

Every ticket must carry a `**Size:**` estimate. Assess holistically across token burn, effort (build/test/review), and complexity:

| Size | Token burn | Effort | Complexity |
|---|---|---|---|
| **XS** | Single-line / rename | Trivial — no new tests needed | Obvious — no design decisions |
| **S** | < 50 lines changed | Low — 1–2 new tests | Straightforward — one clear approach |
| **M** | 50–150 lines | Medium — several tests, build check | Some decisions, limited knock-on risk |
| **L** | 150–400 lines | High — significant test work, review pass | Multiple components, non-trivial design |
| **XL** | 400+ lines or multi-file refactor | Very high — extensive testing, phased review | High complexity, architectural impact |

### Ticket File Format

```markdown
# [ID] Title

**Status:** Open | In Progress | Blocked | UAT-Pending  
**Category:** <category name>  
**Size:** XS | S | M | L | XL  
**Source:** UAT | Code-Review | Manual  
**Files:** `path/to/file.vb:line`

## Problem
<description of current behaviour and why it's wrong or missing>

## Proposed Fix
<approach or code description>

## UAT Evidence  *(omit if Source is not UAT)*
Session: YYYY-MM-DD, instrument, persona, conditions observed.
Log/repro: <paste relevant output>

## Acceptance Criteria
- [ ] item 1
- [ ] Build passes; all tests still pass
```

### Creating a New Ticket

1. Choose the correct prefix and next ID (check `Open_TICKETS.md` + `Closed_Tickets.md` for the high-water mark).
2. Create `tickets/[ID].md` using the schema above, including `**Size:**`, `**Source:**`, and `**Priority:**`.
3. Add a row to **`Open_TICKETS.md`** (sorted by priority then ID).

### Completing a Ticket

1. Verify build passes and all tests pass.
2. Move `tickets/[ID].md` → `tickets/archive/[ID].md` (do not delete it).
3. Append one row to **`Closed_Tickets.md`**.
4. Remove the row from **`Open_TICKETS.md`** and update its `Last updated` date.
5. Increment the **Done** count in the **Completion Summary** table inside `Open_TICKETS.md`.

### Running a Ticket

```
Execute [ID]
```

Claude loads `Open_TICKETS.md` + `tickets/[ID].md` + the referenced source files, implements the change, runs self-verification (build + tests), and completes all ticket artefacts as described above.

---

## Automated Agent Workflow

This section governs the **Ticket Handler** Claude agent (and any other automated agent) that commits fixes directly to the repository. Manual Claude sessions follow the same rules.

### Repository & Branch

| Item | Value |
|---|---|
| Remote | `origin` → `https://github.com/MrPike1426/TopStepTrader.git` |
| Target branch | **`clean-start`** — always, no exceptions |
| Push style | `git push origin clean-start` — **no pull requests**, direct push only |

**Do NOT create a worktree.** Claude Code creates an isolated worktree by default — override this behaviour. Always clone or check out the `clean-start` branch directly and commit there. Never create a branch named `worktree-*` or any other side branch.
git config set advice.addIgnoredFile false

```bash
# Confirm you are on the correct branch before making any changes:
git checkout clean-start
git pull origin clean-start
```

Never reference the old `eToroTrader` repository — it no longer exists.

### Commit Message Format

```
<type>(<ID>): <short imperative description>
```

| Type | When to use |
|---|---|
| `fix` | Bug fix (BUG-XX) |
| `feat` | New feature or strategy change (FEAT-XX, STRAT-XX) |
| `test` | Test coverage (TEST-XX) |
| `refactor` | Code quality / architecture (QUAL-XX, ARCH-XX) |
| `obs` | Observability / logging (OBS-XX) |
| `chore` | Tracker / documentation update only |

Examples:
```
fix(BUG-12): increase fetchCount to 80 to satisfy MinBarsRequired
chore(BUG-12): mark ticket resolved in Open_TICKETS.md
```

### Per-Ticket Procedure (automated)

1. Read `CLAUDE.md` (this file) and `Open_TICKETS.md`.
2. Pick the **highest-priority open row** (lowest P-number, then lowest ID) from `Open_TICKETS.md` (or the ticket explicitly requested).
3. Read `tickets/[ID].md` and all files listed under `**Files:**`.
4. Implement the fix following the code style and conventions in this file.
5. Run self-verification:
   ```bash
   dotnet build --no-restore -v q
   dotnet test --no-build -v q
   ```
   Fix any compilation errors or test failures before continuing.
6. Stage and commit the code change:
   ```bash
   git add -A
   git commit -m "fix(ID): <description>"
   git push origin clean-start
   ```
7. Complete ticket artefacts (see **Completing a Ticket** above):
   - Move `tickets/[ID].md` → `tickets/archive/[ID].md`
   - Append row to `Closed_Tickets.md`
   - Remove row from `Open_TICKETS.md`, update Done count + Last updated date
   ```bash
   git add -A
   git commit -m "chore(ID): mark ticket resolved in Open_TICKETS.md"
   git push origin clean-start
   ```
8. Run `/clear` to reset the context window.
9. Move to the next ticket **only if explicitly instructed** to process multiple tickets.

### Rules

- **One ticket at a time** unless the user says otherwise.
- Never open a pull request — push commits directly to the branch.
- If a task description is ambiguous, ask for clarification **before** making changes.
- Never leave the repository in a broken build or failing test state between commits.
- If a test failure is pre-existing (not caused by the current change), flag it explicitly — do not silently ignore it.

---

## Project Structure

Seven projects in `src/`:

| Project | Role |
|---|---|
| `TopStepTrader.Core` | Domain models, interfaces, enums, settings, pure trading logic (TickMath, FavouriteContracts) |
| `TopStepTrader.API` | HTTP/SignalR clients for TopStepX/ProjectX and Yahoo Finance; ProjectX token manager |
| `TopStepTrader.Data` | EF Core + SQLite (`TopStepTrader.db`); repositories |
| `TopStepTrader.ML` | ML model loading and technical indicators |
| `TopStepTrader.Services` | Business logic — trading engines, market data, backtest, background workers |
| `TopStepTrader.UI` | WPF MVVM desktop app |
| `TopStepTrader.Tests` | xUnit tests |

Dependency flow: UI → Services → (Core, Data, API, ML)

## Architecture

### Composition Root (`AppBootstrapper.vb`)
`src/TopStepTrader.UI/Infrastructure/AppBootstrapper.vb` is the single DI composition root. It:
- Loads `appsettings.json` for all configuration (credentials are managed separately by `ApiKeyStore`)
- Registers all layers via `AddDataServices()`, `AddApiServices()`, `AddApplicationServices()`
- Runs `EnsureCreated()` + `EnsureSchemaCurrent()` on first launch
- Starts the ML `ModelManager` with a `FileSystemWatcher` for hot-reload

### DI Registration
- `ServicesExtensions.vb` — business services
- `ApiServiceExtensions.vb` — HTTP clients and SignalR hubs

**Lifetime conventions:**
- **Singleton:** stateless/long-lived — `TopStepXInstrumentCatalog`, `TradingSessionContext`, `ApiKeyStore`, token manager, background workers
- **Scoped:** EF Core repositories and services that touch DbContext
- **Transient:** execution engines (`StrategyExecutionEngine`, `CryptoStrategyExecutionEngine`), `DiagnosticLogger`

### ViewModelLocator (MVVM Scoping)
`src/TopStepTrader.UI/ViewModels/ViewModelLocator.vb` creates a per-view `IServiceScope` for each ViewModel to ensure EF Core scoped services are resolved correctly. VMs are created on first property access and reused.

### Broker
`IOrderService` is the unified broker interface. `ProjectXOrderService` is registered directly as `IOrderService` — there is no dispatcher. All engines call `IOrderService` which routes to TopStepX via the ProjectX REST API.

`FavouriteContracts.vb` holds the master instrument list with TopStepX specs (ProjectX contract IDs, tick sizes, tick values). The `EToroContractId` and related eToro fields are retained in source for potential future use but are not active.

**TopStepX is the sole trading platform.** `FavouriteContract.GetPointValue(broker)` and `GetTickSize(broker)` return the correct TopStepX values (e.g. MGC = $10/pt, MES = $5/pt). Always call these via `_session.ActiveBroker` — never hardcode tick specs.

### TopStepX / ProjectX Specifics
- **No take-profit brackets** — `ProjectXOrderService` never sends `takeProfitBracket` when `ManageTakeProfit = False` (the default)
- **Tick-based stops** — SL is always expressed in ticks, not price. `TickMath.vb` handles all tick↔price conversion
- **Min-stop enforcement** — `TopStepXInstrumentCatalog.ClampStopTicksAsync()` enforces API-provided minimums (15-min TTL cache, falls back to `FavouriteContracts` local defaults)
- **Synthetic OCO** — `FlattenContractAsync()` cancels resting SL bracket orders (type=4) before closing the position to prevent double-trigger
- **Bracket tick sign convention** — long SL ticks are negative, short SL ticks are positive (ProjectX convention)
- **Stale bar guard** — `StrategyExecutionEngine` declares a bar stale when `barAgeMins > TimeframeMinutes × 3` (e.g. 15 min for 5-min bars). The 3× multiplier accounts for the bar timestamp being the *start* of the period, Yahoo's 1–5 min API propagation lag, and the 30-second engine poll jitter — a stale threshold of 1× or 2× would produce false market-closed events during normal active trading. On the fresh→stale transition (market close) it fires exactly one `ConfidenceUpdated` event with `IsMarketClosed = True` and suppresses all entry signals. Position monitoring (broker sync + trail) continues. The `IsMarketClosed` flag causes Hydra/AssetBassett tiles to show `"⏸ Closed"`. The guard resets (`_lastBarWasStale = False`) on engine `Start()`.
- **Live P&L — `GetLatestPriceAsync`** — TopStepX REST and SignalR both return `openPnL = 0` for futures. Real-time P&L is computed from `(currentPrice - entryPrice) × DollarPerPoint`. Price priority: (1) `_lastQuotePrice` — sub-second MarketHub tick; (2) `_lastBarClose` — updated every 15 s via `IBarIngestionService.GetLatestPriceAsync` (`PXHistoryClient.RetrieveBarsAsync(unit:=1, unitNumber:=15, limit:=5)`). Acceptable residual drift for M6E at 1× is $1–$1.50 (bid/ask spread + bar-close-to-fill lag). **Entry price correction** — `PlaceBracketOrdersAsync` stores `lastClose` as `_lastEntryPrice` (estimate). On the first REST sync after fill, if `snapshot.OpenRate` differs from the estimate by more than 1 tick, `_lastEntryPrice` is corrected to the actual broker fill rate (logged as `📌 Entry corrected`). **MarketHub roll safety** — `OnMarketQuoteReceived` accepts quotes by exact `ContractId` OR root-symbol match (e.g. `M6E`) so a quarterly roll from U26 → M26 does not silently freeze `_lastQuotePrice` at 0.

### Configuration
Copy `appsettings.template.json` → `appsettings.json`. Key sections: `ProjectX` (TopStepX endpoints), `Risk`, `Trading`, `ML`, `Ingestion`, `Personas`.

Settings are bound via `IOptions<T>`:
- `ProjectXSettings` → `"ProjectX"` section
- `RiskSettings` → `"Risk"` section
- `PersonasSettings` → `"Personas"` section (factory defaults for Lewis/Damian/Joe; used by "Reset to Defaults" on the Persona config page)

### Background Services
Two `IHostedService` workers run continuously:
- `TokenRefreshWorker` — proactively refreshes the ProjectX JWT token before expiry
- `BarIngestionWorker` — polls market data at configurable intervals and persists bars to SQLite

## Key Files for Common Tasks

| Task | File |
|---|---|
| Add a new view/screen | Create `Views/XView.xaml` + `ViewModels/XViewModel.vb`, register in `ViewModelLocator.vb` |
| Add a new service | Implement interface in `Core/Interfaces/`, add to `ServicesExtensions.vb` |
| Add a new broker client | Add HTTP client in `API/Http/`, register in `ApiServiceExtensions.vb` |
| Instrument tick specs | `Core/Trading/FavouriteContracts.vb` |
| Tick math | `Core/Trading/TickMath.vb` |
| Risk profiles (hardcoded defaults) | `Core/Trading/RiskProfile.vb` |
| Persona profiles (runtime, editable) | `Core/Models/PersonaProfile.vb` · `Core/Interfaces/IPersonaService.vb` · `Services/Personas/PersonaService.vb` |
| Persona default values (appsettings) | `Core/Settings/PersonasSettings.vb` · `appsettings.json "Personas"` section |
| Persona SQLite persistence | `Data/Entities/PersonaSettingsEntity.vb` · `Data/AppDbContext.vb` (DbSet + EnsureSchemaCurrent) |
| Strategy defaults | `Core/Trading/StrategyDefaults.vb` |
| Strategy condition/indicator enums | `Core/Enums/StrategyConditionType.vb` · `Core/Enums/StrategyIndicatorType.vb` |
| Technical indicator math | `ML/Features/TechnicalIndicators.vb` |
| Test tick/stop logic | `Tests/Trading/TopStepXTickTests.vb` — comprehensive test helpers (`FakeCatalogLocalOnly`, `FakeCatalogWithMinStop`) |
| Bar timeframe enum | `Core/Enums/BarTimeframe.vb` |
| Bar download + cache | `Services/Market/BarCollectionService.vb` |
| Backtest engine | `Services/Backtest/BacktestEngine.vb` |
| QuantLab research page | `UI/ViewModels/QuantLabViewModel.vb` · `UI/Views/QuantLabView.xaml` |
| Sniper live engine | `UI/ViewModels/SniperViewModel.vb` · `UI/Views/SniperView.xaml` · `Services/Trading/SniperExecutionEngine.vb` |
| Claude AI integration | `Services/AI/ClaudeReviewService.vb` |
| API key management | `UI/Views/ApiKeysView.xaml` · `UI/Views/ApiKeysView.xaml.vb` |

## Persona System

### Overview
Personas are the primary way to control risk behaviour across the entire app. There are three: **Lewis** (Risk Averse), **Damian** (Moderate), and **Joe** (Aggressive).

**Two layers:**
1. **`RiskProfile.vb`** (`Core/Trading/`) — hardcoded `Shared ReadOnly` instances. These are the absolute fallback and are never modified at runtime.
2. **`PersonaService`** (`Services/Personas/PersonaService.vb`) — Singleton that holds the _current effective_ values. On startup it loads from SQLite; if no saved row exists it falls back to `appsettings.json "Personas"` section. All ViewModels call `_personaService.GetProfile(name)` — **never** `RiskProfile.Lewis/Damian/Joe` directly.

**Where personas are applied:**
- `HydraViewModel.ApplyRiskProfile()` — pre-fills Hydra form fields
- `AssetBassettViewModel.ApplyRiskProfile()` — pre-fills AssetBassett form fields
- `BacktestViewModel.ApplyPersona()` — pre-fills backtest config form
- `BacktestViewModel.ExecuteMaximumEffort()` — iterates `_personaService.GetAllProfiles()` (all 3)

Changes saved on the Persona config page update the global cache immediately. Running engines are **not** affected mid-session — changes take effect on the next persona button tap.

### Persona Config Page (CONFIG → Personas)
Navigate via sidebar **👤 Personas** (under the CONFIG section). Shows three side-by-side cards.

- **💾 Save Persona** — writes to `PersonaSettings` SQLite table; updates in-memory cache
- **↺ Reset to Defaults** — deletes the SQLite row; reverts to `appsettings.json` values

Default values in `appsettings.json` (the factory reset target):

### Risk Profiles — Lewis / Damian / Joe

The values below are the factory defaults (from `appsettings.json`). They may be overridden via the Persona page and persisted in SQLite.

Three built-in personas. Each controls capital, ADX gate, signal confidence, and ATR bracket width. They are **independent** of the ATR tier selector (Tight/Standard/Wide), which only adjusts bracket elasticity.

The `Leverage` field is carried in `PersonaProfile` and `StrategyDefinition` but is **not used by `StrategyExecutionEngine`** for TopStepX order sizing — futures position size is always `Quantity` contracts (typically 1). The `Leverage` UI input has been removed from CryptoJoeView and AssetBassettView. It remains editable on the Persona config page for potential future brokers.

| Property | Lewis (Averse) | Damian (Moderate) | Joe (Aggressive) |
|---|---|---|---|
| Capital per trade | $200 | $500 | $1,000 |
| Leverage (inactive for TopStepX) | 5× | 5× | 10× |
| Max scale-ins | 1 | 2 | 3 |
| SL multiple of N | 1.5 | 1.0 | 0.75 |
| Leveraged SL multiple of N | 2.5 | 2.0 | 1.5 |
| TP multiple of N | 3.0 | 2.5 | 2.0 |
| R:R ratio | 1:2.0 | 1:2.5 | 1:2.67 |
| Min ADX | 25 | 20 | 15 |
| Min confidence | 90% | 80% | 70% |

**N** = ATR(14) × dollar-per-point for the instrument. SL/TP distances are expressed as multiples of N so brackets automatically scale with market volatility.

### How Lewis Identifies and Manages a Trade (plain-English)

Lewis is the most patient and defensive of the three personas. Here is his full trade lifecycle:

**1. The trend gate — ADX ≥ 25**
Before considering any trade, the system checks ADX (a 0–100 score of how strongly the market is trending). Lewis only acts when ADX ≥ 25 — a clear, committed directional move. Sideways or choppy markets are ignored entirely. Most of the time, nothing happens.

**2. Signal scoring — 90% minimum**
When the trend gate passes, the selected strategy scores the current bar across multiple indicators (moving averages, momentum, volume, etc.) on a 0–100 scale. Lewis requires a score of **90 or above** before entering. This means the evidence must be overwhelming — most signals that Damian or Joe would take, Lewis will not.

**3. Position sizing — $200 capital, 1 contract**
Lewis commits $200 per trade as a risk budget. For TopStepX futures, the engine trades 1 contract (defined by `StrategyDefinition.Quantity`). The ATR-based SL in ticks is derived from `SlMultipleOfN × ATR × (tickValue / tickSize)`.

**4. Stop loss — 1.5 × ATR below entry**
The moment the trade opens, a stop loss is placed automatically at **1.5 × ATR(14)** below the entry price. ATR is the instrument's average daily price range. If Gold normally moves $8/day, Lewis's stop sits $12 away. The trade is automatically closed at a small loss if price reaches this level — no manual intervention required.

**5. Take profit — 3.0 × ATR above entry**
The take-profit target is set at **3.0 × ATR(14)** above entry — twice the stop distance. This 1:2 risk-to-reward ratio means Lewis can be wrong nearly half the time and still profit overall across many trades.

**6. One scale-in allowed**
If the trade moves in Lewis's favour, the system may add one additional position (`MaxScaleIns = 1`), effectively doubling down while the market is proving him right. Damian can do this twice; Joe three times.

**7. Trade management until close**
The trade runs on autopilot. If the ATR tier is Standard or Wide, the stop loss trails upward as price rises (locking in profit). The stop only ever moves in the trade's favour — it never widens. When price reaches the take-profit level, the trade closes automatically and the system returns to watching.

**Summary:** Lewis trades infrequently, requires strong conviction before entering, risks a small fixed amount, targets twice that amount in profit, and lets the computer handle everything from entry to exit.

## Backtest Architecture

### Bar Collection (`BarCollectionService.vb`)
Downloads historical bars from Yahoo Finance and caches them in SQLite. Key behaviours:

- **Cache hit**: returns immediately if ≥ 50 bars exist, span ≥ 80% of requested range, **and latest bar is less than 24 hours old**. The staleness check ensures weekly runs accumulate fresh bars over time (new bars appended via `INSERT OR IGNORE`).
- **Yahoo Finance symbol translation**: `BarCollectionService` translates contract symbol keys (e.g. `"GOLD.24-7"`) to Yahoo tickers (e.g. `"GC=F"`) before calling the API, using `FavouriteContracts.TryGetBySymbol(contractId)?.YahooSymbol`. Storage keys remain stable instrument IDs (do not roll quarterly). Falls back to the raw contractId so direct Yahoo codes also work.
- **Yahoo Finance limits** (enforced in `BacktestViewModel.GetYahooMaxDays`):
  - 1-min → 7 days
  - 5-min, 10-min, 15-min, 30-min → 60 days
  - 1-hr, 2-hr, 4-hr → 730 days
- **Non-native timeframes** — `TenMinute` uses 5-min source data; `TwoHour`/`FourHour` use 1-hr source data. Bars are stored under their own `BarTimeframe` key in SQLite, so they accumulate independently.

### BarTimeframe Enum (`Core/Enums/BarTimeframe.vb`)
Values are the DB discriminator (integer minutes):
`OneMinute=1`, `ThreeMinute=3`, `FiveMinute=5`, `TenMinute=10`, `FifteenMinute=15`, `ThirtyMinute=30`, `OneHour=60`, `TwoHour=120`, `FourHour=240`, `Daily=1440`

### Strategy Defaults (`Core/Trading/StrategyDefaults.vb`)
Only combined multi-indicator strategies are registered (design rule: single-indicator strategies excluded).
Default parameters: **Capital = $1,000 · Qty = 1** — TP and SL vary by strategy:

| Strategy | TP | SL |
|---|---|---|
| EMA/RSI Combined | $20 | $15 |
| Multi-Confluence Engine | $20 | $15 |
| BB Squeeze Scalper | $8 | $4 |
| LULT Divergence | $20 | $10 |
| VIDYA Cross | $20 | $10 |
| Naked Trader | $20 | $10 |
| Double Bubble Butt | $20 | $10 |

`MinConfidence` is stored in the ViewModel as a whole-number percentage (e.g. `"90"`) and divided by 100 before being passed to the engine.

### Strategy Condition / Indicator Enums
`Core/Enums/StrategyConditionType.vb` and `Core/Enums/StrategyIndicatorType.vb` enumerate all supported strategies. The integer values are used as DB discriminators and must not change once data is stored.

**Trading strategies (Hydra / AssetBassett / Backtest):**

| Value | ConditionType | Description |
|---|---|---|
| 6 | `EmaRsiWeightedScore` | Six-signal EMA/RSI weighted score (buy >60%, sell <40%) |
| 7 | `TripleEmaCascade` | 3-EMA Cascade on 1-min bars — Sniper strategy |
| 8 | `MultiConfluence` | Ichimoku + EMA21/50 + MACD + StochRSI + DMI/ADX (all 7 must align) |
| 9 | `LultDivergence` | WaveTrend Anchor/Trigger divergence — 6-step gate, NQ 5-min, 11:00–17:00 UTC |
| 10 | `BbSqueezeScalper` | Dual-mode BB scalper — Squeeze Breakout or Band Bounce |
| 15 | `VidyaCross` | VIDYA CMO-adaptive EMA crossover + delta-volume filter |
| 16 | `NakedTrader` | 4-vote consensus: EMA(9/21), MACD(8,17,9), DMI/ADX(14), VWAP |
| 17 | `DoubleBubbleButt` | Double Bollinger Bands (±1.0 SD inner / ±2.0 SD outer) zone system |

**QuantLab research-only strategies (not live-traded):**

| Value | ConditionType | Description |
|---|---|---|
| 11 | `ConnorsRsi2` | RSI(2) mean reversion filtered by SMA(200); 67–72% win rate |
| 12 | `SuperTrend` | ATR(10)×3.0 trend flip; 40–52% win rate |
| 13 | `DonchianBreakout` | 20-bar Turtle breakout; 30–40% win rate |
| 14 | `BbRsiMeanReversion` | BB(20,2) + RSI(14) dual-confirmation reversion; 55–65% win rate |
| 17 | `DoubleBubbleButt` | Also available in QuantLab |

### ATR-Based SL/TP Mode (`BacktestConfiguration.UseAtrMode`)
When `UseAtrMode = True`, stop loss and take profit are expressed as ATR multiples rather than fixed dollar amounts. At each entry the engine pre-calculates `ATR(14)` across all bars and sets:
- `stopDelta = ATR(14) × config.SlAtrMultiple`
- `tpDelta   = ATR(14) × config.TpAtrMultiple`

This allows meaningful cross-instrument comparison — a fixed $15 SL is inadequate for Gold or Crude, but 1.5 × ATR scales correctly for any instrument. ATR mode is always active in Maximum Effort runs (each persona supplies its own multiples). Single-run backtests toggle ATR mode via the "ATR × Stops" checkbox on the Run Backtest tab.

`dynEnabled` in the engine is True whenever `UseAtrMode` is True or any of `TrailingStopEnabled`, `BreakEvenOnHalfTpEnabled`, `ExtendTpEnabled` is True.

### ATR Elasticity Tiers (Hydra and AssetBassett)
Both views expose three ATR bracket tier buttons — **Tight**, **Standard**, **Wide** — that adjust `SlMultipleOfN` / `TpMultipleOfN` independently of the persona. All three tiers maintain a 1:2 R:R ratio:

| Tier | SL multiple | TP multiple |
|---|---|---|
| Tight | 0.75 × N | 1.5 × N |
| Standard | 1.5 × N | 3.0 × N |
| Wide | 2.5 × N | 5.0 × N |

Persona controls capital, leverage, ADX gate, and confidence. Tier controls bracket elasticity (how many ATR units the stops span).

### BacktestView — Four Tabs

**Tab 1 — Run Backtest**: Persona selector (Lewis / Damian / Joe) at the top of the config form pre-fills Capital, Min ADX, Min Confidence, and ATR multiples (when ATR mode is on). Runs the selected strategy across **8 timeframes** (1min, 5min, 10min, 15min, 30min, 1hr, 2hr, 4hr) simultaneously. Each timeframe's effective date range is clamped to Yahoo's limit. Results appear in a live-filling, **sortable** grid (column headers are clickable; `TotalPnLRaw As Decimal` on `TimeframeResultRowVm` drives numeric sort on the P&L column). Default date range: today − 180 days (clamped per timeframe).

**Tab 2 — Maximum Effort!** (Deadpool button): Runs every combination of **3 personas × 5 instruments × 7 strategies × 8 timeframes = 840 backtest runs**. Each run uses its persona's capital, ADX threshold, confidence, and ATR SL/TP multiples — sourced from `IPersonaService.GetAllProfiles()` so any saved overrides from the Persona config page are automatically reflected. Results populate a sortable DataGrid in real time (with a Persona column), sorted by P&L descending as each run completes. The tab uses a **side-by-side layout**: results grid on the left (`*` width), Claude Haiku analysis panel on the right (380px fixed) — always visible without scrolling. Each result row has a **📌 pin button** (`PinResultCommand` on the ViewModel, bound via `RelativeSource` to the DataGrid's DataContext) that appends the row to `PinnedResults`. On completion, the top 20 results are sent to **Claude Haiku** (`claude-haiku-4-5-20251001`) for a concise analysis covering top persona/instrument/strategy combinations, overfitting warnings, and a live-trade recommendation. Requires Claude API key configured on the API Keys page.

**Tab 3 — Pinned**: Shows `PinnedResults` — an `ObservableCollection(Of MaxEffortRowVm)` seeded in the constructor with the 2025-07 Gold/Multi-Confluence/1hr baseline, and extended at runtime whenever the user pins a row from Maximum Effort. The tab caption explains the 📌 workflow.

**Tab 4 — Previous Runs**: DataGrid of saved `BacktestRunEntity` records from SQLite.

**TopStepX P&L**: Both single-run (`ExecuteRun`) and Maximum Effort (`ExecuteMaximumEffort`) resolve tick size and point value via `contract.GetTickSize(_session.ActiveBroker)` / `contract.GetPointValue(_session.ActiveBroker)`. This correctly returns MGC=$10/pt, MES=$5/pt, MNQ=$2/pt, MCL=$100/pt, M6E=$12,500/pt. `BacktestViewModel` receives `ITradingSessionContext` as a constructor parameter for this purpose.

### Extend TP on Close (Live Engine)

`StrategyDefinition.ExtendTpOnClose` (default `False`; Hydra and AssetBassett default to `True`) enables the live equivalent of the backtest "Extend TP on close" feature.

**How it works:** After each 30-second bar-check tick, `StrategyExecutionEngine.ExtendTpIfClosedBeyondTargetAsync()` checks whether the last completed bar closed at or beyond the current TP price. If so, it advances the TP by `TpMultipleOfN × currentATR` in the trade direction (tick-snapped to the instrument's tick size) and calls `EditPositionSlTpAsync` to push the new target to the broker. Up to **3 advances** per trade; counter resets in `ResetTrailState()` on every position close.

- Independent of the ATR trailing SL — the SL ratchet is not touched.
- UI toggle: "Extend TP on close" checkbox in the ATR tier panel on both Hydra and AssetBassett.
- Backtest winner config had this **ON**: Multi-Confluence · Damian · OIL · 5-min.

**Key field:** `_tpAdvanceCount As Integer` (per-engine instance, reset on close). Max = 3.

### Hydra View — Bracket Management

Each `StrategyExecutionEngine` raises `TurtleBracketChanged` (`TurtleBracketChangedEventArgs`: `SlPrice`, `TpPrice`, `IsAdvance`, `IsFreeRide`, `BracketNumber`) whenever a bracket is placed, reattached on restart, or advanced. `HydraViewModel.WireEngineEvents` handles this event and calls `HydraAssetViewModel.ApplySl(slPrice, tpPrice, isAdvance, isFreeRide)`, which:

- Stores numeric `_currentSlPrice` / `_currentTpPrice` (used by the Nudge command)
- Updates `BracketPriceDisplay` to `"SL: X.XX  TP: Y.YY"` (or just `"SL: X.XX"` if no TP)
- Sets `IsFreeRide` (shows the Free Ride badge once SL clears entry)
- Plays the Turtle click sound on genuine advances (`IsAdvance = True`)

`ClearSlStatus()` zeros all three fields and clears the display on position close.

**Per-asset manual controls** — `HydraAssetViewModel` exposes two `ICommand` properties wired by `HydraViewModel` in the constructor loop:

| Command | What it does |
|---|---|
| `CloseCommand` | Calls `FlattenContractAsync` on the asset's scoped `IOrderService`, then resets the tile via `CloseTrade()` |
| `NudgeBracketCommand` | Fetches a live snapshot, tightens SL by 10% of its distance to entry (min 1 tick), nudges TP inward by same step if tracked, calls `EditPositionSlTpAsync`, updates tile via `ApplySl()` |

Both buttons are only visible when `HasOpenPosition` is `True` (derived from `_positionCount > 0`; notified in `OpenTrade`/`CloseTrade`). The Nudge command falls back to a 20-tick estimate from the broker snapshot entry price when `_currentSlPrice = 0` (no bracket recorded yet).

**Note:** `ApplySl` signature is `(slPrice As Decimal, tpPrice As Decimal, isAdvance As Boolean, isFreeRide As Boolean)`. Do not call the old 3-parameter version.

### QuantLab View (`UI/ViewModels/QuantLabViewModel.vb`)

A dedicated research page for testing academically-validated strategies that are **not** used in live Hydra/AssetBassett trading. Accessed via sidebar.

**Five strategy cards:**
1. **Connors RSI-2** — Mean reversion · RSI(2) dips vs SMA(200) trend · 67–72% win rate · Sharpe 1.0–1.5
2. **SuperTrend** — Trend-following · ATR(10)×3.0 direction flip · 40–52% win rate · Sharpe 0.70–1.05
3. **Donchian Breakout** — Turtle breakout · 20-bar high/low channel · 30–40% win rate · Sharpe 0.4–0.8
4. **BB + RSI Reversion** — Dual-confirm reversion · BB(20,2) AND RSI(14) · 55–65% win rate · Sharpe 0.6–1.2
5. **Double Bubble Butt** — Zone momentum · ±1.0 SD inner / ±2.0 SD outer BB · 45–60% win rate · Sharpe 0.6–1.1

**Workflow:** Select card → choose contract + date range + interval → Run Backtest → view results (win rate, Sharpe, P&L, drawdown) → optionally "Ask Claude" for AI analysis.

- `MinSignalConfidence = 1.0` — QuantLab strategies use price-level exits, not confidence scoring
- `SlDollarBracket = 0 / TpDollarBracket = 0` — exits are indicator-driven (SuperTrend line, Donchian mid, RSI 50)
- CSV export available for the full trade list
- "Ask Claude" calls `ClaudeReviewService.AnalyseBacktestResultsAsync` with strategy description and result summary
- `QuantLabViewModel` accepts `IBacktestService`, `IBarCollectionService`, `ClaudeReviewService`

### Sniper View (`UI/ViewModels/SniperViewModel.vb`)

Live trading interface for the **3-EMA Cascade** strategy (`TripleEmaCascade = 7`) on 1-minute bars.

- EMA8/EMA21/EMA50 — Long when EMA8 crosses above EMA21 AND price is above rising EMA50; Short on reverse
- Supports **pyramiding scale-in** up to 10 contracts
- Has both a **Live tab** (wires `ISniperExecutionEngine`) and a **Backtest tab** (uses `IBacktestService` with `TripleEmaCascade`)
- Live P&L tracked per-session; win/loss counts maintained in `_winCount` / `_lossCount`
- `SniperViewModel` accepts: `IAccountService`, `IBacktestService`, `IBarCollectionService`, `ClaudeReviewService`, `ISniperExecutionEngine`, `ITradingSessionContext`

### Technical Indicators (`ML/Features/TechnicalIndicators.vb`)

Pure-math module — no external dependencies, fully unit-testable. All methods take `IList(Of Decimal)` price series and return `Single()` arrays (NaN-padded during warm-up).

| Function | Description |
|---|---|
| `EMA(prices, period)` | Exponential Moving Average (k = 2/(period+1)) |
| `SMA(prices, period)` | Simple Moving Average |
| `RSI(closes, period)` | Wilder-smoothed RSI |
| `MACD(closes, fast, slow, signal)` | Returns (Line, Signal, Histogram) |
| `ATR(highs, lows, closes, period)` | Wilder-smoothed ATR |
| `VWAP(highs, lows, closes, volumes)` | Cumulative VWAP |
| `BollingerBands(closes, period, sd)` | Returns (Upper, Middle, Lower) |
| `BollingerBandWidth(closes, period, sd)` | (Upper−Lower)/Middle×100 |
| `BollingerPercentB(closes, period, sd)` | 0=lower band, 1=upper band |
| `DMI(highs, lows, closes, period)` | Returns (+DI, −DI, ADX) — Wilder smoothing |
| `IchimokuCloud(highs, lows, closes, ...)` | Returns (Tenkan, Kijun, SpanA, SpanB) — projected 26 bars forward |
| `StochasticRSI(closes, rsi, stoch, signal)` | Returns (%K, %D) normalised 0–1 |
| `WaveTrend(highs, lows, closes, ...)` | Market Cipher B simulation — (WT1, WT2) |
| `SuperTrend(highs, lows, closes, period, mult)` | ATR-based trend line; returns (Line, Direction) |
| `DonchianChannel(highs, lows, period)` | Rolling high/low channel; returns (Upper, Lower, Middle) |
| `CMO(closes, period)` | Chande Momentum Oscillator normalised to [−1, +1] |
| `VIDYA(closes, vidyaLen, cmoLen)` | CMO-adaptive EMA — fast in trends, slow in chop |
| `DeltaVolume(closes, opens, volumes, window)` | Net buy/sell pressure normalised to [−1, +1] |
| `Rsi2(closes)` | Convenience wrapper for RSI(closes, 2) |
| `LastValid(series)` | Last non-NaN value in array |
| `PreviousValid(series)` | Second-to-last non-NaN value |

### API Keys View (`UI/Views/ApiKeysView.xaml`)

Three named credential slots (stored locally via `ApiKeyStore`, never synced):

| Provider | Fields |
|---|---|
| **TopStepX** | Account Email + API Key |
| **Claude (Anthropic)** | Organisation/Workspace ID (optional) + API Key |
| **Binance** | API Key + Secret Key |

Plus **4 Future Slots** (editable label + username + API key) reserved for future integrations.

Toggle **Show/Hide** button masks all password boxes simultaneously. "💾 Save Keys" persists all fields.

### Claude AI Integration (`Services/AI/ClaudeReviewService.vb`)
Three methods, all using the API key from `ApiKeyStore` (falls back to `appsettings.json`):
- `ReviewStrategyAsync` — strategy improvement suggestions (used by HydraView)
- `ConfidenceCheckAsync` — macro/session bias for a contract
- `AnalyseBacktestResultsAsync` — quantitative analysis of a backtest results table (used by Maximum Effort tab and QuantLab "Ask Claude"). Always uses `claude-haiku-4-5-20251001` regardless of the model setting, for cost efficiency.

`ClaudeSettings.Model` defaults to `claude-haiku-4-5`. Registered as **Scoped** in `ServicesExtensions.vb`. Injected into `BacktestViewModel` and `QuantLabViewModel` via constructor.
