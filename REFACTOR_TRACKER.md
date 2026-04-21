# REFACTOR_TRACKER.md
> Last updated: 2026-04-22 | Build target: `net10.0-windows` x64 | Language: VB.NET | Test count baseline: 357 passed, 0 failed
> Session 2026-04-22b: Completed STRAT-06 (Volume gate in MultiConfluence — 1 new test)
> Session 2026-04-22: Completed STRAT-18, STRAT-19, QUAL-02 (Chikou-vs-cloud filter, graduated confidence, rename already applied — 4 new tests)
> Session 2026-04-21e: Completed STRAT-14, STRAT-15, STRAT-17 (MultiConfluence: StochRSI short gate, MACD 8/17/9, cloud twist pre-filter — 4 new tests)
> Session 2026-04-21d: Completed STRAT-10, STRAT-11, STRAT-12, STRAT-13 (EMA/RSI: 3-zone RSI, 3-bar slope, volume gate, configurable RSI period — 10 new tests)
> Session 2026-04-21c: Raised STRAT-14–STRAT-19 (MultiConfluence SWOT — StochRSI semantics, MACD tuning, 7/8 partial signal, cloud twist, Chikou-vs-cloud, graduated confidence)
> Session 2026-04-21b: Raised STRAT-09–STRAT-13 (EMA/RSI strategy review); Completed STRAT-09
> Session 2026-04-20: Completed STRAT-01, STRAT-03, STRAT-08, UX-01, UX-02, UX-04, QUAL-03, TEST-02, TEST-03 (9 tickets)
> Session 2026-04-21: Completed STRAT-02 (1 ticket)

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
| `TopStepTrader.Tests` | xUnit, 320 tests across multiple files |

**Key architectural facts:**
- Persona system: `IPersonaService` → `PersonaService` (Singleton) → SQLite persistence. Never call `RiskProfile.Lewis/Damian/Joe` directly in ViewModels.
- `StrategyConditionType` enum integer values are DB discriminators — must not be changed once data is stored.
- `FavouriteContracts.vb` holds master instrument list. Always call `contract.GetTickSize(_session.ActiveBroker)` — never hardcode specs.
- Signal providers: all strategies implement `IStrategySignalProvider` (ARCH-01 complete). Factory: `StrategySignalProviderFactory`.
- `BacktestViewModel` is now a thin shell over 4 sub-VMs: `BacktestRunViewModel`, `MaxEffortViewModel`, `PinnedResultsViewModel`, `PreviousRunsViewModel` (ARCH-02 complete).

**Self-verification rule (must run after every file edit):**
```bash
dotnet build src/TopStepTrader.Services/TopStepTrader.Services.vbproj --no-restore -v q
dotnet build --no-restore -v q
dotnet test --no-build -v q
# Expected: 356 passed, 0 failed
```

---

## Priority Queue

Next tickets to execute, in order:

**MultiConfluence improvements (from SWOT review):**
1. **[STRAT-06]** Volume confirmation gate in MultiConfluence — M
2. **[STRAT-16]** 7/8 partial-conviction signal at half-size — M

**Other:**
3. **[STRAT-05]** Confluence dissolution exit — M
4. **[STRAT-09]** Fix EMA/RSI bear scoring — independent signals — M

---

## Status Board

| ID | Title | Status | Category | Size |
|---|---|---|---|---|
| ARCH-01a | Define scaffold — interface, SignalResult, factory | ✅ Done | Architecture | — |
| ARCH-01b | Extract EmaRsi + MultiConfluence providers | ✅ Done | Architecture | — |
| ARCH-01c | Extract remaining live-trading providers | ✅ Done | Architecture | — |
| ARCH-01d | Extract QuantLab providers | ✅ Done | Architecture | — |
| ARCH-01e | Gut BacktestEngine main loop | ✅ Done | Architecture | — |
| ARCH-01f | Unit test per signal provider | ✅ Done | Architecture | — |
| ARCH-02a | Extract BacktestRunViewModel | ✅ Done | Architecture | — |
| ARCH-02b | Extract MaxEffortViewModel | ✅ Done | Architecture | — |
| ARCH-02c | Extract PinnedResultsViewModel | ✅ Done | Architecture | — |
| ARCH-02d | Extract PreviousRunsViewModel | ✅ Done | Architecture | — |
| ARCH-02e | Wire shell BacktestViewModel + XAML | ✅ Done | Architecture | — |
| ARCH-03 | Replace IsWorking booleans with WorkPhase enum | ✅ Done | Architecture | — |
| BUG-01 | Replace pending-state locals with PendingEntry record | ✅ Done | Bugs | — |
| BUG-02 | Cap LogEntries at 1,000 in SniperViewModel | ✅ Done | Bugs | — |
| BUG-03 | Add IDisposable to BacktestViewModel | ✅ Done | Bugs | — |
| BUG-04 | TickSize > 0 guard in CalculatePnL | ✅ Done | Bugs | — |
| BUG-05 | NaN propagation guard in BacktestEngine | ✅ Done | Bugs | — |
| BUG-06 | DonchianBreakout exit de-bounce | ✅ Done | Bugs | — |
| TEST-01 | UpdateDynamicExits unit tests | ✅ Done | Tests | — |
| TEST-02 | Scale-in multi-leg exit tests | ✅ Done | Tests | S |
| TEST-03 | BarCollectionService aggregation tests | ✅ Done | Tests | S |
| TEST-04 | BarCollectionService staleness + dedup tests | ✅ Done | Tests | — |
| TEST-05 | NaN warm-up propagation tests | ✅ Done | Tests | — |
| TEST-06 | ViewModel input validation tests | ✅ Done | Tests | — |
| QUAL-01 | Extract duplicated long/short exit check | ✅ Done | Code Quality | — |
| QUAL-02 | Rename InitialSlAmount / InitialTpAmount | ✅ Done | Code Quality | M |
| QUAL-03 | Extract DatePicker styling to resource dictionary | ✅ Done | Code Quality | S |
| UX-01 | Cache/fresh indicator on bar download status | ✅ Done | UI/UX | S |
| UX-02 | Indeterminate progress during bar download | ✅ Done | UI/UX | S |
| UX-03 | CSV export on background thread | ✅ Done | UI/UX | XS |
| UX-04 | Max Effort cancellation path state reset | ✅ Done | UI/UX | S |
| STRAT-01 | Raise re-entry cooldown to 2 full bars | ✅ Done | Strategy | S |
| STRAT-02 | Time-of-day trading window gate | ✅ Done | Strategy | M |
| STRAT-03 | Promote EMA50 to active gate condition | ✅ Done | Strategy | S |
| STRAT-04 | Default ATR tier to Standard for Damian + Gold | ✅ Done | Strategy | XS |
| STRAT-05 | Confluence dissolution exit | ⬜ Open | Strategy | M |
| STRAT-06 | Volume confirmation gate in MultiConfluence | ✅ Done | Strategy | M |
| STRAT-07 | Fix asymmetric StochRSI short gate | ✅ Done | Strategy | XS |
| STRAT-08 | Raise FreeRoll activation to 66–75% of TP | ✅ Done | Strategy | S |
| STRAT-09 | Fix EMA/RSI bear scoring — independent signals | ✅ Done | Strategy | M |
| STRAT-10 | Fix RSI zone boundary (include RSI > 70) | ✅ Done | Strategy | XS |
| STRAT-11 | Add volume confirmation gate to EMA/RSI | ✅ Done | Strategy | S |
| STRAT-12 | Replace single-bar EMA21 momentum with 3-bar slope | ✅ Done | Strategy | XS |
| STRAT-13 | Make RSI period configurable (not hardcoded 14) | ✅ Done | Strategy | S |
| STRAT-14 | Correct StochRSI short gate semantics (K > 0.3 + falling) | ✅ Done | Strategy | XS |
| STRAT-15 | Tune MACD parameters for intraday bars (8,17,9) | ✅ Done | Strategy | S |
| STRAT-16 | 7/8 partial-conviction signal at half-size | ⬜ Open | Strategy | M |
| STRAT-17 | Add cloud twist pre-filter (bullish/bearish cloud alignment) | ✅ Done | Strategy | XS |
| STRAT-18 | Add Chikou-vs-cloud filter | ✅ Done | Strategy | S |
| STRAT-19 | Replace hardcoded Confidence=1.0 with graduated score | ✅ Done | Strategy | M |

---

## Completion Summary

| Category | Tickets | Done |
|---|---|---|
| Architecture | 12 | 12 |
| Bugs | 6 | 6 |
| Tests | 6 | 6 |
| Code Quality | 3 | 3 |
| UI/UX | 4 | 4 |
| Strategy | 19 | 18 |
| **Total** | **50** | **48** |

---

*To start a ticket: load `REFACTOR_TRACKER.md` + `tickets/[ID].md` + the relevant source files, then say "Execute [TICKET-ID]".*  
*Completed ticket specs live in `tickets/archive/`. Open ticket specs live in `tickets/`.*
