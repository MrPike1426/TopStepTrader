"""STRAT-44 — May-2026 trade audit + combine-rule replay.

Read-only analysis of the recorded live trades in Diagnostics/TradeHistory.db
(LiveTradeRecords) and Diagnostics/TopStepTrader.db (TradeOutcomes), producing
a markdown report with:

  A1  per strategy/persona/symbol attribution
  A2  entry-hour (US Central) expectancy buckets
  A3  exit-reason distribution
  A4  R-multiple distribution
  A5  combine-rule replay (soft -600 / hard -750 / trailing lock 150->100 /
      max 4 trades / 2 consecutive losers) over each TopStep trading day
      (17:00 US Central boundary)

Both databases are opened read-only (mode=ro); the script never writes to them.

Usage:
    py tools/audit/may2026_audit.py [--tradehistory PATH] [--appdb PATH] [--out PATH]
"""

from __future__ import annotations

import argparse
import re
import sqlite3
from collections import defaultdict
from datetime import datetime, timedelta, timezone, tzinfo
from pathlib import Path


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

# Owner-confirmed proposed combine parameters (FEAT-73)
SOFT_HALT = -600.0
HARD_FLATTEN = -750.0
LOCK_TRIGGER = 150.0
LOCK_FLOOR = 100.0
MAX_TRADES = 4
MAX_CONSEC_LOSERS = 2

_FRAC = re.compile(r"(\.\d{6})\d+")


def parse_ts(text: str) -> datetime:
    """Parse '2026-05-08 08:53:59.4564019+00:00' (7-digit fractions)."""
    return datetime.fromisoformat(_FRAC.sub(r"\1", text))


def trading_day_key(utc_dt: datetime) -> str:
    """TopStep trading day: 17:00 CT -> 17:00 CT next day, keyed by the CT
    date the day ENDS on (matches the planned TradingDayClock convention)."""
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
            "persona": r["Persona"] or "?",
            "entry": parse_ts(r["EntryTime"]),
            "exit": parse_ts(r["ExitTime"]),
            "pnl": pnl,
            "fees": fees,
            "net": pnl - fees,
            "exit_reason": r["ExitReason"] or "?",
        })
    return trades


def load_outcomes(appdb: Path):
    con = sqlite3.connect(f"file:{appdb.as_posix()}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    rows = con.execute(
        """SELECT SignalType, Timeframe, RMultiple, ExitReason, PnL, IsWinner
           FROM TradeOutcomes WHERE IsOpen = 0"""
    ).fetchall()
    con.close()
    return rows


def section_a1(trades):
    groups = defaultdict(list)
    for t in trades:
        groups[(t["strategy"], t["persona"], t["symbol"])].append(t)
    rows = []
    for key in sorted(groups, key=lambda k: sum(x["net"] for x in groups[k])):
        g = groups[key]
        wins = [t for t in g if t["pnl"] > 0]
        losses = [t for t in g if t["pnl"] <= 0]
        rows.append((
            *key, len(g),
            money(sum(t["pnl"] for t in g)),
            money(sum(t["fees"] for t in g)),
            money(sum(t["net"] for t in g)),
            f"{len(wins) / len(g):.0%}",
            money(sum(t["pnl"] for t in wins) / len(wins)) if wins else "—",
            money(sum(t["pnl"] for t in losses) / len(losses)) if losses else "—",
        ))
    total_pnl = sum(t["pnl"] for t in trades)
    total_fees = sum(t["fees"] for t in trades)
    tbl = md_table(
        ["Strategy", "Persona", "Symbol", "N", "Gross", "Fees", "Net", "Win%", "AvgWin", "AvgLoss"],
        rows)
    return (f"{tbl}\n\n**Totals:** {len(trades)} closed trades · gross {money(total_pnl)} · "
            f"fees {money(total_fees)} · **net {money(total_pnl - total_fees)}**")


def section_a2(trades):
    buckets = defaultdict(list)
    for t in trades:
        buckets[t["entry"].astimezone(CT).hour].append(t)
    rows = []
    for hour in sorted(buckets):
        g = buckets[hour]
        net = sum(t["net"] for t in g)
        wins = sum(1 for t in g if t["pnl"] > 0)
        rows.append((f"{hour:02d}:00 CT", len(g), f"{wins / len(g):.0%}",
                     money(net), money(net / len(g))))
    return md_table(["Entry hour", "N", "Win%", "Net", "Net/trade"], rows)


def section_a3(trades):
    groups = defaultdict(list)
    for t in trades:
        groups[(t["strategy"], t["exit_reason"])].append(t)
    rows = [(s, r, len(g), money(sum(t["pnl"] for t in g)))
            for (s, r), g in sorted(groups.items(),
                                    key=lambda kv: sum(t["pnl"] for t in kv[1]))]
    return md_table(["Strategy", "ExitReason", "N", "Gross PnL"], rows)


def section_a4(outcomes):
    with_r = [o for o in outcomes if o["RMultiple"] is not None]
    if not with_r:
        return ("_No populated RMultiple values found in TradeOutcomes — "
                "distribution unavailable; rely on the $ analysis above._")
    groups = defaultdict(list)
    for o in with_r:
        groups[(o["SignalType"] or "?", o["Timeframe"])].append(float(o["RMultiple"]))
    rows = []
    for key, rs in sorted(groups.items()):
        rows.append((*key, len(rs), f"{sum(rs) / len(rs):+.2f}",
                     sum(1 for r in rs if r >= 1)))
    tbl = md_table(["SignalType", "TF", "N", "Avg R", "N ≥ +1R"], rows)
    hist = defaultdict(int)
    for o in with_r:
        r = float(o["RMultiple"])
        b = "≤ -1R" if r <= -1 else "-1..0R" if r <= 0 else "0..1R" if r < 1 else "≥ +1R"
        hist[b] += 1
    hist_tbl = md_table(["Bucket", "N"],
                        [(b, hist[b]) for b in ("≤ -1R", "-1..0R", "0..1R", "≥ +1R")])
    return (f"{len(with_r)} of {len(outcomes)} resolved outcomes have RMultiple.\n\n"
            f"{tbl}\n\n{hist_tbl}")


def replay_day(day_trades):
    """Replay one trading day's closed-trade sequence through the proposed
    combine rules. Realised granularity: each trade close is an evaluation
    point; the book is treated as flat after each close."""
    cum = 0.0
    high_water = 0.0
    consec_losers = 0
    stopped_by = None
    taken = 0
    for t in day_trades:
        cum += t["net"]
        taken += 1
        high_water = max(high_water, cum)
        consec_losers = consec_losers + 1 if t["pnl"] <= 0 else 0
        if cum <= HARD_FLATTEN:
            stopped_by = "hard -750"
        elif cum <= SOFT_HALT:
            stopped_by = "soft -600"
        elif cum >= LOCK_TRIGGER:
            stopped_by = "lock banked"      # flat book ≥ trigger -> bank the day
        elif high_water >= LOCK_TRIGGER and cum <= LOCK_FLOOR:
            stopped_by = "lock floor"
        elif taken >= MAX_TRADES:
            stopped_by = f"max {MAX_TRADES} trades"
        elif consec_losers >= MAX_CONSEC_LOSERS:
            stopped_by = f"{MAX_CONSEC_LOSERS} consec losers"
        if stopped_by:
            break
    return {"guard_pnl": cum, "stopped_by": stopped_by or "session end", "taken": taken}


def section_a5(trades):
    days = defaultdict(list)
    for t in trades:
        days[trading_day_key(t["exit"])].append(t)
    rows, total_actual, total_guard = [], 0.0, 0.0
    rule_counts = defaultdict(int)
    for day in sorted(days):
        seq = sorted(days[day], key=lambda t: t["exit"])
        actual = sum(t["net"] for t in seq)
        cum = mdd = peak = 0.0
        for t in seq:
            cum += t["net"]
            peak = max(peak, cum)
            mdd = min(mdd, cum - peak)
        r = replay_day(seq)
        rule_counts[r["stopped_by"]] += 1
        total_actual += actual
        total_guard += r["guard_pnl"]
        rows.append((day, len(seq), money(actual), money(mdd),
                     f"{r['taken']}", money(r["guard_pnl"]), r["stopped_by"],
                     money(r["guard_pnl"] - actual)))
    tbl = md_table(
        ["Trading day (17:00 CT)", "Trades", "Actual net", "Intraday DD",
         "Guard trades", "Guard net", "Day ended by", "Guard vs actual"],
        rows)
    ends = md_table(["Day ended by", "Days"],
                    [(k, v) for k, v in sorted(rule_counts.items(), key=lambda kv: -kv[1])])
    return (f"{tbl}\n\n{ends}\n\n"
            f"**Actual total: {money(total_actual)} · Under guard: {money(total_guard)} · "
            f"Improvement: {money(total_guard - total_actual)}**\n\n"
            f"_Limitation: the replay sees realised trade-close granularity only — "
            f"intra-trade unrealised excursions are invisible, so guard trigger "
            f"timing is approximate (conservative: the real guard, ticking every "
            f"5s on combined realised+unrealised P&L, would generally act sooner)._")


def section_findings(trades, days_summary_text):
    by_strategy = defaultdict(list)
    for t in trades:
        by_strategy[t["strategy"]].append(t)
    lines = ["## Findings", ""]
    for s, g in sorted(by_strategy.items(), key=lambda kv: sum(t["net"] for t in kv[1])):
        net = sum(t["net"] for t in g)
        wins = sum(1 for t in g if t["pnl"] > 0)
        exp = net / len(g)
        verdict = "no live edge demonstrated — do not run in combine mode" if exp < 0 \
            else "positive on this window — candidate for combine mode"
        lines.append(f"- **{s}** — {len(g)} trades, net {money(net)}, "
                     f"win rate {wins / len(g):.0%}, expectancy {money(exp)}/trade: {verdict}.")
    lines += [
        "",
        "- The recorded window covers a single strategy family (trend-following) in a "
        "range-bound regime; see `Docs/research/2026-Q2-StrategyCandidates.md` (STRAT-43) "
        "for the regime analysis and the VWAP Mean-Reversion recommendation (FEAT-75).",
        "- The A5 replay above is the evidence base for the FEAT-73 defaults "
        f"(soft {money(SOFT_HALT)} / hard {money(HARD_FLATTEN)} / lock "
        f"{money(LOCK_TRIGGER)}→{money(LOCK_FLOOR)} / max {MAX_TRADES} trades / "
        f"{MAX_CONSEC_LOSERS} consecutive losers).",
        "- No conclusion is possible for SlipStream, Break and Bounce or Ultimate Scalper: "
        "zero recorded live trades. Their combine-mode inclusion (STRAT-45) rests on "
        "structure, not evidence — the 10-session sim protocol (OBS-08) is the test.",
    ]
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parents[2]
    ap.add_argument("--tradehistory", type=Path, default=root / "Diagnostics/TradeHistory.db")
    ap.add_argument("--appdb", type=Path, default=root / "Diagnostics/TopStepTrader.db")
    ap.add_argument("--out", type=Path, default=root / "Docs/research/2026-Q2-MayAudit.md")
    args = ap.parse_args()

    trades = load_trades(args.tradehistory)
    outcomes = load_outcomes(args.appdb)
    span = f"{trades[0]['exit']:%Y-%m-%d} → {trades[-1]['exit']:%Y-%m-%d}" if trades else "n/a"

    a5 = section_a5(trades)
    report = f"""# 2026-Q2 — May Live-Trading Audit (STRAT-44)

**Generated:** {datetime.now():%Y-%m-%d} by `tools/audit/may2026_audit.py`
**Sources (read-only):** `{args.tradehistory.name}` ({len(trades)} closed trades, {span}) ·
`{args.appdb.name}` ({len(outcomes)} resolved TradeOutcomes)
**Purpose:** attribute the May loss precisely and replay the proposed FEAT-73 combine-guard
parameters over the actual trade sequences.

## A1 — Attribution: strategy / persona / symbol

{section_a1(trades)}

## A2 — Entry hour (US Central)

{section_a2(trades)}

## A3 — Exit-reason distribution

{section_a3(trades)}

## A4 — R-multiple distribution (TradeOutcomes)

{section_a4(outcomes)}

## A5 — Combine-rule replay

Rules replayed: soft halt {money(SOFT_HALT)} (no further entries) · hard flatten {money(HARD_FLATTEN)} ·
trailing profit lock arms at {money(LOCK_TRIGGER)}, floors at {money(LOCK_FLOOR)}, flat book ≥ trigger banks
immediately · max {MAX_TRADES} trades/day · halt after {MAX_CONSEC_LOSERS} consecutive losers.

{a5}

{section_findings(trades, a5)}
"""
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(report, encoding="utf-8")
    print(f"Report written to {args.out}")
    print(f"  {len(trades)} closed trades analysed, {len(outcomes)} outcomes")


if __name__ == "__main__":
    main()
