Imports Microsoft.ML.Data

Namespace TopStepTrader.ML.Models

    ''' <summary>
    ''' ML.NET input schema for signal classification.
    ''' All features must be Single (float) for ML.NET compatibility.
    ''' Label = True means the signal was profitable (+N ticks within M bars).
    ''' </summary>
    Public Class BarFeatureVector

        <ColumnName("Label")>
        Public Property Label As Boolean

        ' ── Momentum ─────────────────────────────────
        Public Property RSI14 As Single
        Public Property RSI7 As Single

        ' ── Trend ────────────────────────────────────
        Public Property EMA9 As Single
        Public Property EMA21 As Single
        Public Property EMA9vsEMA21 As Single      ' (EMA9 - EMA21) / EMA21  normalised
        Public Property PriceVsEMA21 As Single     ' (Close - EMA21) / EMA21

        ' ── MACD ─────────────────────────────────────
        Public Property MACDLine As Single
        Public Property MACDSignal As Single
        Public Property MACDHistogram As Single
        Public Property MACDHistogramChange As Single   ' histogram - previous histogram

        ' ── Volatility ───────────────────────────────
        Public Property ATR14 As Single
        Public Property BBWidth As Single              ' (Upper - Lower) / Middle
        Public Property PriceVsBBMiddle As Single      ' (Close - Middle) / Middle

        ' ── Volume ───────────────────────────────────
        Public Property VolumeRatio As Single          ' bar volume / 20-bar avg volume
        Public Property PriceVsVWAP As Single          ' (Close - VWAP) / VWAP

        ' ── Price Action ─────────────────────────────
        Public Property BarRange As Single             ' ATR-normalised bar range
        Public Property BodyRatio As Single            ' |Close - Open| / (High - Low)
        Public Property UpperWick As Single            ' (High - max(O,C)) / ATR14
        Public Property LowerWick As Single            ' (min(O,C) - Low) / ATR14
        Public Property IsBullish As Single            ' 1.0 = bullish bar, 0.0 = bearish

        ' ── Returns ──────────────────────────────────
        Public Property Return1Bar As Single           ' (Close[i] - Close[i-1]) / Close[i-1]
        Public Property Return5Bar As Single
        Public Property Return20Bar As Single

    End Class

End Namespace
