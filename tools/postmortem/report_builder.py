"""
report_builder.py
Assembles a deterministic post-mortem markdown report from a DebugTrade + snapshots.
"""

from __future__ import annotations
from datetime import datetime, timezone
from typing import Optional


# ── helpers ──────────────────────────────────────────────────────────────────

def _fmt(v, suffix: str = "", decimals: int = 2) -> str:
    if v is None:
        return "N/A"
    try:
        return f"{float(v):.{decimals}f}{suffix}"
    except (ValueError, TypeError):
        return str(v)


def _pnl(v) -> str:
    if v is None:
        return "N/A"
    try:
        f = float(v)
        sign = "+" if f >= 0 else ""
        return f"{sign}${f:.2f}"
    except (ValueError, TypeError):
        return str(v)


def _ts(v) -> str:
    """Return a clean timestamp string from whatever is stored."""
    if not v:
        return "N/A"
    return str(v).replace("T", " ").split(".")[0]


def _duration(entry: str, exit_: str) -> str:
    try:
        fmt = "%Y-%m-%d %H:%M:%S"
        e = datetime.strptime(_ts(entry), fmt)
        x = datetime.strptime(_ts(exit_), fmt)
        secs = int((x - e).total_seconds())
        m, s = divmod(abs(secs), 60)
        h, m = divmod(m, 60)
        return f"{h:02d}:{m:02d}:{s:02d}"
    except Exception:
        return "N/A"


# ── main builder ──────────────────────────────────────────────────────────────

def _parse_score(notes: str) -> Optional[int]:
    """Extract the Score= value from a snapshot Notes field, e.g. 'Score=8 [...]'."""
    if not notes:
        return None
    import re
    m = re.search(r"Score=(\d+)", notes)
    return int(m.group(1)) if m else None


def _auto_flags(trade: dict, snapshots: list[dict]) -> list[str]:
    """
    Return a list of plain-English flag strings derived purely from the trade data.
    Each flag starts with an emoji indicator:
      🚩  Likely problem / contradiction worth investigating
      ⚠️  Minor concern or data gap
      ✅  Looks healthy
    """
    flags: list[str] = []

    # ── Duration flags ────────────────────────────────────────────────────────
    entry = _ts(trade.get("EntryTime"))
    exit_ = _ts(trade.get("ClosedAt"))
    dur_str = _duration(entry, exit_)
    try:
        h, m, s = map(int, dur_str.split(":"))
        total_secs = h * 3600 + m * 60 + s
    except Exception:
        total_secs = None

    if total_secs is not None:
        if total_secs < 60:
            flags.append(
                f"🚩 **Trade lasted only {total_secs}s.** The entry signal and the exit signal "
                f"fired within the same minute. This raises the question: was the entry "
                f"condition actually valid, or did the scoring logic approve entry while "
                f"health/exit conditions were already deteriorating?"
            )
        elif total_secs < 180:
            flags.append(
                f"⚠️ Short trade duration ({dur_str}). Consider whether the entry bar's "
                f"conditions were already borderline at the time of entry."
            )

    # ── Entry vs first-bar exit conflict ──────────────────────────────────────
    bar_closes = [s for s in snapshots if s.get("EventType") == "BarClose"]
    if bar_closes:
        first_bar = bar_closes[0]
        health = ""
        notes = first_bar.get("Notes") or ""
        if "Health=Exiting" in notes:
            score = _parse_score(notes)
            score_str = f" (Score={score})" if score is not None else ""
            flags.append(
                f"🚩 **First BarClose after entry already showed `Health=Exiting`{score_str}.** "
                f"The strategy entered the trade and then immediately began its exit sequence "
                f"on the very next bar. This suggests the entry filter and the health/exit "
                f"filter are not aligned — the entry score threshold may be too permissive, "
                f"or health is decaying faster than the entry logic accounts for."
            )
        elif "Health=" in notes:
            import re
            m = re.search(r"Health=(\w+)", notes)
            health = m.group(1) if m else ""
            if health not in ("", "Healthy", "Strong"):
                flags.append(
                    f"⚠️ First BarClose health was `{health}` — not ideal at entry bar close."
                )

    # ── Slippage ──────────────────────────────────────────────────────────────
    try:
        signal = float(trade.get("EntryPrice") or 0)
        fill   = float(trade.get("ActualFillPrice") or 0)
        if signal and fill:
            direction = str(trade.get("Direction", "")).lower()
            slip = (fill - signal) if direction == "long" else (signal - fill)
            if slip > 2:
                flags.append(
                    f"⚠️ Fill slippage was {slip:.1f} pts ({fill} filled vs {signal} signal). "
                    f"For a trade this short, slippage is a meaningful % of any potential gain."
                )
    except Exception:
        pass

    # ── P&L vs risk ───────────────────────────────────────────────────────────
    try:
        pnl  = float(trade.get("RealisedPnLDollars") or 0)
        risk = float(trade.get("InitialRiskDollars") or 0)
        if risk and pnl < 0 and abs(pnl) >= abs(risk) * 0.9:
            flags.append(
                f"🚩 Loss of {_pnl(pnl)} is close to or at full initial risk ({_pnl(risk)}). "
                f"Stop was hit at nearly the worst-case level — no adaptive stop movement occurred."
            )
    except Exception:
        pass

    if not flags:
        flags.append("✅ No automatic concerns detected.")

    return flags


def build_report(
    *,
    order_id: str,
    trade_id: str,
    symbol: str,
    closed_by: str,           # "app" | "manual" | "unknown"
    trade: dict,
    snapshots: list[dict],
    trader_issue: str = "",   # free-text concern from the trader
) -> str:
    """Return a full markdown post-mortem as a string."""

    lines: list[str] = []
    A = lines.append

    # ── Title ─────────────────────────────────────────────────────────────────
    A(f"# Trade Post-Mortem — {symbol.upper().lstrip('/')}")
    A("")
    A(f"_Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}_")
    A("")

    # ── 1. Identity ───────────────────────────────────────────────────────────
    A("## 1. Trade Identity")
    A("")
    A(f"| Field | Value |")
    A(f"|---|---|")
    A(f"| TopStepX Order ID | `{order_id}` |")
    A(f"| TopStepX Trade ID | `{trade_id}` |")
    A(f"| Internal Debug ID | `{trade.get('TradeId', 'N/A')}` |")
    A(f"| Symbol | `{symbol.upper()}` |")
    A(f"| Direction | {trade.get('Direction', 'N/A')} |")
    A(f"| Slot Index | {trade.get('SlotIndex', 'N/A')} |")
    A(f"| Persona / Config | {trade.get('Persona', 'N/A')} |")
    A("")

    # ── 2. Execution Summary ──────────────────────────────────────────────────
    entry_time = _ts(trade.get('EntryTime'))
    closed_at  = _ts(trade.get('ClosedAt'))

    A("## 2. Execution Summary")
    A("")
    A(f"| Field | Value |")
    A(f"|---|---|")
    A(f"| Entry Time (Order Time) | `{entry_time}` |")
    A(f"| Entry Price (signal) | {_fmt(trade.get('EntryPrice'))} |")
    A(f"| Actual Fill Price | {_fmt(trade.get('ActualFillPrice'))} |")
    A(f"| Fill Slippage | {_fmt(trade.get('SlippageTicks'), ' ticks')} |")
    A(f"| Quantity | {trade.get('Quantity', 'N/A')} |")
    A(f"| Exit Time | `{closed_at}` |")
    A(f"| Duration | {_duration(entry_time, closed_at)} |")
    A(f"| Exit Price | {_fmt(trade.get('ExitPrice'))} |")
    A(f"| Realised P&L | **{_pnl(trade.get('RealisedPnLDollars'))}** |")
    A(f"| Commissions | {_pnl(trade.get('Commissions'))} |")
    A(f"| Net P&L | {_pnl(trade.get('NetPnLDollars'))} |")
    A("")

    # ── 3. Risk Profile ───────────────────────────────────────────────────────
    A("## 3. Risk Profile")
    A("")
    A(f"| Field | Value |")
    A(f"|---|---|")
    A(f"| Initial Stop Price | {_fmt(trade.get('InitialStopPrice'))} |")
    A(f"| Initial Target Price | {_fmt(trade.get('InitialTargetPrice'))} |")
    A(f"| Initial Risk $ | {_pnl(trade.get('InitialRiskDollars'))} |")
    A(f"| Planned R:R | {_fmt(trade.get('PlannedRR'))}:1 |")
    A(f"| MFE (best unrealised) | {_fmt(trade.get('MFEDollars'), ' $')} |")
    A(f"| MAE (worst drawdown) | {_fmt(trade.get('MAEDollars'), ' $')} |")
    A(f"| ATR at entry | {_fmt(trade.get('AtrAtEntry'))} |")
    A(f"| ADX at entry | {_fmt(trade.get('AdxAtEntry'))} |")
    A("")

    # ── 4. Closure Source ─────────────────────────────────────────────────────
    A("## 4. Closure")
    A("")
    closed_label = {
        "app":     "✅ Closed by **application** (TP/SL triggered programmatically)",
        "manual":  "🖱️ Closed **manually** on the TopStepX platform",
        "unknown": "❓ Closure source **unknown** — verify against TopStepX Orders tab",
    }.get(closed_by.lower(), f"❓ {closed_by}")
    A(closed_label)
    A("")
    A(f"| Field | Value |")
    A(f"|---|---|")
    A(f"| Exit Reason (debug) | {trade.get('ExitReason', 'N/A')} |")
    A(f"| Final Stop Phase | {trade.get('FinalStopPhase', 'N/A')} |")
    A("")

    # ── 5. Timeline ───────────────────────────────────────────────────────────
    A("## 5. Event Timeline")
    A("")

    if not snapshots:
        A("_No debug snapshots recorded for this trade._")
        A("")
    else:
        A("| # | Time | Event | Price | Unreal P&L | SL | Notes |")
        A("|---|---|---|---|---|---|---|")
        for i, s in enumerate(snapshots, 1):
            A(
                f"| {i} "
                f"| {_ts(s.get('Timestamp'))} "
                f"| {s.get('EventType', '')} "
                f"| {_fmt(s.get('CurrentPrice'))} "
                f"| {_pnl(s.get('UnrealisedPnLDollars'))} "
                f"| {_fmt(s.get('CurrentStopPrice'))} "
                f"| {s.get('Notes') or ''} |"
            )
        A("")

    # ── 6. Bar-by-bar OHLC (BarClose events only) ─────────────────────────────
    bar_snaps = [s for s in snapshots if s.get("EventType") == "BarClose"]
    if bar_snaps:
        A("## 6. Bar-by-Bar (BarClose events)")
        A("")
        A("| Bar | Open | High | Low | Close | ATR | ADX | SuperTrend | StopPhase |")
        A("|---|---|---|---|---|---|---|---|---|")
        for b in bar_snaps:
            A(
                f"| {_ts(b.get('Timestamp'))} "
                f"| {_fmt(b.get('BarOpen'))} "
                f"| {_fmt(b.get('BarHigh'))} "
                f"| {_fmt(b.get('BarLow'))} "
                f"| {_fmt(b.get('BarClose'))} "
                f"| {_fmt(b.get('AtrValue'))} "
                f"| {_fmt(b.get('AdxValue'))} "
                f"| {_fmt(b.get('SuperTrendValue'))} "
                f"| {b.get('StopPhase', '')} |"
            )
        A("")

    # ── 7. AI / Signal checks ─────────────────────────────────────────────────
    ai_snaps = [s for s in snapshots if s.get("EventType") == "AiCheck"]
    if ai_snaps:
        A("## 7. AI / Signal Checks During Trade")
        A("")
        A("| Time | Notes |")
        A("|---|---|")
        for ai in ai_snaps:
            A(f"| {_ts(ai.get('Timestamp'))} | {ai.get('Notes') or ''} |")
        A("")

    # ── 8. Trader Issue ───────────────────────────────────────────────────────
    A("## 8. Trader Issue")
    A("")
    if trader_issue.strip():
        A(f"> {trader_issue.strip()}")
    else:
        A("_No specific issue raised for this trade._")
    A("")

    # ── 9. Auto-Analysis ──────────────────────────────────────────────────────
    A("## 9. Auto-Analysis")
    A("")
    A("_Flags derived automatically from the debug data:_")
    A("")
    for flag in _auto_flags(trade, snapshots):
        A(f"- {flag}")
    A("")

    # ── 10. Verdict ────────────────────────────────────────────────────────────
    A("## 10. Verdict")
    A("")
    try:
        pnl = float(trade.get("RealisedPnLDollars") or 0)
        outcome = "🟢 WINNER" if pnl > 0 else ("🔴 LOSER" if pnl < 0 else "⚪ BREAKEVEN")
    except (ValueError, TypeError):
        outcome = "❓ UNKNOWN"
    A(f"**Outcome: {outcome}**")
    A("")
    A("_Fill in your observations below:_")
    A("")
    A("- **What went well:**")
    A("- **What went wrong:**")
    A("- **What would you do differently:**")
    A("")
    A("---")
    A(f"_Post-mortem generated by `tools/postmortem/postmortem.py`_")

    return "\n".join(lines)
