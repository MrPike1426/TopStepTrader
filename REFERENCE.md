# REFERENCE.md

Static lookup tables extracted from CLAUDE.md to reduce per-session token burn.
Read this file only when working on the subsystems below.

---

## Strategy Condition / Indicator Enums

`Core/Enums/StrategyConditionType.vb` and `Core/Enums/StrategyIndicatorType.vb`

Integer values are DB discriminators — **must not change** once data is stored.

**Trading strategies (Hydra / AssetBassett / Backtest):**

| Value | ConditionType | Description |
|---|---|---|
| 6 | `EmaRsiWeightedScore` | Six-signal EMA/RSI weighted score (buy >60%, sell <40%) |
| 7 | `TripleEmaCascade` | 3-EMA Cascade on 1-min bars — Sniper strategy |
| 8 | `MultiConfluence` | Ichimoku + EMA21/50 + MACD + StochRSI + DMI/ADX (all 7 must align) |
| 9 | `LultDivergence` | WaveTrend Anchor/Trigger divergence — 6-step gate, NQ 5-min, 11:00–17:00 UTC |
| 10 | `BbSqueezeScalper` | Dual-mode BB scalper — Squeeze Breakout or Band Bounce |
| 15 | `VidyaCross` | VIDYA CMO-adaptive EMA crossover + delta-volume filter |
| 16 | `NakedTrader` | 4-vote consensus: EMA(9/21), MACD(8,17,9), DMI/ADX(14), VWAP |
| 17 | `DoubleBubbleButt` | Double Bollinger Bands (±1.0 SD inner / ±2.0 SD outer) zone system |
| 18 | `OpeningRangeBreakout` | First-30-min OR high/low breakout with volume ≥ 1.2× avg; SL = opposite OR extreme; TP = 1.5× OR width |
| 19 | `PumpNDump` | 3 consecutive same-direction 1-min bars (3 green → Long, 3 red → Short); ATR-based SL/TP |

**QuantLab research-only strategies (not live-traded):**

| Value | ConditionType | Description |
|---|---|---|
| 11 | `ConnorsRsi2` | RSI(2) mean reversion filtered by SMA(200); 67–72% win rate |
| 12 | `SuperTrend` | ATR(10)×3.0 trend flip; 40–52% win rate |
| 13 | `DonchianBreakout` | 20-bar Turtle breakout; 30–40% win rate |
| 14 | `BbRsiMeanReversion` | BB(20,2) + RSI(14) dual-confirmation reversion; 55–65% win rate |
| 17 | `DoubleBubbleButt` | Also available in QuantLab |

---

## QuantLab Strategy Cards

Five cards in `UI/ViewModels/QuantLabViewModel.vb` / `UI/Views/QuantLabView.xaml`:

1. **Connors RSI-2** — Mean reversion · RSI(2) dips vs SMA(200) trend · 67–72% win rate · Sharpe 1.0–1.5
2. **SuperTrend** — Trend-following · ATR(10)×3.0 direction flip · 40–52% win rate · Sharpe 0.70–1.05
3. **Donchian Breakout** — Turtle breakout · 20-bar high/low channel · 30–40% win rate · Sharpe 0.4–0.8
4. **BB + RSI Reversion** — Dual-confirm reversion · BB(20,2) AND RSI(14) · 55–65% win rate · Sharpe 0.6–1.2
5. **Double Bubble Butt** — Zone momentum · ±1.0 SD inner / ±2.0 SD outer BB · 45–60% win rate · Sharpe 0.6–1.1

---

## Technical Indicators (`ML/Features/TechnicalIndicators.vb`)

Pure-math module — no external dependencies, fully unit-testable. All methods take `IList(Of Decimal)` price series and return `Single()` arrays (NaN-padded during warm-up).

| Function | Description |
|---|---|
| `EMA(prices, period)` | Exponential Moving Average (k = 2/(period+1)) |
| `SMA(prices, period)` | Simple Moving Average |
| `RSI(closes, period)` | Wilder-smoothed RSI |
| `MACD(closes, fast, slow, signal)` | Returns (Line, Signal, Histogram) |
| `ATR(highs, lows, closes, period)` | Wilder-smoothed ATR |
| `VWAP(highs, lows, closes, volumes)` | Cumulative VWAP |
| `BollingerBands(closes, period, sd)` | Returns (Upper, Middle, Lower) |
| `BollingerBandWidth(closes, period, sd)` | (Upper−Lower)/Middle×100 |
| `BollingerPercentB(closes, period, sd)` | 0=lower band, 1=upper band |
| `DMI(highs, lows, closes, period)` | Returns (+DI, −DI, ADX) — Wilder smoothing |
| `IchimokuCloud(highs, lows, closes, ...)` | Returns (Tenkan, Kijun, SpanA, SpanB) — projected 26 bars forward |
| `StochasticRSI(closes, rsi, stoch, signal)` | Returns (%K, %D) normalised 0–1 |
| `WaveTrend(highs, lows, closes, ...)` | Market Cipher B simulation — (WT1, WT2) |
| `SuperTrend(highs, lows, closes, period, mult)` | ATR-based trend line; returns (Line, Direction) |
| `DonchianChannel(highs, lows, period)` | Rolling high/low channel; returns (Upper, Lower, Middle) |
| `CMO(closes, period)` | Chande Momentum Oscillator normalised to [−1, +1] |
| `VIDYA(closes, vidyaLen, cmoLen)` | CMO-adaptive EMA — fast in trends, slow in chop |
| `DeltaVolume(closes, opens, volumes, window)` | Net buy/sell pressure normalised to [−1, +1] |
| `Rsi2(closes)` | Convenience wrapper for RSI(closes, 2) |
| `LastValid(series)` | Last non-NaN value in array |
| `PreviousValid(series)` | Second-to-last non-NaN value |
