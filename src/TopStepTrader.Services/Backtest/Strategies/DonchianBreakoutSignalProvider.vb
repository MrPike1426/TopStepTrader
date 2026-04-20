Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Donchian Channel Breakout (20-bar Turtle) entry provider.
    ''' Mirrors the DonchianBreakout block in BacktestEngine (lines 899–940).
    '''
    ''' Entry fires when close breaks out of the prior bar's 20-bar channel:
    '''   Long  when close &gt; prior bar's 20-bar Donchian upper.
    '''   Short when close &lt; prior bar's 20-bar Donchian lower.
    '''
    ''' <see cref="SignalResult.IndicatorExitLevel"/> carries the 10-bar mid-channel
    ''' level at signal time so BacktestEngine can arm the neutral-zone exit
    ''' (exit Long when close drops below mid; exit Short when close rises above mid).
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   DonchianUpper = 20-bar Donchian upper channel
    '''   DonchianLower = 20-bar Donchian lower channel
    '''   DonchianMid   = 10-bar Donchian middle channel (exit level)
    '''   Atr           = ATR(14)  [used only when UseAtrMode = True]
    ''' </summary>
    Public Class DonchianBreakoutSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Need prior bar for previous channel levels
            If barIndex < 1 Then Return Nothing
            If indicators.DonchianUpper Is Nothing OrElse indicators.DonchianLower Is Nothing OrElse
               indicators.DonchianMid Is Nothing Then Return Nothing

            Dim donUpperNow  = indicators.DonchianUpper(barIndex)
            Dim donLowerNow  = indicators.DonchianLower(barIndex)
            Dim donMidNow    = indicators.DonchianMid(barIndex)
            Dim donUpperPrev = indicators.DonchianUpper(barIndex - 1)
            Dim donLowerPrev = indicators.DonchianLower(barIndex - 1)

            If Single.IsNaN(donUpperNow) OrElse Single.IsNaN(donLowerNow) OrElse
               Single.IsNaN(donMidNow) OrElse Single.IsNaN(donUpperPrev) OrElse
               Single.IsNaN(donLowerPrev) Then Return Nothing

            ' Breakout uses prior bar's channel boundaries (as in the engine)
            Dim donSide As String = Nothing
            If bar.Close > SafeD(donUpperPrev) Then
                donSide = "Buy"
            ElseIf bar.Close < SafeD(donLowerPrev) Then
                donSide = "Sell"
            End If

            If donSide Is Nothing Then Return Nothing

            ' Store 10-bar mid-channel exit level at signal time
            Dim midExitLevel = SafeD(donMidNow)

            Dim result As New SignalResult With {
                .Side               = donSide,
                .Confidence         = 1.0F,
                .IsLong             = (donSide = "Buy"),
                .IndicatorExitLevel = midExitLevel
            }

            ' ATR-relative stops when UseAtrMode is active
            If config.UseAtrMode AndAlso indicators.Atr IsNot Nothing Then
                Dim atrVal = indicators.Atr(barIndex)
                If Not Single.IsNaN(atrVal) AndAlso atrVal > 0.0F Then
                    result.StopDelta = CDec(atrVal) * config.SlAtrMultiple
                    result.TpDelta   = CDec(atrVal) * config.TpAtrMultiple
                End If
            End If

            Return result
        End Function

    End Class

End Namespace
