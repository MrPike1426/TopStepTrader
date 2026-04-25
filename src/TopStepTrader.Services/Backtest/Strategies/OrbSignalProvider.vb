Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Opening Range Breakout (ORB) entry provider.
    ''' The first 30 minutes of the regular session (configurable via timeframe) establish the OR.
    ''' Long  when bar close exceeds OR high AND volume ≥ 1.2× 20-bar average.
    ''' Short when bar close falls below OR low  AND volume ≥ 1.2× 20-bar average.
    ''' SL  = opposite extreme of OR; TP = 1.5× OR width (minimum 1:1.5 R:R).
    ''' No-trade filter 1: OR width &gt; 2× ATR(14) — range too wide to trade.
    ''' No-trade filter 2: bar is past the session midpoint — late-session entries suppressed.
    ''' Returns Nothing while the OR is still building or when any filter fires.
    ''' </summary>
    Public Class OrbSignalProvider
        Implements IStrategySignalProvider

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.AllBars Is Nothing OrElse indicators.Atr Is Nothing Then Return Nothing

            ' ── Resolve the session boundaries ───────────────────────────────────
            Dim barDate = bar.Timestamp.Date

            ' Walk back to find the first bar of today's session
            Dim sessionStartIdx = barIndex
            Do While sessionStartIdx > 0 AndAlso
                     indicators.AllBars(sessionStartIdx - 1).Timestamp.Date = barDate
                sessionStartIdx -= 1
            Loop

            ' Walk forward to find the last bar of today's session
            Dim sessionEndIdx = barIndex
            Do While sessionEndIdx + 1 < indicators.AllBars.Count AndAlso
                     indicators.AllBars(sessionEndIdx + 1).Timestamp.Date = barDate
                sessionEndIdx += 1
            Loop

            ' ── Opening range phase ───────────────────────────────────────────────
            Dim orbBarCount = Math.Max(1, If(config.Timeframe > 0, 30 \ config.Timeframe, 6))
            Dim orbEndIdx = sessionStartIdx + orbBarCount - 1

            ' Still building the opening range — no signal yet
            If barIndex <= orbEndIdx Then Return Nothing

            ' ── Late-session filter ───────────────────────────────────────────────
            Dim sessionMidIdx = sessionStartIdx + (sessionEndIdx - sessionStartIdx) \ 2
            If barIndex > sessionMidIdx Then Return Nothing

            ' ── Compute OR high / low ─────────────────────────────────────────────
            Dim orHigh As Decimal = Decimal.MinValue
            Dim orLow As Decimal = Decimal.MaxValue
            For i = sessionStartIdx To Math.Min(orbEndIdx, indicators.AllBars.Count - 1)
                orHigh = Math.Max(orHigh, indicators.AllBars(i).High)
                orLow = Math.Min(orLow, indicators.AllBars(i).Low)
            Next
            If orHigh = Decimal.MinValue OrElse orLow = Decimal.MaxValue OrElse orHigh <= orLow Then Return Nothing

            Dim orWidth = orHigh - orLow

            ' ── No-trade filter: OR too wide (> 2× ATR) ──────────────────────────
            Dim atrNow = indicators.Atr(barIndex)
            If Not Single.IsNaN(atrNow) AndAlso atrNow > 0F AndAlso
               orWidth > CDec(atrNow) * 2D Then Return Nothing

            ' ── Volume gate: fail-closed when data is missing ─────────────────────
            Dim volOk As Boolean
            If indicators.VolMa20 IsNot Nothing AndAlso
               Not Single.IsNaN(indicators.VolMa20(barIndex)) AndAlso
               indicators.VolMa20(barIndex) > 0F Then
                volOk = (bar.Volume >= CDec(indicators.VolMa20(barIndex)) * 1.2D)
            Else
                volOk = False   ' fail-closed: no volume data = no trade
            End If
            If Not volOk Then Return Nothing

            ' ── Entry signals ─────────────────────────────────────────────────────
            Dim close = bar.Close

            If close > orHigh Then
                ' Long breakout: SL at OR low, TP = 1.5× OR width above entry
                Dim rawStop = Math.Abs(close - orLow)
                Dim clampedStop = If(config.MinStopDollars > 0D AndAlso rawStop < config.MinStopDollars,
                                     config.MinStopDollars, rawStop)
                Dim clampedTp = If(rawStop > 0D AndAlso clampedStop > rawStop,
                                   orWidth * 1.5D * (clampedStop / rawStop),
                                   orWidth * 1.5D)
                Return New SignalResult With {
                    .Side       = "Buy",
                    .IsLong     = True,
                    .Confidence = 0.8F,
                    .StopDelta  = clampedStop,
                    .TpDelta    = clampedTp
                }
            ElseIf close < orLow Then
                ' Short breakout: SL at OR high, TP = 1.5× OR width below entry
                Dim rawStop = Math.Abs(orHigh - close)
                Dim clampedStop = If(config.MinStopDollars > 0D AndAlso rawStop < config.MinStopDollars,
                                     config.MinStopDollars, rawStop)
                Dim clampedTp = If(rawStop > 0D AndAlso clampedStop > rawStop,
                                   orWidth * 1.5D * (clampedStop / rawStop),
                                   orWidth * 1.5D)
                Return New SignalResult With {
                    .Side       = "Sell",
                    .IsLong     = False,
                    .Confidence = 0.8F,
                    .StopDelta  = clampedStop,
                    .TpDelta    = clampedTp
                }
            End If

            Return Nothing
        End Function

    End Class

End Namespace
