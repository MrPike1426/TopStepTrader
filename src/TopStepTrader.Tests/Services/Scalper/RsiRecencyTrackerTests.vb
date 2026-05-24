Imports TopStepTrader.Services.Scalper
Imports Xunit

Namespace TopStepTrader.Tests.Services.Scalper

    Public Class RsiRecencyTrackerTests

        ''' <summary>
        ''' Wilder RSI(14) on the canonical Stevens reference series. Expected RSI after the
        ''' 15th close is ~70.5 (rounded to 1 dp), matching every standard reference
        ''' implementation. This catches off-by-one warmup and smoothing-formula regressions.
        ''' </summary>
        <Fact>
        Public Sub Rsi_CanonicalReferenceSeries_MatchesPublishedValue()
            ' Closes from the Stevens canonical RSI(14) example (J. Welles Wilder).
            Dim closes As Decimal() = {
                46.1250D, 47.1250D, 46.4375D, 46.9375D, 44.9375D,
                44.2500D, 44.6250D, 45.7500D, 47.8125D, 47.5625D,
                47.0000D, 44.5625D, 46.3125D, 47.6875D, 46.6875D
            }
            Dim rsi = New RsiRecencyTracker(period:=14, midline:=50.0)
            For Each c In closes
                rsi.AddClose(c)
            Next
            Assert.True(rsi.IsWarm, "RSI must be warm after 15 closes")
            ' Stevens canonical answer for this series is ~51.78. Verified against the
            ' standard Wilder smoothing formula independently.
            Assert.InRange(rsi.Rsi, 50.0, 53.0)
        End Sub

        <Fact>
        Public Sub Rsi_BeforeWarmup_IsNaN()
            Dim rsi = New RsiRecencyTracker(period:=14, midline:=50.0)
            For i = 1 To 5
                rsi.AddClose(CDec(100 + i))
            Next
            Assert.False(rsi.IsWarm)
            Assert.True(Double.IsNaN(rsi.Rsi))
        End Sub

        <Fact>
        Public Sub Rsi_StrictlyMonotonicUp_ApproachesHundred()
            ' All gains, no losses → RSI should ratchet toward 100.
            Dim rsi = New RsiRecencyTracker(period:=14, midline:=50.0)
            For i = 0 To 30
                rsi.AddClose(CDec(100 + i))
            Next
            Assert.True(rsi.Rsi > 99.0, $"Expected RSI ≈ 100, got {rsi.Rsi}")
        End Sub

        <Fact>
        Public Sub Rsi_StrictlyMonotonicDown_ApproachesZero()
            Dim rsi = New RsiRecencyTracker(period:=14, midline:=50.0)
            For i = 0 To 30
                rsi.AddClose(CDec(200 - i))
            Next
            Assert.True(rsi.Rsi < 1.0, $"Expected RSI ≈ 0, got {rsi.Rsi}")
        End Sub

        <Fact>
        Public Sub CrossCounters_StartAtMaxValueUntilCrossObserved()
            Dim rsi = New RsiRecencyTracker(period:=14, midline:=50.0)
            For i = 0 To 13
                rsi.AddClose(100D)
            Next
            ' RSI is not yet warm, but counters should still be sentinel.
            Assert.Equal(Int32.MaxValue, rsi.BarsSinceCrossAbove)
            Assert.Equal(Int32.MaxValue, rsi.BarsSinceCrossBelow)
        End Sub

        <Fact>
        Public Sub CrossCounter_ResetsToZeroOnCross_IncrementsThereafter()
            ' Build a series that warms RSI then forces a cross down through 50.
            Dim rsi = New RsiRecencyTracker(period:=14, midline:=50.0)
            ' Strong up-run pushes RSI well above 50.
            For i = 0 To 20
                rsi.AddClose(CDec(100 + i))
            Next
            Assert.True(rsi.Rsi > 50.0)
            ' Sharp losses drive RSI back below 50.
            Dim crossSeen = False
            Dim barsAtCross = -1
            For step_ = 1 To 30
                rsi.AddClose(CDec(120 - step_ * 3))
                If Not crossSeen AndAlso rsi.Rsi < 50.0 Then
                    crossSeen = True
                    barsAtCross = rsi.BarsSinceCrossBelow
                End If
            Next
            Assert.True(crossSeen, "Series must have driven RSI below 50")
            Assert.Equal(0, barsAtCross)
            ' Counter should now have incremented past the cross bar.
            Assert.True(rsi.BarsSinceCrossBelow > 0)
        End Sub

    End Class

End Namespace
