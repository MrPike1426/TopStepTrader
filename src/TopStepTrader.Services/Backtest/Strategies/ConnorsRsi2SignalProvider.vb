Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Connors RSI-2 mean-reversion entry provider.
    ''' Mirrors the ConnorsRsi2 block in BacktestEngine (lines 824–856).
    '''
    ''' Long  when RSI(2) &lt; 10 AND close &gt; SMA(200) — short-term dip in long-term uptrend.
    ''' Short when RSI(2) &gt; 90 AND close &lt; SMA(200) — short-term rally in long-term downtrend.
    ''' Confidence = 1.0 (rule-based, no scoring).
    '''
    ''' Exit is indicator-driven per bar (SMA(5) / RSI(2) crosses) and is handled by
    ''' BacktestEngine — this provider only signals entry.  No IndicatorExitLevel is
    ''' stored at signal time because the exit level (SMA(5)) changes every bar.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   Rsi2   = RSI(2)   — warm-up: 2 bars
    '''   Sma200 = SMA(200) — warm-up: 200 bars
    '''   Atr    = ATR(14)  — used only when UseAtrMode = True
    ''' </summary>
    Public Class ConnorsRsi2SignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Guard: RSI(2) and SMA(200) are required for entry
            If indicators.Rsi2 Is Nothing OrElse indicators.Sma200 Is Nothing Then Return Nothing

            Dim rsi2Val   = indicators.Rsi2(barIndex)
            Dim sma200Val = indicators.Sma200(barIndex)

            If Single.IsNaN(rsi2Val) OrElse Single.IsNaN(sma200Val) Then Return Nothing

            ' Entry rules
            Dim crSide As String = Nothing
            If rsi2Val < 10.0F AndAlso bar.Close > SafeD(sma200Val) Then
                crSide = "Buy"
            ElseIf rsi2Val > 90.0F AndAlso bar.Close < SafeD(sma200Val) Then
                crSide = "Sell"
            End If

            If crSide Is Nothing Then Return Nothing

            Dim result As New SignalResult With {
                .Side       = crSide,
                .Confidence = 1.0F,
                .IsLong     = (crSide = "Buy")
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
