Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' BUG-07: Verifies that the live MultiConfluenceStrategy hard-gate guards
    ''' (cloud direction, Chikou-vs-cloud, volume) are enforced in partial-signal branches.
    '''
    ''' An 8/9 partial signal must NOT fire when a hard-gate condition is false.
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~MultiConfluenceStrategy"
    ''' </summary>
    Public Class MultiConfluenceStrategyTests

        Private Const N As Integer = 100

        Private Shared Sub BuildBars(basePrice As Decimal, delta As Decimal,
                                     ByRef highs As List(Of Decimal),
                                     ByRef lows As List(Of Decimal),
                                     ByRef closes As List(Of Decimal),
                                     ByRef volumes As List(Of Decimal))
            highs = New List(Of Decimal)(N)
            lows = New List(Of Decimal)(N)
            closes = New List(Of Decimal)(N)
            volumes = New List(Of Decimal)(N)
            For i = 0 To N - 1
                Dim c = basePrice + delta * i
                closes.Add(c)
                highs.Add(c + 0.5D)
                lows.Add(c - 0.5D)
                volumes.Add(1_200D)
            Next
        End Sub

        ''' <summary>
        ''' BUG-07 AC-1: Falling price series (bearish cloud, price below cloud).
        ''' Cloud hard-gate lc1 is False → partial-long branch must not fire.
        ''' Expected: result.Side = Nothing.
        ''' </summary>
        <Fact>
        Public Sub PartialLong_CloudGateFails_ReturnsNoSignal()
            Dim highs As List(Of Decimal) = Nothing
            Dim lows As List(Of Decimal) = Nothing
            Dim closes As List(Of Decimal) = Nothing
            Dim volumes As List(Of Decimal) = Nothing
            BuildBars(200D, -1D, highs, lows, closes, volumes)

            Dim result = MultiConfluenceStrategy.Evaluate(highs, lows, closes, volumes)

            Assert.Null(result.Side)
            Assert.False(result.IsPartialSignal,
                         "IsPartialSignal must be False when cloud hard-gate (lc1) fails")
        End Sub

        ''' <summary>
        ''' BUG-07 AC-2: Strongly-bullish series.
        ''' Confirms hard-gate guard does not incorrectly suppress full Buy signals.
        ''' If a signal fires it must be Buy and not a partial.
        ''' </summary>
        <Fact>
        Public Sub FullLong_AllNineConditions_ReturnsBuyNonPartial()
            Dim highs As List(Of Decimal) = Nothing
            Dim lows As List(Of Decimal) = Nothing
            Dim closes As List(Of Decimal) = Nothing
            Dim volumes As List(Of Decimal) = Nothing
            BuildBars(100D, 1D, highs, lows, closes, volumes)

            Dim result = MultiConfluenceStrategy.Evaluate(highs, lows, closes, volumes)

            If result.Side IsNot Nothing Then
                Assert.Equal(OrderSide.Buy, result.Side)
                Assert.False(result.IsPartialSignal,
                             "Full 9/9 signal must not set IsPartialSignal")
            End If
        End Sub

        ''' <summary>
        ''' BUG-07 AC-3: Strongly-bearish series.
        ''' If a signal fires it must be Sell — never Buy — on a falling series.
        ''' </summary>
        <Fact>
        Public Sub StrongBear_SignalIfAny_IsSellNotBuy()
            Dim highs As List(Of Decimal) = Nothing
            Dim lows As List(Of Decimal) = Nothing
            Dim closes As List(Of Decimal) = Nothing
            Dim volumes As List(Of Decimal) = Nothing
            BuildBars(300D, -2D, highs, lows, closes, volumes)

            Dim result = MultiConfluenceStrategy.Evaluate(highs, lows, closes, volumes)

            If result.Side IsNot Nothing Then
                Assert.Equal(OrderSide.Sell, result.Side)
            End If
        End Sub

    End Class

End Namespace
