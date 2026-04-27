# Strategy Review — 2026-04-26

## Context

Evaluated against TopStepX session constraints:
- **Hard close:** 20:10 UTC (Risk Manager starts ~20:08 UTC)
- **Reopen:** 22:00 UTC (Sunday–Thursday)
- **Dead window:** 20:10–22:00 UTC (~110 min)
- **Session:** CME Futures 22:00 UTC → 20:10 UTC (22 hrs)

---

## Strategy Rankings — Day-Trading / TopStepX Session Fit

### Tier 1 — Session-native (built-in time filters, zero EOD conflict)

| Rank | Strategy | Why |
|---|---|---|
| **1** | **Opening Range Breakout** | Hardcoded to NY open window; no-trade filter past session midpoint. Stops firing ~17:00 UTC, 3+ hrs before cutoff. Volume-confirmed breakout = fewest bad fills. Best on MNQ/MES/MGC. |
| **2** | **VWAP Mean Reversion** | Hard 15:00–19:00 UTC (10am–2pm ET) gate. Stops 70+ min before the 20:08 risk manager. Session-anchored VWAP + 1.5/2.0 SD = institutionally-grounded R:R. 60–65% win rate. |
| **3** | **LULT Divergence** | Time-filtered 11:00–17:00 UTC (London + NY pre-market). 6-step confirmation gate is the most discriminating entry logic in the codebase. Zero EOD conflict by design. NQ-optimised. |

### Tier 2 — High conviction, low EOD exposure

| Rank | Strategy | Why |
|---|---|---|
| **4** | **Multi-Confluence Engine** | Proven backtest winner (OIL/Damian/5-min). All-7-signals gate means very low signal frequency = very few late-session entries. 5-min bars → last viable entry ~19:45 UTC (one bar before the stale-bar guard fires). Strong candidate for live use. |
| **5** | **Naked Trader** | 4-vote consensus (EMA9/21, MACD, DMI/ADX, VWAP). VWAP component naturally degrades as the session ages and anchor drifts — provides organic late-session suppression. 5-min bars; ADX≥20 gate filters chop. |

### Tier 3 — Viable with session awareness

| Rank | Strategy | Why |
|---|---|---|
| **6** | **BB Squeeze Scalper** | 1-min bars, sub-5-min hold times mean forced-close risk is low even if fired at 19:55 UTC. Tight 0.2% SL caps damage. Weakness: 1-min bar noise in the thin 18:00–20:00 UTC window. |
| **7** | **Double Bubble Butt** | Trend continuation (1.0 SD → 2.0 SD zone). Solid concept but 2×ATR(20) TP may take hours on anything above 5-min, colliding with EOD. Works best on 1–5-min timeframes here. |
| **8** | **VIDYA Cross** | Adaptive EMA suppresses chop better than static EMA crosses. No time filter means it can fire at 20:05 UTC. Mitigated if paired with Hydra's stale-bar guard (3× multiplier). |

### Tier 4 — Session-risk concerns

| Rank | Strategy | Why |
|---|---|---|
| **9** | **EMA/RSI Combined** | Six-signal weighted score is weaker than Multi-Confluence (60% vs all-7). More signals = more late-session firing. No time filter. The same logic runs in Hydra's EmaRsiWeightedScore path — usable but noise-prone near EOD. |
| **10** | **Pump-n-Dump** | Pure 3-bar momentum. Very high signal frequency on 1-min bars. Likely to fire inside the 19:45–20:08 danger window several times per week and get cut mid-trade. Acceptable only on 5-min+ timeframes. |

---

## Backtest / Research Only

**Connors RSI-2, SuperTrend, Donchian Breakout, BB+RSI Mean Reversion** — backtest research only; not wired to `IOrderService`. Donchian in particular is a multi-day swing system (30–40% win rate, large R:R) and is wholly incompatible with a mandatory EOD close.

**Sniper (Triple EMA Cascade)** — separate live view, pyramid-capable. The scaled-position risk at EOD is the primary concern: a 5-contract pyramid entered at 19:50 UTC getting cut at 20:08 can be costly. Best used strictly before 19:00 UTC.

---

## Summary

ORB → VWAP → LULT are the cleanest session fits. Multi-Confluence is the proven backtest winner and is safe at 5-min given the all-7 gate. Everything below rank 6 needs an explicit latest-entry cutoff (recommend 19:30 UTC) added to the engine or configured via Hydra's session controls.
