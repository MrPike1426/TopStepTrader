Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' SuperTrend + ADX backtest signal provider.
    ''' Long  when SuperTrend direction = +1, ADX >= config.AdxThreshold, and +DI > -DI.
    ''' Short when SuperTrend direction = -1, ADX >= config.AdxThreshold, and -DI > +DI.
    ''' SL = SuperTrend line at entry (AbsoluteSlPrice); no TP — BacktestEngine end-of-day close handles forced exits.
    ''' </summary>
    Public Class SuperTrendAdxSignalProvider
        Implements IStrategySignalProvider

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.AllBars Is Nothing OrElse barIndex < 1 Then Return Nothing
            If indicators.PlusDi Is Nothing OrElse indicators.MinusDi Is Nothing OrElse indicators.Adx Is Nothing Then Return Nothing

            Dim allBars = indicators.AllBars
            Dim n = barIndex + 1
            Dim highs(n - 1) As Decimal
            Dim lows(n - 1) As Decimal
            Dim closes(n - 1) As Decimal
            For i = 0 To n - 1
                highs(i) = allBars(i).High
                lows(i) = allBars(i).Low
                closes(i) = allBars(i).Close
            Next
            Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=If(config.StMultiplier > 0, config.StMultiplier, 3.0))
            Dim curDir = st.Direction(n - 1)
            If curDir = 0.0F OrElse Single.IsNaN(curDir) Then Return Nothing

            Dim adxVal = indicators.Adx(barIndex)
            If Single.IsNaN(adxVal) Then Return Nothing
            Dim adxThreshold As Single = If(config.MinAdxThreshold > 0, config.MinAdxThreshold, 20.0F)
            If adxVal < adxThreshold Then Return Nothing

            Dim plusDi = indicators.PlusDi(barIndex)
            Dim minusDi = indicators.MinusDi(barIndex)
            If Single.IsNaN(plusDi) OrElse Single.IsNaN(minusDi) Then Return Nothing

            Dim stLinePrice = CDec(st.Line(n - 1))

            If curDir > 0.0F AndAlso plusDi > minusDi Then
                Dim entryClose = CDec(closes(n - 1))
                Dim initialRisk = Math.Abs(entryClose - stLinePrice)
                Dim tpDelta = If(config.TpMultiple > 0D, initialRisk * config.TpMultiple, 0D)
                Return New SignalResult With {
                    .Side            = "Buy",
                    .IsLong          = True,
                    .Confidence      = 1.0F,
                    .AbsoluteSlPrice = stLinePrice,
                    .StopDelta       = 0D,
                    .TpDelta         = tpDelta
                }
            ElseIf curDir < 0.0F AndAlso minusDi > plusDi Then
                Dim entryClose = CDec(closes(n - 1))
                Dim initialRisk = Math.Abs(entryClose - stLinePrice)
                Dim tpDelta = If(config.TpMultiple > 0D, initialRisk * config.TpMultiple, 0D)
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
