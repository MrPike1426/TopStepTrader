#!/usr/bin/env python3
"""
postmortem.py  —  TopStepTrader Trade Post-Mortem Skill
========================================================
Usage (interactive):
    python tools/postmortem/postmortem.py

Usage (non-interactive / CI):
    python tools/postmortem/postmortem.py \
        --order-id 2927418157 \
        --trade-id 2549065559 \
        --symbol /MNQ \
        --entry-time "2026-05-05T14:15:03" \
        --closed-by app

Outputs:
  • Markdown report written to tools/postmortem/reports/<symbol>_<date>_<order-id>.md
  • Human-readable summary printed to stdout

Options:
  --db PATH       Override the default debug_trades.db location
  --list          List the 20 most recent debug trades and exit
"""

import argparse
import sys
import os
from pathlib import Path
from datetime import datetime

# ── make imports work whether called from repo root or tools/postmortem ───────
_HERE = Path(__file__).parent.resolve()
sys.path.insert(0, str(_HERE))

from db_reader import resolve_db_path, fetch_trade_by_symbol_and_entry, \
    fetch_snapshots, list_recent_trades
from report_builder import build_report


REPORTS_DIR = _HERE / "reports"


# ── helpers ───────────────────────────────────────────────────────────────────

def _prompt(label: str, default: str = "") -> str:
    hint = f" [{default}]" if default else ""
    val = input(f"  {label}{hint}: ").strip()
    return val if val else default


def _list_trades(db_path):
    trades = list_recent_trades(db_path)
    if not trades:
        print("No debug trades found in the database.")
        return
    print(f"\n{'#':<4} {'Instrument':<10} {'Dir':<6} {'EntryTime':<22} {'EntryPrice':<12} {'P&L':>8}  TradeId")
    print("-" * 100)
    for i, t in enumerate(trades, 1):
        pnl = t.get("RealisedPnLDollars")
        pnl_str = f"+${pnl:.2f}" if pnl and float(pnl) >= 0 else (f"-${abs(float(pnl)):.2f}" if pnl else "N/A")
        print(f"{i:<4} {t.get('Instrument',''):<10} {t.get('Direction',''):<6} "
              f"{str(t.get('EntryTime',''))[:22]:<22} "
              f"{str(t.get('EntryPrice','')):<12} {pnl_str:>8}  {t.get('TradeId','')}")
    print()


# ── main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="TopStepTrader Trade Post-Mortem")
    parser.add_argument("--order-id",    help="TopStepX Order ID (initial entry order)")
    parser.add_argument("--trade-id",    help="TopStepX Trade ID (closed trade / TP-SL wrap-up)")
    parser.add_argument("--symbol",      help="Instrument symbol, e.g. /MNQ or MNQ")
    parser.add_argument("--entry-time",  help="Order Time prefix, e.g. 2026-05-05T14:15:03")
    parser.add_argument("--closed-by",   choices=["app", "manual", "unknown"],
                                         help="Who closed the trade: app | manual | unknown")
    parser.add_argument("--issue",       default="",
                                         help="Your specific concern about this trade (free text)")
    parser.add_argument("--db",          help="Path to debug_trades.db (optional override)")
    parser.add_argument("--list",        action="store_true",
                                         help="List 20 most recent debug trades and exit")
    parser.add_argument("--replay-bb",   action="store_true",
                                         help="Append a BB median / lower-BB SL replay section to the report")
    args = parser.parse_args()

    db_path = resolve_db_path(args.db)

    if args.list:
        _list_trades(db_path)
        return

    # ── gather inputs (interactive fallback) ──────────────────────────────────
    interactive = not any([args.order_id, args.trade_id, args.symbol, args.entry_time])
    if interactive:
        print()
        print("╔══════════════════════════════════════════════════╗")
        print("║       TopStepTrader — Trade Post-Mortem          ║")
        print("╚══════════════════════════════════════════════════╝")
        print()
        print("  Tip: Run with --list to browse recent debug trades.")
        print()
        order_id   = _prompt("TopStepX Order ID   (from Orders tab)")
        trade_id   = _prompt("TopStepX Trade ID   (from Trades tab)")
        symbol     = _prompt("Symbol              (e.g. /MNQ)")
        entry_time = _prompt("Order Time prefix   (e.g. 2026-05-05T14:15:03)")
        print()
        print("  How was the trade closed?")
        print("    1 = Application (TP/SL fired programmatically)")
        print("    2 = Manually on the TopStepX platform")
        print("    3 = Unknown")
        choice = _prompt("Choice", "3")
        closed_by = {"1": "app", "2": "manual", "3": "unknown"}.get(choice, "unknown")
        print()
        trader_issue = _prompt("Your issue / question about this trade (optional, Enter to skip)")
        replay_bb_choice = _prompt("Run BB median / lower-BB SL replay? (y/N)", "n")
        replay_bb = replay_bb_choice.lower() == "y"
    else:
        order_id     = args.order_id   or ""
        trade_id     = args.trade_id   or ""
        symbol       = args.symbol     or ""
        entry_time   = args.entry_time or ""
        closed_by    = args.closed_by  or "unknown"
        trader_issue = args.issue      or ""
    replay_bb = getattr(args, "replay_bb", False)

    # ── validate ──────────────────────────────────────────────────────────────
    missing = [name for name, val in [
        ("Order ID",   order_id),
        ("Trade ID",   trade_id),
        ("Symbol",     symbol),
        ("Entry Time", entry_time),
    ] if not val.strip()]
    if missing:
        print(f"\nERROR: Missing required values: {', '.join(missing)}", file=sys.stderr)
        parser.print_help()
        sys.exit(1)

    # ── look up debug record ───────────────────────────────────────────────────
    print(f"\nSearching debug_trades.db for {symbol} @ {entry_time} …")
    trade = fetch_trade_by_symbol_and_entry(db_path, symbol, entry_time)

    if not trade:
        print()
        print("⚠️  No matching debug trade found.")
        print(f"   Symbol:     {symbol}")
        print(f"   EntryTime:  {entry_time}*")
        print()
        print("   Possible reasons:")
        print("   • Debug Capture was not enabled during this session.")
        print("   • The entry time does not match to the second — try a shorter prefix.")
        print("   • The DB was purged since the session.")
        print()
        print("   Use --list to see what is in the database.")
        print()
        print("Generating a skeleton report with TopStepX IDs only …")
        trade = {
            "TradeId": "NOT FOUND IN DEBUG DB",
            "Direction": "N/A", "SlotIndex": "N/A", "Persona": "N/A",
            "EntryTime": entry_time, "EntryPrice": "N/A",
            "ActualFillPrice": "N/A", "SlippageTicks": "N/A",
            "Quantity": "N/A", "ClosedAt": "N/A", "ExitPrice": "N/A",
            "RealisedPnLDollars": None, "Commissions": None, "NetPnLDollars": None,
            "InitialStopPrice": "N/A", "InitialTargetPrice": "N/A",
            "InitialRiskDollars": None, "PlannedRR": None,
            "MFEDollars": None, "MAEDollars": None,
            "AtrAtEntry": None, "AdxAtEntry": None,
            "ExitReason": "N/A", "FinalStopPhase": "N/A",
        }
        snapshots = []
    else:
        internal_id = trade.get("TradeId", "")
        snapshots = fetch_snapshots(db_path, internal_id)
        print(f"✅ Found trade  Internal ID: {internal_id}")
        print(f"   {len(snapshots)} debug snapshot(s) loaded.")

    # ── build report ──────────────────────────────────────────────────────────
    report_md = build_report(
        order_id=order_id,
        trade_id=trade_id,
        symbol=symbol,
        closed_by=closed_by,
        trade=trade,
        snapshots=snapshots,
        trader_issue=trader_issue,
    )

    # ── optional BB replay section ────────────────────────────────────────────
    if replay_bb:
        from bb_replay import run_replay, format_replay_markdown
        entry_px  = trade.get("EntryPrice") or trade.get("ActualFillPrice") or 0
        direction = trade.get("Direction") or "Long"
        try:
            entry_px = float(entry_px)
        except (ValueError, TypeError):
            entry_px = 0.0
        print("\nRunning BB median / lower-BB SL replay …")
        replay_result = run_replay(
            snapshots=snapshots,
            entry_price=entry_px,
            direction=direction,
        )
        replay_section = format_replay_markdown(replay_result)
        report_md = report_md + "\n\n---\n\n" + replay_section
        print(f"   ✅ Replay complete — {len(replay_result.get('rows', []))} ticks analysed.")

    # ── write file ────────────────────────────────────────────────────────────
    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    clean_sym = symbol.lstrip("/").upper()
    date_tag  = entry_time[:10].replace("-", "")
    filename  = f"{clean_sym}_{date_tag}_{order_id}.md"
    report_path = REPORTS_DIR / filename
    report_path.write_text(report_md, encoding="utf-8")

    # ── stdout summary ────────────────────────────────────────────────────────
    print()
    print("═" * 70)
    print(report_md)
    print("═" * 70)
    print()
    print(f"📄 Report saved → {report_path}")
    print()


if __name__ == "__main__":
    main()
