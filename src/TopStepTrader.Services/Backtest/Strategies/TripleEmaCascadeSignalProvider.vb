Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' 3-EMA Cascade (Sniper) entry provider.
    ''' Mirrors the TripleEmaCascade block in BacktestEngine (lines 694–738).
    '''
    ''' Long  when EMA8 crosses above EMA21 AND close is above a rising EMA50.
    ''' Short when EMA8 crosses below EMA21 AND close is below a falling EMA50.
    ''' Confidence = 1.0 (binary — pattern either fires or it does not).
    ''' ATR stop/TP deltas are set when UseAtrMode is on.
    ''' </summary>
    Public Class TripleEmaCascadeSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.Ema8 Is Nothing OrElse indicators.Ema21 Is Nothing OrElse
               indicators.Ema50 Is Nothing Then Return Nothing

            If barIndex < 1 Then Return Nothing

            Dim ema8Now  = indicators.Ema8(barIndex)
            Dim ema8Prev = indicators.Ema8(barIndex - 1)
            Dim ema21Now  = indicators.Ema21(barIndex)
            Dim ema21Prev = indicators.Ema21(barIndex - 1)
            Dim ema50Now  = indicators.Ema50(barIndex)
            Dim ema50Prev = indicators.Ema50(barIndex - 1)

            If Single.IsNaN(ema8Now) OrElse Single.IsNaN(ema8Prev) OrElse
               Single.IsNaN(ema21Now) OrElse Single.IsNaN(ema50Now) OrElse
               Single.IsNaN(ema50Prev) Then Return Nothing

            Dim crossedAbove = (ema8Prev <= ema21Prev AndAlso ema8Now > ema21Now)
            Dim crossedBelow = (ema8Prev >= ema21Prev AndAlso ema8Now < ema21Now)
            Dim ema50Rising  = (ema50Now > ema50Prev)
            Dim ema50Falling = (ema50Now < ema50Prev)

            Dim cascadeSide As String = Nothing
            If crossedAbove AndAlso bar.Close > SafeD(ema50Now) AndAlso ema50Rising Then
                cascadeSide = "Buy"
            ElseIf crossedBelow AndAlso bar.Close < SafeD(ema50Now) AndAlso ema50Falling Then
                cascadeSide = "Sell"
            End If

            If cascadeSide Is Nothing Then Return Nothing

            Dim result As New SignalResult With {
                .Side       = cascadeSide,
                .Confidence = 1.0F,
                .IsLong     = (cascadeSide = "Buy")
            }

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
