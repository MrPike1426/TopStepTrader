Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' FEAT-62: Pure-function candle-pattern detectors used by the Break and Bounce
    ''' entry chain. Definitions are strict per the strategy PDF — do not relax
    ''' the engulfing rules to "close-engulfs-body" without explicit ticket sign-off.
    ''' </summary>
    Public Module CandlePatternDetector

        ''' <summary>
        ''' Hammer: lower wick &gt; 2× body AND upper wick &lt; body.
        ''' Body and wick lengths are absolute distances; a doji (body = 0) is not a
        ''' hammer because the lower-wick comparison degenerates to 0 &gt; 0.
        ''' </summary>
        Public Function IsHammer(bar As MarketBar) As Boolean
            If bar Is Nothing Then Return False
            Dim body As Decimal = Math.Abs(bar.Close - bar.Open)
            If body <= 0D Then Return False
            Dim upperWick As Decimal = bar.High - Math.Max(bar.Open, bar.Close)
            Dim lowerWick As Decimal = Math.Min(bar.Open, bar.Close) - bar.Low
            Return lowerWick > body * 2D AndAlso upperWick < body
        End Function

        ''' <summary>Inverted hammer: upper wick &gt; 2× body AND lower wick &lt; body.</summary>
        Public Function IsInvertedHammer(bar As MarketBar) As Boolean
            If bar Is Nothing Then Return False
            Dim body As Decimal = Math.Abs(bar.Close - bar.Open)
            If body <= 0D Then Return False
            Dim upperWick As Decimal = bar.High - Math.Max(bar.Open, bar.Close)
            Dim lowerWick As Decimal = Math.Min(bar.Open, bar.Close) - bar.Low
            Return upperWick > body * 2D AndAlso lowerWick < body
        End Function

        ''' <summary>
        ''' Strict outside-bar bullish engulfing — both extremes of the prior bar are
        ''' contained within the current bar's open/close range and the current bar
        ''' is bullish: Close &gt; Open AND Close &gt; prev.High AND Open &lt; prev.Low.
        ''' </summary>
        Public Function IsBullishEngulfing(curr As MarketBar, prev As MarketBar) As Boolean
            If curr Is Nothing OrElse prev Is Nothing Then Return False
            Return curr.Close > curr.Open AndAlso curr.Close > prev.High AndAlso curr.Open < prev.Low
        End Function

        ''' <summary>
        ''' Strict outside-bar bearish engulfing — mirror of <see cref="IsBullishEngulfing"/>:
        ''' Close &lt; Open AND Close &lt; prev.Low AND Open &gt; prev.High.
        ''' </summary>
        Public Function IsBearishEngulfing(curr As MarketBar, prev As MarketBar) As Boolean
            If curr Is Nothing OrElse prev Is Nothing Then Return False
            Return curr.Close < curr.Open AndAlso curr.Close < prev.Low AndAlso curr.Open > prev.High
        End Function

    End Module

End Namespace
