Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-51: Phased stop was called with closes(n) (strategy-TF bar close), making it
    ''' blind to intra-bar price spikes. Fix: pass currentClose (15-second bar) instead.
    ''' Source: UAT 2026-05-04 trade 2542011663.
    ''' </summary>
    Public Class SuperTrendPlusBug51Tests

        Private ReadOnly _engine As ExitSignalEngine =
            New ExitSignalEngine(NullLogger(Of ExitSignalEngine).Instance)

        Private Shared Function DefaultConfig() As SuperTrendPlusConfig
            Return New SuperTrendPlusConfig()
        End Function

        ''' <summary>
        ''' Long slot at entry=100, R=10 (stop at 90), ST line at 95.
        ''' BreakevenTriggerR default = 0.5R → threshold at profit = 5.0 (price = 105).
        ''' </summary>
        Private Shared Function MakeLongSlot() As PositionSlot
            Return New PositionSlot With {
                .SlotIndex       = 0,
                .Instrument      = "ES",
                .Side            = "Buy",
                .IsOpen          = True,
                .EntryPrice      = 100.0D,
                .StopPrice       = 90.0D,
                .InitialRisk     = 10.0D,   ' R = 10
                .StopPhase       = StopPhase.Initial,
                .IsEarlyModeEntry = False
            }
        End Function

        ' ── Test 1: Root cause — closes(n) misses the intra-bar spike ─────────
        ' With the stale strategy-TF close (0.4R profit) the stop stays in Initial
        ' phase and only trails the ST line.  This is what the buggy code produced.

        <Fact>
        Public Sub ComputePhasedStop_WithStaleBarClose_StopStaysInitial()
            Dim slot   = MakeLongSlot()
            Dim stLine = 95.0D
            ' closes(n) = 104 → profit = 4 = 0.4R < BreakevenTriggerR (0.5R)
            Dim stalePrice = 104.0D

            Dim result = _engine.ComputePhasedStop(slot, stalePrice, stLine, 0D, DefaultConfig())

            ' Initial phase: ratchet to ST line max(90, 95) = 95
            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(95.0D, result.NewStop)
        End Sub

        ' ── Test 2: Fix — currentClose sees the spike and advances to Breakeven ─
        ' The 15-second bar already shows 0.6R profit, crossing the 0.5R threshold.
        ' ComputePhasedStop must advance the stop to entry (breakeven).

        <Fact>
        Public Sub ComputePhasedStop_WithCurrentClose_AdvancesToBreakeven()
            Dim slot   = MakeLongSlot()
            Dim stLine = 95.0D
            ' currentClose = 106 → profit = 6 = 0.6R ≥ BreakevenTriggerR (0.5R)
            Dim livePrice = 106.0D

            Dim result = _engine.ComputePhasedStop(slot, livePrice, stLine, 0D, DefaultConfig())

            ' Breakeven phase: stop = entry = 100
            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(100.0D, result.NewStop)
        End Sub

        ' ── Test 3: Contrast — same bar, different price argument, different outcome ─
        ' Proves that the sole difference between old (bug) and new (fix) behaviour
        ' is which price is passed to ComputePhasedStop.

        <Fact>
        Public Sub ComputePhasedStop_SpikedIntraBar_LivePriceAdvances_StaleDoesNot()
            Dim slot   = MakeLongSlot()
            Dim stLine = 95.0D
            ' Spike is visible only in the 15s bar; strategy-TF bar has not closed yet.
            Dim staleBarClose = 103.0D   ' 0.3R — still Initial
            Dim liveBarClose  = 108.0D   ' 0.8R — crosses ProfitLockTriggerR (1.0R)? No, 0.8R < 1R.
            '                              Actually crosses BreakevenTriggerR (0.5R): advances to Breakeven.

            Dim staleResult = _engine.ComputePhasedStop(slot, staleBarClose, stLine, 0D, DefaultConfig())
            Dim liveResult  = _engine.ComputePhasedStop(slot, liveBarClose,  stLine, 0D, DefaultConfig())

            ' Old code (stale): Initial phase, stop trails ST line
            Assert.Equal(StopPhase.Initial, staleResult.Phase)

            ' New code (live): Breakeven phase, stop = entry
            Assert.Equal(StopPhase.Breakeven, liveResult.Phase)
            Assert.True(liveResult.NewStop > staleResult.NewStop,
                        $"Live-price stop {liveResult.NewStop} must exceed stale-price stop {staleResult.NewStop}")
        End Sub

        ' ── Test 4: Ratchet-only invariant — stop never retreats ──────────────
        ' Even if currentClose dips back after a spike, the stop must not retreat.

        <Fact>
        Public Sub ComputePhasedStop_CurrentClose_NeverRetreatsStop()
            Dim slot = MakeLongSlot()
            slot.StopPhase = StopPhase.Breakeven
            slot.StopPrice = 100.0D    ' already at breakeven
            Dim stLine = 95.0D

            ' Price fell back below breakeven threshold — profit = 0.3R
            Dim retracedPrice = 103.0D
            Dim result = _engine.ComputePhasedStop(slot, retracedPrice, stLine, 0D, DefaultConfig())

            ' Ratchet: stop must not drop below the recorded breakeven level
            Assert.True(result.NewStop >= slot.StopPrice,
                        $"Stop retreated from {slot.StopPrice} to {result.NewStop} — ratchet broken.")
        End Sub

    End Class

End Namespace
