Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Bollinger Band + RSI Mean-Reversion entry provider.
    ''' Mirrors the BbRsiMeanReversion block in BacktestEngine (lines 942–980).
    '''
    ''' Dual-confirmation oversold/overbought entry:
    '''   Long  when close &lt; lower BB(20, 2σ) AND RSI(14) &lt; 30 — dual oversold.
    '''   Short when close &gt; upper BB(20, 2σ) AND RSI(14) &gt; 70 — dual overbought.
    ''' Confidence = 1.0 (rule-based, no scoring).
    '''
    ''' <see cref="SignalResult.IndicatorExitLevel"/> carries the BB middle band (SMA20)
    ''' at signal time so BacktestEngine can arm the mean-reversion exit
    ''' (exit Long when close ≥ mid or RSI crosses back above 50;
    '''  exit Short when close ≤ mid or RSI crosses back below 50).
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   BbUpper  = Bollinger upper band  BB(20, 2σ)
    '''   BbMiddle = Bollinger middle band (SMA20) — exit anchor
    '''   BbLower  = Bollinger lower band  BB(20, 2σ)
    '''   Rsi      = RSI(14)
    '''   Atr      = ATR(14)  [used only when UseAtrMode = True]
    ''' </summary>
    Public Class BbRsiReversionSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Guard: all three BB bands and RSI are required
            If indicators.BbUpper Is Nothing OrElse indicators.BbLower Is Nothing OrElse
               indicators.BbMiddle Is Nothing OrElse indicators.Rsi Is Nothing Then Return Nothing

            Dim bbUpperVal = indicators.BbUpper(barIndex)
            Dim bbLowerVal = indicators.BbLower(barIndex)
            Dim bbMidVal   = indicators.BbMiddle(barIndex)
            Dim rsiVal     = indicators.Rsi(barIndex)

            If Single.IsNaN(bbUpperVal) OrElse Single.IsNaN(bbLowerVal) OrElse
               Single.IsNaN(bbMidVal) OrElse Single.IsNaN(rsiVal) Then Return Nothing

            ' Dual-confirmation entry
            Dim mrSide As String = Nothing
            If bar.Close < SafeD(bbLowerVal) AndAlso rsiVal < 30.0F Then
                mrSide = "Buy"
            ElseIf bar.Close > SafeD(bbUpperVal) AndAlso rsiVal > 70.0F Then
                mrSide = "Sell"
            End If

            If mrSide Is Nothing Then Return Nothing

            ' BB middle band stored at signal time for mean-reversion exit
            Dim midExitLevel = SafeD(bbMidVal)

            Dim result As New SignalResult With {
                .Side               = mrSide,
                .Confidence         = 1.0F,
                .IsLong             = (mrSide = "Buy"),
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
