Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-51 (rewritten for FEAT-47): exit management must operate on the
    ''' just-closed 15-second bar, not on a stale strategy-timeframe close.
    ''' Under the new architecture, ScalperExitManager.Evaluate consumes the
    ''' 15s bar list and uses its last close as currentPrice, so phase advances
    ''' (Initial → Breakeven → ProfitLock) react to live 15s price movement.
    ''' These tests pin that behaviour at the service contract.
    ''' </summary>
    Public Class SuperTrendPlusBug51Tests

        Private ReadOnly _scalper As ScalperExitManager =
            New ScalperExitManager(NullLogger(Of ScalperExitManager).Instance)

        Private Shared Function DefaultConfig() As ScalperConfig
            Return New ScalperConfig()
        End Function

        Private Shared Function MakeUptrendBars(startPrice As Decimal,
                                                stepSize As Decimal,
                                                count As Integer) As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim t = New DateTimeOffset(2025, 5, 4, 14, 0, 0, TimeSpan.Zero)
            For i = 0 To count - 1
                Dim closePrice = startPrice + stepSize * i
                bars.Add(New MarketBar With {
                    .ContractId = "ES",
                    .Timestamp  = t.AddSeconds(15 * i),
                    .Open       = closePrice - 0.05D,
                    .High       = closePrice + 0.10D,
                    .Low        = closePrice - 0.10D,
                    .Close      = closePrice,
                    .Volume     = 100
                })
            Next
            Return bars
        End Function

        ''' <summary>
        ''' LONG slot at entry=100, R=10 (stop at 90).
        ''' BreakevenTriggerR default = 0.5R → threshold profit = 5 (price = 105).
        ''' ProfitLockTriggerR default = 1.5R → threshold profit = 15 (price = 115).
        ''' </summary>
        Private Shared Function MakeLongSlot() As PositionSlot
            Return New PositionSlot With {
                .SlotIndex   = 0,
                .Instrument  = "ES",
                .Side        = "Buy",
                .IsOpen      = True,
                .EntryPrice  = 100.0D,
                .StopPrice   = 90.0D,
                .InitialRisk = 10.0D,
                .StopPhase   = StopPhase.Initial
            }
        End Function

        ' ── Test 1: stale strategy-TF close (below BE trigger) keeps slot Initial ─
        ' If we feed Evaluate a currentPrice = 104 (0.4R), the slot must stay in
        ' Initial — the same outcome the old buggy code produced when it received
        ' the stale strategy-TF bar close.

        <Fact>
        Public Sub Evaluate_StaleClose_KeepsSlotInInitial()
            Dim slot  = MakeLongSlot()
            Dim state As New ScalperState()
            Dim bars  = MakeUptrendBars(startPrice:=100D, stepSize:=0.10D, count:=30)
            Dim staleClose As Decimal = 104D

            Dim decision = _scalper.Evaluate(slot, state, bars, staleClose, DefaultConfig())

            Assert.Equal(StopPhase.Initial, decision.NewPhase)
        End Sub

        ' ── Test 2: live 15s close ≥ BE trigger advances slot to Breakeven ─
        ' currentPrice = 106 (0.6R) crosses BreakevenTriggerR; the scalper must
        ' advance the phase and pull the stop up to at least entry (100).

        <Fact>
        Public Sub Evaluate_LiveClose_AdvancesToBreakeven()
            Dim slot  = MakeLongSlot()
            Dim state As New ScalperState()
            Dim bars  = MakeUptrendBars(startPrice:=100D, stepSize:=0.10D, count:=30)
            Dim liveClose As Decimal = 106D

            Dim decision = _scalper.Evaluate(slot, state, bars, liveClose, DefaultConfig())

            Assert.Equal(StopPhase.Breakeven, decision.NewPhase)
            Assert.True(decision.NewStop >= slot.EntryPrice,
                        $"Breakeven stop {decision.NewStop} must be ≥ entry {slot.EntryPrice}.")
        End Sub

        ' ── Test 3: stale vs live within the same bar set — different outcomes ─
        ' Proves the only difference between the buggy and fixed paths is which
        ' price reaches the scalper service.

        <Fact>
        Public Sub Evaluate_StaleVsLive_OnSameBars_ProducesDifferentPhases()
            Dim bars      = MakeUptrendBars(startPrice:=100D, stepSize:=0.10D, count:=30)
            Dim cfg       = DefaultConfig()
            Dim staleSlot = MakeLongSlot()
            Dim liveSlot  = MakeLongSlot()
            Dim staleState As New ScalperState()
            Dim liveState  As New ScalperState()

            Dim staleResult = _scalper.Evaluate(staleSlot, staleState, bars, 103D, cfg)   ' 0.3R
            Dim liveResult  = _scalper.Evaluate(liveSlot,  liveState,  bars, 108D, cfg)   ' 0.8R

            Assert.Equal(StopPhase.Initial,   staleResult.NewPhase)
            Assert.Equal(StopPhase.Breakeven, liveResult.NewPhase)
            Assert.True(liveResult.NewStop >= staleResult.NewStop,
                        $"Live-price stop {liveResult.NewStop} must be ≥ stale-price stop {staleResult.NewStop}.")
        End Sub

        ' ── Test 4: ratchet-only invariant — stop never retreats across calls ─

        <Fact>
        Public Sub Evaluate_LongSlot_StopNeverRetreatsAcrossEvaluations()
            Dim slot  = MakeLongSlot()
            Dim state As New ScalperState()
            Dim bars  = MakeUptrendBars(startPrice:=100D, stepSize:=0.10D, count:=30)

            ' First evaluation pushes the slot to Breakeven.
            Dim first = _scalper.Evaluate(slot, state, bars, 106D, DefaultConfig())
            slot.StopPrice = first.NewStop
            slot.StopPhase = first.NewPhase

            ' Second evaluation with a retraced price must not lower the stop.
            Dim second = _scalper.Evaluate(slot, state, bars, 103D, DefaultConfig())

            Assert.True(second.NewStop >= slot.StopPrice,
                        $"Stop retreated from {slot.StopPrice} to {second.NewStop} — ratchet broken.")
        End Sub

    End Class

End Namespace
