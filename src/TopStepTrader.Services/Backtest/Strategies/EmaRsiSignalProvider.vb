Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Six-signal EMA/RSI weighted-score entry provider.
    ''' Mirrors the EmaRsiWeightedScore block in BacktestEngine (lines 1352–1478)
    ''' and the live StrategyExecutionEngine signal algorithm.
    '''
    ''' Scoring (0–110 raw, clamped to 100 before EMA21 momentum + candle checks):
    '''   1. EMA21 > EMA50 × 1.0005  — 25 pts
    '''   2. Close > EMA21            — 20 pts
    '''   3. Close > EMA50            — 15 pts
    '''   4. RSI in 55–70 range       — 20 pts  (clamped to 100 here)
    '''   5. EMA21 rising             — 10 pts
    '''   6. ≥ 2 of last 3 bars bullish — 10 pts
    '''
    ''' Buy  when bullScore  ≥ MinSignalConfidence × 100.
    ''' Sell when (100 − bullScore) ≥ MinSignalConfidence × 100.
    '''
    ''' Note: The neutral-zone exit (score 40–60 while in a position) is an exit
    ''' mechanism that stays in BacktestEngine until ARCH-01e; this provider
    ''' handles entry signals only.
    ''' </summary>
    Public Class EmaRsiSignalProvider
        Implements IStrategySignalProvider

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' ── Indicator values ─────────────────────────────────────────────────
            Dim ema21Now  = indicators.Ema21(barIndex)
            Dim ema21Prev = indicators.Ema21(barIndex - 1)
            Dim ema50Now  = indicators.Ema50(barIndex)
            Dim rsiVal    = indicators.Rsi(barIndex)

            ' Skip if any required indicator hasn't finished warming up
            If Single.IsNaN(ema21Now) OrElse Single.IsNaN(ema21Prev) OrElse
               Single.IsNaN(ema50Now) OrElse Single.IsNaN(rsiVal) Then Return Nothing

            ' ── Bull score ───────────────────────────────────────────────────────
            Dim bullScore As Double = 0

            ' 1. EMA21 vs EMA50 crossover — 25 pts (≥0.05% separation)
            If ema21Now > ema50Now * 1.0005F Then bullScore += 25

            ' 2. Close vs EMA21 — 20 pts
            If bar.Close > CDec(ema21Now) Then bullScore += 20

            ' 3. Close vs EMA50 — 15 pts
            If bar.Close > CDec(ema50Now) Then bullScore += 15

            ' 4. RSI trending zone 55–70 — 20 pts
            If rsiVal >= 55.0F AndAlso rsiVal < 70.0F Then bullScore += 20

            ' Clamp before momentum + candle checks (mirrors live engine)
            bullScore = Math.Max(0.0, Math.Min(100.0, bullScore))

            ' 5. EMA21 momentum (rising since prior bar) — 10 pts
            If ema21Now > ema21Prev Then bullScore += 10

            ' 6. Recent 3 candles: ≥ 2 bullish — 10 pts
            Dim bullCandles As Integer = 0
            For c = barIndex - 2 To barIndex
                If indicators.AllBars(c).IsBullish Then bullCandles += 1
            Next
            If bullCandles >= 2 Then bullScore += 10

            Dim downPct As Double = 100.0 - bullScore

            ' ── ADX trend-strength gate ──────────────────────────────────────────
            Dim adxGate As Boolean = (config.MinAdxThreshold <= 0.0F)
            If Not adxGate AndAlso indicators.Adx IsNot Nothing Then
                Dim adxVal = indicators.Adx(barIndex)
                adxGate = Not Single.IsNaN(adxVal) AndAlso adxVal >= config.MinAdxThreshold
            End If

            ' ── Entry decision ───────────────────────────────────────────────────
            Dim minPct As Double = config.MinSignalConfidence * 100.0
            Dim side As String = Nothing
            Dim conf As Single = 0.0F

            If adxGate Then
                If bullScore >= minPct Then
                    side = "Buy"
                    conf = CSng(bullScore) / 100.0F
                ElseIf downPct >= minPct Then
                    side = "Sell"
                    conf = CSng(downPct) / 100.0F
                End If
            End If

            ' Neutral-zone flag: score 40–60 = direction-less market.
            ' BacktestEngine closes any open position at bar.Close when this is True.
            Dim neutralExit = (bullScore >= 40.0 AndAlso bullScore <= 60.0)

            If side Is Nothing Then
                Return If(neutralExit, New SignalResult With {.NeutralExit = True}, Nothing)
            End If

            ' ── Build result ─────────────────────────────────────────────────────
            Dim result As New SignalResult With {
                .Side       = side,
                .Confidence = conf,
                .IsLong     = (side = "Buy")
            }

            ' ATR-based stop deltas (when UseAtrMode is on and ATR series is available)
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
