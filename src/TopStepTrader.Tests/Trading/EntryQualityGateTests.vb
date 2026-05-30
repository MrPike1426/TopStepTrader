Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    Public Class EntryQualityGateTests

        ' ──────────────────────────────────────────────────────────────────────
        '  F2 — BB position gate
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub BbPosition_Short_AllClosesAboveMedian_Blocked()
            ' 2026-05-18 MBT 08:38 entry signature: 4 prior green closes left price above
            ' the BB median when the SHORT signal fired. Default lookback N=2.
            Dim closes = New List(Of Single) From {77000F, 77010F, 77020F, 77030F}
            Dim median As Single = 76950F
            Dim result = EntryQualityGate.EvaluateBbPosition(closes, median, isLong:=False, lookbackBars:=2)
            Assert.Equal(EntryGateOutcome.BlockBbPosition, result.Outcome)
            Assert.Contains("BB median", result.Reason)
        End Sub

        <Fact>
        Public Sub BbPosition_Short_OneCloseBelowMedian_Allowed()
            ' Same setup but the most recent close pierced back through the median — gate clears.
            Dim closes = New List(Of Single) From {77000F, 77010F, 77020F, 76940F}
            Dim median As Single = 76950F
            Dim result = EntryQualityGate.EvaluateBbPosition(closes, median, isLong:=False, lookbackBars:=2)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub BbPosition_Long_AllClosesBelowMedian_Blocked()
            Dim closes = New List(Of Single) From {7400F, 7398F, 7396F, 7394F}
            Dim median As Single = 7410F
            Dim result = EntryQualityGate.EvaluateBbPosition(closes, median, isLong:=True, lookbackBars:=2)
            Assert.Equal(EntryGateOutcome.BlockBbPosition, result.Outcome)
        End Sub

        <Fact>
        Public Sub BbPosition_InsufficientBars_Allowed()
            Dim closes = New List(Of Single) From {7400F}
            Dim result = EntryQualityGate.EvaluateBbPosition(closes, 7395F, isLong:=False, lookbackBars:=2)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub BbPosition_NaNMedian_Allowed()
            Dim closes = New List(Of Single) From {7400F, 7398F}
            Dim result = EntryQualityGate.EvaluateBbPosition(closes, Single.NaN, isLong:=True, lookbackBars:=2)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  F3 — momentum-against gate
        ' ──────────────────────────────────────────────────────────────────────

        <Fact>
        Public Sub Momentum_Short_FourHigherCloses_NonFlip_Blocked()
            ' 2026-05-18 MBT 08:38: 4 consecutive higher closes ("green bars") before
            ' a SHORT re-entry. Default lookback K=4.
            Dim closes = New List(Of Single) From {76900F, 76950F, 76980F, 77000F, 77030F}
            Dim result = EntryQualityGate.EvaluateMomentumAgainst(closes, isLong:=False, isFlip:=False, lookbackBars:=4)
            Assert.Equal(EntryGateOutcome.BlockMomentumAgainst, result.Outcome)
            Assert.Contains("Momentum-against", result.Reason)
        End Sub

        <Fact>
        Public Sub Momentum_Short_FourHigherCloses_OnFlip_Allowed()
            ' Same bar pattern but stDir just flipped — F3 is exempt so genuine reversal entries fire.
            Dim closes = New List(Of Single) From {76900F, 76950F, 76980F, 77000F, 77030F}
            Dim result = EntryQualityGate.EvaluateMomentumAgainst(closes, isLong:=False, isFlip:=True, lookbackBars:=4)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub Momentum_Short_OneRedBarInWindow_Allowed()
            ' Three higher closes but the last move is flat — gate clears.
            Dim closes = New List(Of Single) From {76900F, 76950F, 76980F, 77000F, 77000F}
            Dim result = EntryQualityGate.EvaluateMomentumAgainst(closes, isLong:=False, isFlip:=False, lookbackBars:=4)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub Momentum_Long_FourLowerCloses_NonFlip_Blocked()
            Dim closes = New List(Of Single) From {7430F, 7425F, 7420F, 7415F, 7410F}
            Dim result = EntryQualityGate.EvaluateMomentumAgainst(closes, isLong:=True, isFlip:=False, lookbackBars:=4)
            Assert.Equal(EntryGateOutcome.BlockMomentumAgainst, result.Outcome)
        End Sub

        <Fact>
        Public Sub Momentum_InsufficientBars_Allowed()
            Dim closes = New List(Of Single) From {76900F, 76950F}
            Dim result = EntryQualityGate.EvaluateMomentumAgainst(closes, isLong:=False, isFlip:=False, lookbackBars:=4)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  F6 — entry-bar confirmation candle (flips only)
        ' ──────────────────────────────────────────────────────────────────────

        Private Shared Function MakeBar(t As Integer, o As Single, h As Single, l As Single, c As Single) As MarketBar
            Return New MarketBar With {
                .Timestamp = New DateTime(2026, 5, 18, 0, 0, 0).AddMinutes(t),
                .Open = o, .High = h, .Low = l, .Close = c, .Volume = 100
            }
        End Function

        <Fact>
        Public Sub Confirmation_Short_Flip_CloseBelowPriorLow_Allowed()
            Dim bars As New List(Of MarketBar) From {
                MakeBar(0, 77000F, 77050F, 76990F, 77040F),
                MakeBar(15, 77040F, 77060F, 76930F, 76925F) ' entry bar closes below prior low (76990)
            }
            Dim result = EntryQualityGate.EvaluateConfirmationCandle(bars, isLong:=False, isFlip:=True)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub Confirmation_Short_Flip_CloseAbovePriorLow_Blocked()
            Dim bars As New List(Of MarketBar) From {
                MakeBar(0, 77000F, 77050F, 76990F, 77040F),
                MakeBar(15, 77040F, 77060F, 77020F, 77030F) ' entry bar fails to break prior low
            }
            Dim result = EntryQualityGate.EvaluateConfirmationCandle(bars, isLong:=False, isFlip:=True)
            Assert.Equal(EntryGateOutcome.BlockConfirmationCandle, result.Outcome)
            Assert.Contains("Confirmation-candle", result.Reason)
        End Sub

        <Fact>
        Public Sub Confirmation_Long_Flip_CloseAbovePriorHigh_Allowed()
            Dim bars As New List(Of MarketBar) From {
                MakeBar(0, 7400F, 7420F, 7395F, 7398F),
                MakeBar(15, 7398F, 7440F, 7397F, 7430F) ' close (7430) > prior high (7420)
            }
            Dim result = EntryQualityGate.EvaluateConfirmationCandle(bars, isLong:=True, isFlip:=True)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub Confirmation_NonFlip_AlwaysAllowed()
            ' Same failing-candle pattern as above, but isFlip=False (re-entry) — F6 exempts it.
            Dim bars As New List(Of MarketBar) From {
                MakeBar(0, 77000F, 77050F, 76990F, 77040F),
                MakeBar(15, 77040F, 77060F, 77020F, 77030F)
            }
            Dim result = EntryQualityGate.EvaluateConfirmationCandle(bars, isLong:=False, isFlip:=False)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub Confirmation_InsufficientBars_Allowed()
            Dim bars As New List(Of MarketBar) From {MakeBar(0, 7400F, 7420F, 7395F, 7398F)}
            Dim result = EntryQualityGate.EvaluateConfirmationCandle(bars, isLong:=True, isFlip:=True)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  STRAT-40 F1 — BB-median relax-on-flip (F5-a)
        ' ──────────────────────────────────────────────────────────────────────

        ''' <summary>Builds 30 closes that slope steadily upward; with bbLookback=4 the
        ''' BB-mid slope is positive.</summary>
        Private Shared Function UpSlopeCloses() As IList(Of Single)
            Dim out As New List(Of Single)
            For i = 0 To 29
                out.Add(7400F + CSng(i) * 0.5F)
            Next
            Return out
        End Function

        ''' <summary>Steadily falling closes for a down-sloping BB-mid.</summary>
        Private Shared Function DownSlopeCloses() As IList(Of Single)
            Dim out As New List(Of Single)
            For i = 0 To 29
                out.Add(7500F - CSng(i) * 0.5F)
            Next
            Return out
        End Function

        <Fact>
        Public Sub BbMedianRelax_FlipPlusStrongAdx_Bypassed()
            ' Up-sloping BB-mid + SHORT flip + ADX 35 → relax path lets the entry through
            ' even though the slope opposes the SHORT direction. Mirrors the long-bias
            ' fix on the multi-week index rally.
            Dim closes = UpSlopeCloses()
            Dim result = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                closes, isLong:=False, isFlip:=True, adxValue:=35.0F,
                bbLookback:=4, strongAdxThreshold:=30.0F)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
            Assert.Contains("BB-median bypassed", result.Reason)
        End Sub

        <Fact>
        Public Sub BbMedianRelax_NoFlipOpposingSlope_Blocked()
            ' Up-sloping BB-mid + SHORT (no flip) → standard slope check fires and blocks.
            Dim closes = UpSlopeCloses()
            Dim result = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                closes, isLong:=False, isFlip:=False, adxValue:=35.0F,
                bbLookback:=4, strongAdxThreshold:=30.0F)
            Assert.Equal(EntryGateOutcome.BlockBbMedianSlope, result.Outcome)
            Assert.Contains("BB-median slope conflicts", result.Reason)
        End Sub

        <Fact>
        Public Sub BbMedianRelax_FlipButLowAdx_Blocked()
            ' Flip is present but ADX is below the relax threshold — the relax path does
            ' not apply, so the standard slope check still blocks.
            Dim closes = UpSlopeCloses()
            Dim result = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                closes, isLong:=False, isFlip:=True, adxValue:=22.0F,
                bbLookback:=4, strongAdxThreshold:=30.0F)
            Assert.Equal(EntryGateOutcome.BlockBbMedianSlope, result.Outcome)
        End Sub

        <Fact>
        Public Sub BbMedianRelax_SlopeAgrees_Allowed()
            ' Up-sloping BB-mid + LONG re-entry (no flip) → standard slope check passes.
            Dim closes = UpSlopeCloses()
            Dim result = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                closes, isLong:=True, isFlip:=False, adxValue:=20.0F,
                bbLookback:=4, strongAdxThreshold:=30.0F)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub BbMedianRelax_DownslopeSupportsShort_Allowed()
            ' Down-sloping BB-mid + SHORT → slope agrees so the gate passes.
            Dim closes = DownSlopeCloses()
            Dim result = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                closes, isLong:=False, isFlip:=False, adxValue:=20.0F,
                bbLookback:=4, strongAdxThreshold:=30.0F)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub BbMedianRelax_InsufficientBars_AllowedFailOpen()
            Dim closes As IList(Of Single) = New List(Of Single) From {7400F, 7401F, 7402F, 7403F}
            Dim result = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                closes, isLong:=True, isFlip:=False, adxValue:=25.0F,
                bbLookback:=4, strongAdxThreshold:=30.0F)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
            Assert.Contains("insufficient bars", result.Reason)
        End Sub

        ' ──────────────────────────────────────────────────────────────────────
        '  STRAT-40 F2 — lower-TF agreement (F5-b)
        ' ──────────────────────────────────────────────────────────────────────

        ''' <summary>Builds 30 lower-TF bars in a steady uptrend so SuperTrend direction
        ''' is +1 by the end.</summary>
        Private Shared Function UptrendLowerTfBars() As IList(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 29
                Dim close = 100.0F + 0.5F * i
                bars.Add(New MarketBar With {
                    .Timestamp = New DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(3 * i),
                    .Open = close - 0.05F,
                    .High = close + 0.10F,
                    .Low = close - 0.10F,
                    .Close = close,
                    .Volume = 1000
                })
            Next
            Return bars
        End Function

        ''' <summary>Steadily-falling lower-TF bars so SuperTrend direction is -1.</summary>
        Private Shared Function DowntrendLowerTfBars() As IList(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 29
                Dim close = 200.0F - 0.5F * i
                bars.Add(New MarketBar With {
                    .Timestamp = New DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(3 * i),
                    .Open = close + 0.05F,
                    .High = close + 0.10F,
                    .Low = close - 0.10F,
                    .Close = close,
                    .Volume = 1000
                })
            Next
            Return bars
        End Function

        <Fact>
        Public Sub LowerTf_UptrendAgreesWithLong_Allowed()
            Dim bars = UptrendLowerTfBars()
            Dim result = EntryQualityGate.EvaluateLowerTfAgreement(
                bars, isLong:=True, stPeriod:=10, stMultiplier:=3.0R)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
            Assert.Contains("Lower-TF agrees", result.Reason)
        End Sub

        <Fact>
        Public Sub LowerTf_UptrendDisagreesWithShort_Blocked()
            Dim bars = UptrendLowerTfBars()
            Dim result = EntryQualityGate.EvaluateLowerTfAgreement(
                bars, isLong:=False, stPeriod:=10, stMultiplier:=3.0R)
            Assert.Equal(EntryGateOutcome.BlockLowerTfDisagrees, result.Outcome)
            Assert.Contains("Lower-TF disagrees", result.Reason)
            Assert.Contains("defer", result.Reason)
        End Sub

        <Fact>
        Public Sub LowerTf_DowntrendAgreesWithShort_Allowed()
            Dim bars = DowntrendLowerTfBars()
            Dim result = EntryQualityGate.EvaluateLowerTfAgreement(
                bars, isLong:=False, stPeriod:=10, stMultiplier:=3.0R)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
        End Sub

        <Fact>
        Public Sub LowerTf_DowntrendDisagreesWithLong_Blocked()
            Dim bars = DowntrendLowerTfBars()
            Dim result = EntryQualityGate.EvaluateLowerTfAgreement(
                bars, isLong:=True, stPeriod:=10, stMultiplier:=3.0R)
            Assert.Equal(EntryGateOutcome.BlockLowerTfDisagrees, result.Outcome)
        End Sub

        <Fact>
        Public Sub LowerTf_InsufficientBars_AllowedFailOpen()
            Dim bars As IList(Of MarketBar) = New List(Of MarketBar) From {
                MakeBar(0, 100F, 101F, 99F, 100F),
                MakeBar(3, 100F, 102F, 99F, 101F)
            }
            Dim result = EntryQualityGate.EvaluateLowerTfAgreement(
                bars, isLong:=True, stPeriod:=10, stMultiplier:=3.0R)
            Assert.Equal(EntryGateOutcome.Allow, result.Outcome)
            Assert.Contains("insufficient bars", result.Reason)
        End Sub

    End Class

End Namespace
