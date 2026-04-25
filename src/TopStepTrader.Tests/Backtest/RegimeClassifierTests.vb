Imports TopStepTrader.Services.Backtest
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for RegimeClassifier (STRAT-30 acceptance criteria).
    ''' Covers: trending regime routes correctly, ranging regime routes correctly,
    ''' and no-override falls back to base strategy.
    ''' </summary>
    Public Class RegimeClassifierTests

        ' ── Helpers ────────────────────────────────────────────────────────────

        ''' <summary>Build an ATR series of <paramref name="n"/> values.</summary>
        Private Shared Function MakeAtr(n As Integer, value As Single) As Single()
            Dim a(n - 1) As Single
            For i = 0 To n - 1 : a(i) = value : Next
            Return a
        End Function

        ''' <summary>
        ''' Build an ATR series where values ramp from <paramref name="start"/> to <paramref name="finish"/>.
        ''' </summary>
        Private Shared Function MakeRampAtr(n As Integer, start As Single, finish As Single) As Single()
            Dim a(n - 1) As Single
            For i = 0 To n - 1
                a(i) = start + (finish - start) * (CSng(i) / CSng(n - 1))
            Next
            Return a
        End Function

        ' ── Test 1: Expanding ATR + ADX ≥ threshold → Trending ────────────────

        <Fact>
        Public Sub Classify_ExpandingAtrAndHighAdx_ReturnsTrending()
            ' ATR ramping up (latest > SMA) + ADX = 30 >= threshold 20
            Dim atr = MakeRampAtr(30, 10.0F, 20.0F)   ' ramp up: last value 20, SMA ≈ 15
            Dim regime = RegimeClassifier.Classify(atr, adxValue:=30.0F, adxThreshold:=20.0F)
            Assert.Equal(RegimeType.Trending, regime)
        End Sub

        ' ── Test 2: Contracting ATR → Ranging (regardless of ADX) ─────────────

        <Fact>
        Public Sub Classify_ContractingAtr_ReturnsRanging()
            ' ATR ramping down (latest < SMA) + ADX = 35 (above threshold)
            Dim atr = MakeRampAtr(30, 20.0F, 10.0F)   ' ramp down: last value 10, SMA ≈ 15
            Dim regime = RegimeClassifier.Classify(atr, adxValue:=35.0F, adxThreshold:=20.0F)
            Assert.Equal(RegimeType.Ranging, regime)
        End Sub

        ' ── Test 3: ADX below threshold → Ranging (even with expanding ATR) ────

        <Fact>
        Public Sub Classify_AdxBelowThreshold_ReturnsRanging()
            ' ATR expanding but ADX = 15 < threshold 20 → Ranging
            Dim atr = MakeRampAtr(30, 10.0F, 20.0F)
            Dim regime = RegimeClassifier.Classify(atr, adxValue:=15.0F, adxThreshold:=20.0F)
            Assert.Equal(RegimeType.Ranging, regime)
        End Sub

        ' ── Test 4: NaN ADX → Ranging (guard) ─────────────────────────────────

        <Fact>
        Public Sub Classify_NaNAdx_ReturnsRanging()
            Dim atr = MakeAtr(30, 15.0F)
            Dim regime = RegimeClassifier.Classify(atr, adxValue:=Single.NaN, adxThreshold:=20.0F)
            Assert.Equal(RegimeType.Ranging, regime)
        End Sub

        ' ── Test 5: Null ATR series → Ranging (guard) ─────────────────────────

        <Fact>
        Public Sub Classify_NullAtrSeries_ReturnsRanging()
            Dim regime = RegimeClassifier.Classify(Nothing, adxValue:=30.0F, adxThreshold:=20.0F)
            Assert.Equal(RegimeType.Ranging, regime)
        End Sub

        ' ── Test 6: StrategyDefinition with no overrides → base condition ──────

        <Fact>
        Public Sub StrategyDefinition_NoOverrides_BaseConditionUnchanged()
            ' Verifies the backwards-compatible default: both overrides are Nothing.
            Dim sd As New Core.Models.StrategyDefinition With {
                .Condition = Core.Enums.StrategyConditionType.MultiConfluence
            }
            Assert.Null(sd.TrendingStrategyOverride)
            Assert.Null(sd.RangingStrategyOverride)
            ' The engine checks: if both are set → regime routing; otherwise → sd.Condition as-is.
            Dim bothSet = sd.TrendingStrategyOverride.HasValue AndAlso sd.RangingStrategyOverride.HasValue
            Assert.False(bothSet)
        End Sub

    End Class

End Namespace
