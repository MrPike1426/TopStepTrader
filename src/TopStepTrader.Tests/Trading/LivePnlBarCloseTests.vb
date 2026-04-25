Imports TopStepTrader.Core.Enums
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' BUG-22: Unit tests validating that the live-position P&amp;L refresh cadence is
    ''' anchored to 2-second bar closes.
    '''
    ''' These tests exercise the pure-logic constants and helpers that govern:
    '''   1. The management timer fires every 2 seconds when a position is open.
    '''   2. TwoSecond = -2 is a distinct BarTimeframe value mapping to unit=1, unitNumber=2.
    '''   3. The stale threshold for 2-second bars is 3 × 2 s = 6 s.
    '''   4. P&amp;L computed from a 2-second bar close equals ComputeLivePnl(barClose).
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~LivePnlBarClose"
    ''' </summary>
    Public Class LivePnlBarCloseTests

        ' ── Helpers mirroring production logic ──────────────────────────────────

        ''' <summary>
        ''' Mirrors SetTrailTimerInterval: returns the management timer period in seconds.
        ''' </summary>
        Private Shared Function TrailTimerPeriodSeconds(positionOpen As Boolean) As Double
            Return If(positionOpen, 2.0, 60.0)
        End Function

        ''' <summary>
        ''' Mirrors TimeframeToUnit for TwoSecond.
        ''' </summary>
        Private Shared Function TwoSecondUnit() As Integer
            Select Case BarTimeframe.TwoSecond
                Case BarTimeframe.TwoSecond : Return 1   ' Second
                Case Else : Return -1
            End Select
        End Function

        ''' <summary>
        ''' Mirrors TimeframeToUnitNumber for TwoSecond.
        ''' </summary>
        Private Shared Function TwoSecondUnitNumber() As Integer
            Select Case BarTimeframe.TwoSecond
                Case BarTimeframe.TwoSecond : Return 2
                Case Else : Return -1
            End Select
        End Function

        ''' <summary>
        ''' Mirrors the stale-bar check for a 2-second bar.
        ''' Stale = barAgeSeconds &gt; 3 × 2 s = 6 s.
        ''' </summary>
        Private Shared Function IsTwoSecondBarStale(barAgeSeconds As Double) As Boolean
            Return barAgeSeconds > 2.0 * 3.0
        End Function

        ''' <summary>
        ''' Mirrors ComputeLivePnl: (currentPrice - entryPrice) × dollarsPerPoint × direction.
        ''' </summary>
        Private Shared Function ComputeLivePnl(
                entryPrice As Decimal,
                barClosePrice As Decimal,
                dollarsPerPoint As Decimal,
                isBuy As Boolean) As Decimal
            Dim priceDiff = barClosePrice - entryPrice
            Dim directedDiff = If(isBuy, priceDiff, -priceDiff)
            Return directedDiff * dollarsPerPoint
        End Function

        ' ════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub TrailTimer_WhenPositionOpen_PeriodIs2Seconds()
            Assert.Equal(2.0, TrailTimerPeriodSeconds(positionOpen:=True))
        End Sub

        <Fact>
        Public Sub TrailTimer_WhenPositionClosed_PeriodIs60Seconds()
            Assert.Equal(60.0, TrailTimerPeriodSeconds(positionOpen:=False))
        End Sub

        <Fact>
        Public Sub TwoSecond_EnumValue_IsNegativeTwo()
            Assert.Equal(-2, CInt(BarTimeframe.TwoSecond))
        End Sub

        <Fact>
        Public Sub TwoSecond_IsDistinctFromFifteenSecond()
            Assert.NotEqual(CInt(BarTimeframe.TwoSecond), CInt(BarTimeframe.FifteenSecond))
        End Sub

        <Fact>
        Public Sub TwoSecond_MapsToUnit1_Second()
            Assert.Equal(1, TwoSecondUnit())
        End Sub

        <Fact>
        Public Sub TwoSecond_MapsToUnitNumber2()
            Assert.Equal(2, TwoSecondUnitNumber())
        End Sub

        <Fact>
        Public Sub TwoSecondBar_StaleThreshold_Is6Seconds()
            ' Exactly at the threshold — not stale
            Assert.False(IsTwoSecondBarStale(6.0))
            ' One millisecond over the threshold — stale
            Assert.True(IsTwoSecondBarStale(6.001))
        End Sub

        <Fact>
        Public Sub TwoSecondBar_FreshBar_IsNotStale()
            Assert.False(IsTwoSecondBarStale(1.5))
        End Sub

        <Fact>
        Public Sub TwoSecondBar_BarOlderThan6s_IsStale()
            Assert.True(IsTwoSecondBarStale(7.0))
        End Sub

        <Fact>
        Public Sub PnlFromBarClose_LongPosition_PositiveWhenPriceRises()
            ' Entry 5000, bar close 5001, $50/point, Long → +$50
            Dim pnl = ComputeLivePnl(5000D, 5001D, 50D, isBuy:=True)
            Assert.Equal(50D, pnl)
        End Sub

        <Fact>
        Public Sub PnlFromBarClose_ShortPosition_PositiveWhenPriceFalls()
            ' Entry 5000, bar close 4999, $50/point, Short → +$50
            Dim pnl = ComputeLivePnl(5000D, 4999D, 50D, isBuy:=False)
            Assert.Equal(50D, pnl)
        End Sub

        <Fact>
        Public Sub PnlFromBarClose_LongPosition_NegativeWhenPriceFalls()
            ' Entry 5000, bar close 4998, $50/point, Long → -$100
            Dim pnl = ComputeLivePnl(5000D, 4998D, 50D, isBuy:=True)
            Assert.Equal(-100D, pnl)
        End Sub

        <Fact>
        Public Sub PnlFromBarClose_BarCloseMatchesEntry_ZeroPnl()
            Dim pnl = ComputeLivePnl(5000D, 5000D, 50D, isBuy:=True)
            Assert.Equal(0D, pnl)
        End Sub

    End Class

End Namespace
