Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-51 / follow-up fix: phase-ladder transitions (Initial → Breakeven → ProfitLock)
    ''' must be driven by the last *closed* 15-second bar, NOT by the live price.
    ''' Using the live price allowed intra-bar spikes to snap the SL to Breakeven within
    ''' seconds of entry before any bar confirmed the move — causing premature exits.
    ''' These tests pin the corrected behaviour at the ScalperExitManager service contract:
    '''   • Phase advances when the last closed bar's close reaches the trigger.
    '''   • A live price spike that does NOT appear in the last closed bar cannot advance phase.
    '''   • The live currentPrice parameter is still used for the exit-cross check only.
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

        ' ── Test 1: closed-bar close below BE trigger keeps slot in Initial ─
        ' Last bar close = 100 + 29*0.10 = 102.9 (0.29R) — below the BE trigger of 105 (0.5R).
        ' currentPrice = 104 is ABOVE the bar close but must not affect the phase decision.

        <Fact>
        Public Sub Evaluate_StaleClose_KeepsSlotInInitial()
            Dim slot  = MakeLongSlot()
            Dim state As New ScalperState()
            ' Bars end at 102.9 — below the Breakeven trigger (105 = entry+0.5R with R=10).
            Dim bars  = MakeUptrendBars(startPrice:=100D, stepSize:=0.10D, count:=30)
            ' currentPrice above bar close — must be ignored for phase ladder.
            Dim staleClose As Decimal = 104D

            Dim decision = _scalper.Evaluate(slot, state, bars, staleClose, DefaultConfig())

            Assert.Equal(StopPhase.Initial, decision.NewPhase)
        End Sub

        ' ── Test 2: closed-bar close ≥ BE trigger advances slot to Breakeven ─
        ' Last bar close = 106 (0.6R above entry=100, R=10) crosses BreakevenTriggerR (0.5R=105).
        ' The scalper must advance the phase and raise the stop to at least entry (100).
        ' currentPrice is set LOWER than the trigger (104) to prove it is NOT what drives phase.

        <Fact>
        Public Sub Evaluate_LiveClose_AdvancesToBreakeven()
            Dim slot  = MakeLongSlot()
            Dim state As New ScalperState()
            ' Bars end at 106 — last closed bar close is at the BE trigger.
            ' startPrice chosen so that last bar = startPrice + 29*step = 106.
            Dim bars  = MakeUptrendBars(startPrice:=103.1D, stepSize:=0.10D, count:=30)
            ' currentPrice is BELOW the trigger to prove the phase is driven by the bar close.
            Dim liveClose As Decimal = 104D

            Dim decision = _scalper.Evaluate(slot, state, bars, liveClose, DefaultConfig())

            Assert.Equal(StopPhase.Breakeven, decision.NewPhase)
            Assert.True(decision.NewStop >= slot.EntryPrice,
                        $"Breakeven stop {decision.NewStop} must be ≥ entry {slot.EntryPrice}.")
        End Sub

        ' ── Test 3: low closed-bar close stays Initial; high closed-bar close advances ─
        ' Proves that only the last bar's close drives the phase ladder, not currentPrice.
        ' Both calls receive an identical currentPrice (200D, far above any SL) so only
        ' the bar close can explain the difference in outcome.

        <Fact>
        Public Sub Evaluate_StaleVsLive_OnSameBars_ProducesDifferentPhases()
            Dim cfg       = DefaultConfig()
            ' staleBars end at 103 (0.3R) — below BE trigger of 105.
            Dim staleBars = MakeUptrendBars(startPrice:=100.1D, stepSize:=0.10D, count:=30)
            ' liveBars end at 108 (0.8R) — above BE trigger of 105.
            Dim liveBars  = MakeUptrendBars(startPrice:=105.1D, stepSize:=0.10D, count:=30)
            Dim staleSlot = MakeLongSlot()
            Dim liveSlot  = MakeLongSlot()
            Dim staleState As New ScalperState()
            Dim liveState  As New ScalperState()
            ' currentPrice is the same for both — only the bar closes differ.
            Dim commonPrice As Decimal = 200D

            Dim staleResult = _scalper.Evaluate(staleSlot, staleState, staleBars, commonPrice, cfg)
            Dim liveResult  = _scalper.Evaluate(liveSlot,  liveState,  liveBars,  commonPrice, cfg)

            Assert.Equal(StopPhase.Initial,   staleResult.NewPhase)
            Assert.Equal(StopPhase.Breakeven, liveResult.NewPhase)
            Assert.True(liveResult.NewStop >= staleResult.NewStop,
                        $"Live-bar stop {liveResult.NewStop} must be ≥ stale-bar stop {staleResult.NewStop}.")
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
