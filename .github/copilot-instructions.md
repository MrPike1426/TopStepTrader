# Copilot Instructions

## Project Guidelines
- The correct GitHub remote repository for this project is https://github.com/MrPike1426/TopStepTrader.git. The old eToroTrader repository no longer exists and should never be referenced.
- All eToro-era fields (`EToroContractId`, `BrokerType.eToro`, `IsTradableOn`, `GetDefaults(broker)`) have been removed (ARCH-05). Do not re-introduce them.
- The crypto-specific UI (`CryptoJoeView`, `CryptoJoeViewModel`, `CryptoStrategyExecutionEngine`, `PriceTrackerView`, `PriceTrackerViewModel`) has been removed (ARCH-13). Do not re-introduce those views.
- **MBT (Micro Bitcoin) is re-instated as a SuperTrend+ watchlist instrument — ARCH-13's MBT exclusion is overruled.** MBT is treated as a pure technical-analysis intraday futures slot by the unified SuperTrend+ engine (no crypto-specific code path).
- SuperTrend+ Autopilot uses the **Position Slot model** (FEAT-23/24/25) — there are no personas (Joe/Damian/Lewis) in this view. Use `PositionSlot`, `SlotManager`, `SuperTrendPlusConfig`, and `ExitSignalEngine` instead.
- `FavouriteContracts.GetDefaults()` includes: SPX500 (MES), NQ (MNQ), M2K, MBT (Bitcoin), GOLD (MGC), M6E (EUR/USD), OIL (MCLE) — 7 instruments. SIL and MYM removed.
- `SuperTrendPlusViewModel.Instruments` is derived from `FavouriteContracts.GetDefaults()` — do not maintain a parallel hard-coded list.

## Ticket Workflow
See `CLAUDE.md § Ticket & Issue Tracking` for the authoritative workflow — that is the single source of truth.

Short version:
1. **Read `tickets/<ID>.md`** before touching any code (use `python tickets/tickets.py show <ID>`).
2. **Fix the code** exactly as the ticket specifies.
3. **Build** — confirm clean (`dotnet build --no-restore -v q && dotnet test --no-build -v q`).
4. **Close the ticket** — two steps, both required:
   - Run `python tickets/tickets.py close <ID> --resolution "<summary>"`
     (this moves the markdown to `tickets/archive/<ID>.md` and updates the SQLite DB)
   - Verify it no longer appears in `python tickets/tickets.py list`
5. **Commit, push, and pull** — stage everything, commit with the typed format `git add -A && git commit -m "<type>(<ID>): <short description>"` (e.g. `fix(BUG-12): increase fetchCount to 80`), push with `git push origin HEAD`, then `git pull`. Never mark a ticket done without completing this step.

   | Type | When |
   |---|---|
   | `fix` | Bug fix (BUG-XX) |
   | `feat` | New feature / strategy change (FEAT-XX, STRAT-XX) |
   | `test` | Test coverage (TEST-XX) |
   | `refactor` | Code quality / architecture (QUAL-XX, ARCH-XX) |
   | `obs` | Observability / logging (OBS-XX) |
   | `chore` | Tracker / documentation update only |

Never reference `REFACTOR_TRACKER.md`, `Open_TICKETS.md`, or `Closed_Tickets.md` — ticket state is managed exclusively via the SQLite DB in `tickets/tickets.db` and the CLI.
Never close a ticket without first confirming the build and tests pass.
Keep the ticket description concise; let code comments and tests capture details.