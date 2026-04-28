"""ticket_db.py — SQLite schema init and helper functions for the ticket workflow."""

import sqlite3
import os
import re
from datetime import datetime, timezone

DB_PATH = os.path.join(os.path.dirname(__file__), "tickets.db")


def get_conn(db_path: str = DB_PATH) -> sqlite3.Connection:
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    return conn


def init_db(db_path: str = DB_PATH) -> None:
    """Create schema if it doesn't exist. Safe to call on an existing DB."""
    with get_conn(db_path) as conn:
        conn.executescript("""
            CREATE TABLE IF NOT EXISTS tickets (
                ticket_id   TEXT PRIMARY KEY,
                prefix      TEXT NOT NULL,
                number      INTEGER NOT NULL,
                priority    TEXT,
                title       TEXT NOT NULL,
                category    TEXT,
                size        TEXT,
                source      TEXT,
                status      TEXT,
                state       TEXT NOT NULL DEFAULT 'Open',
                resolution  TEXT,
                created_at  TEXT,
                closed_at   TEXT,
                ticket_path TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_tickets_state    ON tickets(state);
            CREATE INDEX IF NOT EXISTS idx_tickets_priority ON tickets(priority);
            CREATE INDEX IF NOT EXISTS idx_tickets_category ON tickets(category);
            CREATE INDEX IF NOT EXISTS idx_tickets_prefix_number ON tickets(prefix, number);

            CREATE VIRTUAL TABLE IF NOT EXISTS tickets_fts
                USING fts5(title, resolution, content='tickets', content_rowid='rowid');

            CREATE TRIGGER IF NOT EXISTS tickets_ai AFTER INSERT ON tickets BEGIN
                INSERT INTO tickets_fts(rowid, title, resolution)
                VALUES (new.rowid, new.title, new.resolution);
            END;

            CREATE TRIGGER IF NOT EXISTS tickets_au AFTER UPDATE ON tickets BEGIN
                INSERT INTO tickets_fts(tickets_fts, rowid, title, resolution)
                VALUES ('delete', old.rowid, old.title, old.resolution);
                INSERT INTO tickets_fts(rowid, title, resolution)
                VALUES (new.rowid, new.title, new.resolution);
            END;

            CREATE TRIGGER IF NOT EXISTS tickets_ad AFTER DELETE ON tickets BEGIN
                INSERT INTO tickets_fts(tickets_fts, rowid, title, resolution)
                VALUES ('delete', old.rowid, old.title, old.resolution);
            END;
        """)


def parse_ticket_id(ticket_id: str):
    """Return (prefix, number) or raise ValueError."""
    m = re.match(r'^([A-Z]+(?:-[A-Z]+)?)-(\d+)([a-z]?)$', ticket_id.strip())
    if not m:
        raise ValueError(f"Invalid ticket ID: {ticket_id!r}")
    return m.group(1), int(m.group(2))


def next_id(prefix: str, db_path: str = DB_PATH) -> str:
    """Return the next available ID for a given prefix."""
    with get_conn(db_path) as conn:
        row = conn.execute(
            "SELECT MAX(number) FROM tickets WHERE prefix=?", (prefix,)
        ).fetchone()
        n = (row[0] or 0) + 1
    return f"{prefix}-{n:02d}"


def upsert_ticket(row: dict, db_path: str = DB_PATH) -> None:
    """Insert or update a ticket row. Only updates fields that are provided."""
    with get_conn(db_path) as conn:
        existing = conn.execute(
            "SELECT * FROM tickets WHERE ticket_id=?", (row["ticket_id"],)
        ).fetchone()
        if existing is None:
            conn.execute("""
                INSERT INTO tickets
                    (ticket_id, prefix, number, priority, title, category,
                     size, source, status, state, resolution, created_at, closed_at, ticket_path)
                VALUES
                    (:ticket_id, :prefix, :number, :priority, :title, :category,
                     :size, :source, :status, :state, :resolution, :created_at, :closed_at, :ticket_path)
            """, row)
        else:
            # Update only non-None supplied fields
            fields = [k for k in row if k != "ticket_id" and row[k] is not None]
            if fields:
                set_clause = ", ".join(f"{f}=:{f}" for f in fields)
                conn.execute(
                    f"UPDATE tickets SET {set_clause} WHERE ticket_id=:ticket_id",
                    {k: row[k] for k in fields + ["ticket_id"]}
                )


def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def resolve_ticket_path(ticket_id: str, repo_root: str | None = None) -> str | None:
    """Find where the ticket markdown lives: open dir or archive."""
    if repo_root is None:
        repo_root = os.path.join(os.path.dirname(__file__), "..", "..")
    open_path = os.path.join(repo_root, "tickets", f"{ticket_id}.md")
    arch_path = os.path.join(repo_root, "tickets", "archive", f"{ticket_id}.md")
    if os.path.exists(open_path):
        return f"tickets/{ticket_id}.md"
    if os.path.exists(arch_path):
        return f"tickets/archive/{ticket_id}.md"
    return None
