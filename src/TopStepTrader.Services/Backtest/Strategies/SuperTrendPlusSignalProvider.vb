Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' SuperTrend+ backtest signal provider (FEAT-35).
    ''' Entry identical to SuperTrendAdx: ST direction gate + ADX threshold + DI cross.
    ''' Reads pre-computed StDirectionSeries and StLineSeries from StrategyIndicators
    ''' instead of recomputing SuperTrend per bar.
    ''' AbsoluteSlPrice = ST line at entry — BacktestEngine trails it each bar.
    ''' TpDelta = initial risk × config.TpMultiple (0 = no TP, exit on flip only).
    ''' </summary>
    Public Class SuperTrendPlusSignalProvider
        Implements IStrategySignalProvider

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.StDirectionSeries Is Nothing OrElse
               indicators.StLineSeries Is Nothing OrElse
               indicators.PlusDi Is Nothing OrElse
               indicators.MinusDi Is Nothing OrElse
               indicators.Adx Is Nothing Then Return Nothing

            If barIndex < 1 Then Return Nothing

            Dim curDir = indicators.StDirectionSeries(barIndex)
            If curDir = 0.0F OrElse Single.IsNaN(curDir) Then Return Nothing

            Dim adxVal = indicators.Adx(barIndex)
            If Single.IsNaN(adxVal) Then Return Nothing
            Dim adxThreshold As Single = If(config.MinAdxThreshold > 0, config.MinAdxThreshold, 20.0F)
            If adxVal < adxThreshold Then Return Nothing

            Dim plusDi = indicators.PlusDi(barIndex)
            Dim minusDi = indicators.MinusDi(barIndex)
            If Single.IsNaN(plusDi) OrElse Single.IsNaN(minusDi) Then Return Nothing

            Dim stLine = indicators.StLineSeries(barIndex)
            If Single.IsNaN(stLine) OrElse stLine <= 0.0F Then Return Nothing
            Dim stLinePrice = CDec(stLine)

            Dim initialRisk = Math.Abs(bar.Close - stLinePrice)
            Dim tpDelta = If(config.TpMultiple > 0D, initialRisk * config.TpMultiple, 0D)

            If curDir > 0.0F AndAlso plusDi > minusDi Then
                Return New SignalResult With {
                    .Side            = "Buy",
                    .IsLong          = True,
                    .Confidence      = 1.0F,
                    .AbsoluteSlPrice = stLinePrice,
                    .StopDelta       = 0D,
                    .TpDelta         = tpDelta
                }
            ElseIf curDir < 0.0F AndAlso minusDi > plusDi Then
                Return New SignalResult With {
                    .Side            = "Sell",
                    .IsLong          = False,
                    .Confidence      = 1.0F,
                    .AbsoluteSlPrice = stLinePrice,
                    .StopDelta       = 0D,
                    .TpDelta         = tpDelta
                }
            End If

            Return Nothing
        End Function

    End Class

End Namespace
