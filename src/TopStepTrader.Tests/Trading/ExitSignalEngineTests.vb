Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Microsoft.Extensions.Logging.Abstractions
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    Public Class ExitSignalEngineTests

        ' Helper: create a minimal ExitSignalEngine instance with no-op logger
        Private Shared Function MakeEngine() As ExitSignalEngine
            Return New ExitSignalEngine(NullLogger(Of ExitSignalEngine).Instance)
        End Function

        ' Helper: create a minimal open PositionSlot
        Private Shared Function MakeSlot(side As String,
                                         entryPrice As Decimal,
                                         initialRisk As Decimal,
                                         stopPrice As Decimal,
                                         phase As StopPhase) As PositionSlot
            Return New PositionSlot With {
                .Side = side,
                .EntryPrice = entryPrice,
                .InitialRisk = initialRisk,
                .StopPrice = stopPrice,
                .StopPhase = phase,
                .IsOpen = True
            }
        End Function

        ' ──────────────────────────────────────────────────────────────────────
        '  Initial phase — trailing the SuperTrend line (ratchet only)
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub Initial_Long_StopAdvancesWithStLine()
            Dim engine = MakeEngine()
            ' profit < 1R, so stays Initial
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=95D, phase:=StopPhase.Initial)
            ' ST line is now at 96 (above old stop of 95) — should advance
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=101D,
                                                  stLine:=96D, currentAtr:=1D)
            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(96D, result.NewStop)
        End Sub

        <Fact>
        Public Sub Initial_Long_StopDoesNotRetreat()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=97D, phase:=StopPhase.Initial)
            ' ST line drops below current stop — ratchet keeps the higher value
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=101D,
                                                  stLine:=96D, currentAtr:=1D)
            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(97D, result.NewStop) ' unchanged — stop never retreats
        End Sub

        <Fact>
        Public Sub Initial_Short_StopAdvancesWithStLine()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Sell", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=105D, phase:=StopPhase.Initial)
            ' ST line at 104 (lower than current stop of 105) — short stop should advance down
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=99D,
                                                  stLine:=104D, currentAtr:=1D)
            Assert.Equal(StopPhase.Initial, result.Phase)
            Assert.Equal(104D, result.NewStop)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  Breakeven phase — profit >= 1R → stop = entry + 0.5R
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub Breakeven_Long_StopAtEntryPlusHalfR()
            Dim engine = MakeEngine()
            ' profit = 1R exactly
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=95D, phase:=StopPhase.Initial)
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=102D,
                                                  stLine:=96D, currentAtr:=1D)
            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(101D, result.NewStop) ' entry + 0.5R = 100 + 1 = 101
        End Sub

        <Fact>
        Public Sub Breakeven_Short_StopAtEntryMinusHalfR()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Sell", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=105D, phase:=StopPhase.Initial)
            ' profit = 1R: price fell to 98
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=98D,
                                                  stLine:=104D, currentAtr:=1D)
            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(99D, result.NewStop) ' entry - 0.5R = 100 - 1 = 99
        End Sub

        <Fact>
        Public Sub Breakeven_Long_StopDoesNotRetreat()
            Dim engine = MakeEngine()
            ' Already at breakeven stop of 101
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=101D, phase:=StopPhase.Breakeven)
            ' Price still in breakeven zone (profit = 1.2R < 1.5R)
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=102.4D,
                                                  stLine:=99D, currentAtr:=1D)
            Assert.Equal(StopPhase.Breakeven, result.Phase)
            Assert.Equal(101D, result.NewStop) ' ratchet — no retreat
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  ProfitTrail phase — profit >= 1.5R → ATR-based trail
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub ProfitTrail_Long_StopAtCurrentPriceMinusAtr()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=101D, phase:=StopPhase.Breakeven)
            ' profit = 1.5R → enters ProfitTrail; price=103, ATR=1.5
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=103D,
                                                  stLine:=101D, currentAtr:=1.5D)
            Assert.Equal(StopPhase.ProfitTrail, result.Phase)
            Assert.Equal(101.5D, result.NewStop) ' 103 - 1.5 = 101.5
        End Sub

        <Fact>
        Public Sub ProfitTrail_Short_StopAtCurrentPricePlusAtr()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Sell", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=99D, phase:=StopPhase.Breakeven)
            ' profit = 1.5R: price=97, ATR=1.5
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=97D,
                                                  stLine:=99D, currentAtr:=1.5D)
            Assert.Equal(StopPhase.ProfitTrail, result.Phase)
            Assert.Equal(98.5D, result.NewStop) ' 97 + 1.5 = 98.5
        End Sub

        <Fact>
        Public Sub ProfitTrail_Long_RatchetHolds()
            Dim engine = MakeEngine()
            ' Stop already at 103; current trail would be 102 — must not retreat
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=103D, phase:=StopPhase.ProfitTrail)
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=104D,
                                                  stLine:=101D, currentAtr:=2D)
            ' New trail = 104 - 2 = 102 < 103 → keep 103
            Assert.Equal(103D, result.NewStop)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  Harvest phase — profit >= 2R → stop = entry + 1.5R
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub Harvest_Long_StopAtEntryPlusOnePointFiveR()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=101D, phase:=StopPhase.Breakeven)
            ' profit = 2R: price = 104
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=104D,
                                                  stLine:=101D, currentAtr:=1D)
            Assert.Equal(StopPhase.Harvest, result.Phase)
            Assert.Equal(103D, result.NewStop) ' entry + 1.5R = 100 + 3 = 103
        End Sub

        <Fact>
        Public Sub Harvest_Short_StopAtEntryMinusOnePointFiveR()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Sell", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=99D, phase:=StopPhase.Breakeven)
            ' profit = 2R: price = 96
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=96D,
                                                  stLine:=99D, currentAtr:=1D)
            Assert.Equal(StopPhase.Harvest, result.Phase)
            Assert.Equal(97D, result.NewStop) ' entry - 1.5R = 100 - 3 = 97
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  FreeRide phase — profit >= 3R → stop = entry + 2R
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub FreeRide_Long_StopAtEntryPlusTwoR()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=103D, phase:=StopPhase.Harvest)
            ' profit = 3R: price = 106
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=106D,
                                                  stLine:=103D, currentAtr:=1D)
            Assert.Equal(StopPhase.FreeRide, result.Phase)
            Assert.Equal(104D, result.NewStop) ' entry + 2R = 100 + 4 = 104
        End Sub

        <Fact>
        Public Sub FreeRide_Short_StopAtEntryMinusTwoR()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Sell", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=97D, phase:=StopPhase.Harvest)
            ' profit = 3R: price = 94
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=94D,
                                                  stLine:=97D, currentAtr:=1D)
            Assert.Equal(StopPhase.FreeRide, result.Phase)
            Assert.Equal(96D, result.NewStop) ' entry - 2R = 100 - 4 = 96
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  Ratchet invariant — stop never retreats once advanced
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub Ratchet_FreeRide_Long_StopNeverRetreats()
            Dim engine = MakeEngine()
            ' Stop is already at 104 (entry+2R); if price pulls back but stays > 3R,
            ' the FreeRide formula gives the same floor (not lower)
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=104D, phase:=StopPhase.FreeRide)
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=106.1D,
                                                  stLine:=103D, currentAtr:=1D)
            Assert.Equal(StopPhase.FreeRide, result.Phase)
            Assert.True(result.NewStop >= 104D, "Stop must never retreat below previous value")
        End Sub

        <Fact>
        Public Sub Ratchet_ProfitTrail_StopNeverRetreatsOnPullback()
            Dim engine = MakeEngine()
            ' Already advanced to 103 in ProfitTrail; price pulls back slightly
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=2D,
                                stopPrice:=103D, phase:=StopPhase.ProfitTrail)
            ' price=103.5, ATR=2 → would trail to 101.5 — below current 103 → keep 103
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=103.5D,
                                                  stLine:=101D, currentAtr:=2D)
            Assert.Equal(103D, result.NewStop)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  Edge cases — zero EntryPrice / zero InitialRisk
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub EdgeCase_ZeroEntryPrice_ReturnsOriginalStop()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Buy", entryPrice:=0D, initialRisk:=2D,
                                stopPrice:=95D, phase:=StopPhase.Initial)
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=100D,
                                                  stLine:=96D, currentAtr:=1D)
            Assert.Equal(95D, result.NewStop)
            Assert.Equal(StopPhase.Initial, result.Phase)
        End Sub

        <Fact>
        Public Sub EdgeCase_ZeroInitialRisk_ReturnsOriginalStop()
            Dim engine = MakeEngine()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, initialRisk:=0D,
                                stopPrice:=95D, phase:=StopPhase.Initial)
            Dim result = engine.ComputePhasedStop(slot, currentPrice:=100D,
                                                  stLine:=96D, currentAtr:=1D)
            Assert.Equal(95D, result.NewStop)
            Assert.Equal(StopPhase.Initial, result.Phase)
        End Sub

    End Class

End Namespace
