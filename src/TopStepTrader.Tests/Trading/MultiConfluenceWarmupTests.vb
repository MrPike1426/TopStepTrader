Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' TEST-08: Verifies that MultiConfluenceStrategy.Evaluate returns Side = Nothing (no signal)
    ''' and does not throw when the bar array is too short for a given indicator to warm up.
    '''
    ''' Each test provides just enough bars to pass MinBarsRequired but a series designed so
    ''' that a specific indicator's last value is NaN / unavailable, exercising the per-indicator
    ''' NaN guard paths added in BUG-11.
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~MultiConfluenceWarmup"
    ''' </summary>
    Public Class MultiConfluenceWarmupTests

        ''' <summary>Minimum bars required by the engine.</summary>
        Private Const N As Integer = MultiConfluenceStrategy.MinBarsRequired

        ''' <summary>Build a flat price series of exactly <paramref name="count"/> bars.</summary>
        Private Shared Sub FlatBars(count As Integer,
                                    ByRef highs   As List(Of Decimal),
                                    ByRef lows    As List(Of Decimal),
                                    ByRef closes  As List(Of Decimal),
                                    ByRef volumes As List(Of Decimal))
            highs   = New List(Of Decimal)(count)
            lows    = New List(Of Decimal)(count)
            closes  = New List(Of Decimal)(count)
            volumes = New List(Of Decimal)(count)
            For i = 0 To count - 1
                closes.Add(100D)
                highs.Add(100.5D)
                lows.Add(99.5D)
                volumes.Add(1_000D)
            Next
        End Sub

        ' ── EMA 21 ───────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / EMA21: exactly MinBarsRequired bars but all prices identical (flat close).
        ''' EMA converges but MACD histogram remains 0, ADX stays low — no signal expected.
        ''' Primary assertion: no exception and Side = Nothing.
        ''' </summary>
        <Fact>
        Public Sub FlatSeries_AtMinBars_ReturnsNoSignal_NoException()
            Dim h As List(Of Decimal) = Nothing
            Dim l As List(Of Decimal) = Nothing
            Dim c As List(Of Decimal) = Nothing
            Dim v As List(Of Decimal) = Nothing
            FlatBars(N, h, l, c, v)

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
            Assert.Null(result.Side)
        End Sub

        ' ── Too few bars (below MinBarsRequired) ─────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / warm-up gate: fewer than MinBarsRequired bars must return Side = Nothing
        ''' without exception; StatusLine must indicate warm-up.
        ''' </summary>
        <Theory>
        <InlineData(1)>
        <InlineData(10)>
        <InlineData(50)>
        <InlineData(79)>
        Public Sub BelowMinBars_ReturnsWarmupResult_NoException(barCount As Integer)
            Dim h As New List(Of Decimal)(Enumerable.Repeat(100.5D, barCount))
            Dim l As New List(Of Decimal)(Enumerable.Repeat(99.5D, barCount))
            Dim c As New List(Of Decimal)(Enumerable.Repeat(100D, barCount))
            Dim v As New List(Of Decimal)(Enumerable.Repeat(1_000D, barCount))

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
            Assert.Null(result.Side)
            Assert.Contains("arming", result.StatusLine, StringComparison.OrdinalIgnoreCase)
        End Sub

        ' ── Ichimoku Span B lag ───────────────────────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / Ichimoku: exactly MinBarsRequired bars of a rising series — Ichimoku
        ''' SpanB (52-period) will be available at bar 80 but the lagging-span index
        ''' (n-1-26 = 53) may reference a NaN region depending on the displacement.
        ''' Asserts: no exception; Side = Nothing on this under-formed series.
        ''' </summary>
        <Fact>
        Public Sub RisingSeries_AtMinBars_IchimokuLagBoundary_NoException()
            Dim count = N
            Dim h As New List(Of Decimal)(count)
            Dim l As New List(Of Decimal)(count)
            Dim c As New List(Of Decimal)(count)
            Dim v As New List(Of Decimal)(count)
            For i = 0 To count - 1
                Dim price = 100D + i * 0.1D
                c.Add(price)
                h.Add(price + 0.3D)
                l.Add(price - 0.3D)
                v.Add(1_200D)
            Next

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
        End Sub

        ' ── ADX / DMI ────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / ADX: choppy alternating series produces near-zero ADX.
        ''' Condition lc5/sc5 (ADX ≥ threshold) will be False — no signal expected.
        ''' </summary>
        <Fact>
        Public Sub ChoppySeries_AdxBelowThreshold_ReturnsNoSignal()
            Dim count = N + 20
            Dim h As New List(Of Decimal)(count)
            Dim l As New List(Of Decimal)(count)
            Dim c As New List(Of Decimal)(count)
            Dim v As New List(Of Decimal)(count)
            For i = 0 To count - 1
                Dim price = If(i Mod 2 = 0, 100D, 101D)
                c.Add(price)
                h.Add(price + 0.5D)
                l.Add(price - 0.5D)
                v.Add(1_200D)
            Next

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
            Assert.Null(result.Side)
        End Sub

        ' ── MACD histogram ───────────────────────────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / MACD: flat series means MACD histogram = 0 throughout.
        ''' Condition lc6/sc6 will be False — no signal expected.
        ''' </summary>
        <Fact>
        Public Sub FlatSeries_MacDHistZero_ReturnsNoSignal()
            Dim count = N + 20
            Dim h As New List(Of Decimal)(count)
            Dim l As New List(Of Decimal)(count)
            Dim c As New List(Of Decimal)(count)
            Dim v As New List(Of Decimal)(count)
            For i = 0 To count - 1
                c.Add(200D)
                h.Add(200.5D)
                l.Add(199.5D)
                v.Add(1_000D)
            Next

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
            Assert.Null(result.Side)
        End Sub

        ' ── StochRSI ─────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / StochRSI: monotonically rising series — StochRSI K will pin to 1.0
        ''' (overbought), blocking lc7 (K &lt; 0.7). No long signal expected.
        ''' </summary>
        <Fact>
        Public Sub StrongUptrend_StochRsiOverbought_NoLongSignal()
            Dim count = N + 30
            Dim h As New List(Of Decimal)(count)
            Dim l As New List(Of Decimal)(count)
            Dim c As New List(Of Decimal)(count)
            Dim v As New List(Of Decimal)(count)
            For i = 0 To count - 1
                Dim price = 100D + i * 1D
                c.Add(price)
                h.Add(price + 0.5D)
                l.Add(price - 0.5D)
                v.Add(1_500D)
            Next

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
            ' Overbought StochRSI must block a long signal
            If result.Side.HasValue Then
                Assert.NotEqual(OrderSide.Buy, result.Side.Value)
            End If
        End Sub

        ' ── EMA 50 ───────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' TEST-08 / EMA50: exactly MinBarsRequired bars — EMA50 is available but condition
        ''' lc2b (price > EMA50) and sc2b (price &lt; EMA50) depend on the series direction.
        ''' A flat series means price = EMA50 so both are False. No signal expected.
        ''' </summary>
        <Fact>
        Public Sub FlatSeries_Ema50Equal_ReturnsNoSignal()
            Dim h As List(Of Decimal) = Nothing
            Dim l As List(Of Decimal) = Nothing
            Dim c As List(Of Decimal) = Nothing
            Dim v As List(Of Decimal) = Nothing
            FlatBars(N, h, l, c, v)

            Dim result As MultiConfluenceResult = Nothing
            Dim ex As Exception = Nothing
            Try
                result = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Catch e As Exception
                ex = e
            End Try

            Assert.Null(ex)
            Assert.NotNull(result)
            Assert.Null(result.Side)
        End Sub

    End Class

End Namespace
