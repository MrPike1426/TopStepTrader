"""OBS-08 — Daily combine report + promotion-gate checklist.

Recurring, read-only report over the recorded live trades
(Diagnostics/TradeHistory.db, LiveTradeRecords) and the guard's persisted
risk events (Diagnostics/TopStepTrader.db, RiskEvents). Run it after every
practice session; it prints (and optionally writes) markdown with:

  D1  per trading-day table: trades, net after fees, green/red, guard events
      (type + CT timestamp), max adverse excursion of the cumulative day P&L,
      and whether the day ended by profit lock
  D2  per-strategy expectancy: n, net, win rate, $/trade, R/trade
  D3  aggregates: green-day rate, TopStep winning days (>= $150 net),
      median green-day P&L, app-side −$750 breaches, equity curve vs the
      trailing MLL floor
  D4  promotion-gate checklist (Docs/research/CombineSimProtocol.md) with
      PASS / FAIL / INSUFFICIENT DATA per gate, evaluated over the most
      recent 10 sessions

Trading days follow the TopStep convention: 17:00 US Central -> 17:00 US
Central next day, keyed by the CT date the day ENDS on (same as the app's
TradingDayClock and tools/audit/may2026_audit.py).

Both databases are opened read-only (mode=ro); the script never writes to them.

Usage:
    py tools/combine_report.py [--days N] [--tradehistory PATH] [--appdb PATH] [--out PATH]
"""

from __future__ import annotations

import argparse
import re
import sqlite3
import sys
import statistics
from collections import defaultdict
from datetime import datetime, timedelta, timezone, tzinfo
from pathlib import Path


# ── US Central clock (mirror of tools/audit/may2026_audit.py) ───────────────

class _UsCentral(tzinfo):
    """US Central fallback for Windows Pythons without the tzdata package.
    DST: second Sunday of March 02:00 -> first Sunday of November 02:00 (2007+ rule)."""

    @staticmethod
    def _nth_sunday(year, month, n):
        d = datetime(year, month, 1)
        d += timedelta(days=(6 - d.weekday()) % 7 + 7 * (n - 1))
        return d

    def _is_dst(self, dt):
        start = self._nth_sunday(dt.year, 3, 2) + timedelta(hours=2)
        end = self._nth_sunday(dt.year, 11, 1) + timedelta(hours=2)
        return start <= dt.replace(tzinfo=None) < end

    def utcoffset(self, dt):
        return timedelta(hours=-5) if self._is_dst(dt) else timedelta(hours=-6)

    def dst(self, dt):
        return timedelta(hours=1) if self._is_dst(dt) else timedelta(0)

    def tzname(self, dt):
        return "CDT" if self._is_dst(dt) else "CST"


try:
    from zoneinfo import ZoneInfo
    CT = ZoneInfo("America/Chicago")
except Exception:
    CT = _UsCentral()


# ── Combine parameters (mirror of CombineSettings.vb — locked, FEAT-73/74) ──

STARTING_BALANCE = 50_000.0
SOFT_HALT = -600.0
HARD_FLATTEN = -750.0
LOCK_TRIGGER = 220.0
LOCK_FLOOR = 170.0  # $20 over WINNING_DAY: flatten slippage can't drop a banked day under the line
# TopStep payout policy: a "winning day" needs >= $150 net P&L (STRAT-46).
WINNING_DAY = 150.0
MAX_TRADES = 4
MAX_CONSEC_LOSERS = 2
TRAILING_MLL = -2_000.0
MLL_SAFETY_BUFFER = 100.0

# RiskEvents.EventType values written by DailyLossGuardService.
HARD_HALT_EVENTS = ("CombineHardLoss", "CombineMaxDrawdown", "CombineProfitLock")

GATE_SESSIONS = 10

_FRAC = re.compile(r"(\.\d{6})\d+")


def parse_ts(text: str) -> datetime:
    """Parse '2026-05-08 08:53:59.4564019+00:00' (7-digit fractions)."""
    return datetime.fromisoformat(_FRAC.sub(r"\1", text))


def trading_day_key(utc_dt: datetime) -> str:
    """TopStep trading day: 17:00 CT -> 17:00 CT next day, keyed by the CT
    date the day ENDS on (matches TradingDayClock)."""
    ct = utc_dt.astimezone(CT)
    if ct.hour >= 17:
        ct = ct + timedelta(days=1)
    return ct.strftime("%Y-%m-%d")


def money(v) -> str:
    v = float(v or 0)
    return f"-${abs(v):,.2f}" if v < 0 else f"${v:,.2f}"


def md_table(headers, rows) -> str:
    out = ["| " + " | ".join(headers) + " |",
           "|" + "|".join("---" for _ in headers) + "|"]
    out += ["| " + " | ".join(str(c) for c in row) + " |" for row in rows]
    return "\n".join(out)


# ── Loading ──────────────────────────────────────────────────────────────────

def load_trades(tradehistory: Path):
    con = sqlite3.connect(f"file:{tradehistory.as_posix()}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    rows = con.execute(
        """SELECT Symbol, Direction, StrategyName, Persona, Timeframe,
                  EntryTime, ExitTime, PnL, CommissionUsd, FeesUsd, ExitReason
           FROM LiveTradeRecords
           WHERE IsOpen = 0 AND ExitTime IS NOT NULL AND PnL IS NOT NULL
           ORDER BY ExitTime"""
    ).fetchall()
    con.close()
    trades = []
    for r in rows:
        fees = float(r["CommissionUsd"] or 0) + float(r["FeesUsd"] or 0)
        pnl = float(r["PnL"])
        trades.append({
            "symbol": (r["Symbol"] or "?").lstrip("/"),
            "strategy": r["StrategyName"] or "?",
            "entry": parse_ts(r["EntryTime"]),
            "exit": parse_ts(r["ExitTime"]),
            "pnl": pnl,
            "net": pnl - fees,
            "day": trading_day_key(parse_ts(r["ExitTime"])),
        })
    return trades


def load_risk_events(appdb: Path):
    con = sqlite3.connect(f"file:{appdb.as_posix()}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    rows = con.execute(
        """SELECT OccurredAt, EventType, DailyPnLAtEvent, DrawdownAtEvent,
                  RuleValue, DetailsJson
           FROM RiskEvents ORDER BY OccurredAt"""
    ).fetchall()
    con.close()
    events = []
    for r in rows:
        at = parse_ts(r["OccurredAt"])
        events.append({
            "at": at,
            "type": r["EventType"],
            "daily_pnl": r["DailyPnLAtEvent"],
            "day": trading_day_key(at),
        })
    return events


def load_r_multiples(appdb: Path):
    """TradeOutcomes.RMultiple keyed by nothing usable per-strategy (SignalType
    is Buy/Sell), so R expectancy is reported in aggregate when populated."""
    con = sqlite3.connect(f"file:{appdb.as_posix()}?mode=ro", uri=True)
    rows = con.execute(
        "SELECT RMultiple FROM TradeOutcomes WHERE IsOpen = 0 AND RMultiple IS NOT NULL"
    ).fetchall()
    con.close()
    return [float(r[0]) for r in rows]


# ── Per-day model ────────────────────────────────────────────────────────────

def build_days(trades, events, since_day: str):
    """One record per trading day that has >= 1 closed trade (a 'session')."""
    by_day = defaultdict(list)
    for t in trades:
        if t["day"] >= since_day:
            by_day[t["day"]].append(t)
    ev_by_day = defaultdict(list)
    for e in events:
        if e["day"] >= since_day:
            ev_by_day[e["day"]].append(e)

    days = []
    for day in sorted(by_day):
        seq = sorted(by_day[day], key=lambda t: t["exit"])
        cum = peak = mae = 0.0
        for t in seq:
            cum += t["net"]
            peak = max(peak, cum)
            mae = min(mae, cum)
        evs = ev_by_day.get(day, [])
        halts = [e for e in evs if e["type"] in HARD_HALT_EVENTS]
        days.append({
            "day": day,
            "trades": seq,
            "n": len(seq),
            "net": cum,
            "peak": peak,          # intraday peak of cumulative net (trade-close granularity)
            "mae": mae,            # max adverse excursion of cumulative day P&L
            "events": evs,
            "halts": halts,
            "lock_banked": any(e["type"] == "CombineProfitLock" for e in evs),
            "lock_armed": peak >= LOCK_TRIGGER,
        })
    return days, ev_by_day


# ── Sections ─────────────────────────────────────────────────────────────────

def fmt_events(evs) -> str:
    if not evs:
        return "—"
    parts = []
    for e in evs:
        if e["type"] in ("DailyLossReset", "DayRollover"):
            continue  # housekeeping noise in a daily table
        parts.append(f"{e['type']} {e['at'].astimezone(CT):%H:%M} CT")
    return "; ".join(parts) if parts else "—"


def section_days(days):
    if not days:
        return "_No closed trades in the window._"
    rows = []
    for d in days:
        rows.append((
            d["day"], d["n"], money(d["net"]),
            "GREEN" if d["net"] > 0 else "RED",
            money(d["mae"]),
            fmt_events(d["events"]),
            "yes" if d["lock_banked"] else ("armed" if d["lock_armed"] else "—"),
        ))
    return md_table(
        ["Trading day (17:00 CT)", "Trades", "Net (after fees)", "G/R",
         "Max adverse excursion", "Guard events", "Lock banked"],
        rows)


def section_strategy(trades, r_multiples):
    if not trades:
        return "_No closed trades in the window._"
    groups = defaultdict(list)
    for t in trades:
        groups[t["strategy"]].append(t)
    rows = []
    for s in sorted(groups, key=lambda k: sum(t["net"] for t in groups[k])):
        g = groups[s]
        wins = sum(1 for t in g if t["pnl"] > 0)
        net = sum(t["net"] for t in g)
        rows.append((s, len(g), money(net), f"{wins / len(g):.0%}",
                     money(net / len(g)), "—"))
    tbl = md_table(["Strategy", "N", "Net", "Win%", "$ expectancy/trade",
                    "R expectancy/trade"], rows)
    if r_multiples:
        note = (f"Aggregate R expectancy (TradeOutcomes, {len(r_multiples)} populated): "
                f"{sum(r_multiples) / len(r_multiples):+.2f}R/trade. "
                "TradeOutcomes carries no strategy attribution (SignalType is Buy/Sell), "
                "so per-strategy R is not derivable.")
    else:
        note = ("No populated RMultiple values in TradeOutcomes — R expectancy "
                "unavailable; rely on the $ expectancy column.")
    return f"{tbl}\n\n_{note}_"


def section_aggregate(days):
    if not days:
        return "_No sessions in the window._"
    green = [d for d in days if d["net"] > 0]
    winning = [d for d in days if d["net"] >= WINNING_DAY]
    breaches = [d for d in days if d["mae"] <= HARD_FLATTEN]
    lines = [
        f"- Sessions (trading days with ≥1 closed trade): **{len(days)}**",
        f"- Green-day rate: **{len(green)}/{len(days)} ({len(green) / len(days):.0%})**",
        f"- Winning days (≥ {money(WINNING_DAY)} net — TopStep payout eligibility): "
        f"**{len(winning)}/{len(days)}**",
        f"- Median green-day P&L: **{money(statistics.median(d['net'] for d in green)) if green else '—'}**",
        f"- Days past {money(HARD_FLATTEN)} app-side (must be zero): **{len(breaches)}**"
        + (f" ({', '.join(d['day'] for d in breaches)})" if breaches else ""),
        f"- Total net over window: **{money(sum(d['net'] for d in days))}**",
    ]

    rows, equity, peak_eq = [], STARTING_BALANCE, STARTING_BALANCE
    for d in days:
        # Trade-close granularity: track the intraday equity peak for the trail.
        peak_eq = max(peak_eq, equity + d["peak"])
        equity += d["net"]
        floor = min(peak_eq + TRAILING_MLL, STARTING_BALANCE)
        halt_line = floor + MLL_SAFETY_BUFFER
        rows.append((d["day"], money(equity), money(peak_eq), money(floor),
                     money(halt_line), money(equity - halt_line)))
    eq_tbl = md_table(
        ["Trading day", "Equity (close)", "Peak equity", "TopStep MLL floor",
         "App halt line", "Distance to halt"],
        rows)
    return ("\n".join(lines)
            + f"\n\n### Equity vs trailing MLL floor (start {money(STARTING_BALANCE)}, "
              f"trail {money(TRAILING_MLL)}, freeze at start balance, "
              f"app buffer {money(MLL_SAFETY_BUFFER)})\n\n{eq_tbl}\n\n"
              "_Granularity: peak equity ratchets at trade closes only; the app's "
              "IntradayPeak trail (every tick) can be slightly stricter._")


def gate_malfunctions(window):
    """Automated proxy: after a hard-halt event, no trade may be ENTERED later
    that same trading day. Full check (flatten completeness, orphan positions)
    is the manual TradeReconciliationWorker log cross-check in the protocol."""
    violations = []
    for d in window:
        for h in d["halts"]:
            late = [t for t in d["trades"]
                    if t["entry"] > h["at"] and trading_day_key(t["entry"]) == d["day"]]
            if late:
                violations.append(
                    f"{d['day']}: {len(late)} entry(ies) after {h['type']} "
                    f"at {h['at'].astimezone(CT):%H:%M} CT")
    return violations


def section_gates(days):
    window = days[-GATE_SESSIONS:]
    n = len(window)
    insufficient = n < GATE_SESSIONS
    header = (f"Evaluated over the most recent {n} session(s)"
              + (f" — **INSUFFICIENT DATA, {GATE_SESSIONS} required**" if insufficient else "")
              + ". A gate only counts once 10 consecutive sessions exist on the "
                "final combine config (any guard code change resets the counter — "
                "see `Docs/research/CombineSimProtocol.md`).")

    green = [d for d in window if d["net"] > 0]
    breaches = [d for d in window if d["mae"] <= HARD_FLATTEN]
    malfunctions = gate_malfunctions(window)
    armed = [d for d in window if d["lock_armed"]]
    lock_fails = [d for d in armed if d["net"] < LOCK_FLOOR]

    def verdict(ok, detail):
        if insufficient:
            return f"INSUFFICIENT DATA ({n}/{GATE_SESSIONS}) — currently {detail}"
        return ("PASS" if ok else "FAIL") + f" — {detail}"

    med = statistics.median(d["net"] for d in green) if green else None
    rows = [
        ("G1", f"≥7/{GATE_SESSIONS} green days",
         verdict(len(green) >= 7, f"{len(green)}/{n} green")),
        ("G2", f"Median green day ≥ {money(WINNING_DAY)} (TopStep winning day)",
         verdict(med is not None and med >= WINNING_DAY,
                 f"median {money(med) if med is not None else '—'}")),
        ("G3", f"Zero days past {money(HARD_FLATTEN)} app-side",
         verdict(not breaches, f"{len(breaches)} breach day(s)")),
        ("G4", "Zero guard malfunctions (automated proxy)",
         verdict(not malfunctions,
                 "; ".join(malfunctions) if malfunctions else "no entries after any hard halt")),
        ("G5", f"Lock banked ≥ {money(LOCK_FLOOR)} every day it armed",
         verdict(not lock_fails,
                 f"{len(armed)} armed day(s), {len(lock_fails)} closed under floor"
                 + ("" if armed else " (vacuous — lock never armed)"))),
    ]
    tbl = md_table(["Gate", "Requirement", "Result"], rows)
    return (f"{header}\n\n{tbl}\n\n"
            "_G4 is a proxy: it detects entries recorded after a hard-halt RiskEvent. "
            "Flatten completeness and orphan positions must still be cross-checked "
            "against TradeReconciliationWorker logs before calling the gate passed._")


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    # Windows consoles default to cp1252, which cannot print ≥ / — / ·.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    ap = argparse.ArgumentParser(
        description="Daily combine report (OBS-08). Read-only.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    root = Path(__file__).resolve().parents[1]
    ap.add_argument("--tradehistory", type=Path, default=root / "Diagnostics/TradeHistory.db")
    ap.add_argument("--appdb", type=Path, default=root / "Diagnostics/TopStepTrader.db")
    ap.add_argument("--days", type=int, default=30,
                    help="look-back window in trading days (default 30)")
    ap.add_argument("--out", type=Path, default=None,
                    help="also write the markdown report to this path")
    args = ap.parse_args()

    now_utc = datetime.now(timezone.utc)
    since_day = (datetime.strptime(trading_day_key(now_utc), "%Y-%m-%d")
                 - timedelta(days=args.days)).strftime("%Y-%m-%d")

    trades = load_trades(args.tradehistory)
    events = load_risk_events(args.appdb)
    r_multiples = load_r_multiples(args.appdb)
    days, _ = build_days(trades, events, since_day)
    window_trades = [t for t in trades if t["day"] >= since_day]

    report = f"""# Daily Combine Report

**Generated:** {now_utc.astimezone(CT):%Y-%m-%d %H:%M} CT by `tools/combine_report.py`
**Window:** trading days since {since_day} ({args.days} days) · **Sessions:** {len(days)}
**Sources (read-only):** `{args.tradehistory.name}` ({len(window_trades)} closed trades in window) ·
`{args.appdb.name}` ({sum(len(d['events']) for d in days)} risk events in window)

## D1 — Trading days

{section_days(days)}

## D2 — Strategy expectancy

{section_strategy(window_trades, r_multiples)}

## D3 — Aggregates

{section_aggregate(days)}

## D4 — Promotion-gate checklist

{section_gates(days)}
"""
    print(report)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(report, encoding="utf-8")
        print(f"\nReport also written to {args.out}")


if __name__ == "__main__":
    main()
