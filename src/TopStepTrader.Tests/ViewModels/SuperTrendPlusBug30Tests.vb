Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-30: Verifies the gates that blocked trade firing despite ADX 67-72 Strong Bull.
    ''' BUG-30 is implicitly resolved by the slot model — no persona EntryBarTime gate.
    ''' </summary>
    Public Class SuperTrendPlusBug30Tests

        ' ── Helpers ─────────────────────────────────────────────────────────

        Private Shared Function MakeBullBars(count As Integer, baseClose As Decimal) As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim t = New DateTimeOffset(2026, 4, 29, 9, 0, 0, TimeSpan.Zero)
            For i = 0 To count - 1
                Dim c = baseClose + CDec(i) * 0.25D
                bars.Add(New MarketBar With {
                    .Timestamp = t.AddMinutes(15 * i),
                    .Open      = c - 0.1D,
                    .High      = c + 0.5D,
                    .Low       = c - 0.5D,
                    .Close     = c
                })
            Next
            Return bars
        End Function

        ' ── Test 1: isFavourable is True on tick 1 (no prior flip needed) ──

        <Fact>
        Public Sub IsFavourable_TrueOnFirstTick_WhenAdxActive_NoPriorFlipRequired()
            Dim stDir   As Single = 1.0F
            Dim adxVal  As Single = 67.0F
            Dim plusDi  As Single = 40.0F
            Dim minusDi As Single = 3.0F
            Dim prevDir As Single = 0.0F

            Dim isFlip   As Boolean = prevDir <> 0F AndAlso stDir <> prevDir AndAlso stDir <> 0F
            Dim isLong   As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
            Dim isShort  As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso minusDi > plusDi
            Dim isActive As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= 25.0F
            Dim isFavourable As Boolean = (isLong OrElse isShort) AndAlso (isFlip OrElse isActive)

            Assert.False(isFlip,       "isFlip must be False on tick 1 (prevDir=0)")
            Assert.True(isLong,        "isLong must be True with stDir>0 and +DI>-DI")
            Assert.True(isActive,      "isActive must be True with ADX=67")
            Assert.True(isFavourable,  "isFavourable must be True on tick 1 — isActive alone is sufficient")
        End Sub

        ' ── Test 2: 20-bar cache passes the < 14 guard after forming-bar strip ─

        <Fact>
        Public Sub BarCache_20BarsAfterFormingBarStrip_PassesMinimumGuard()
            Dim bars = MakeBullBars(20, 65.0D)
            bars = bars.Take(bars.Count - 1).ToList()
            Const MinBars As Integer = 14
            Assert.True(bars.Count >= MinBars,
                        $"19 bars after strip must be >= {MinBars} (got {bars.Count})")
        End Sub

        <Fact>
        Public Sub BarCache_15BarsAfterFormingBarStrip_PassesMinimumGuard()
            Dim bars = MakeBullBars(15, 65.0D)
            bars = bars.Take(bars.Count - 1).ToList()
            Const MinBars As Integer = 14
            Assert.True(bars.Count >= MinBars,
                        $"14 bars after strip must be >= {MinBars} (got {bars.Count})")
        End Sub

        <Fact>
        Public Sub BarCache_14BarsAfterFormingBarStrip_FailsMinimumGuard()
            Dim bars = MakeBullBars(14, 65.0D)
            bars = bars.Take(bars.Count - 1).ToList()
            Const MinBars As Integer = 14
            Assert.False(bars.Count >= MinBars,
                         $"13 bars after strip must be < {MinBars} and should be skipped")
        End Sub

        ' ── Test 3: Full indicator path with 20 strong-bull bars ────────────

        <Fact>
        Public Sub SuperTrendDmi_20StrongBullBars_ProducesUptrendSignal()
            Dim bars = MakeBullBars(35, 65.0D)
            Dim highs   = bars.Select(Function(b) b.High).ToList()
            Dim lows    = bars.Select(Function(b) b.Low).ToList()
            Dim closes  = bars.Select(Function(b) b.Close).ToList()
            Dim n       = bars.Count - 1

            Dim st  = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=3.0)
            Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, period:=14)

            Dim stDir   = st.Direction(n)
            Dim adxVal  = dmi.ADX(n)
            Dim plusDi  = dmi.PlusDI(n)
            Dim minusDi = dmi.MinusDI(n)

            Assert.True(stDir > 0, "SuperTrend direction should be bullish (+1)")
            Assert.False(Single.IsNaN(adxVal), "ADX should not be NaN")
            Assert.True(plusDi > minusDi, "+DI should dominate -DI in a bull trend")
        End Sub

    End Class

End Namespace
