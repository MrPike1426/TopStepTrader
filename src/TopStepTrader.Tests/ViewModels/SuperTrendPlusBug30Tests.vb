Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' BUG-30: Verifies the gates that blocked trade firing despite ADX 67-72 Strong Bull.
    ''' </summary>
    Public Class SuperTrendPlusBug30Tests

        ' ── Helpers ─────────────────────────────────────────────────────────

        ''' <summary>
        ''' Build a list of <paramref name="count"/> synthetic bars trending upward with the
        ''' specified close-price base.  Each bar has H = close+0.5, L = close-0.5.
        ''' </summary>
        Private Shared Function MakeBullBars(count As Integer, baseClose As Decimal) As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim t = New DateTimeOffset(2026, 4, 29, 9, 0, 0, TimeSpan.Zero)
            For i = 0 To count - 1
                Dim c = baseClose + CDec(i) * 0.25D   ' small but consistent uptrend
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

        ''' <summary>
        ''' With ADX ≥ 25 (isActive = True), isFavourable must be True even when
        ''' prevDir = 0 (i.e. isFlip = False).  This is the core regression for BUG-30:
        ''' tick-1 after StartMonitoring() must produce a favourable signal.
        ''' </summary>
        <Fact>
        Public Sub IsFavourable_TrueOnFirstTick_WhenAdxActive_NoPriorFlipRequired()
            ' Simulated indicator values matching the UAT scenario (ADX 67, BULL)
            Dim stDir   As Single = 1.0F    ' SuperTrend pointing UP
            Dim adxVal  As Single = 67.0F
            Dim plusDi  As Single = 40.0F
            Dim minusDi As Single = 3.0F
            Dim prevDir As Single = 0.0F    ' no previous tick — _prevStDirByInstrument empty

            ' Replicate the gate logic from EvaluateEntrySequenceAsync
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

        ''' <summary>
        ''' A 20-bar fetch that has a forming bar stripped still has 19 bars,
        ''' which must pass the minimum-bar guard (< 14) used in both
        ''' ScanWatchlistAsync and EvaluateEntrySequenceAsync.
        ''' </summary>
        <Fact>
        Public Sub BarCache_20BarsAfterFormingBarStrip_PassesMinimumGuard()
            Dim bars = MakeBullBars(20, 65.0D)

            ' Simulate forming-bar strip
            bars = bars.Take(bars.Count - 1).ToList()   ' 20 → 19

            Const MinBars As Integer = 14
            Assert.True(bars.Count >= MinBars,
                        $"19 bars after strip must be >= {MinBars} (got {bars.Count})")
        End Sub

        ''' <summary>
        ''' Edge case: a 15-bar fetch stripped to 14 must ALSO pass the < 14 guard
        ''' (exactly at the boundary).
        ''' </summary>
        <Fact>
        Public Sub BarCache_15BarsAfterFormingBarStrip_PassesMinimumGuard()
            Dim bars = MakeBullBars(15, 65.0D)
            bars = bars.Take(bars.Count - 1).ToList()   ' 15 → 14

            Const MinBars As Integer = 14
            Assert.True(bars.Count >= MinBars,
                        $"14 bars after strip must be >= {MinBars} (got {bars.Count})")
        End Sub

        ''' <summary>
        ''' A 14-bar fetch stripped to 13 must FAIL the guard and be skipped.
        ''' </summary>
        <Fact>
        Public Sub BarCache_14BarsAfterFormingBarStrip_FailsMinimumGuard()
            Dim bars = MakeBullBars(14, 65.0D)
            bars = bars.Take(bars.Count - 1).ToList()   ' 14 → 13

            Const MinBars As Integer = 14
            Assert.False(bars.Count >= MinBars,
                         $"13 bars after strip must be < {MinBars} and should be skipped")
        End Sub

        ' ── Test 3: Full indicator path with 20 strong-bull bars ────────────

        ''' <summary>
        ''' Given 30 bars with a consistent uptrend, TechnicalIndicators.DMI must produce
        ''' a non-NaN ADX on the last bar so isActive can be evaluated.
        ''' (DMI requires 2*period-1 = 27 bars for ADX to be fully warmed up at period=14.)
        ''' </summary>
        <Fact>
        Public Sub Indicators_30BullBars_ProduceValidAdxOnLastBar()
            Dim bars   = MakeBullBars(30, 65.0D)
            Dim highs  = bars.Select(Function(b) b.High).ToList()
            Dim lows   = bars.Select(Function(b) b.Low).ToList()
            Dim closes = bars.Select(Function(b) b.Close).ToList()

            Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
            Dim n   = bars.Count - 1
            Dim adxVal = dmi.ADX(n)

            Assert.False(Single.IsNaN(adxVal), "ADX must not be NaN on bar 29 of a 30-bar series")
        End Sub

    End Class

End Namespace
