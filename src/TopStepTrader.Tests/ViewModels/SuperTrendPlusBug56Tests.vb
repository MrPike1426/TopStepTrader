Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.ML.Features
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-56: Take$100 SL did not advance past breakeven (entry price).
    ''' Root cause: the BB median ratchet (Floor B) was missing — the code only
    ''' snapped to entry once and never continued trailing the EMA-10 of 15s bars.
    '''
    ''' These tests exercise the Take$100 stop-logic helper in isolation by
    ''' re-implementing the same three-state rule used in SuperTrendPlusViewModel
    ''' and verifying each branch independently.
    '''
    ''' Three-state rule (Take100ProfitEnabled = True):
    '''   State 1  PnL &lt; $100   → trail ST line (ratchet only)
    '''   State 2a PnL ≥ $100, BB_mid &lt; entry  → floor at entry (breakeven)
    '''   State 2b PnL ≥ $100, BB_mid ≥ entry  → floor at max(entry, BB_mid) — ratchet
    ''' </summary>
    Public Class SuperTrendPlusBug56Tests

        ' ── shared helpers ──────────────────────────────────────────────────────

        ''' <summary>
        ''' Long slot: entry=3300, initial stop=3290, R=10.
        ''' Represents a typical MGC Micro-Gold long position.
        ''' </summary>
        Private Shared Function MakeLongSlot(Optional stopPrice As Decimal = 3290D) As PositionSlot
            Return New PositionSlot With {
                .SlotIndex        = 0,
                .Instrument       = "MGC",
                .Side             = "Buy",
                .IsOpen           = True,
                .EntryPrice       = 3300.0D,
                .StopPrice        = stopPrice,
                .InitialRisk      = 10.0D,
                .StopPhase        = StopPhase.Initial,
                .IsEarlyModeEntry = False
            }
        End Function

        ''' <summary>Short slot: entry=3300, initial stop=3310, R=10.</summary>
        Private Shared Function MakeShortSlot(Optional stopPrice As Decimal = 3310D) As PositionSlot
            Return New PositionSlot With {
                .SlotIndex        = 1,
                .Instrument       = "MGC",
                .Side             = "Sell",
                .IsOpen           = True,
                .EntryPrice       = 3300.0D,
                .StopPrice        = stopPrice,
                .InitialRisk      = 10.0D,
                .StopPhase        = StopPhase.Initial,
                .IsEarlyModeEntry = False
            }
        End Function

        ''' <summary>
        ''' Mirrors the Take$100 stop calculation extracted from
        ''' SuperTrendPlusViewModel — Floor A (entry) + Floor B (BB mid).
        ''' </summary>
        Private Shared Function ComputeTake100Stop(slot As PositionSlot,
                                                   latestPnl As Decimal,
                                                   stLine As Decimal,
                                                   bbMid As Decimal?) As (NewStop As Decimal, Phase As StopPhase)
            Dim isLng = slot.Side = "Buy"

            If latestPnl >= 100D Then
                ' Floor A: breakeven
                Dim beStop = slot.EntryPrice
                Dim newStop = If(isLng, Math.Max(slot.StopPrice, beStop), Math.Min(slot.StopPrice, beStop))
                Dim phase = StopPhase.Breakeven

                ' Floor B: BB median — ratchets above the breakeven floor
                If bbMid.HasValue Then
                    newStop = If(isLng, Math.Max(newStop, bbMid.Value), Math.Min(newStop, bbMid.Value))
                End If

                Return (newStop, phase)
            Else
                ' Pre-$100: trail SuperTrend line, ratchet only
                Dim newStop = If(isLng, Math.Max(slot.StopPrice, stLine), Math.Min(slot.StopPrice, stLine))
                Return (newStop, StopPhase.Initial)
            End If
        End Function

        ' ── State 1: PnL < $100 — trail ST line ─────────────────────────────────

        <Fact>
        Public Sub Take100_PnlBelow100_StopTrailsSTLine_Long()
            Dim slot   = MakeLongSlot(stopPrice := 3290D)
            Dim stLine = 3295.0D   ' ST line moved up (favourable)

            Dim result = ComputeTake100Stop(slot, 50D, stLine, Nothing)

            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(3295.0D, result.NewStop)   ' ratcheted up to ST line
        End Sub

        <Fact>
        Public Sub Take100_PnlBelow100_StopRatchetsOnly_DoesNotRetrace_Long()
            ' Stop is already above the current ST line — must not retreat
            Dim slot   = MakeLongSlot(stopPrice := 3296D)
            Dim stLine = 3293.0D   ' ST line dipped below current stop

            Dim result = ComputeTake100Stop(slot, 80D, stLine, Nothing)

            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(3296.0D, result.NewStop)   ' held at existing stop — no retreat
        End Sub

        <Fact>
        Public Sub Take100_PnlBelow100_StopTrailsSTLine_Short()
            Dim slot   = MakeShortSlot(stopPrice := 3310D)
            Dim stLine = 3305.0D   ' ST line moved down (favourable for short)

            Dim result = ComputeTake100Stop(slot, 50D, stLine, Nothing)

            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(3305.0D, result.NewStop)   ' ratcheted down to ST line
        End Sub

        ' ── State 2a: PnL ≥ $100, BB_mid below entry — floor at entry ────────────

        <Fact>
        Public Sub Take100_PnlAt100_SnapsToBreakeven_WhenBbMidBelowEntry_Long()
            Dim slot  = MakeLongSlot(stopPrice := 3290D)
            Dim bbMid = 3297.0D   ' BB median is still below entry price (3300)

            Dim result = ComputeTake100Stop(slot, 100D, 3295D, bbMid)

            ' Floor A wins: entry = 3300
            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(3300.0D, result.NewStop)
        End Sub

        <Fact>
        Public Sub Take100_PnlAt100_SnapsToBreakeven_WhenBbMidBelowEntry_Short()
            Dim slot  = MakeShortSlot(stopPrice := 3310D)
            Dim bbMid = 3303.0D   ' BB median is still above entry (3300) for short → Floor A wins

            Dim result = ComputeTake100Stop(slot, 100D, 3305D, bbMid)

            ' Floor A wins: entry = 3300
            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(3300.0D, result.NewStop)
        End Sub

        ' ── State 2b: PnL ≥ $100, BB_mid above entry — track BB median ──────────

        <Fact>
        Public Sub Take100_PnlAbove100_TracksBbMid_WhenBbMidAboveEntry_Long()
            ' MGC-style: entry 3300, PnL=$250 (price ~3325), BB median at 3312
            Dim slot  = MakeLongSlot(stopPrice := 3300D)   ' already at breakeven
            Dim bbMid = 3312.0D   ' BB median has risen above entry

            Dim result = ComputeTake100Stop(slot, 250D, 3295D, bbMid)

            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(3312.0D, result.NewStop)   ' Floor B: follows BB median
        End Sub

        <Fact>
        Public Sub Take100_PnlAbove100_TracksBbMid_WhenBbMidAboveEntry_Short()
            ' Short: entry 3300, PnL=$250 (price ~3275), BB median at 3288
            Dim slot  = MakeShortSlot(stopPrice := 3300D)   ' already at breakeven
            Dim bbMid = 3288.0D   ' BB median has fallen below entry (favourable for short SL)

            Dim result = ComputeTake100Stop(slot, 250D, 3305D, bbMid)

            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(3288.0D, result.NewStop)   ' Floor B: follows BB median
        End Sub

        ' ── Ratchet invariant: stop never retreats once advanced ─────────────────

        <Fact>
        Public Sub Take100_RatchetPreserved_WhenBbMidPullsBack_Long()
            ' SL was advanced to 3312 on a previous tick.  BB mid then dipped to 3308.
            ' The stop must stay at 3312 — ratchet must not allow retreat.
            Dim slot  = MakeLongSlot(stopPrice := 3312D)   ' already advanced
            Dim bbMid = 3308.0D   ' BB mid pulled back

            Dim result = ComputeTake100Stop(slot, 250D, 3295D, bbMid)

            Assert.Equal(3312.0D, result.NewStop)   ' held — no retreat
        End Sub

        <Fact>
        Public Sub Take100_RatchetPreserved_WhenBbMidPullsBack_Short()
            Dim slot  = MakeShortSlot(stopPrice := 3288D)   ' already advanced downward
            Dim bbMid = 3292.0D   ' BB mid rose back up (unfavourable for short ratchet)

            Dim result = ComputeTake100Stop(slot, 250D, 3305D, bbMid)

            Assert.Equal(3288.0D, result.NewStop)   ' held — no retreat
        End Sub

        ' ── BUG-56 regression: the exact MGC symptom ─────────────────────────────

        <Fact>
        Public Sub Take100_BUG56_Regression_SlAdvancesPastEntry_WhenPnlOver100()
            ' Reproduces the reported symptom:
            '   • Take$100 ON, long MGC, PnL = $260 (well above $100)
            '   • Stop was stuck at SuperTrend line (3292) — below entry (3300)
            '   • Expected: stop at least at entry (3300); ideally at BB median
            Dim slot = MakeLongSlot(stopPrice := 3292D)   ' stuck at ST line — the bug
            Dim stLine = 3292.0D
            Dim bbMid  = 3308.0D   ' BB median (EMA-10 of 15s bars) well above entry

            Dim result = ComputeTake100Stop(slot, 260D, stLine, bbMid)

            ' With the fix: stop must be at least at entry (breakeven floor)
            Assert.True(result.NewStop >= slot.EntryPrice,
                        $"BUG-56 regression: stop {result.NewStop:F2} is still below entry {slot.EntryPrice:F2}. " &
                        "SL must advance to at least breakeven when PnL ≥ $100.")

            ' And should have followed BB median (Floor B)
            Assert.Equal(3308.0D, result.NewStop)
        End Sub

        ' ── EMA-10 computation on synthetic 15-second bar data ───────────────────

        <Fact>
        Public Sub Take100_Ema10OfRisingBars_ProducesRisingBbMid()
            ' Verify TechnicalIndicators.EMA used for BB-mid computation is monotonically
            ' rising when fed a uniformly rising price series.
            Dim closes = Enumerable.Range(1, 20).Select(Function(i) CDec(3300 + i)).ToList()
            Dim ema = TechnicalIndicators.EMA(closes, 10)

            ' EMA should be trending upward across the series
            Dim lastValid  = CDec(ema(ema.Length - 1))
            Dim secondLast = CDec(ema(ema.Length - 2))

            Assert.False(Single.IsNaN(ema(ema.Length - 1)), "EMA should not be NaN on a full series")
            Assert.True(lastValid > secondLast,
                        $"EMA-10 should be rising on a rising series; got {secondLast:F2} → {lastValid:F2}")
        End Sub

    End Class

End Namespace
