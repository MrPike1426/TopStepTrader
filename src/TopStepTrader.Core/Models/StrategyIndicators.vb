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
        Public Property SuperTrendLine As Single()
        Public Property SuperTrendDir As Single()
        Public Property DonchianUpper As Single()
        Public Property DonchianLower As Single()
        Public Property DonchianMid As Single()
        Public Property Vidya As Single()
        Public Property DeltaVolume As Single()
        Public Property Vwap As Single()
        Public Property ConnorsRsi As Single()

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

        ' ── QuantLab-specific (ARCH-01d) ──────────────────────────────────────
        ''' <summary>RSI(2) — ConnorsRsi2 entry gate (dip &lt; 10 / rally &gt; 90).</summary>
        Public Property Rsi2 As Single()
        ''' <summary>SMA(5) — ConnorsRsi2 mean-reversion exit trigger.</summary>
        Public Property Sma5 As Single()
        ''' <summary>SMA(200) — ConnorsRsi2 long-term trend gate.</summary>
        Public Property Sma200 As Single()
        ''' <summary>ATR(10) — SuperTrend TP sizing (2× ATR from entry).</summary>
        Public Property SuperTrendAtr As Single()

    End Class

End Namespace
