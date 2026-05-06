"""
bb_replay.py
============
Reconstructs the Take$100 stop-loss trajectory from debug snapshots using
the BB median (EMA-10 of LastPrice) and the lower Bollinger Band
(EMA-10 minus 1.5 × rolling std-dev of the same window).

Called from postmortem.py when --replay-bb is passed.
Outputs a Markdown comparison table and a plain-text summary.
"""

from __future__ import annotations
import math
from typing import Optional


# ── EMA ──────────────────────────────────────────────────────────────────────

def _ema(prices: list[float], period: int) -> list[Optional[float]]:
    """Return EMA series same length as prices; None-padded until enough bars."""
    result: list[Optional[float]] = [None] * len(prices)
    if len(prices) < period:
        return result
    k = 2.0 / (period + 1)
    sma = sum(prices[:period]) / period
    result[period - 1] = sma
    for i in range(period, len(prices)):
        prev = result[i - 1]
        result[i] = prices[i] * k + prev * (1 - k)  # type: ignore[operator]
    return result


def _rolling_std(prices: list[float], period: int) -> list[Optional[float]]:
    """Population std-dev over a rolling window."""
    result: list[Optional[float]] = [None] * len(prices)
    for i in range(period - 1, len(prices)):
        window = prices[i - period + 1: i + 1]
        mean = sum(window) / period
        variance = sum((x - mean) ** 2 for x in window) / period
        result[i] = math.sqrt(variance)
    return result


# ── core replay ───────────────────────────────────────────────────────────────

def run_replay(
    snapshots: list[dict],
    entry_price: float,
    direction: str,          # "Long" or "Short"
    take100_threshold: float = 100.0,
    ema_period: int = 10,
    bb_mult: float = 1.5,
) -> dict:
    """
    Simulate the Take$100 SL trajectory over the recorded snapshot series.

    Returns a dict with:
      rows         – list of per-snapshot dicts for tabular output
      first_100    – timestamp when PnL first crossed $100
      median_exit  – first timestamp where LastPrice crossed the median SL
      lower_exit   – first timestamp where LastPrice crossed the lower-BB SL
      actual_final_sl – the last recorded CurrentSL in the snapshots
      summary      – plain-text narrative
    """
    is_long = direction.strip().lower() in ("long", "buy")

    # Only Heartbeat/BarClose snapshots carry LastPrice
    ticks = [
        s for s in snapshots
        if s.get("LastPrice") and s.get("UnrealizedPnLDollars") is not None
    ]
    if not ticks:
        return {"rows": [], "summary": "No price snapshots found — Debug Capture may have been off."}

    prices = [float(s["LastPrice"]) for s in ticks]
    pnls   = [float(s["UnrealizedPnLDollars"]) for s in ticks]
    actual_sls = [s.get("CurrentSL") for s in ticks]

    ema_vals = _ema(prices, ema_period)
    std_vals = _rolling_std(prices, ema_period)

    rows: list[dict] = []
    # Ratcheted SL simulations
    median_sl   = None   # initialised after $100 hit
    lower_sl    = None
    first_100   = None
    median_exit = None
    lower_exit  = None

    for i, snap in enumerate(ticks):
        ts   = snap.get("Timestamp", "")
        pnl  = pnls[i]
        px   = prices[i]
        ema  = ema_vals[i]
        std  = std_vals[i]
        asl  = actual_sls[i]

        bb_mid   = ema
        bb_lower = (ema - bb_mult * std) if (ema is not None and std is not None) else None
        # For shorts the "lower" band is above price (ema + mult*std)
        if not is_long:
            bb_lower = (ema + bb_mult * std) if (ema is not None and std is not None) else None

        # Track first $100 crossing
        if first_100 is None and pnl >= take100_threshold:
            first_100 = ts

        # Only start simulating after $100 trigger
        if pnl >= take100_threshold:
            # ── Median SL simulation ──────────────────────────────────────
            floor_median = entry_price   # Floor A: breakeven
            if bb_mid is not None:
                candidate = bb_mid
                if is_long:
                    floor_median = max(floor_median, candidate)
                else:
                    floor_median = min(floor_median, candidate)
            # Ratchet: median_sl never retreats
            if median_sl is None:
                median_sl = floor_median
            else:
                if is_long:
                    median_sl = max(median_sl, floor_median)
                else:
                    median_sl = min(median_sl, floor_median)

            # ── Lower-BB SL simulation ────────────────────────────────────
            floor_lower = entry_price   # Floor A: breakeven
            if bb_lower is not None:
                candidate = bb_lower
                if is_long:
                    floor_lower = max(floor_lower, candidate)
                else:
                    floor_lower = min(floor_lower, candidate)
            if lower_sl is None:
                lower_sl = floor_lower
            else:
                if is_long:
                    lower_sl = max(lower_sl, floor_lower)
                else:
                    lower_sl = min(lower_sl, floor_lower)

            # ── Check if price has crossed either simulated SL ────────────
            if median_exit is None:
                triggered = (is_long and px <= median_sl) or (not is_long and px >= median_sl)
                if triggered:
                    median_exit = ts

            if lower_exit is None:
                triggered = (is_long and px <= lower_sl) or (not is_long and px >= lower_sl)
                if triggered:
                    lower_exit = ts

        rows.append({
            "ts":         ts[:19].replace("T", " "),
            "price":      f"{px:.2f}",
            "pnl":        f"${pnl:+.2f}",
            "ema":        f"{bb_mid:.2f}"   if bb_mid   is not None else "—",
            "lower_bb":   f"{bb_lower:.2f}" if bb_lower is not None else "—",
            "median_sl":  f"{median_sl:.2f}" if median_sl is not None else "—",
            "lower_sl":   f"{lower_sl:.2f}"  if lower_sl  is not None else "—",
            "actual_sl":  f"{float(asl):.2f}" if asl is not None else "—",
            "event":      snap.get("EventType", ""),
        })

    actual_final = actual_sls[-1]

    # ── narrative ─────────────────────────────────────────────────────────────
    lines = []
    direction_label = "Long" if is_long else "Short"
    lines.append(f"**Direction:** {direction_label}  |  **Entry:** {entry_price:.2f}  "
                 f"|  **Take$100 threshold:** ${take100_threshold:.0f}")
    lines.append("")
    if first_100:
        lines.append(f"- 🟡 **$100 triggered at:** `{first_100[:19].replace('T', ' ')}`")
    else:
        lines.append("- ⚪ **PnL never reached $100** — Take$100 logic would not have activated.")

    if median_exit:
        lines.append(f"- 📊 **Median-SL exit at:** `{median_exit[:19].replace('T', ' ')}` "
                     f"(SL = EMA-{ema_period} of 15s bars, floor at entry)")
    else:
        lines.append(f"- 📊 **Median-SL exit:** position still open at end of snapshot window")

    if lower_exit:
        lines.append(f"- 📉 **Lower-BB exit at:** `{lower_exit[:19].replace('T', ' ')}` "
                     f"(SL = EMA-{ema_period} − {bb_mult}×StdDev, floor at entry)")
    else:
        lines.append(f"- 📉 **Lower-BB exit:** position still open at end of snapshot window")

    if actual_final is not None:
        lines.append(f"- 🔴 **Actual final SL recorded:** `{float(actual_final):.2f}` "
                     f"(from debug_trades.db CurrentSL column)")

    if median_exit and lower_exit:
        lines.append("")
        lines.append("> **Comparison:** Median SL is tighter — exits earlier, banking less profit "
                     "but protecting a larger portion of the move. Lower-BB SL is wider — "
                     "survives more noise but risks giving back more on a sharp reversal.")

    return {
        "rows":            rows,
        "first_100":       first_100,
        "median_exit":     median_exit,
        "lower_exit":      lower_exit,
        "actual_final_sl": actual_final,
        "summary":         "\n".join(lines),
    }


# ── markdown formatter ────────────────────────────────────────────────────────

def format_replay_markdown(result: dict, max_rows: int = 60) -> str:
    """Return a Markdown section for embedding in the post-mortem report."""
    lines: list[str] = []
    lines.append("## BB Replay Analysis (Take$100)")
    lines.append("")
    lines.append(result["summary"])
    lines.append("")

    rows = result.get("rows", [])
    if not rows:
        lines.append("_No snapshot data available for replay._")
        return "\n".join(lines)

    # Subsample if too many rows to keep the report readable
    step = max(1, len(rows) // max_rows)
    sample = rows[::step]

    lines.append("### Per-Tick SL Comparison (sampled)")
    lines.append("")
    lines.append("| Time | Price | P&L | EMA-10 | Lower BB | Sim: Median SL | Sim: Lower-BB SL | Actual SL | Event |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for r in sample:
        lines.append(
            f"| {r['ts']} | {r['price']} | {r['pnl']} | {r['ema']} | {r['lower_bb']} "
            f"| {r['median_sl']} | {r['lower_sl']} | {r['actual_sl']} | {r['event']} |"
        )

    lines.append("")
    lines.append(
        f"_Table sampled every {step} row(s) from {len(rows)} total snapshots. "
        f"All price/SL values are in instrument points._"
    )
    return "\n".join(lines)
