Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Double Bollinger Bands (Double Bubble Butt) entry provider.
    ''' Mirrors the DoubleBubbleButt block in BacktestEngine (lines 982–1027).
    '''
    ''' Zone system using ±1.0 SD inner and ±2.0 SD outer Bollinger Bands (both period 20):
    '''   Long  when close &gt; upper inner band (enters Buy Zone above 1 SD).
    '''   Short when close &lt; lower inner band (enters Sell Zone below 1 SD).
    '''
    ''' Stop = distance from close to opposite outer band (hard stop outside 2 SD);
    '''        falls back to 2×ATR(20) when outer-band distance is zero or negative.
    ''' TP   = 2×ATR(20); falls back to StopDelta when ATR is unavailable.
    '''
    ''' Neutral-zone exit (handled by engine, not this provider):
    '''   Exit Long  when close drops back below upper inner band.
    '''   Exit Short when close rises back above lower inner band.
    ''' <see cref="SignalResult.IndicatorExitLevel"/> carries the inner band level at signal
    ''' time so the engine can arm the neutral-zone exit.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   DbbInnerUpper / DbbInnerLower = BB(20, 1.0 SD) upper/lower
    '''   BbUpper / BbLower             = BB(20, 2.0 SD) upper/lower  (outer bands)
    '''   Atr                           = ATR(20)
    ''' </summary>
    Public Class DoubleBubbleButtSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Guard: inner bands required; outer bands and ATR are needed for sizing
            If indicators.DbbInnerUpper Is Nothing OrElse indicators.DbbInnerLower Is Nothing OrElse
               indicators.BbUpper Is Nothing OrElse indicators.BbLower Is Nothing Then Return Nothing

            Dim dbbIU     = indicators.DbbInnerUpper(barIndex)
            Dim dbbIL     = indicators.DbbInnerLower(barIndex)
            Dim dbbOU     = indicators.BbUpper(barIndex)
            Dim dbbOL     = indicators.BbLower(barIndex)
            Dim dbbAtrVal = If(indicators.Atr IsNot Nothing, indicators.Atr(barIndex), Single.NaN)

            If Single.IsNaN(dbbIU) OrElse Single.IsNaN(dbbIL) OrElse
               Single.IsNaN(dbbOU) OrElse Single.IsNaN(dbbOL) Then Return Nothing

            ' Entry zone determination
            Dim dbbSide As String = Nothing
            If bar.Close > SafeD(dbbIU) Then
                dbbSide = "Buy"
            ElseIf bar.Close < SafeD(dbbIL) Then
                dbbSide = "Sell"
            End If

            If dbbSide Is Nothing Then Return Nothing

            Dim isDbbBuy = (dbbSide = "Buy")

            ' Inner band level stored at signal time for the neutral-zone exit
            Dim innerExitLevel = If(isDbbBuy, SafeD(dbbIU), SafeD(dbbIL))

            ' Stop = distance to the opposite outer band (beyond 2 SD).
            ' Fallback uses persona's SlAtrMultiple × ATR so different personas produce different SL widths.
            ' TP uses persona's TpAtrMultiple × ATR so Lewis/Damian/Joe targets are independent.
            Dim dbbAtr = If(Not Single.IsNaN(dbbAtrVal), SafeD(dbbAtrVal), 0D)
            Dim slMult = If(config.SlAtrMultiple > 0D, config.SlAtrMultiple, 2D)
            Dim tpMult = If(config.TpAtrMultiple > 0D, config.TpAtrMultiple, 2D)
            Dim outerBandDist = If(isDbbBuy,
                                   bar.Close - SafeD(dbbOL),
                                   SafeD(dbbOU) - bar.Close)
            Dim stopDelta = If(outerBandDist > 0D, outerBandDist,
                               If(dbbAtr > 0D, dbbAtr * slMult, 0D))
            Dim tpDelta = If(dbbAtr > 0D, dbbAtr * tpMult, stopDelta)

            Return New SignalResult With {
                .Side               = dbbSide,
                .Confidence         = 1.0F,
                .IsLong             = isDbbBuy,
                .StopDelta          = stopDelta,
                .TpDelta            = tpDelta,
                .IndicatorExitLevel = innerExitLevel
            }
        End Function

    End Class

End Namespace
