Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Pump-n-Dump entry provider: 3 consecutive bars all closing in the same direction.
    ''' Long when the last 3 bars (including barIndex) are all green (Close &gt; Open).
    ''' Short when the last 3 bars are all red (Close &lt; Open).
    ''' Mirrors the FLAT entry path in PumpNDumpExecutionEngine.DoCheckAsync.
    ''' SL/TP are ATR-based via config.SlAtrMultiple / config.TpAtrMultiple.
    ''' </summary>
    Public Class PumpNDumpSignalProvider
        Implements IStrategySignalProvider

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.AllBars Is Nothing OrElse barIndex < 2 Then Return Nothing

            Dim b0 = indicators.AllBars(barIndex - 2)
            Dim b1 = indicators.AllBars(barIndex - 1)
            Dim b2 = indicators.AllBars(barIndex)

            Dim allGreen = b0.Close > b0.Open AndAlso b1.Close > b1.Open AndAlso b2.Close > b2.Open
            Dim allRed   = b0.Close < b0.Open AndAlso b1.Close < b1.Open AndAlso b2.Close < b2.Open

            If Not allGreen AndAlso Not allRed Then Return Nothing

            ' ── SL/TP sizing — ATR-based ─────────────────────────────────────────
            Dim atrNow As Decimal = 0D
            If indicators.Atr IsNot Nothing AndAlso barIndex < indicators.Atr.Length AndAlso
               Not Single.IsNaN(indicators.Atr(barIndex)) Then
                atrNow = CDec(indicators.Atr(barIndex))
            End If

            Dim stopDelta As Decimal
            Dim tpDelta As Decimal

            If config.UseAtrMode AndAlso atrNow > 0D Then
                stopDelta = atrNow * config.SlAtrMultiple
                tpDelta   = atrNow * config.TpAtrMultiple
            Else
                ' Fallback: fixed tick counts (ticket defaults: 8t SL, 80t TP — ES convention)
                stopDelta = 8D * config.TickSize
                tpDelta   = 80D * config.TickSize
            End If

            If allGreen Then
                Return New SignalResult With {
                    .Side       = "Buy",
                    .IsLong     = True,
                    .Confidence = 1.0F,
                    .StopDelta  = stopDelta,
                    .TpDelta    = tpDelta
                }
            Else
                Return New SignalResult With {
                    .Side       = "Sell",
                    .IsLong     = False,
                    .Confidence = 1.0F,
                    .StopDelta  = stopDelta,
                    .TpDelta    = tpDelta
                }
            End If
        End Function

    End Class

End Namespace
