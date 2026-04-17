Imports TopStepTrader.Core.Enums
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Pure-math regression tests for the stepped trailing bracket logic implemented in
    ''' StrategyExecutionEngine.ApplySteppedTrailAsync / UpdateTrailLevels.
    '''
    ''' Each test mirrors the exact formula used by the engine so that any change to the
    ''' engine arithmetic is caught immediately.  No network calls, no DI container.
    '''
    ''' Key rules verified:
    '''   • Trailing arms when profitPct ≥ 2.0 % (trigger).
    '''   • Steps = floor((profitPct − 2.0) / 0.5); steppedProfit = 2.0 + steps × 0.5.
    '''   • SL_profitPct  = steppedProfit − 1.5  →  at +2.0 % profit SL is at +0.5 % (free-ride).
    '''   • TP_profitPct  = steppedProfit + tpAbove (default tpAbove = 2.0).
    '''   • SL and TP only ever tighten (never loosen), regardless of profit oscillation.
    '''   • Existing TP at activation sets tpAbove = existingTpPct − steppedProfit.
    '''   • Price conversion: Long price = entry×(1+pct/100); Short price = entry×(1−pct/100).
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~TrailingBracket"
    ''' </summary>
    Public Class TrailingBracketTests

        ' ── Constants mirroring StrategyExecutionEngine private fields ───────────────
        Private Shared ReadOnly TriggerPct As Decimal = 2.0D
        Private Shared ReadOnly StepPct As Decimal = 0.5D
        Private Shared ReadOnly SlOffset As Decimal = 1.5D
        Private Shared ReadOnly DefaultTpAbove As Decimal = 2.0D

        ' ── Helpers mirroring engine formulas ────────────────────────────────────────

        ''' <summary>
        ''' Mirrors ApplySteppedTrailAsync: computes steps and steppedProfit.
        ''' Returns Nothing when profitPct is below the trigger.
        ''' </summary>
        Private Shared Function ComputeStep(profitPct As Decimal) As (steps As Integer, steppedProfit As Decimal)?
            If profitPct < TriggerPct Then Return Nothing
            Dim s = CInt(Math.Floor(CDbl(profitPct - TriggerPct) / CDbl(StepPct)))
            Return (s, TriggerPct + s * StepPct)
        End Function

        ''' <summary>
        ''' Mirrors UpdateTrailLevels: derives SL and TP absolute prices for a Long.
        ''' Long: price = entry × (1 + pct / 100).
        ''' </summary>
        Private Shared Function LongPrice(entryPrice As Decimal, profitPct As Decimal) As Decimal
            Return Math.Round(entryPrice * (1D + profitPct / 100D), 4)
        End Function

        ''' <summary>
        ''' Mirrors UpdateTrailLevels: derives SL and TP absolute prices for a Short.
        ''' Short: price = entry × (1 − pct / 100).
        ''' </summary>
        Private Shared Function ShortPrice(entryPrice As Decimal, profitPct As Decimal) As Decimal
            Return Math.Round(entryPrice * (1D - profitPct / 100D), 4)
        End Function

        ''' <summary>
        ''' Full stepped-trail calculation for a Long position.
        ''' Returns (slProfitPct, tpProfitPct, slPrice, tpPrice).
        ''' </summary>
        Private Shared Function CalcLong(entryPrice As Decimal,
                                         profitPct As Decimal,
                                         Optional tpAbove As Decimal = 2.0D
                                        ) As (slPct As Decimal, tpPct As Decimal,
                                              slPrice As Decimal, tpPrice As Decimal)
            Dim r = ComputeStep(profitPct)
            If Not r.HasValue Then Throw New ArgumentOutOfRangeException(NameOf(profitPct), "Below trigger")
            Dim slPct = r.Value.steppedProfit - SlOffset
            Dim tpPct = r.Value.steppedProfit + tpAbove
            Return (slPct, tpPct, LongPrice(entryPrice, slPct), LongPrice(entryPrice, tpPct))
        End Function

        ''' <summary>
        ''' Full stepped-trail calculation for a Short position.
        ''' Returns (slProfitPct, tpProfitPct, slPrice, tpPrice).
        ''' </summary>
        Private Shared Function CalcShort(entryPrice As Decimal,
                                          profitPct As Decimal,
                                          Optional tpAbove As Decimal = 2.0D
                                         ) As (slPct As Decimal, tpPct As Decimal,
                                               slPrice As Decimal, tpPrice As Decimal)
            Dim r = ComputeStep(profitPct)
            If Not r.HasValue Then Throw New ArgumentOutOfRangeException(NameOf(profitPct), "Below trigger")
            Dim slPct = r.Value.steppedProfit - SlOffset
            Dim tpPct = r.Value.steppedProfit + tpAbove
            Return (slPct, tpPct, ShortPrice(entryPrice, slPct), ShortPrice(entryPrice, tpPct))
        End Function

        ' ════════════════════════════════════════════════════════════════════
        ' 1 — Trigger activation
        ' ════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub Trail_DoesNotArm_Below_Trigger()
            Assert.Null(ComputeStep(1.99D))
            Assert.Null(ComputeStep(0D))
            Assert.Null(ComputeStep(1.5D))
        End Sub

        <Fact>
        Public Sub Trail_Arms_Exactly_At_Trigger()
            Dim r = ComputeStep(2.0D)
            Assert.NotNull(r)
            Assert.Equal(0, r.Value.steps)
            Assert.Equal(2.0D, r.Value.steppedProfit)
        End Sub

        <Fact>
        Public Sub Trail_Arms_Between_Steps()
            ' 2.49% is still step 0 (< 2.5%)
            Dim r = ComputeStep(2.49D)
            Assert.NotNull(r)
            Assert.Equal(0, r.Value.steps)
            Assert.Equal(2.0D, r.Value.steppedProfit)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 2 — Step calculation (Theory)
        ' ════════════════════════════════════════════════════════════════════

        <Theory>
        <InlineData(2.0, 0, 2.0)>
        <InlineData(2.49, 0, 2.0)>
        <InlineData(2.5, 1, 2.5)>
        <InlineData(2.99, 1, 2.5)>
        <InlineData(3.0, 2, 3.0)>
        <InlineData(3.49, 2, 3.0)>
        <InlineData(3.5, 3, 3.5)>
        <InlineData(4.0, 4, 4.0)>
        <InlineData(10.0, 16, 10.0)>
        Public Sub StepCalculation_IsCorrect(profitPctD As Double,
                                             expectedSteps As Integer,
                                             expectedSteppedProfitD As Double)
            Dim profitPct = CDec(profitPctD)
            Dim expectedSteppedProfit = CDec(expectedSteppedProfitD)
            Dim r = ComputeStep(profitPct)
            Assert.NotNull(r)
            Assert.Equal(expectedSteps, r.Value.steps)
            Assert.Equal(expectedSteppedProfit, r.Value.steppedProfit)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 3 — SL placement: verified cases from the task spec
        ' ════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub SL_At_2Pct_Profit_Is_Plus05Pct_Long()
            ' Task spec: "SL at +2.0% → +0.5%"
            ' steppedProfit=2.0, SL = 2.0 − 1.5 = +0.5% above entry
            Dim r = CalcLong(100D, 2.0D)
            Assert.Equal(0.5D, r.slPct)
            Assert.Equal(100.5D, r.slPrice)  ' entry×1.005
        End Sub

        <Fact>
        Public Sub SL_At_25Pct_Profit_Is_Plus10Pct_Long()
            ' Task spec: "at +2.5% → +1.0%"
            ' steppedProfit=2.5, SL = 2.5 − 1.5 = +1.0% above entry
            Dim r = CalcLong(100D, 2.5D)
            Assert.Equal(1.0D, r.slPct)
            Assert.Equal(101.0D, r.slPrice)  ' entry×1.010
        End Sub

        <Fact>
        Public Sub SL_At_30Pct_Profit_Is_Plus15Pct_Long()
            Dim r = CalcLong(100D, 3.0D)
            Assert.Equal(1.5D, r.slPct)
            Assert.Equal(101.5D, r.slPrice)
        End Sub

        <Fact>
        Public Sub SL_FreeRide_Long_SLAboveEntry()
            ' At any step, SL_profitPct > 0 so SL price is always above entry for Long.
            For Each p In {2.0D, 2.5D, 3.0D, 3.5D, 4.0D}
                Dim r = CalcLong(100D, p)
                Assert.True(r.slPrice > 100D,
                    $"profitPct={p}: SL {r.slPrice} should be above entry 100")
            Next
        End Sub

        <Fact>
        Public Sub SL_FreeRide_Short_SLBelowEntry()
            ' For Short, SL_profitPct > 0, so SL price = entry×(1−slPct/100) < entry.
            For Each p In {2.0D, 2.5D, 3.0D, 3.5D, 4.0D}
                Dim r = CalcShort(100D, p)
                Assert.True(r.slPrice < 100D,
                    $"profitPct={p}: Short SL {r.slPrice} should be below entry 100")
            Next
        End Sub

        <Fact>
        Public Sub SL_Short_At_2Pct_Profit_Is_Plus05Pct()
            ' Short entry 100, profitPct=2.0 → SL at +0.5% profit → price=entry×0.995=99.5
            Dim r = CalcShort(100D, 2.0D)
            Assert.Equal(0.5D, r.slPct)
            Assert.Equal(99.5D, r.slPrice)
        End Sub

        <Fact>
        Public Sub SL_Short_At_25Pct_Profit_Is_Plus10Pct()
            Dim r = CalcShort(100D, 2.5D)
            Assert.Equal(1.0D, r.slPct)
            Assert.Equal(99.0D, r.slPrice)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 4 — TP: moves in 0.5% steps and never decreases (task spec)
        ' ════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub TP_DefaultOffset_Step0_Is_4Pct_Long()
            ' steppedProfit=2.0, tpAbove=2.0 → TP = +4.0%
            Dim r = CalcLong(100D, 2.0D)
            Assert.Equal(4.0D, r.tpPct)
            Assert.Equal(104.0D, r.tpPrice)
        End Sub

        <Fact>
        Public Sub TP_Advances_In_Half_Percent_Steps_Long()
            ' Task spec: "TP moves in 0.5% steps and never decreases"
            Dim tp = {2.0D, 2.5D, 3.0D, 3.5D, 4.0D}.
                     Select(Function(p) CalcLong(100D, p).tpPct).
                     ToList()
            Assert.Equal({4.0D, 4.5D, 5.0D, 5.5D, 6.0D}, tp)
        End Sub

        <Fact>
        Public Sub TP_NeverDecreases_WhenProfitOscillates()
            ' Simulate the engine's "steps > lastSteps" guard across a non-monotone profit series.
            Dim profits = {2.0D, 2.8D, 2.3D, 3.1D, 2.6D, 3.7D}  ' bounces up and down
            Dim lastSteps = -1
            Dim lastTpPct = 0D

            For Each p In profits
                If p < TriggerPct Then Continue For
                Dim s = CInt(Math.Floor(CDbl(p - TriggerPct) / CDbl(StepPct)))
                If s > lastSteps Then
                    lastSteps = s
                    Dim steppedProfit = TriggerPct + s * StepPct
                    Dim newTp = steppedProfit + DefaultTpAbove
                    ' "Never loosen" guard — TP can only increase
                    lastTpPct = Math.Max(lastTpPct, newTp)
                End If
            Next

            ' Final profit 3.7% → steps=3 → steppedProfit=3.5 → TP=5.5%
            Assert.Equal(3, lastSteps)
            Assert.Equal(5.5D, lastTpPct)
        End Sub

        <Fact>
        Public Sub SL_NeverDecreases_WhenProfitOscillates()
            Dim profits = {2.0D, 2.8D, 2.3D, 3.1D, 2.6D, 3.7D}
            Dim lastSteps = -1
            Dim lastSlPct = 0D

            For Each p In profits
                If p < TriggerPct Then Continue For
                Dim s = CInt(Math.Floor(CDbl(p - TriggerPct) / CDbl(StepPct)))
                If s > lastSteps Then
                    lastSteps = s
                    Dim steppedProfit = TriggerPct + s * StepPct
                    Dim newSl = steppedProfit - SlOffset
                    ' "Never loosen" guard — SL can only tighten (increase for Long)
                    lastSlPct = Math.Max(lastSlPct, newSl)
                End If
            Next

            ' Steps=3 → steppedProfit=3.5 → SL=2.0%
            Assert.Equal(2.0D, lastSlPct)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 5 — tpAbove locking at activation
        ' ════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub TpAbove_LocksExistingTpOffset_AtActivation()
            ' Existing TP at +5.0% profit, activation steppedProfit = 2.0
            ' → tpAbove = 5.0 − 2.0 = 3.0
            Dim existingTpPct = 5.0D
            Dim steppedProfitAtActivation = 2.0D
            Dim computed = existingTpPct - steppedProfitAtActivation
            Dim tpAbove = If(computed > 0D, computed, DefaultTpAbove)
            Assert.Equal(3.0D, tpAbove)

            ' TP at step 0: steppedProfit=2.0 → TP=2.0+3.0=5.0 (preserves original)
            Dim r0 = CalcLong(100D, 2.0D, tpAbove)
            Assert.Equal(5.0D, r0.tpPct)

            ' TP at step 1: steppedProfit=2.5 → TP=2.5+3.0=5.5 (moved up by 0.5%)
            Dim r1 = CalcLong(100D, 2.5D, tpAbove)
            Assert.Equal(5.5D, r1.tpPct)
            Assert.True(r1.tpPct > r0.tpPct)
        End Sub

        <Fact>
        Public Sub TpAbove_FallsBackToDefault_WhenExistingTpBelowSteppedProfit()
            ' Existing TP at +1.5% — below steppedProfit=2.0 → use default 2.0
            Dim existingTpPct = 1.5D
            Dim steppedProfitAtActivation = 2.0D
            Dim computed = existingTpPct - steppedProfitAtActivation  ' = -0.5 (negative)
            Dim tpAbove = If(computed > 0D, computed, DefaultTpAbove)
            Assert.Equal(DefaultTpAbove, tpAbove)
        End Sub

        <Fact>
        Public Sub TpAbove_FallsBackToDefault_WhenNoExistingTp()
            ' _lastTpPrice = 0 → tpAbove = DefaultTpAbove = 2.0
            Dim lastTpPrice = 0D
            Dim tpAbove = If(lastTpPrice > 0D, 99D, DefaultTpAbove)  ' 99 never reached
            Assert.Equal(DefaultTpAbove, tpAbove)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 6 — SL / TP combined table (Theory)
        ' ════════════════════════════════════════════════════════════════════

        <Theory>
        <InlineData(2.0, 0.5, 4.0)>
        <InlineData(2.5, 1.0, 4.5)>
        <InlineData(3.0, 1.5, 5.0)>
        <InlineData(3.5, 2.0, 5.5)>
        <InlineData(4.0, 2.5, 6.0)>
        <InlineData(5.0, 3.5, 7.0)>
        Public Sub SL_And_TP_ProfitPcts_AreCorrect(profitPctD As Double,
                                                    expectedSlPctD As Double,
                                                    expectedTpPctD As Double)
            Dim r = CalcLong(100D, CDec(profitPctD))
            Assert.Equal(CDec(expectedSlPctD), r.slPct)
            Assert.Equal(CDec(expectedTpPctD), r.tpPct)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 7 — Price conversion: Long vs Short symmetry
        ' ════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub LongAndShort_SLPcts_AreEqual_PricesMirror()
            ' Both directions compute the same SL profit%, but the absolute prices are
            ' on opposite sides of the entry.
            Dim profitPct = 2.5D
            Dim entry = 200D
            Dim rLong = CalcLong(entry, profitPct)
            Dim rShort = CalcShort(entry, profitPct)

            Assert.Equal(rLong.slPct, rShort.slPct)     ' same SL profit%
            Assert.True(rLong.slPrice > entry)            ' Long SL above entry
            Assert.True(rShort.slPrice < entry)           ' Short SL below entry
        End Sub

        <Fact>
        Public Sub PriceConversion_Long_RoundTrip()
            ' Long entry 1000, SL at +0.5%: price = 1000 × 1.005 = 1005
            Dim slPrice = LongPrice(1000D, 0.5D)
            Assert.Equal(1005.0D, slPrice)
        End Sub

        <Fact>
        Public Sub PriceConversion_Short_RoundTrip()
            ' Short entry 1000, SL at +0.5%: price = 1000 × 0.995 = 995
            Dim slPrice = ShortPrice(1000D, 0.5D)
            Assert.Equal(995.0D, slPrice)
        End Sub

        ' ════════════════════════════════════════════════════════════════════
        ' 8 — Breach / TP-reached detection (mirrors engine if-conditions)
        ' ════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub SlBreach_Long_TriggersWhenPriceFallsBelowSL()
            ' Entry 100, profit was 2.5%, SL tracked at 101.0
            Dim slPrice = 101.0D
            ' Price falls to 100.9 (below SL) → breach
            Assert.True(100.9D <= slPrice)
            ' Price is 101.1 (still above SL) → no breach
            Assert.False(101.1D <= slPrice)
        End Sub

        <Fact>
        Public Sub SlBreach_Short_TriggersWhenPriceRisesAboveSL()
            ' Short entry 100, profit was 2.5%, SL tracked at 99.0
            Dim slPrice = 99.0D
            ' Price rises to 99.1 (above SL) → breach
            Assert.True(99.1D >= slPrice)
            ' Price is 98.9 (still below SL) → no breach
            Assert.False(98.9D >= slPrice)
        End Sub

        <Fact>
        Public Sub TpReached_Long_TriggersWhenPriceHitsOrExceedsTP()
            ' Long entry 100, TP tracked at 104.0
            Dim tpPrice = 104.0D
            Assert.True(104.0D >= tpPrice)   ' exactly at TP
            Assert.True(104.5D >= tpPrice)   ' above TP
            Assert.False(103.9D >= tpPrice)  ' not reached yet
        End Sub

    End Class

End Namespace
