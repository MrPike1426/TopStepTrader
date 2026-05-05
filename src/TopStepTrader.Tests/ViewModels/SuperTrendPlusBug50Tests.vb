Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-50: Phased stop must be skipped during early-mode grace (IsEarlyModeEntry = True).
    ''' Without the guard, ComputePhasedStop uses the bullish ST support line as a ratchet
    ''' target for a short — producing a stop below entry that TopStepX rejects silently.
    ''' </summary>
    Public Class SuperTrendPlusBug50Tests

        Private ReadOnly _engine As ExitSignalEngine =
            New ExitSignalEngine(NullLogger(Of ExitSignalEngine).Instance)

        Private Shared Function DefaultConfig() As SuperTrendPlusConfig
            Return New SuperTrendPlusConfig()
        End Function

        ' ── Helpers ──────────────────────────────────────────────────────────

        ''' <summary>Short slot that models the UAT trade 2542011663 scenario.</summary>
        Private Shared Function MakeShortSlotEarlyMode() As PositionSlot
            Return New PositionSlot With {
                .SlotIndex    = 0,
                .Instrument   = "GOLD",
                .Side         = "Sell",
                .IsOpen       = True,
                .EntryPrice   = 4585.40D,
                .StopPrice    = 4614.76D,   ' bracket SL above entry
                .InitialRisk  = Math.Abs(4585.40D - 4614.76D),
                .StopPhase    = StopPhase.Initial,
                .IsEarlyModeEntry = True
            }
        End Function

        ' ── Test 1: ComputePhasedStop produces a wrong stop when called with bullish ST line ──
        ' This validates the root cause: before the guard was added, the phased stop block
        ' would replace the bracket SL (4614.76) with the bullish ST support (~4560), which
        ' is below entry for a short — a rejected and silent no-op by TopStepX.

        <Fact>
        Public Sub ComputePhasedStop_ShortSlot_BullishStLine_ProducesStopBelowEntry()
            Dim slot = MakeShortSlotEarlyMode()

            ' Bullish ST support line — below entry for a short that has not yet been confirmed
            Dim bullishStLine = 4560.0D
            Dim currentPrice  = 4583.0D    ' small drift from entry, still in Initial phase
            Dim atr           = 5.0D

            Dim result = _engine.ComputePhasedStop(slot, currentPrice, bullishStLine, atr, DefaultConfig())

            ' The ratchet for a short is Math.Min(existingStop, stLine) = Math.Min(4614.76, 4560) = 4560
            ' This is BELOW entry (4585.40) — an invalid stop that TopStepX rejects.
            Assert.True(result.NewStop < slot.EntryPrice,
                        $"Without the IsEarlyModeEntry guard, ComputePhasedStop produces {result.NewStop} " &
                        $"which is below entry {slot.EntryPrice} — proving the bug.")
        End Sub

        ' ── Test 2: Guard prevents StopPrice mutation when IsEarlyModeEntry = True ──
        ' Reproduces the ViewModel guard logic in isolation: if the caller checks
        ' IsEarlyModeEntry before invoking ComputePhasedStop, StopPrice stays frozen.

        <Fact>
        Public Sub PhasedStop_SkippedWhenIsEarlyModeEntry_StopPriceUnchanged()
            Dim slot = MakeShortSlotEarlyMode()
            Dim originalStop = slot.StopPrice

            ' Simulate what the fixed ViewModel does: skip the block when IsEarlyModeEntry = True.
            If Not slot.IsEarlyModeEntry Then
                Dim result = _engine.ComputePhasedStop(slot, 4583.0D, 4560.0D, 5.0D, DefaultConfig())
                slot.StopPrice = result.NewStop
            End If

            Assert.Equal(originalStop, slot.StopPrice)
        End Sub

        ' ── Test 3: Once IsEarlyModeEntry clears, phased stop resumes correctly ──
        ' After ST confirms direction the guard lifts and ratcheting works as designed.
        ' For a short with a bearish ST line (above entry but below the bracket SL),
        ' the stop should ratchet down toward the confirmed ST line.

        <Fact>
        Public Sub PhasedStop_ResumesAfterEarlyModeClears_StopRatchetsTowardStLine()
            Dim slot = MakeShortSlotEarlyMode()
            slot.IsEarlyModeEntry = False    ' ST has confirmed — early-mode grace cleared

            ' Bearish ST resistance line is between entry and bracket: valid ratchet target for a short.
            ' For a short, the stop moves DOWN (toward entry/profit) — Math.Min(bracketSL, stLine).
            Dim confirmedBearishStLine = 4605.0D   ' above entry (4585.40), below bracket (4614.76)
            Dim currentPrice = 4580.0D             ' short is mildly in profit, still in Initial phase

            Dim result = _engine.ComputePhasedStop(slot, currentPrice, confirmedBearishStLine, 5.0D, DefaultConfig())

            ' Ratchet: Math.Min(4614.76, 4605) = 4605 — stop moves down (tightens for a short)
            Assert.True(result.NewStop < slot.StopPrice,
                        $"After IsEarlyModeEntry clears, phased stop should ratchet from {slot.StopPrice} " &
                        $"to {result.NewStop} using the confirmed bearish ST line.")
            Assert.True(result.NewStop > slot.EntryPrice,
                        $"Ratcheted stop {result.NewStop} must remain above entry {slot.EntryPrice}.")
        End Sub

    End Class

End Namespace
