"""tickets.py — CLI entrypoint for the SQLite-backed ticket workflow.

Commands:
    list                          List open tickets sorted by priority then ID.
    show <ID>                     Show DB row + ticket markdown content.
    new <PREFIX> <PRIORITY> <TITLE> [--category C] [--size S] [--source SRC]
                                  Create a new ticket row + markdown file.
    close <ID> --resolution "..." Close a ticket; move markdown to archive.
    reopen <ID>                   Re-open a closed ticket; move markdown back.
    search <QUERY>                Full-text search title/resolution.
    stats                         Show count by state/prefix.

Usage examples:
    python tools/tickets/tickets.py list
    python tools/tickets/tickets.py new BUG P1 "Something is broken" --category Bugs --size S --source UAT
    python tools/tickets/tickets.py show BUG-42
    python tools/tickets/tickets.py close BUG-42 --resolution "Fixed by removing stale cache."
    python tools/tickets/tickets.py reopen BUG-42
    python tools/tickets/tickets.py search "stale contract"
    python tools/tickets/tickets.py stats
"""

import argparse
import os
import shutil
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
sys.path.insert(0, SCRIPT_DIR)

from ticket_db import (
    init_db, get_conn, next_id, upsert_ticket, parse_ticket_id,
    resolve_ticket_path, now_iso, DB_PATH
)

TICKETS_DIR = os.path.join(REPO_ROOT, "tickets")
ARCHIVE_DIR = os.path.join(REPO_ROOT, "tickets", "archive")

TICKET_TEMPLATE = """\
# {ticket_id} — {title}

**Status:** Open
**Priority:** {priority}
**Category:** {category}
**Size:** {size}
**Source:** {source}
**Files:** ``

## Problem
<description of current behaviour and why it's wrong or missing>

## Proposed Fix
<approach or code description>

## Acceptance Criteria
- [ ] item 1
- [ ] Build passes; all tests still pass
"""

PRIORITY_ORDER = {"P0": 0, "P1": 1, "P2": 2, "P3": 3}


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _ensure_dirs():
    os.makedirs(TICKETS_DIR, exist_ok=True)
    os.makedirs(ARCHIVE_DIR, exist_ok=True)


def _priority_key(row) -> tuple:
    p = str(row["priority"] or "P9")
    return (PRIORITY_ORDER.get(p, 9), row["prefix"], row["number"])


def _print_row(row):
    r = dict(row)
    path = r.get("ticket_path") or "-"
    print(f"  {r['ticket_id']:<12} {r['priority'] or '-':<4}  {r['state']:<7}  {r['status'] or '-':<14}  {r['size'] or '-':<4}  {r['category'] or '-':<20}  {r['title']}")
    print(f"  {'':12} path: {path}")


def _read_md(ticket_id: str) -> str | None:
    for subdir in ("", "archive"):
        p = os.path.join(TICKETS_DIR, subdir, f"{ticket_id}.md")
        if os.path.exists(p):
            return open(p, encoding="utf-8").read()
    return None


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def cmd_list(args):
    with get_conn() as conn:
        rows = conn.execute(
            "SELECT * FROM tickets WHERE state='Open' ORDER BY priority, prefix, number"
        ).fetchall()
    if not rows:
        print("No open tickets.")
        return
    print(f"{'ID':<12} {'Pri':<4}  {'State':<7}  {'Status':<14}  {'Size':<4}  {'Category':<20}  Title")
    print("-" * 100)
    for row in sorted(rows, key=_priority_key):
        r = dict(row)
        print(f"  {r['ticket_id']:<12} {r['priority'] or '-':<4}  {r['state']:<7}  {r['status'] or '-':<14}  {r['size'] or '-':<4}  {r['category'] or '-':<20}  {r['title']}")


def cmd_show(args):
    ticket_id = args.id.upper()
    with get_conn() as conn:
        row = conn.execute("SELECT * FROM tickets WHERE ticket_id=?", (ticket_id,)).fetchone()
    if row is None:
        print(f"Ticket {ticket_id} not found in DB.")
        return
    print("=== DB Row ===")
    for k in row.keys():
        print(f"  {k}: {row[k]}")
    content = _read_md(ticket_id)
    if content:
        print("\n=== Ticket Markdown ===")
        print(content)
    else:
        print(f"\n[warn] No markdown file found for {ticket_id}.")


def cmd_new(args):
    init_db()
    _ensure_dirs()
    prefix = args.prefix.upper()
    ticket_id = next_id(prefix)
    try:
        p, n = parse_ticket_id(ticket_id)
    except ValueError as e:
        print(f"Error: {e}")
        sys.exit(1)

    category = args.category or prefix
    size = args.size or "M"
    source = args.source or "Manual"
    priority = args.priority.upper()

    md_content = TICKET_TEMPLATE.format(
        ticket_id=ticket_id,
        title=args.title,
        priority=priority,
        category=category,
        size=size,
        source=source,
    )
    md_path = os.path.join(TICKETS_DIR, f"{ticket_id}.md")
    with open(md_path, "w", encoding="utf-8") as f:
        f.write(md_content)

    upsert_ticket({
        "ticket_id":   ticket_id,
        "prefix":      p,
        "number":      n,
        "priority":    priority,
        "title":       args.title,
        "category":    category,
        "size":        size,
        "source":      source,
        "status":      "Open",
        "state":       "Open",
        "resolution":  None,
        "created_at":  now_iso(),
        "closed_at":   None,
        "ticket_path": f"tickets/{ticket_id}.md",
    })
    print(f"Created {ticket_id}: {md_path}")


def cmd_close(args):
    ticket_id = args.id.upper()
    with get_conn() as conn:
        row = conn.execute("SELECT * FROM tickets WHERE ticket_id=?", (ticket_id,)).fetchone()
    if row is None:
        print(f"Ticket {ticket_id} not found.")
        sys.exit(1)

    _ensure_dirs()
    src = os.path.join(TICKETS_DIR, f"{ticket_id}.md")
    dst = os.path.join(ARCHIVE_DIR, f"{ticket_id}.md")

    if os.path.exists(src):
        # Append resolution section to markdown
        with open(src, encoding="utf-8") as f:
            md = f.read()
        if args.resolution and "## Resolution" not in md:
            md += f"\n## Resolution\n{args.resolution}\n"
            with open(src, "w", encoding="utf-8") as f:
                f.write(md)
        shutil.move(src, dst)
        print(f"Moved {src} → {dst}")
    elif os.path.exists(dst):
        print(f"[warn] {ticket_id}.md already in archive.")
    else:
        print(f"[warn] No markdown file found for {ticket_id}.")

    upsert_ticket({
        "ticket_id":   ticket_id,
        "prefix":      row["prefix"],
        "number":      row["number"],
        "state":       "Closed",
        "status":      "Closed",
        "resolution":  args.resolution or row["resolution"],
        "closed_at":   now_iso(),
        "ticket_path": f"tickets/archive/{ticket_id}.md",
        # pass through required non-null fields
        "priority":    row["priority"],
        "title":       row["title"],
        "category":    row["category"],
        "size":        row["size"],
        "source":      row["source"],
        "created_at":  row["created_at"],
    })
    print(f"Closed {ticket_id}.")


def cmd_reopen(args):
    ticket_id = args.id.upper()
    with get_conn() as conn:
        row = conn.execute("SELECT * FROM tickets WHERE ticket_id=?", (ticket_id,)).fetchone()
    if row is None:
        print(f"Ticket {ticket_id} not found.")
        sys.exit(1)

    _ensure_dirs()
    src = os.path.join(ARCHIVE_DIR, f"{ticket_id}.md")
    dst = os.path.join(TICKETS_DIR, f"{ticket_id}.md")
    if os.path.exists(src):
        shutil.move(src, dst)
        print(f"Moved {src} → {dst}")
    elif os.path.exists(dst):
        print(f"[info] {ticket_id}.md already in tickets/.")
    else:
        print(f"[warn] No markdown file found for {ticket_id}.")

    upsert_ticket({
        "ticket_id":   ticket_id,
        "prefix":      row["prefix"],
        "number":      row["number"],
        "state":       "Open",
        "status":      "Open",
        "resolution":  None,
        "closed_at":   None,
        "ticket_path": f"tickets/{ticket_id}.md",
        "priority":    row["priority"],
        "title":       row["title"],
        "category":    row["category"],
        "size":        row["size"],
        "source":      row["source"],
        "created_at":  row["created_at"],
    })
    print(f"Reopened {ticket_id}.")


def cmd_search(args):
    with get_conn() as conn:
        rows = conn.execute("""
            SELECT t.* FROM tickets t
            JOIN tickets_fts f ON t.rowid = f.rowid
            WHERE tickets_fts MATCH ?
            ORDER BY t.state, t.priority, t.prefix, t.number
        """, (args.query,)).fetchall()
    if not rows:
        print("No results.")
        return
    print(f"{'ID':<12} {'State':<7}  {'Pri':<4}  Title")
    print("-" * 70)
    for row in rows:
        r = dict(row)
        print(f"  {r['ticket_id']:<12} {r['state']:<7}  {r['priority'] or '-':<4}  {r['title']}")


def cmd_stats(args):
    with get_conn() as conn:
        rows = conn.execute("""
            SELECT state, prefix, COUNT(*) as cnt
            FROM tickets GROUP BY state, prefix ORDER BY state, prefix
        """).fetchall()
        total = conn.execute("SELECT COUNT(*) FROM tickets").fetchone()[0]
    if not rows:
        print("DB is empty.")
        return
    print(f"{'State':<8}  {'Prefix':<10}  Count")
    print("-" * 35)
    for row in rows:
        print(f"  {row['state']:<8}  {row['prefix']:<10}  {row['cnt']}")
    print(f"  Total: {total}")


# ---------------------------------------------------------------------------
# Argument parser
# ---------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="tickets.py",
        description="SQLite-backed ticket workflow CLI"
    )
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("list", help="List open tickets")

    p_show = sub.add_parser("show", help="Show a ticket")
    p_show.add_argument("id")

    p_new = sub.add_parser("new", help="Create a new ticket")
    p_new.add_argument("prefix", help="Ticket prefix, e.g. BUG")
    p_new.add_argument("priority", help="P0-P3")
    p_new.add_argument("title")
    p_new.add_argument("--category", default=None)
    p_new.add_argument("--size", default="M")
    p_new.add_argument("--source", default="Manual")

    p_close = sub.add_parser("close", help="Close a ticket")
    p_close.add_argument("id")
    p_close.add_argument("--resolution", default="")

    p_reopen = sub.add_parser("reopen", help="Reopen a closed ticket")
    p_reopen.add_argument("id")

    p_search = sub.add_parser("search", help="FTS search")
    p_search.add_argument("query")

    sub.add_parser("stats", help="Show statistics")

    return parser


def main():
    init_db()
    parser = build_parser()
    args = parser.parse_args()
    dispatch = {
        "list":   cmd_list,
        "show":   cmd_show,
        "new":    cmd_new,
        "close":  cmd_close,
        "reopen": cmd_reopen,
        "search": cmd_search,
        "stats":  cmd_stats,
    }
    dispatch[args.command](args)


if __name__ == "__main__":
    main()
