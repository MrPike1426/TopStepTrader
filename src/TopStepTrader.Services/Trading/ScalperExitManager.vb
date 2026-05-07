Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Result of one ScalperExitManager evaluation. The view layer applies
    ''' NewStop and NewPhase back onto its <see cref="PositionSlot"/> and
    ''' flattens the position when <see cref="ShouldExit"/> is True.
    ''' </summary>
    Public Class ScalperDecision
        Public Property NewStop As Decimal
        Public Property NewPhase As StopPhase
        Public Property ShouldExit As Boolean
        Public Property Reason As String = String.Empty
        Public Property NewState As ScalperState
    End Class

    ''' <summary>
    ''' "The Scalper" — a reusable, stateless service that owns open-position
    ''' management for any scalping strategy view.
    '''
    ''' Phase ladder (per ticket QUAL-04):
    '''   Initial    — SL trails the 15s SuperTrend line (ratchet only).
    '''   Breakeven  — once profit ≥ BreakevenTriggerR × R: SL = max(BE, 15s ST)
    '''                for LONG (mirrored for SHORT). Never retraces.
    '''   ProfitLock — once profit ≥ ProfitLockTriggerR × R: SL trails the 15s
    '''                BB lower band (LONG) / upper band (SHORT). Ratchet only.
    '''                The 15s ST trail is no longer applied at this phase.
    '''   ScaredyCat — one-way trigger: when the 15s BB middle has moved against
    '''                the trade direction on each of the last ScaredyLookbackBars
    '''                closed 15s bars AND the 15s ST direction disagrees with
    '''                the slot side. On trigger the BB multiplier drops from
    '''                BBMultNormal (2.0) to BBMultCautious (1.5) for the rest
    '''                of the trade. Persists until exit.
    '''   Exit       — produced when the closed 15s bar's price crosses the
    '''                latest SL.  ShouldExit=True; the view flattens.
    '''
    ''' No UI / WPF / DI / broker dependencies.  Service does not mutate the
    ''' supplied <see cref="PositionSlot"/> — the caller copies decisions back.
    ''' </summary>
    Public Class ScalperExitManager

        Private ReadOnly _logger As ILogger(Of ScalperExitManager)

        Public Sub New(logger As ILogger(Of ScalperExitManager))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Evaluate the exit ladder for one slot on a closed-bar tick.
        ''' </summary>
        ''' <param name="slot">Open position slot (not mutated).</param>
        ''' <param name="state">Per-slot scalper state (mutated in place and returned on the decision).</param>
        ''' <param name="tickBars15s">Closed 15s bars, oldest first. Last element is the just-closed bar.</param>
        ''' <param name="currentPrice">Most recent price (typically the just-closed bar's close).</param>
        ''' <param name="config">Scalper configuration thresholds.</param>
        Public Function Evaluate(slot As PositionSlot,
                                 state As ScalperState,
                                 tickBars15s As IList(Of MarketBar),
                                 currentPrice As Decimal,
                                 config As ScalperConfig) As ScalperDecision

            If state Is Nothing Then state = New ScalperState()

            Dim decision As New ScalperDecision With {
                .NewStop = slot.StopPrice,
                .NewPhase = slot.StopPhase,
                .ShouldExit = False,
                .Reason = String.Empty,
                .NewState = state
            }

            If slot Is Nothing OrElse config Is Nothing OrElse
               tickBars15s Is Nothing OrElse tickBars15s.Count = 0 Then
                Return decision
            End If

            If slot.EntryPrice = 0D OrElse slot.InitialRisk = 0D Then
                Return decision
            End If

            Dim n = tickBars15s.Count - 1
            Dim highs   = tickBars15s.Select(Function(b) b.High).ToList()
            Dim lows    = tickBars15s.Select(Function(b) b.Low).ToList()
            Dim closes  = tickBars15s.Select(Function(b) b.Close).ToList()

            Dim isLong  = slot.Side = "Buy"
            Dim entry   = slot.EntryPrice
            Dim R       = slot.InitialRisk
            Dim profit  = If(isLong, currentPrice - entry, entry - currentPrice)

            ' ── Indicators ───────────────────────────────────────────────────
            Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes)
            Dim bbMult = If(state.IsScaredyCatActive, config.BBMultCautious, config.BBMultNormal)
            Dim bb = TechnicalIndicators.BollingerBands(closes, config.BBLength, CDbl(bbMult))

            ' ── ScaredyCat one-way trigger detection ─────────────────────────
            ' Only evaluate while the trigger is still latent. Once active it persists.
            If Not state.IsScaredyCatActive AndAlso n >= config.ScaredyLookbackBars Then
                Dim middleAgainst = True
                For i = n - config.ScaredyLookbackBars + 1 To n
                    Dim cur  = bb.Middle(i)
                    Dim prev = bb.Middle(i - 1)
                    If Single.IsNaN(cur) OrElse Single.IsNaN(prev) Then
                        middleAgainst = False
                        Exit For
                    End If
                    If isLong Then
                        If Not (cur < prev) Then
                            middleAgainst = False
                            Exit For
                        End If
                    Else
                        If Not (cur > prev) Then
                            middleAgainst = False
                            Exit For
                        End If
                    End If
                Next

                Dim stDirOpposes As Boolean = If(isLong, st.Direction(n) < 0, st.Direction(n) > 0)

                If middleAgainst AndAlso stDirOpposes Then
                    state.IsScaredyCatActive = True
                    ' Re-compute BB with the cautious multiplier for this bar so
                    ' the tightened band drives the trail immediately.
                    bb = TechnicalIndicators.BollingerBands(closes, config.BBLength,
                                                            CDbl(config.BBMultCautious))
                    _logger.LogInformation(
                        "Scalper [Slot {Idx}] {Contract} ScaredyCat ARMED — BB mult → {Mult}",
                        slot.SlotIndex, slot.Instrument, config.BBMultCautious)
                End If
            End If

            ' ── Phase ladder (ratchet-only stop) ─────────────────────────────
            Dim phase   = slot.StopPhase
            Dim newStop = slot.StopPrice
            Dim stLine  = If(n < st.Line.Length AndAlso Not Single.IsNaN(st.Line(n)),
                             CDec(st.Line(n)), newStop)

            If profit >= config.ProfitLockTriggerR * R Then
                If phase < StopPhase.ProfitLock Then phase = StopPhase.ProfitLock
                Dim bandRaw As Single = If(isLong, bb.Lower(n), bb.Upper(n))
                If Not Single.IsNaN(bandRaw) Then
                    Dim trail = CDec(bandRaw)
                    newStop = If(isLong, Math.Max(newStop, trail), Math.Min(newStop, trail))
                End If
                ' 15s ST trail is intentionally NOT applied at ProfitLock.

            ElseIf profit >= config.BreakevenTriggerR * R Then
                If phase < StopPhase.Breakeven Then phase = StopPhase.Breakeven
                ' SL = max(BE, 15s ST) for LONG  (mirrored for SHORT)
                Dim candidate = If(isLong, Math.Max(entry, stLine), Math.Min(entry, stLine))
                newStop = If(isLong, Math.Max(newStop, candidate), Math.Min(newStop, candidate))

            Else
                ' Initial — trail the 15s SuperTrend line, ratchet only.
                ' Only follow the ST line while its direction agrees with the slot
                ' side; otherwise the line sits on the wrong side of price and
                ' would drag the SL straight through the trade.
                Dim stDirAgrees As Boolean = If(isLong, st.Direction(n) > 0, st.Direction(n) < 0)
                If stDirAgrees Then
                    newStop = If(isLong, Math.Max(newStop, stLine), Math.Min(newStop, stLine))
                End If
            End If

            ' ── Exit check ───────────────────────────────────────────────────
            Dim shouldExit As Boolean = If(isLong, currentPrice <= newStop, currentPrice >= newStop)
            Dim reason As String = String.Empty
            If shouldExit Then
                reason = $"Closed-bar price {currentPrice} crossed SL {newStop} ({slot.Side})"
            End If

            state.LastEvaluatedBarTime = tickBars15s(n).Timestamp

            decision.NewStop    = newStop
            decision.NewPhase   = phase
            decision.ShouldExit = shouldExit
            decision.Reason     = reason
            decision.NewState   = state

            _logger.LogInformation(
                "Scalper [Slot {Idx}] {Contract} side={Side} profit={Profit:F2} R={R:F2} phase={Phase} stop={Stop:F2} scaredy={Scaredy} exit={Exit}",
                slot.SlotIndex, slot.Instrument, slot.Side, profit, R, phase, newStop,
                state.IsScaredyCatActive, shouldExit)

            Return decision
        End Function

    End Class

End Namespace
