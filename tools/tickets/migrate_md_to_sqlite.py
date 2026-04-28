"""migrate_md_to_sqlite.py — One-shot (idempotent) migration from markdown files to SQLite.

Usage:
    python tools/tickets/migrate_md_to_sqlite.py
"""

import os
import re
import sys
import html
import warnings

# Resolve repo root relative to this script
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
sys.path.insert(0, SCRIPT_DIR)

from ticket_db import init_db, upsert_ticket, parse_ticket_id, resolve_ticket_path, now_iso, DB_PATH


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _strip(text: str) -> str:
    """Strip HTML tags, decode entities, collapse whitespace."""
    text = re.sub(r'<[^>]+>', '', text)
    text = html.unescape(text)
    return re.sub(r'\s+', ' ', text).strip()


def _parse_html_table(content: str) -> list[dict]:
    """Extract rows from an HTML <table> block. Returns list of dicts keyed by lowercased header."""
    table_m = re.search(r'<table[^>]*>(.*?)</table>', content, re.DOTALL | re.IGNORECASE)
    if not table_m:
        return []
    table_html = table_m.group(1)
    rows = re.findall(r'<tr[^>]*>(.*?)</tr>', table_html, re.DOTALL | re.IGNORECASE)
    if not rows:
        return []
    headers = [_strip(h).lower() for h in re.findall(r'<th[^>]*>(.*?)</th>', rows[0], re.DOTALL | re.IGNORECASE)]
    if not headers:
        # Try first row as header via <td>
        headers = [_strip(h).lower() for h in re.findall(r'<td[^>]*>(.*?)</td>', rows[0], re.DOTALL | re.IGNORECASE)]
        data_rows = rows[1:]
    else:
        data_rows = rows[1:]
    result = []
    for row in data_rows:
        cells = [_strip(c) for c in re.findall(r'<td[^>]*>(.*?)</td>', row, re.DOTALL | re.IGNORECASE)]
        if not any(cells):
            continue
        padded = cells + [''] * max(0, len(headers) - len(cells))
        result.append(dict(zip(headers, padded)))
    return result


def _parse_pipe_table(content: str) -> list[dict]:
    """Extract rows from the first Markdown pipe table that has an 'id' column."""
    # Split content into candidate tables: sequences of lines containing '|'
    # We find the first table whose header contains 'id'
    lines = content.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        if '|' not in line:
            i += 1
            continue
        # Potential header row
        headers = [h.strip().lower() for h in line.strip('|').split('|')]
        if 'id' not in headers:
            i += 1
            continue
        # Consume separator row
        if i + 1 < len(lines) and re.match(r'^[\s|:-]+$', lines[i + 1]):
            i += 2
        else:
            i += 1
        # Consume data rows
        result = []
        while i < len(lines) and '|' in lines[i]:
            row_line = lines[i]
            if re.match(r'^[\s|:-]+$', row_line):
                i += 1
                continue
            cells = [c.strip() for c in row_line.strip('|').split('|')]
            padded = cells + [''] * max(0, len(headers) - len(cells))
            result.append(dict(zip(headers, padded)))
            i += 1
        return result
    return []


def _parse_table(content: str, label: str) -> list[dict]:
    rows = _parse_html_table(content)
    if rows:
        return rows
    rows = _parse_pipe_table(content)
    if rows:
        return rows
    warnings.warn(f"[migrate] Could not parse table in {label} — skipping.")
    return []


def _map_open_row(raw: dict) -> dict | None:
    """Map an Open_TICKETS.md HTML-table row to a DB dict."""
    ticket_id = raw.get('id', '').strip()
    if not ticket_id or ticket_id in ('---', 'id'):
        return None
    try:
        prefix, number = parse_ticket_id(ticket_id)
    except ValueError:
        warnings.warn(f"[migrate] Skipping unrecognised ID: {ticket_id!r}")
        return None
    return {
        "ticket_id":   ticket_id,
        "prefix":      prefix,
        "number":      number,
        "priority":    raw.get('priority', '').strip() or None,
        "title":       raw.get('title', ticket_id).strip() or ticket_id,
        "category":    raw.get('category', '').strip() or None,
        "size":        raw.get('size', '').strip() or None,
        "source":      raw.get('source', '').strip() or None,
        "status":      raw.get('status', 'Open').strip() or 'Open',
        "state":       'Open',
        "resolution":  None,
        "created_at":  now_iso(),
        "closed_at":   None,
        "ticket_path": resolve_ticket_path(ticket_id, REPO_ROOT),
    }


def _map_closed_row(raw: dict) -> dict | None:
    """Map a Closed_Tickets.md pipe-table row to a DB dict."""
    ticket_id = (raw.get('id') or '').strip()
    if not ticket_id or ticket_id in ('---', 'id'):
        return None
    try:
        prefix, number = parse_ticket_id(ticket_id)
    except ValueError:
        warnings.warn(f"[migrate] Skipping unrecognised closed ID: {ticket_id!r}")
        return None
    closed_date = (raw.get('closed date') or raw.get('closed_date') or '').strip()
    notes = (raw.get('notes') or '').strip()
    return {
        "ticket_id":   ticket_id,
        "prefix":      prefix,
        "number":      number,
        "priority":    None,
        "title":       (raw.get('title') or ticket_id).strip() or ticket_id,
        "category":    (raw.get('category') or '').strip() or None,
        "size":        (raw.get('size') or '').strip() or None,
        "source":      None,
        "status":      'Closed',
        "state":       'Closed',
        "resolution":  notes or None,
        "created_at":  None,
        "closed_at":   closed_date if closed_date and closed_date != '–' else None,
        "ticket_path": resolve_ticket_path(ticket_id, REPO_ROOT),
    }


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def migrate(db_path: str = DB_PATH) -> None:
    init_db(db_path)

    open_md = os.path.join(REPO_ROOT, "Open_TICKETS.md")
    closed_md = os.path.join(REPO_ROOT, "Closed_Tickets.md")

    # --- Open tickets ---
    if os.path.exists(open_md):
        content = open(open_md, encoding='utf-8').read()
        rows = _parse_table(content, "Open_TICKETS.md")
        inserted = updated = skipped = 0
        for raw in rows:
            mapped = _map_open_row(raw)
            if mapped is None:
                skipped += 1
                continue
            upsert_ticket(mapped, db_path)
            inserted += 1
        print(f"[migrate] Open_TICKETS.md: {inserted} tickets processed, {skipped} skipped.")
    else:
        print("[migrate] Open_TICKETS.md not found — skipping.")

    # --- Closed tickets ---
    if os.path.exists(closed_md):
        try:
            # Try UTF-8 first, fall back to cp1252 (Windows-1252) for legacy files
            for enc in ('utf-8', 'cp1252', 'latin-1'):
                try:
                    content = open(closed_md, encoding=enc).read()
                    break
                except UnicodeDecodeError:
                    continue
            else:
                warnings.warn("[migrate] WARNING: Could not decode Closed_Tickets.md — skipping.")
                content = ""
            rows = _parse_table(content, "Closed_Tickets.md")
            inserted = skipped = 0
            for raw in rows:
                mapped = _map_closed_row(raw)
                if mapped is None:
                    skipped += 1
                    continue
                upsert_ticket(mapped, db_path)
                inserted += 1
            print(f"[migrate] Closed_Tickets.md: {inserted} tickets processed, {skipped} skipped.")
        except Exception as exc:
            warnings.warn(f"[migrate] WARNING: Could not process Closed_Tickets.md: {exc}")
    else:
        print("[migrate] Closed_Tickets.md not found — skipping.")

    print(f"[migrate] Done. DB: {db_path}")


if __name__ == "__main__":
    migrate()
