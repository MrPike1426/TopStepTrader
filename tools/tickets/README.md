# Ticket Tooling — `tools/tickets/`

SQLite-backed ticket workflow helper for the TopStepTrader repository.  
The DB is the **index and search surface**; individual markdown files in `tickets/` remain the **detail record**.

## Files

| File | Purpose |
|---|---|
| `ticket_db.py` | Schema init, upsert, helpers |
| `migrate_md_to_sqlite.py` | One-shot (idempotent) migration from `Open_TICKETS.md` + `Closed_Tickets.md` |
| `tickets.py` | CLI entrypoint — create, list, show, close, reopen, search, stats |

## Requirements

Python 3.11+ (stdlib only — `sqlite3` is built-in, no pip installs needed).

## Smoke Test / Quick-Start

```bash
# 1. Initialise DB and migrate existing markdown data
python tools/tickets/migrate_md_to_sqlite.py

# 2. List open tickets
python tools/tickets/tickets.py list

# 3. Create a new ticket
python tools/tickets/tickets.py new BUG P1 "Example bug title" --category Bugs --size S --source UAT

# 4. Show the ticket (replace BUG-XX with the actual ID printed in step 3)
python tools/tickets/tickets.py show BUG-XX

# 5. Close the ticket
python tools/tickets/tickets.py close BUG-XX --resolution "Fixed by doing X."

# 6. Confirm it no longer appears in the open list
python tools/tickets/tickets.py list

# 7. Full-text search
python tools/tickets/tickets.py search "stale contract"

# 8. Stats
python tools/tickets/tickets.py stats
```

## Workflow (replaces editing Open_TICKETS.md / Closed_Tickets.md)

### Creating a ticket
```bash
python tools/tickets/tickets.py new <PREFIX> <PRIORITY> "<TITLE>" \
    [--category <CATEGORY>] [--size XS|S|M|L|XL] [--source Manual|UAT|Code-Review]
```
- Determines the next sequential ID automatically (scans the DB).
- Creates `tickets/<ID>.md` with the standard template.
- Inserts a row into the DB with `state=Open`.

### Closing a ticket
```bash
python tools/tickets/tickets.py close <ID> --resolution "<ONE-LINE SUMMARY>"
```
- Appends a `## Resolution` section to the markdown file.
- Moves `tickets/<ID>.md` → `tickets/archive/<ID>.md`.
- Sets `state=Closed`, `closed_at=<now>`, and writes `resolution` in the DB.

### Reopening a ticket
```bash
python tools/tickets/tickets.py reopen <ID>
```
- Moves `tickets/archive/<ID>.md` → `tickets/<ID>.md`.
- Sets `state=Open` in the DB.

### Listing open tickets
```bash
python tools/tickets/tickets.py list
```
Sorted by priority (P0→P3) then prefix+number.

### Searching
```bash
python tools/tickets/tickets.py search "keyword or phrase"
```
Uses SQLite FTS5 over `title` and `resolution`.

## DB Location

`tools/tickets/tickets.db` — excluded from git via `.gitignore`.

## Migration Notes

`migrate_md_to_sqlite.py` is **idempotent**: re-running it will upsert (not duplicate) existing tickets.  
It tolerates a missing or unreadable `Closed_Tickets.md` with a single warning.
