Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Six-signal EMA/RSI weighted-score entry provider.
    ''' Mirrors the EmaRsiWeightedScore block in BacktestEngine (lines 1352–1478)
    ''' and the live StrategyExecutionEngine signal algorithm.
    '''
    ''' Bull scoring (0–120 raw, clamped to 100 before momentum + candle + volume checks):
    '''   1. EMA21 > EMA50 × 1.0005     — 25 pts
    '''   2. Close > EMA21              — 20 pts
    '''   3. Close > EMA50              — 15 pts
    '''   4. RSI zone — 20/12/8 pts     (55–70=20, >70=12, 50–55=8)  (clamped to 100 here)
    '''   5. EMA21 slope > 0.03% / 3 bars — 10 pts
    '''   6. ≥ 2 of last 3 bars bullish — 10 pts
    '''   7. Volume > 20-bar avg × 1.1  — 10 pts  (skipped when absent)
    '''
    ''' Bear scoring (independent — NOT 100 − bullScore):
    '''   1. EMA21 &lt; EMA50 × 0.9995 — 25 pts
    '''   2. Close &lt; EMA21           — 20 pts
    '''   3. Close &lt; EMA50           — 15 pts
    '''   4. RSI zone — 20/12/8 pts   (30–45=20, &lt;30=12, 45–50=8)
    '''   5. EMA21 slope &lt; −0.03% / 3 bars — 10 pts
    '''   6. ≥ 2 of last 3 bars bearish — 10 pts
    '''   7. Volume > 20-bar avg × 1.1 — 10 pts  (skipped when absent)
    '''
    ''' Buy  when bullScore  ≥ MinSignalConfidence × 100.
    ''' Sell when bearScore  ≥ MinSignalConfidence × 100.
    '''
    ''' Neutral exit: both bullScore and bearScore in 40–60 (direction-less market).
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

            ' 4. RSI zone scoring — 3 tiers
            If rsiVal >= 55.0F AndAlso rsiVal < 70.0F Then
                bullScore += 20    ' confirmed bullish trend zone
            ElseIf rsiVal >= 70.0F Then
                bullScore += 12    ' extended/overbought — still bullish, reduced weight
            ElseIf rsiVal >= 50.0F Then
                bullScore += 8     ' mildly bullish, above midline
            End If

            ' Clamp before momentum + candle + volume checks (mirrors live engine)
            bullScore = Math.Max(0.0, Math.Min(100.0, bullScore))

            ' 5. EMA21 slope over 3 bars — 10 pts (bounds guard: need barIndex ≥ 3)
            If barIndex >= 3 Then
                Dim ema21ThreeBack = indicators.Ema21(barIndex - 3)
                If Not Single.IsNaN(ema21ThreeBack) AndAlso ema21ThreeBack > 0.0F Then
                    Dim slope = (ema21Now - ema21ThreeBack) / ema21ThreeBack
                    If slope > 0.0003F Then bullScore += 10
                End If
            End If

            ' 6. Recent 3 candles: ≥ 2 bullish — 10 pts
            Dim bullCandles As Integer = 0
            For c = barIndex - 2 To barIndex
                If indicators.AllBars(c).IsBullish Then bullCandles += 1
            Next
            If bullCandles >= 2 Then bullScore += 10

            ' 7. Volume confirmation — 10 pts (gracefully skipped when volume is absent/zero)
            Dim volScore As Integer = 0
            If indicators.AllBars IsNot Nothing AndAlso barIndex >= 20 Then
                Dim sumVol As Long = 0
                For vi = barIndex - 20 To barIndex - 1
                    sumVol += indicators.AllBars(vi).Volume
                Next
                Dim avgVol20 = CSng(sumVol) / 20.0F
                If avgVol20 > 0 AndAlso bar.Volume > 0 Then
                    If CSng(bar.Volume) > avgVol20 * 1.1F Then volScore = 10
                End If
            End If
            bullScore += volScore

            ' ── Bear score (independent — not 100 − bullScore) ──────────────────
            Dim bearScore As Double = 0

            ' 1. EMA21 < EMA50 × 0.9995 — 25 pts
            If ema21Now < ema50Now * 0.9995F Then bearScore += 25
            ' 2. Close < EMA21 — 20 pts
            If bar.Close < CDec(ema21Now) Then bearScore += 20
            ' 3. Close < EMA50 — 15 pts
            If bar.Close < CDec(ema50Now) Then bearScore += 15
            ' 4. RSI zone scoring — 3 tiers
            If rsiVal >= 30.0F AndAlso rsiVal <= 45.0F Then
                bearScore += 20    ' confirmed bearish trend zone
            ElseIf rsiVal < 30.0F Then
                bearScore += 12    ' oversold — still bearish, reduced weight
            ElseIf rsiVal <= 50.0F Then
                bearScore += 8     ' mildly bearish, below midline
            End If
            ' Clamp before momentum + candle + volume checks
            bearScore = Math.Max(0.0, Math.Min(100.0, bearScore))
            ' 5. EMA21 slope falling over 3 bars — 10 pts
            If barIndex >= 3 Then
                Dim ema21ThreeBack = indicators.Ema21(barIndex - 3)
                If Not Single.IsNaN(ema21ThreeBack) AndAlso ema21ThreeBack > 0.0F Then
                    Dim slope = (ema21Now - ema21ThreeBack) / ema21ThreeBack
                    If slope < -0.0003F Then bearScore += 10
                End If
            End If
            ' 6. ≥ 2 of last 3 candles bearish — 10 pts
            Dim bearCandles As Integer = 0
            For c = barIndex - 2 To barIndex
                If Not indicators.AllBars(c).IsBullish Then bearCandles += 1
            Next
            If bearCandles >= 2 Then bearScore += 10
            ' 7. Volume confirmation — 10 pts (same ratio computed for bull)
            bearScore += volScore

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
                ElseIf bearScore >= minPct Then
                    side = "Sell"
                    conf = CSng(bearScore) / 100.0F
                End If
            End If

            ' Neutral-zone flag: both bull and bear scores 40–60 = direction-less market.
            ' BacktestEngine closes any open position at bar.Close when this is True.
            Dim neutralExit = (bullScore >= 40.0 AndAlso bullScore <= 60.0 AndAlso
                               bearScore >= 40.0 AndAlso bearScore <= 60.0)

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
