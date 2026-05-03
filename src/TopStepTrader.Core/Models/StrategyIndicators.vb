Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Holds all pre-calculated indicator series for a backtest run.
    ''' Populated once before the bar loop and passed to each
    ''' <see cref="TopStepTrader.Core.Interfaces.IStrategySignalProvider.Evaluate"/> call
    ''' so providers access indicators by index without recomputing them per bar.
    ''' Arrays are Single() to match TechnicalIndicators output; NaN = warm-up period.
    ''' </summary>
    Public Class StrategyIndicators

        ' ── Full bar list (for multi-bar pattern checks, e.g. 3-candle colour) ───
        ''' <summary>Read-only reference to the full ordered bar list for this backtest run.</summary>
        Public Property AllBars As IReadOnlyList(Of MarketBar)

        ' ── EMA series ────────────────────────────────────────────────────────────
        Public Property Ema8 As Single()
        Public Property Ema21 As Single()
        Public Property Ema50 As Single()

        ' ── Momentum ──────────────────────────────────────────────────────────────
        Public Property Rsi As Single()
        Public Property MacdLine As Single()
        Public Property MacdSignal As Single()
        Public Property MacdHistogram As Single()

        ' ── Trend / volatility ────────────────────────────────────────────────────
        Public Property Atr As Single()
        Public Property PlusDi As Single()
        Public Property MinusDi As Single()
        Public Property Adx As Single()

        ' ── Ichimoku ──────────────────────────────────────────────────────────────
        Public Property IchiTenkan As Single()
        Public Property IchiKijun As Single()
        Public Property IchiSpanA As Single()
        Public Property IchiSpanB As Single()

        ' ── Oscillators ───────────────────────────────────────────────────────────
        Public Property StochRsiK As Single()
        Public Property StochRsiD As Single()
        Public Property WaveTrend1 As Single()
        Public Property WaveTrend2 As Single()

        ' ── Bollinger Bands ───────────────────────────────────────────────────────
        Public Property BbUpper As Single()
        Public Property BbMiddle As Single()
        Public Property BbLower As Single()
        Public Property BbWidth As Single()
        Public Property BbPctB As Single()

        ' ── Other ─────────────────────────────────────────────────────────────────
        Public Property Vidya As Single()
        Public Property DeltaVolume As Single()
        Public Property Vwap As Single()

        ' ── Strategy-specific additions (ARCH-01c) ────────────────────────────
        ''' <summary>EMA(5) — used by BbSqueezeScalper for slope confirmation.</summary>
        Public Property Ema5 As Single()
        ''' <summary>EMA(9) — used by NakedTrader for the EMA vote.</summary>
        Public Property Ema9 As Single()
        ''' <summary>
        ''' SMA of BollingerBandWidth — used by BbSqueezeScalper to detect squeeze
        ''' (BBW &lt; BbwSma for ≥ 3 consecutive bars = squeeze active).
        ''' </summary>
        Public Property BbwSma As Single()
        ''' <summary>SMA(20) of bar volume — used by NakedTrader for high-confidence volume gate.</summary>
        Public Property VolMa20 As Single()
        ''' <summary>DoubleBubbleButt inner upper band (BB(20, 1.0 SD)).</summary>
        Public Property DbbInnerUpper As Single()
        ''' <summary>DoubleBubbleButt inner lower band (BB(20, 1.0 SD)).</summary>
        Public Property DbbInnerLower As Single()

        ''' <summary>
        ''' SuperTrend(10, 3.0) direction series: +1 up-trend, −1 down-trend, 0/NaN during warm-up.
        ''' Pre-computed over all bars in BuildIndicators for the SuperTrendAdx strategy so the
        ''' BacktestEngine can detect direction flips bar-by-bar without recomputing the indicator.
        ''' </summary>
        Public Property StDirectionSeries As Single()

        ''' <summary>
        ''' SuperTrend(10, 3.0) price line series.
        ''' Acts as support below price in an up-trend and resistance above price in a down-trend.
        ''' Used by SuperTrendPlus to trail the stop to the ST line each bar.
        ''' </summary>
        Public Property StLineSeries As Single()

    End Class

End Namespace
