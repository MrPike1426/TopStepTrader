"""
db_reader.py
Reads debug_trades.db and returns typed dicts for one trade + its snapshots.
The DB lives at %LOCALAPPDATA%/TopStepTrader/debug_trades.db by default.
"""

import sqlite3
import os
import sys
from pathlib import Path
from typing import Optional


DEFAULT_DB_PATH = Path(os.environ.get("LOCALAPPDATA", "")) / "TopStepTrader" / "debug_trades.db"


def resolve_db_path(override: Optional[str] = None) -> Path:
    path = Path(override) if override else DEFAULT_DB_PATH
    if not path.exists():
        print(f"ERROR: debug_trades.db not found at: {path}", file=sys.stderr)
        print("Make sure Debug Capture was enabled during the session.", file=sys.stderr)
        sys.exit(1)
    return path


def _connect(db_path: Path) -> sqlite3.Connection:
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    return conn


def fetch_trade_by_internal_id(db_path: Path, trade_id: str) -> Optional[dict]:
    """Look up a DebugTrade row by its internal TradeId (GUID)."""
    with _connect(db_path) as conn:
        row = conn.execute(
            "SELECT * FROM DebugTrades WHERE TradeId = ?", (trade_id,)
        ).fetchone()
        return dict(row) if row else None


def fetch_trade_by_entry_time(db_path: Path, entry_time_prefix: str) -> Optional[dict]:
    """
    Look up a DebugTrade row by a prefix of EntryTime.
    entry_time_prefix can be e.g. '2026-05-05T14:15:03' — will match any millisecond.
    TopStepX EntryTime and our EntryTime should agree to the second.
    """
    with _connect(db_path) as conn:
        clean_time = entry_time_prefix.strip().replace(" ", "T")
        rows = conn.execute(
            "SELECT * FROM DebugTrades WHERE EntryTime LIKE ?",
            (clean_time + "%",)
        ).fetchall()
        if not rows:
            return None
        if len(rows) > 1:
            print(f"WARNING: {len(rows)} trades match entry time prefix '{entry_time_prefix}'. "
                  f"Using the first match. Pass a more specific prefix if needed.", file=sys.stderr)
        return dict(rows[0])


def fetch_trade_by_symbol_and_entry(db_path: Path, symbol: str, entry_time_prefix: str) -> Optional[dict]:
    """Narrow search by symbol + entry time prefix (most reliable join key)."""
    with _connect(db_path) as conn:
        # Normalise symbol: TopStepX prefixes with '/' (/MNQ), our DB stores root (MNQ)
        clean_symbol = symbol.lstrip("/").upper()
        # Normalise time prefix: DB stores ISO-8601 with 'T' separator and 'Z' suffix
        # Accept both "2026-05-05 16:50:47" and "2026-05-05T16:50:47" forms
        clean_time = entry_time_prefix.strip().replace(" ", "T")
        rows = conn.execute(
            "SELECT * FROM DebugTrades WHERE Instrument LIKE ? AND EntryTime LIKE ?",
            (f"%{clean_symbol}%", clean_time + "%")
        ).fetchall()
        if not rows:
            return None
        if len(rows) > 1:
            print(f"WARNING: {len(rows)} trades match. Using first.", file=sys.stderr)
        return dict(rows[0])


def fetch_snapshots(db_path: Path, trade_id: str) -> list[dict]:
    """Return all DebugSnapshots for a trade, ordered by Timestamp ascending."""
    with _connect(db_path) as conn:
        rows = conn.execute(
            "SELECT * FROM DebugSnapshots WHERE TradeId = ? ORDER BY Timestamp ASC",
            (trade_id,)
        ).fetchall()
        return [dict(r) for r in rows]


def list_recent_trades(db_path: Path, limit: int = 20) -> list[dict]:
    """List the most recent N trades for discovery / selection."""
    with _connect(db_path) as conn:
        rows = conn.execute(
            "SELECT TradeId, Instrument, Direction, EntryTime, EntryPrice, "
            "       RealisedPnLDollars, ClosedAt, Persona "
            "FROM DebugTrades ORDER BY CreatedAt DESC LIMIT ?",
            (limit,)
        ).fetchall()
        return [dict(r) for r in rows]
