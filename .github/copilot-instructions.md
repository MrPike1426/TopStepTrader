# Copilot Instructions

## Project Guidelines
- The correct GitHub remote repository for this project is https://github.com/MrPike1426/TopStepTrader.git. The old eToroTrader repository no longer exists and should never be referenced.

## Ticket Workflow
See `CLAUDE.md § Ticket & Issue Tracking` for the authoritative workflow — that is the single source of truth.

Short version:
1. **Read `tickets/<ID>.md`** before touching any code.
2. **Fix the code** exactly as the ticket specifies.
3. **Build** — confirm clean (`dotnet build --no-restore -v q && dotnet test --no-build -v q`).
4. **Close the ticket** — move `tickets/<ID>.md` → `tickets/archive/<ID>.md`, append a row to `Closed_Tickets.md`, remove the row from `Open_TICKETS.md`.

Never reference `REFACTOR_TRACKER.md` — it has been superseded by `Open_TICKETS.md`.
Never close a ticket without first confirming the build and tests pass.
Keep the ticket description concise; let code comments and tests capture details.