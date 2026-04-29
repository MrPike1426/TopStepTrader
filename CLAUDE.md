# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Output Style

Be maximally terse. No preamble, no summaries, no "done", no trailing confirmation. Report only failures or decisions that require user input. A single sentence is the maximum acceptable response for any completed task.

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

**Expected output:** `506 passed, 0 failed` (as of 2026-04-28). If the count changes after adding new tests, update this number.

**Rules:**
- Never leave the project in a broken build state.
- Never report a task complete until both build and test pass.
- If a test failure is pre-existing (not caused by the current change), flag it explicitly — do not silently ignore it.

## Data Integrity — Hard Rules

**No trade decision may ever be based on stale or inaccurate market data.** This is a non-negotiable constraint. Violation caused BUG-36: the app computed SuperTrend on MCL K26 (stuck at ~$92, BEAR) while the live front-month M26 was at $99.52 (strongly BULL). The engine entered the wrong side.

### What counts as "stale or inaccurate data"

| Category | Example failure |
|---|---|
| Wrong contract | Bars from a rolled/expired contract (K26 instead of M26) |
| Stale cached ID | `_rootToActiveId` never refreshed → engine uses old contract indefinitely |
| Forming bar | Incomplete bar fed to indicators → direction flips before bar closes (repaint) |
| Stale bars | Last bar older than `TimeframeMinutes × 3` → market may be closed or feed dead |
| Wrong tick specs | Hardcoded tick size / point value instead of reading `FavouriteContracts` |

### Enforcement mechanisms already in place

Every mechanism below must be preserved. Do not remove or weaken any of them.

| Mechanism | Where | What it does |
|---|---|---|
| `RollLeadDays` on `FavouriteContract` | `Core/Trading/FavouriteContracts.vb` | Per-instrument roll lead time. Monthly (MCL/MGC) = 28 days; quarterly (MES/MNQ/M6E/MBT) = 7 days. |
| `SelectBestContract` cutoff | `Services/Trading/TopStepXInstrumentCatalog.vb` | Excludes any contract whose expiry falls within `asOf + RollLeadDays`. Automatically selects the true front-month after a roll. Covered by 15 regression tests in `InstrumentCatalogRollTests.vb`. |
| `EnsureCacheAsync` called by `GetResolvedContractIdAsync` | `Services/Trading/TopStepXInstrumentCatalog.vb` | Forces the 15-min TTL to govern every caller — including SuperTrend+ — not just order-placement paths. Previously the fast-path returned a stale cached ID forever. |
| Stale bar guard (`barAgeMins > TF × 3`) | `Services/Trading/StrategyExecutionEngine.vb` | Suppresses all entry signals when bars are too old. Fires `IsMarketClosed = True` once on transition; resets on `Start()`. |
| Partial-bar guard | `Services/Trading/StrategyExecutionEngine.vb` | Drops the last bar when its timestamp is within the still-open TF period. Prevents repaint from incomplete forming bars feeding all 9 MC indicators. |
| MarketHub roll safety | `Services/Trading/StrategyExecutionEngine.vb` | `OnMarketQuoteReceived` matches by exact `ContractId` OR root symbol so a quarterly roll does not silently freeze `_lastQuotePrice` at 0. |

### Rules for any code that touches the data pipeline

1. **Never bypass `GetResolvedContractIdAsync`.** Any code that fetches bars, quotes, or tick specs must go through `TopStepXInstrumentCatalog` — never cache a raw contract ID in a field and reuse it across ticks without re-resolving.
2. **Never hardcode a contract expiry month.** `FavouriteContracts.PxContractId` is the *fallback* when the live API is unavailable, not the primary. Update it when a roll is approaching, but it is not a substitute for live resolution.
3. **When adding a new instrument**, set `RollLeadDays` explicitly. Monthly contracts = 28; quarterly = 7. Add at least one `SelectBestContract` regression test covering the roll window and the pre-roll period.
4. **When adding a new data-fetch path** (new engine, new view, new background worker), always call `GetResolvedContractIdAsync` — never read `fav.PxContractId` directly for live use.
5. **When modifying `TopStepXInstrumentCatalog`**, run `dotnet test --filter "FullyQualifiedName~InstrumentCatalogRoll"` to verify the 15 contract-roll regression tests still pass before committing.

### Post-mortem: BUG-36 (2026-04-22 → 2026-04-28)

MCL rolled from K26 to M26 on April 22. `SearchForFrontMonthAsync` used `cutoff = UtcNow - 28 days` — both K26 (expiry May 20) and M26 (expiry June 20) passed, and nearest-expiry ordering selected K26. `GetResolvedContractIdAsync` had no TTL on its fast path, so the stale K26 ID was returned indefinitely. The engine computed SuperTrend on K26 bars (~$92, BEAR) for six days while the live market (M26, ~$99.52) was strongly BULL.

---

## Ticket & Issue Tracking

All open work is tracked in a **local SQLite database** — the single source of truth for index/search — plus individual markdown files for full ticket detail:

- **`tools/tickets/tickets.db`** — SQLite index (single source of truth for state, priority, search). Local-only, excluded from git.
- **`tickets/<ID>.md`** — full description, problem, proposed fix, and acceptance criteria for each open ticket.
- **`tickets/archive/<ID>.md`** — completed ticket files (moved here on close, never deleted).
- **`Open_TICKETS.md`** / **`Closed_Tickets.md`** — obsolete; do not read or write these files.

All files are local-only and excluded from the GitHub remote via `.gitignore`.

### Ticket CLI

```bash
# List open tickets (sorted P0→P3 then by ID)
python tools/tickets/tickets.py list

# Show a ticket (DB row + markdown content)
python tools/tickets/tickets.py show BUG-17

# Create a new ticket
python tools/tickets/tickets.py new BUG P1 "Short title" --category Bugs --size S --source UAT

# Close a ticket (moves markdown to archive, updates DB)
python tools/tickets/tickets.py close BUG-17 --resolution "Fixed by removing stale cache."

# Full-text search
python tools/tickets/tickets.py search "stale contract"

# Stats
python tools/tickets/tickets.py stats
```

See `tools/tickets/README.md` for full usage.

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

Use the next sequential number within each prefix (the CLI determines it automatically by scanning the DB).

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

1. Run `python tools/tickets/tickets.py new <PREFIX> <PRIORITY> "<TITLE>" [--category C] [--size S] [--source SRC]`.
   - The CLI determines the next ID automatically and creates `tickets/[ID].md`.
   - A DB row is inserted with `state=Open`.
2. Edit `tickets/[ID].md` to fill in **Problem**, **Proposed Fix**, and **Acceptance Criteria**.

### Completing a Ticket

1. Verify build passes and all tests pass.
2. Run `python tools/tickets/tickets.py close [ID] --resolution "<one-line summary>"`.
   - This moves `tickets/[ID].md` → `tickets/archive/[ID].md` and updates the DB (`state=Closed`).
3. **Commit, push, and pull** — stage all changes and push to origin:
   ```bash
   git add -A
   git commit -m "<type>(<ID>): <short description>"
   git push origin HEAD
   git pull
   ```
   Never mark a ticket done without completing this step.

### Running a Ticket

```
Execute [ID]
```

Claude loads `python tools/tickets/tickets.py show [ID]` + the referenced source files, implements the change, runs self-verification (build + tests), and closes the ticket via the CLI as described above.

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

1. Read `CLAUDE.md` (this file).
2. Pick the **highest-priority open ticket** by running `python tools/tickets/tickets.py list` (lowest P-number, then lowest ID), or use the ticket explicitly requested.
3. Run `python tools/tickets/tickets.py show [ID]` and read all files listed under `**Files:**`.
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
7. Close the ticket via CLI:
   ```bash
   python tools/tickets/tickets.py close [ID] --resolution "<one-line summary>"
   git add -A
   git commit -m "chore(ID): close ticket"
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
| `TopStepTrader.API` | HTTP/SignalR clients for TopStepX/ProjectX; ProjectX token manager |
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
- **Stale bar guard** — `StrategyExecutionEngine` declares a bar stale when `barAgeMins > TimeframeMinutes × 3` (e.g. 15 min for 5-min bars). The 3× multiplier accounts for the bar timestamp being the *start* of the period, TopStepX's 1–5 min API propagation lag, and the 30-second engine poll jitter — a stale threshold of 1× or 2× would produce false market-closed events during normal active trading. On the fresh→stale transition (market close) it fires exactly one `ConfidenceUpdated` event with `IsMarketClosed = True` and suppresses all entry signals. Position monitoring (broker sync + trail) continues. The `IsMarketClosed` flag causes Hydra/AssetBassett tiles to show `"⏸ Closed"`. The guard resets (`_lastBarWasStale = False`) on engine `Start()`.
- **Partial-bar guard** — after `GetBarsForMLAsync`, the engine drops the last bar when its timestamp falls within the still-open strategy-TF period (i.e., `UtcNow - barTimestamp < TimeframeMinutes`). This prevents repaint on all nine Multi-Confluence indicators caused by an incomplete forming bar being fed into the evaluator.
- **Market regime filter** — when both `StrategyDefinition.TrendingStrategyOverride` and `RangingStrategyOverride` are set, `RegimeClassifier.Classify(atr, adx, adxThreshold)` runs before the strategy `Select Case` and injects the appropriate `activeCondition` (trending → `TrendingStrategyOverride`; ranging → `RangingStrategyOverride`). Regime overrides are configurable via ComboBoxes on Hydra and AssetBassett views.
- **AI pre-trade gate** — when `StrategyDefinition.UseAiPreTradeGate = True`, the engine calls `IClaudeReviewService.PreTradeCheckAsync` before placing an order. Result is logged as `↳ AI: PROCEED` or `↳ AI: VETO`. Engine tracks `_consecutiveLosses` and `_totalTradesThisSession` and includes them in `PreTradeContext`.
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

## Backtest Architecture

### Bar Collection (`BarCollectionService.vb`)
Downloads historical bars from TopStepX (`PXHistoryClient`) and caches them in SQLite. Key behaviours:

- **Cache hit**: returns immediately if ≥ 50 bars exist, span ≥ 80% of requested range, **and latest bar is less than 24 hours old**. The staleness check ensures weekly runs accumulate fresh bars over time (new bars appended via `INSERT OR IGNORE`).
- **Symbol translation**: `BarCollectionService` uses the stable contract ID (e.g. `"MES"`) as the SQLite storage key; the raw PX contract ID is passed directly to `PXHistoryClient.RetrieveBarsAsync`.
- **Lookback limits** (enforced in `BacktestRunViewModel.GetMaxLookbackDays`):
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
| Multi-Confluence Engine | ATR | ATR |
| BB Squeeze Scalper | ATR | ATR |
| LULT Divergence | ATR | ATR |
| VIDYA Cross | ATR | ATR |
| Naked Trader | ATR | ATR |
| Double Bubble Butt | ATR | ATR |
| Opening Range Breakout | ATR | ATR |
| Pump-n-Dump | ATR | ATR |

`MinConfidence` is stored in the ViewModel as a whole-number percentage (e.g. `"90"`) and divided by 100 before being passed to the engine.

### Strategy Condition / Indicator Enums
Integer values are DB discriminators — must not change once data is stored. See **REFERENCE.md** for full enum tables.

### Backtest Engine — Notable Behaviours

- **End-of-day forced close** — `BacktestEngine.RunReplay` detects day boundaries by comparing adjacent bar dates. When a new trading day starts, all open legs are closed at the previous bar's `Close` price with `ExitReason = "EndOfDay"` and 1-bar SL-slippage applied. A 1-bar re-entry cooldown follows. `BacktestResult.EndOfDayCloseCount` tracks how many trades were force-closed this way.
- **Per-contract round-trip commission** — `FavouriteContract.RoundTripFee` stores the full round-trip fee (OIL=$1.04, GOLD=$1.24, MES=$0.74, M6E=$0.74, MBT=$2.34; default=$0.80). `BacktestEngine` deducts `RoundTripFee / 2` per entry leg and per exit leg. `RoundTripFeeUsd` is stored on `BacktestResult` and surfaced in `ParametersJson` on `BacktestRunEntity`.
- **Train/test split** — `BacktestConfiguration.TrainTestSplit` (0.0 = off; 0.6 = 60/40). When set, the engine runs the training set first (first `TrainTestSplit` fraction of bars), then the out-of-sample test set (remaining bars). `BacktestResult.OutOfSampleResult` carries the test-set result. `TestPnL` and `DegradationPct` appear on `TimeframeResultRowVm` and `MaxEffortRowVm` with amber/red row highlighting.
- **VolumeGateEnabled** — `MultiConfluenceConfig.VolumeGateEnabled` (default `True`). When `False`, the volume condition (Close-volume ≥ 1.2× 20-bar average) is bypassed entirely. Set `McVolumeGateEnabled = False` on `BacktestConfiguration` for instruments where PX returns zero intraday volume (e.g. M6E). The `StatusLine` shows `SKIP(PX-no-vol)` when skipped in live mode.

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

**Tab 1 — Run Backtest**: Persona selector (Lewis / Damian / Joe) at the top of the config form pre-fills Capital, Min ADX, Min Confidence, and ATR multiples (when ATR mode is on). Runs the selected strategy across **8 timeframes** (1min, 5min, 10min, 15min, 30min, 1hr, 2hr, 4hr) simultaneously. Each timeframe's effective date range is clamped to Yahoo's limit. Results appear in a live-filling, **sortable** grid (column headers are clickable; `TotalPnLRaw As Decimal` on `TimeframeResultRowVm` drives numeric sort on the P&L column). Default date range: today − 180 days (clamped per timeframe). **Validate (60/40 split)** toggle enables `TrainTestSplit = 0.6` — the engine runs both the training set (first 60% of bars) and the out-of-sample test set (last 40%), surfacing `TestPnL` and `DegradationPct` with amber/red highlighting on the results grid.

**Tab 2 — Maximum Effort!** (Deadpool button): Runs every combination of **3 personas × 5 instruments × 7 strategies × 8 timeframes = 840 backtest runs**. Each run uses its persona's capital, ADX threshold, confidence, and ATR SL/TP multiples — sourced from `IPersonaService.GetAllProfiles()` so any saved overrides from the Persona config page are automatically reflected. Results populate a sortable DataGrid in real time (with a Persona column), sorted by P&L descending as each run completes. The tab uses a **side-by-side layout**: results grid on the left (`*` width), Claude Haiku analysis panel on the right (380px fixed) — always visible without scrolling. Each result row has a **📌 pin button** (`PinResultCommand` on the ViewModel, bound via `RelativeSource` to the DataGrid's DataContext) that appends the row to `PinnedResults`. On completion, the top 20 results are sent to **Claude Haiku** (`claude-haiku-4-5-20251001`) for a concise analysis covering top persona/instrument/strategy combinations, overfitting warnings, and a live-trade recommendation. Requires Claude API key configured on the API Keys page. **Validate (60/40 split)** toggle activates `TrainTestSplit` for all 840 runs; MaxEffort then sorts by `TestPnL` instead of in-sample P&L; Haiku prompt includes train/test metrics.

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

### Sniper View (`UI/ViewModels/SniperViewModel.vb`)

Live trading interface for the **3-EMA Cascade** strategy (`TripleEmaCascade = 7`) on 1-minute bars.

- EMA8/EMA21/EMA50 — Long when EMA8 crosses above EMA21 AND price is above rising EMA50; Short on reverse
- Supports **pyramiding scale-in** up to 10 contracts
- Has both a **Live tab** (wires `ISniperExecutionEngine`) and a **Backtest tab** (uses `IBacktestService` with `TripleEmaCascade`)
- Live P&L tracked per-session; win/loss counts maintained in `_winCount` / `_lossCount`
- `SniperViewModel` accepts: `IAccountService`, `IBacktestService`, `IBarCollectionService`, `ClaudeReviewService`, `ISniperExecutionEngine`, `ITradingSessionContext`

### Technical Indicators (`ML/Features/TechnicalIndicators.vb`)
Pure-math module — no external dependencies; methods take `IList(Of Decimal)`, return `Single()` (NaN-padded). See **REFERENCE.md** for the full function table.

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
- `AnalyseBacktestResultsAsync` — quantitative analysis of a backtest results table (used by Maximum Effort tab). Always uses `claude-haiku-4-5-20251001` regardless of the model setting, for cost efficiency.

`ClaudeSettings.Model` defaults to `claude-haiku-4-5`. Registered as **Scoped** in `ServicesExtensions.vb`. Injected into `BacktestViewModel` via constructor.
