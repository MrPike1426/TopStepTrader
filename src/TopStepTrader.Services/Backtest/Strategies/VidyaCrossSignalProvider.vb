Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' VIDYA Cross entry provider.
    ''' Mirrors the VidyaCross block in BacktestEngine (lines 1029–1071).
    '''
    ''' Long  when close crosses above VIDYA(14) AND 6-bar ΔVol ≥ +20%.
    ''' Short when close crosses below VIDYA(14) AND 6-bar ΔVol ≤ −20%.
    ''' Confidence = |ΔVol| × 100 (capped at 1.0), checked against MinSignalConfidence.
    ''' ATR stop/TP deltas are set when UseAtrMode is on.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   Vidya       = VIDYA(14, CMO 9)
    '''   DeltaVolume = DeltaVolume(6)
    '''   AllBars     = full bar list (for previous bar close)
    '''   Atr         = ATR(14) [used only when UseAtrMode = True]
    ''' </summary>
    Public Class VidyaCrossSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.Vidya Is Nothing OrElse indicators.DeltaVolume Is Nothing OrElse
               indicators.AllBars Is Nothing Then Return Nothing

            If barIndex < 1 Then Return Nothing

            Dim vidyaNow  = indicators.Vidya(barIndex)
            Dim vidyaPrev = indicators.Vidya(barIndex - 1)
            Dim deltaVol  = indicators.DeltaVolume(barIndex)

            If Single.IsNaN(vidyaNow) OrElse Single.IsNaN(vidyaPrev) OrElse Single.IsNaN(deltaVol) Then
                Return Nothing
            End If

            Dim prevClose = indicators.AllBars(barIndex - 1).Close
            Dim vcSide As String = Nothing

            ' Cross above VIDYA + positive volume delta ≥ 20%
            If prevClose <= SafeD(vidyaPrev) AndAlso bar.Close > SafeD(vidyaNow) AndAlso deltaVol >= 0.2F Then
                vcSide = "Buy"
            ' Cross below VIDYA + negative volume delta ≤ −20%
            ElseIf prevClose >= SafeD(vidyaPrev) AndAlso bar.Close < SafeD(vidyaNow) AndAlso deltaVol <= -0.2F Then
                vcSide = "Sell"
            End If

            If vcSide Is Nothing Then Return Nothing

            Dim vcConf = CSng(Math.Min(100.0, Math.Abs(deltaVol) * 100.0)) / 100.0F
            If vcConf < config.MinSignalConfidence Then Return Nothing

            Dim result As New SignalResult With {
                .Side       = vcSide,
                .Confidence = vcConf,
                .IsLong     = (vcSide = "Buy")
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
