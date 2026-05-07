Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-50 (rewritten for FEAT-47): exit management must never move a SHORT
    ''' slot's stop below entry, even when the 15s SuperTrend line sits on the
    ''' wrong side of price during the early bars of a trade. Under the new
    ''' architecture, "early-mode grace" is enforced by the view-model entry
    ''' gate; the ScalperExitManager itself enforces the ratchet-only invariant
    ''' that proves the BUG-50 scenario can no longer reproduce.
    ''' </summary>
    Public Class SuperTrendPlusBug50Tests

        Private ReadOnly _scalper As ScalperExitManager =
            New ScalperExitManager(NullLogger(Of ScalperExitManager).Instance)

        Private Shared Function DefaultConfig() As ScalperConfig
            Return New ScalperConfig()
        End Function

        ''' <summary>
        ''' Build N closed 15s bars for a SHORT-friendly downtrend from
        ''' <paramref name="startPrice"/> drifting down by <paramref name="step"/>
        ''' per bar. Range high/low are kept tight around close.
        ''' </summary>
        Private Shared Function MakeDowntrendBars(startPrice As Decimal,
                                                  stepSize As Decimal,
                                                  count As Integer,
                                                  Optional contract As String = "GOLD") As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim t = New DateTimeOffset(2025, 5, 4, 14, 0, 0, TimeSpan.Zero)
            Dim price = startPrice
            For i = 0 To count - 1
                Dim closePrice = price - stepSize * i
                bars.Add(New MarketBar With {
                    .ContractId = contract,
                    .Timestamp  = t.AddSeconds(15 * i),
                    .Open       = closePrice + 0.05D,
                    .High       = closePrice + 0.10D,
                    .Low        = closePrice - 0.10D,
                    .Close      = closePrice,
                    .Volume     = 100
                })
            Next
            Return bars
        End Function

        Private Shared Function MakeUptrendBars(startPrice As Decimal,
                                                stepSize As Decimal,
                                                count As Integer,
                                                Optional contract As String = "GOLD") As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim t = New DateTimeOffset(2025, 5, 4, 14, 0, 0, TimeSpan.Zero)
            Dim price = startPrice
            For i = 0 To count - 1
                Dim closePrice = price + stepSize * i
                bars.Add(New MarketBar With {
                    .ContractId = contract,
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

        Private Shared Function MakeShortSlot() As PositionSlot
            ' Mirrors the UAT trade 2542011663: SHORT GOLD, R ≈ 29.36
            Return New PositionSlot With {
                .SlotIndex   = 0,
                .Instrument  = "GOLD",
                .Side        = "Sell",
                .IsOpen      = True,
                .EntryPrice  = 4585.40D,
                .StopPrice   = 4614.76D,
                .InitialRisk = 4614.76D - 4585.40D,
                .StopPhase   = StopPhase.Initial
            }
        End Function

        ' ── Test 1: SHORT ratchet-only invariant — stop never moves above bracket ──
        ' Even when the 15s SuperTrend line is bullish (sitting BELOW price for a
        ' short, i.e. the wrong side), the scalper must not widen the SHORT stop.

        <Fact>
        Public Sub Evaluate_ShortSlot_BullishStLine_StopNeverWidensAboveBracket()
            Dim slot  = MakeShortSlot()
            Dim state As New ScalperState()
            ' Uptrend in 15s bars → SuperTrend will print bullish (line below price).
            ' Price is between entry and bracket SL — slot is in small drawdown.
            Dim bars  = MakeUptrendBars(startPrice:=4582D, stepSize:=0.20D, count:=30)
            Dim currentPrice = bars(bars.Count - 1).Close
            Dim originalStop = slot.StopPrice

            Dim decision = _scalper.Evaluate(slot, state, bars, currentPrice, DefaultConfig())

            ' SHORT ratchet: stop may only move DOWN (toward entry / profit). It
            ' must never exceed the original bracket SL — that is the invariant
            ' BUG-50 proved was violated by the old phased-stop block.
            Assert.True(decision.NewStop <= originalStop,
                        $"SHORT stop widened from {originalStop} to {decision.NewStop} — ratchet broken.")
        End Sub

        ' ── Test 2: SHORT in Initial — stop never falls below entry from ST line ──
        ' The original BUG-50 symptom: stop ended up BELOW entry on a short. The
        ' scalper's Initial-phase trail only follows the ST line when its
        ' direction agrees with the slot side, so a bullish ST cannot drag a
        ' SHORT slot's stop through entry.

        <Fact>
        Public Sub Evaluate_ShortSlot_InInitial_StopStaysAtOrAboveEntry()
            Dim slot  = MakeShortSlot()
            Dim state As New ScalperState()
            Dim bars  = MakeUptrendBars(startPrice:=4582D, stepSize:=0.20D, count:=30)
            Dim currentPrice = bars(bars.Count - 1).Close

            Dim decision = _scalper.Evaluate(slot, state, bars, currentPrice, DefaultConfig())

            Assert.True(decision.NewStop >= slot.EntryPrice,
                        $"SHORT stop {decision.NewStop} must remain at or above entry {slot.EntryPrice}.")
        End Sub

        ' ── Test 3: SHORT with confirmed bearish 15s ST — stop ratchets DOWN ──
        ' Once the 15s ST direction confirms the short, the Initial trail tightens
        ' the stop toward the ST line — but never beyond the ratchet.

        <Fact>
        Public Sub Evaluate_ShortSlot_BearishStLine_StopRatchetsDown()
            Dim slot  = MakeShortSlot()
            Dim state As New ScalperState()
            ' Downtrend in 15s bars → SuperTrend will print bearish (line above price).
            Dim bars  = MakeDowntrendBars(startPrice:=4584D, stepSize:=0.20D, count:=30)
            Dim currentPrice = bars(bars.Count - 1).Close
            Dim originalStop = slot.StopPrice

            Dim decision = _scalper.Evaluate(slot, state, bars, currentPrice, DefaultConfig())

            Assert.True(decision.NewStop <= originalStop,
                        $"SHORT stop should ratchet down from {originalStop}, got {decision.NewStop}.")
        End Sub

    End Class

End Namespace
