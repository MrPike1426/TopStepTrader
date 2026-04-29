Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Evaluates seven degradation signals per bar for an open position slot and
    ''' returns a composite ExitEvaluation result.  Independently unit-testable —
    ''' no UI or broker dependencies.
    ''' </summary>
    Public Class ExitSignalEngine

        Private ReadOnly _logger As ILogger(Of ExitSignalEngine)

        Public Sub New(logger As ILogger(Of ExitSignalEngine))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Evaluate all exit signals for one closed bar.
        ''' </summary>
        ''' <param name="slot">The open position slot.</param>
        ''' <param name="highs">Full bar series — highs (oldest first).</param>
        ''' <param name="lows">Full bar series — lows.</param>
        ''' <param name="closes">Full bar series — closes.</param>
        ''' <param name="stLines">SuperTrend price line array (same length).</param>
        ''' <param name="stDirections">SuperTrend direction array (+1/-1, same length).</param>
        ''' <param name="plusDIs">+DI array (same length).</param>
        ''' <param name="minusDIs">-DI array (same length).</param>
        ''' <param name="adxValues">ADX array (same length).</param>
        ''' <param name="atrValues">14-period ATR array (same length).</param>
        ''' <param name="vwapValues">Intraday VWAP array (same length). Nothing or empty skips E8.</param>
        ''' <param name="rsiValues">14-period RSI array (same length). Nothing or empty skips E9.</param>
        ''' <returns>ExitEvaluation containing score, signals list, recommended health, and phased stop.</returns>
        Public Function Evaluate(slot As PositionSlot,
                                 highs As IList(Of Decimal),
                                 lows As IList(Of Decimal),
                                 closes As IList(Of Decimal),
                                 stLines As Single(),
                                 stDirections As Single(),
                                 plusDIs As Single(),
                                 minusDIs As Single(),
                                 adxValues As Single(),
                                 atrValues As Single(),
                                 Optional vwapValues As Single() = Nothing,
                                 Optional rsiValues As Single() = Nothing) As ExitEvaluation

            Dim n = closes.Count - 1
            Dim eval As New ExitEvaluation()
            Dim signals As New List(Of String)

            ' ── E1: SuperTrend flip (weight 8 — immediate) ──────────────────────
            Dim currentDir = stDirections(n)
            Dim prevDir    = If(n > 0, stDirections(n - 1), currentDir)
            Dim isLong     = slot.Side = "Buy"
            Dim flipped    = (isLong AndAlso currentDir < 0) OrElse (Not isLong AndAlso currentDir > 0)
            If flipped Then
                eval.ImmediateExit = True
                signals.Add("E1:8")
                eval.Score += 8
            End If

            ' ── E2: SuperTrend momentum slowing (weight 3) ──────────────────────
            If n >= 2 Then
                Dim atrN = If(Not Single.IsNaN(atrValues(n)), CDec(atrValues(n)), 0D)
                Dim d0   = CDec(closes(n))     - CDec(stLines(n))
                Dim d1   = CDec(closes(n - 1)) - CDec(stLines(n - 1))
                Dim d2   = CDec(closes(n - 2)) - CDec(stLines(n - 2))
                If isLong Then
                    Dim contracting = d0 < d1 AndAlso d1 < d2
                    Dim rate = If(d1 <> 0D, Math.Abs(d1 - d0), 0D)
                    If contracting AndAlso atrN > 0D AndAlso rate > 0.5D * atrN Then
                        signals.Add("E2:3")
                        eval.Score += 3
                    End If
                Else
                    Dim contracting = d0 > d1 AndAlso d1 > d2  ' distance narrows (less negative) for short
                    Dim rate = If(d1 <> 0D, Math.Abs(d1 - d0), 0D)
                    If contracting AndAlso atrN > 0D AndAlso rate > 0.5D * atrN Then
                        signals.Add("E2:3")
                        eval.Score += 3
                    End If
                End If
            End If

            ' ── E3: ADX declining (weight 2) ────────────────────────────────────
            If n >= 2 Then
                Dim adxN  = adxValues(n)
                Dim adxN1 = adxValues(n - 1)
                Dim adxN2 = adxValues(n - 2)
                If Not Single.IsNaN(adxN) AndAlso Not Single.IsNaN(adxN1) AndAlso Not Single.IsNaN(adxN2) Then
                    Dim falling = adxN < adxN1 AndAlso adxN1 < adxN2
                    Dim tenBelow = adxN < slot.EntryAdx - 10.0F
                    If falling AndAlso tenBelow Then
                        signals.Add("E3:2")
                        eval.Score += 2
                    End If
                End If
            End If

            ' ── E4: DI compression (weight 2) ───────────────────────────────────
            If n >= 1 Then
                Dim pdN  = plusDIs(n)
                Dim mdN  = minusDIs(n)
                Dim pdN1 = plusDIs(n - 1)
                Dim mdN1 = minusDIs(n - 1)
                If Not Single.IsNaN(pdN) AndAlso Not Single.IsNaN(mdN) AndAlso
                   Not Single.IsNaN(pdN1) AndAlso Not Single.IsNaN(mdN1) Then
                    Dim spreadN  = Math.Abs(pdN - mdN)
                    Dim spreadN1 = Math.Abs(pdN1 - mdN1)
                    If spreadN < spreadN1 AndAlso spreadN < 10.0F Then
                        signals.Add("E4:2")
                        eval.Score += 2
                    End If
                End If
            End If

            ' ── E5: DI crossover warning (weight 4) ─────────────────────────────
            If n >= 1 Then
                Dim pdN  = plusDIs(n)
                Dim mdN  = minusDIs(n)
                Dim pdN1 = plusDIs(n - 1)
                Dim mdN1 = minusDIs(n - 1)
                If Not Single.IsNaN(pdN) AndAlso Not Single.IsNaN(mdN) AndAlso
                   Not Single.IsNaN(pdN1) AndAlso Not Single.IsNaN(mdN1) Then
                    Dim crossed As Boolean
                    If isLong Then
                        crossed = pdN < mdN AndAlso pdN1 > mdN1
                    Else
                        crossed = mdN < pdN AndAlso mdN1 > pdN1
                    End If
                    If crossed Then
                        signals.Add("E5:4")
                        eval.Score += 4
                    End If
                End If
            End If

            ' ── E6: Price rejection bar (weight 2) ──────────────────────────────
            Dim barHigh  = highs(n)
            Dim barLow   = lows(n)
            Dim barClose = closes(n)
            Dim barOpen  = barClose  ' fallback if we have no open; rejection based on close vs midpoint
            Dim barRange = barHigh - barLow
            If barRange > 0D Then
                Dim midpoint = barLow + barRange / 2D
                Dim body     = Math.Abs(barClose - barOpen)
                If isLong Then
                    Dim upperWick = barHigh - Math.Max(barClose, barOpen)
                    If upperWick > 2D * body AndAlso barClose < midpoint Then
                        signals.Add("E6:2")
                        eval.Score += 2
                    End If
                Else
                    Dim lowerWick = Math.Min(barClose, barOpen) - barLow
                    If lowerWick > 2D * body AndAlso barClose > midpoint Then
                        signals.Add("E6:2")
                        eval.Score += 2
                    End If
                End If
            End If

            ' ── E7: ATR contraction (weight 1) ──────────────────────────────────
            If n >= 2 AndAlso slot.EntryAtr > 0D Then
                Dim atrN  = atrValues(n)
                Dim atrN1 = atrValues(n - 1)
                Dim atrN2 = atrValues(n - 2)
                If Not Single.IsNaN(atrN) AndAlso Not Single.IsNaN(atrN1) AndAlso Not Single.IsNaN(atrN2) Then
                    If atrN < atrN1 AndAlso atrN1 < atrN2 AndAlso CDec(atrN) < 0.8D * slot.EntryAtr Then
                        signals.Add("E7:1")
                        eval.Score += 1
                    End If
                End If
            End If

            ' ── E8: VWAP cross (weight 2) ────────────────────────────────────────
            ' Long:  close[n] < VWAP[n]  AND close[n-1] >= VWAP[n-1]
            ' Short: close[n] > VWAP[n]  AND close[n-1] <= VWAP[n-1]
            If n >= 1 AndAlso vwapValues IsNot Nothing AndAlso vwapValues.Length > n Then
                Dim vN  = vwapValues(n)
                Dim vN1 = vwapValues(n - 1)
                If Not Single.IsNaN(vN) AndAlso Not Single.IsNaN(vN1) Then
                    Dim crossedBelowVwap As Boolean = isLong AndAlso
                                                      closes(n) < CDec(vN) AndAlso
                                                      closes(n - 1) >= CDec(vN1)
                    Dim crossedAboveVwap As Boolean = Not isLong AndAlso
                                                      closes(n) > CDec(vN) AndAlso
                                                      closes(n - 1) <= CDec(vN1)
                    If crossedBelowVwap OrElse crossedAboveVwap Then
                        signals.Add("E8:2")
                        eval.Score += 2
                    End If
                End If
            End If

            ' ── E9: RSI hidden divergence (weight 3) ────────────────────────────
            ' Long (hidden bearish): price making higher high, RSI making lower high
            '   high[n] > high[n-2]  AND  RSI[n] < RSI[n-2]  AND  RSI[n] > 50
            ' Short (hidden bullish): price making lower low, RSI making higher low
            '   low[n] < low[n-2]   AND  RSI[n] > RSI[n-2]  AND  RSI[n] < 50
            If n >= 2 AndAlso rsiValues IsNot Nothing AndAlso rsiValues.Length > n Then
                Dim rsiN  = rsiValues(n)
                Dim rsiN2 = rsiValues(n - 2)
                If Not Single.IsNaN(rsiN) AndAlso Not Single.IsNaN(rsiN2) Then
                    If isLong Then
                        Dim higherHigh    As Boolean = highs(n) > highs(n - 2)
                        Dim rsiLowerHigh  As Boolean = rsiN < rsiN2
                        Dim bullTerritory As Boolean = rsiN > 50.0F
                        If higherHigh AndAlso rsiLowerHigh AndAlso bullTerritory Then
                            signals.Add("E9:3")
                            eval.Score += 3
                        End If
                    Else
                        Dim lowerLow      As Boolean = lows(n) < lows(n - 2)
                        Dim rsiHigherLow  As Boolean = rsiN > rsiN2
                        Dim bearTerritory As Boolean = rsiN < 50.0F
                        If lowerLow AndAlso rsiHigherLow AndAlso bearTerritory Then
                            signals.Add("E9:3")
                            eval.Score += 3
                        End If
                    End If
                End If
            End If

            eval.ContributingSignals = signals

            ' ── Phased stop calculation ──────────────────────────────────────────
            Dim phasedStop = ComputePhasedStop(slot, closes(n),
                                               If(Not Single.IsNaN(stLines(n)), CDec(stLines(n)), slot.StopPrice),
                                               If(n < atrValues.Length AndAlso Not Single.IsNaN(atrValues(n)), CDec(atrValues(n)), 0D))
            eval.PhasedStopPrice = phasedStop.NewStop
            eval.StopPhase       = phasedStop.Phase

            _logger.LogInformation(
                "ExitEngine [Slot {Idx}] {Contract} score={Score} health={Health} signals=[{Sigs}] phase={Phase} stop={Stop:F2}",
                slot.SlotIndex, slot.Instrument, eval.Score, eval.RecommendedHealth,
                String.Join(",", signals), eval.StopPhase, eval.PhasedStopPrice)

            Return eval
        End Function

        ''' <summary>
        ''' Compute the phased stop price.  The stop only ratchets — never retreats.
        ''' R = initial risk = |entryPrice - stopPrice at entry|
        ''' </summary>
        Private Function ComputePhasedStop(slot As PositionSlot,
                                           currentPrice As Decimal,
                                           stLine As Decimal,
                                           currentAtr As Decimal) As (NewStop As Decimal, Phase As StopPhase)
            If slot.EntryPrice = 0D OrElse slot.InitialRisk = 0D Then
                Return (slot.StopPrice, slot.StopPhase)
            End If

            Dim R     = slot.InitialRisk
            Dim entry = slot.EntryPrice
            Dim isLng = slot.Side = "Buy"

            ' distance moved in favour of the trade
            Dim profit = If(isLng, currentPrice - entry, entry - currentPrice)

            Dim phase   = slot.StopPhase
            Dim newStop = slot.StopPrice

            If profit >= 3D * R Then
                phase = StopPhase.FreeRide
                Dim freeStop = If(isLng, entry + 2D * R, entry - 2D * R)
                newStop = If(isLng, Math.Max(newStop, freeStop), Math.Min(newStop, freeStop))

            ElseIf profit >= 2D * R Then
                If phase < StopPhase.Harvest Then phase = StopPhase.Harvest
                Dim harvestStop = If(isLng, entry + 1.5D * R, entry - 1.5D * R)
                newStop = If(isLng, Math.Max(newStop, harvestStop), Math.Min(newStop, harvestStop))

            ElseIf profit >= 1.5D * R Then
                If phase < StopPhase.ProfitTrail Then phase = StopPhase.ProfitTrail
                ' ATR-based trail: stop at price - 1×ATR (long) / price + 1×ATR (short)
                If currentAtr > 0D Then
                    Dim trailStop = If(isLng, currentPrice - currentAtr, currentPrice + currentAtr)
                    newStop = If(isLng, Math.Max(newStop, trailStop), Math.Min(newStop, trailStop))
                End If

            ElseIf profit >= 1D * R Then
                If phase < StopPhase.Breakeven Then phase = StopPhase.Breakeven
                Dim beStop = If(isLng, entry + 0.5D * R, entry - 0.5D * R)
                newStop = If(isLng, Math.Max(newStop, beStop), Math.Min(newStop, beStop))

            Else
                ' Initial phase — trail the SuperTrend line (ratchet only)
                newStop = If(isLng, Math.Max(newStop, stLine), Math.Min(newStop, stLine))
            End If

            Return (newStop, phase)
        End Function

    End Class

End Namespace
