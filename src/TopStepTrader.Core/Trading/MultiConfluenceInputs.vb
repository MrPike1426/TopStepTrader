Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Pre-computed scalar indicator values for a single bar, passed to
    ''' <see cref="TopStepTrader.Core.Interfaces.IMultiConfluenceEvaluator.Evaluate"/>.
    ''' Both the live and backtest paths populate this struct and delegate to the
    ''' single shared evaluator — no condition logic lives in two places (ARCH-04).
    ''' </summary>
    Public Class MultiConfluenceInputs

        ' ── Ichimoku ──────────────────────────────────────────────────────────────
        Public Property SpanA As Single = Single.NaN
        Public Property SpanB As Single = Single.NaN
        Public Property Tenkan As Single = Single.NaN
        Public Property Kijun As Single = Single.NaN

        ' ── Ichimoku at Chikou lag position (barIndex − displacement) ─────────────
        Public Property LagClose As Decimal = Decimal.MinValue
        Public Property LagSpanA As Single = Single.NaN
        Public Property LagSpanB As Single = Single.NaN

        ' ── EMAs ──────────────────────────────────────────────────────────────────
        Public Property Ema21 As Single = Single.NaN
        Public Property Ema50 As Single = Single.NaN

        ' ── DMI / ADX ─────────────────────────────────────────────────────────────
        Public Property Adx As Single = Single.NaN
        Public Property PlusDI As Single = Single.NaN
        Public Property MinusDI As Single = Single.NaN

        ' ── MACD histogram (current and previous bar) ─────────────────────────────
        Public Property MacdHistNow As Single = Single.NaN
        Public Property MacdHistPrev As Single = Single.NaN

        ' ── Stochastic RSI ────────────────────────────────────────────────────────
        Public Property StochK As Single = Single.NaN
        Public Property StochKPrev As Single = Single.NaN

        ' ── ATR ───────────────────────────────────────────────────────────────────
        Public Property Atr As Single = 0.0F

        ' ── Bar close ─────────────────────────────────────────────────────────────
        Public Property LastClose As Decimal = 0D

        ' ── Volume gate ───────────────────────────────────────────────────────────
        ''' <summary>20-bar average volume (0 = unavailable).</summary>
        Public Property VolMa20 As Decimal = 0D
        ''' <summary>Current bar volume.</summary>
        Public Property CurrentVolume As Decimal = 0D

        ' ── Config ────────────────────────────────────────────────────────────────
        Public Property Config As Settings.MultiConfluenceConfig

    End Class

End Namespace
