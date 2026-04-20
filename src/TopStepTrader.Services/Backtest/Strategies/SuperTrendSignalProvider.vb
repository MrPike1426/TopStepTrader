Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' SuperTrend trend-following entry provider.
    ''' Mirrors the SuperTrend block in BacktestEngine (lines 858–897).
    '''
    ''' Entry fires on a SuperTrend direction flip:
    '''   Long  when direction changes from −1 to +1 (bull flip).
    '''   Short when direction changes from +1 to −1 (bear flip).
    '''
    ''' Absolute exit levels set at signal time:
    '''   AbsoluteSlPrice = SuperTrend indicator line at signal bar (hard stop).
    '''   AbsoluteTpPrice = 2 × ATR(10) above (long) or below (short) bar.Close.
    '''                     Falls back to ±2% of close when ATR is unavailable.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   SuperTrendLine = SuperTrend price line  (ATR(10) × 3.0)
    '''   SuperTrendDir  = SuperTrend direction (+1 = bull, −1 = bear)
    '''   SuperTrendAtr  = ATR(10) for TP sizing
    ''' </summary>
    Public Class SuperTrendSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Need at least one prior bar for direction comparison
            If barIndex < 1 Then Return Nothing
            If indicators.SuperTrendLine Is Nothing OrElse indicators.SuperTrendDir Is Nothing Then Return Nothing

            Dim stDirNow  = indicators.SuperTrendDir(barIndex)
            Dim stDirPrev = indicators.SuperTrendDir(barIndex - 1)
            Dim stLineNow = indicators.SuperTrendLine(barIndex)

            If Single.IsNaN(stDirNow) OrElse Single.IsNaN(stDirPrev) OrElse Single.IsNaN(stLineNow) Then Return Nothing

            ' Previous direction must be valid (not zero = uninitialized)
            If stDirPrev = 0.0F Then Return Nothing

            ' Entry only on direction flip
            Dim stSide As String = Nothing
            If stDirNow = 1.0F AndAlso stDirPrev = -1.0F Then
                stSide = "Buy"
            ElseIf stDirNow = -1.0F AndAlso stDirPrev = 1.0F Then
                stSide = "Sell"
            End If

            If stSide Is Nothing Then Return Nothing

            Dim isLong = (stSide = "Buy")

            ' ATR(10) for TP sizing
            Dim stAtrVal As Decimal = 0D
            If indicators.SuperTrendAtr IsNot Nothing AndAlso barIndex < indicators.SuperTrendAtr.Length Then
                Dim raw = indicators.SuperTrendAtr(barIndex)
                If Not Single.IsNaN(raw) Then stAtrVal = SafeD(raw)
            End If

            ' Absolute exit levels anchored to the signal bar
            Dim absSlPrice = SafeD(stLineNow)
            Dim absTpPrice = If(isLong,
                                If(stAtrVal > 0D, bar.Close + stAtrVal * 2D, bar.Close * 1.02D),
                                If(stAtrVal > 0D, bar.Close - stAtrVal * 2D, bar.Close * 0.98D))

            Return New SignalResult With {
                .Side            = stSide,
                .Confidence      = 1.0F,
                .IsLong          = isLong,
                .AbsoluteSlPrice = absSlPrice,
                .AbsoluteTpPrice = absTpPrice
            }
        End Function

    End Class

End Namespace
