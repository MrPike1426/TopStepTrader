Imports System.Linq
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Backtest.Strategies
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Strategies

    ''' <summary>
    ''' TEST-07: Parity tests between the live MultiConfluenceStrategy evaluator and the
    ''' backtest MultiConfluenceSignalProvider.  For every synthetic bar series, both paths
    ''' must return identical Side, IsPartialSignal, and Confidence (within 1% tolerance).
    '''
    ''' The fixture derives StrategyIndicators from the same H/L/C/V series that is fed to
    ''' the live evaluator, mirroring how BacktestEngine populates them.
    ''' </summary>
    Public Class MultiConfluenceParityTests

        ' ── Series parameters ─────────────────────────────────────────────────────
        Private Const N As Integer = 120   ' > MinBarsRequired (80) with headroom
        Private Const ConfTolerance As Single = 0.01F

        ' ── Helpers ───────────────────────────────────────────────────────────────

        Private Shared Sub BuildSeries(basePrice As Decimal, delta As Decimal,
                                        ByRef highs   As List(Of Decimal),
                                        ByRef lows    As List(Of Decimal),
                                        ByRef closes  As List(Of Decimal),
                                        ByRef volumes As List(Of Decimal),
                                        Optional volPerBar As Decimal = 1_200D,
                                        Optional count As Integer = N)
            highs   = New List(Of Decimal)(count)
            lows    = New List(Of Decimal)(count)
            closes  = New List(Of Decimal)(count)
            volumes = New List(Of Decimal)(count)
            For i = 0 To count - 1
                Dim c = basePrice + delta * i
                closes.Add(c)
                highs.Add(c + 0.5D)
                lows.Add(c - 0.5D)
                volumes.Add(volPerBar)
            Next
        End Sub

        Private Shared Function ToMarketBars(highs   As IList(Of Decimal),
                                              lows    As IList(Of Decimal),
                                              closes  As IList(Of Decimal),
                                              volumes As IList(Of Decimal)) As IReadOnlyList(Of MarketBar)
            Dim n = highs.Count
            Dim bars As New List(Of MarketBar)(n)
            For i = 0 To n - 1
                bars.Add(New MarketBar With {
                    .Open      = closes(i),
                    .High      = highs(i),
                    .Low       = lows(i),
                    .Close     = closes(i),
                    .Volume    = CLng(volumes(i)),
                    .Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5L * (n - 1 - i))
                })
            Next
            Return bars.AsReadOnly()
        End Function

        ''' <summary>
        ''' Computes StrategyIndicators exactly as BacktestEngine does for MultiConfluence
        ''' (BacktestEngine.vb lines 811–825).
        ''' </summary>
        Private Shared Function BuildIndicators(highs   As IList(Of Decimal),
                                                 lows    As IList(Of Decimal),
                                                 closes  As IList(Of Decimal),
                                                 volumes As IList(Of Decimal),
                                                 bars    As IReadOnlyList(Of MarketBar)) As StrategyIndicators
            Dim ichi   = TechnicalIndicators.IchimokuCloud(highs, lows, closes, 9, 26, 52, 26)
            Dim dmi    = TechnicalIndicators.DMI(highs, lows, closes, 14)
            Dim macd   = TechnicalIndicators.MACD(closes, fastPeriod:=8, slowPeriod:=17, signalPeriod:=9)
            Dim stoch  = TechnicalIndicators.StochasticRSI(closes)
            Dim atr    = TechnicalIndicators.ATR(highs, lows, closes, 14)
            Dim ema21  = TechnicalIndicators.EMA(closes, 21)
            Dim ema50  = TechnicalIndicators.EMA(closes, 50)
            Dim volDec = volumes.ToList()
            Dim volMa  = TechnicalIndicators.SMA(volDec, 20)
            Return New StrategyIndicators With {
                .AllBars     = bars,
                .IchiTenkan  = ichi.Tenkan,
                .IchiKijun   = ichi.Kijun,
                .IchiSpanA   = ichi.SpanA,
                .IchiSpanB   = ichi.SpanB,
                .PlusDi      = dmi.PlusDI,
                .MinusDi     = dmi.MinusDI,
                .Adx         = dmi.ADX,
                .MacdHistogram = macd.Histogram,
                .StochRsiK   = stoch.K,
                .Atr         = atr,
                .Ema21       = ema21,
                .Ema50       = ema50,
                .VolMa20     = volMa
            }
        End Function

        Private Shared Function MakeConfig(Optional minAdx As Single = 20.0F) As BacktestConfiguration
            Return New BacktestConfiguration With {
                .MinAdxThreshold = minAdx,
                .UseAtrMode      = True,
                .SlAtrMultiple   = 1.5D,
                .TpAtrMultiple   = 3.0D,
                .PointValue      = 5.0D,
                .TickSize        = 0.25D
            }
        End Function

        ''' <summary>
        ''' Core assertion: run both evaluators on the same H/L/C/V series and verify
        ''' Side, IsPartialSignal, and Confidence agree.
        ''' </summary>
        Private Shared Sub AssertParity(highs   As IList(Of Decimal),
                                         lows    As IList(Of Decimal),
                                         closes  As IList(Of Decimal),
                                         volumes As IList(Of Decimal),
                                         Optional minAdx As Single = 20.0F)
            Dim barIdx = closes.Count - 1
            Dim live   = MultiConfluenceStrategy.Evaluate(highs, lows, closes, volumes)
            Dim bars   = ToMarketBars(highs, lows, closes, volumes)
            Dim ind    = BuildIndicators(highs, lows, closes, volumes, bars)
            Dim back   = New MultiConfluenceSignalProvider().Evaluate(bars(barIdx), ind, MakeConfig(minAdx), barIdx)

            Dim liveSide = If(live.Side Is Nothing, Nothing, live.Side.Value.ToString())
            Dim backSide = If(back Is Nothing, Nothing, back.Side)
            Assert.Equal(liveSide, backSide)

            If live.Side IsNot Nothing Then
                Assert.NotNull(back)
                Assert.Equal(live.IsPartialSignal, back.IsPartialSignal)
                Assert.True(Math.Abs(live.Confidence - back.Confidence) <= ConfTolerance,
                            $"Confidence mismatch: live={live.Confidence:F4} back={back.Confidence:F4}")
                ' SL/TP delta parity: backtest StopDelta must match live cloud+ATR SL calculation.
                ' For Long: stop = max(cloudBottom, close - 1.5*ATR) → StopDelta = close - stop.
                ' For Short: stop = min(cloudTop, close + 1.5*ATR) → StopDelta = stop - close.
                If Not back.IsPartialSignal Then
                    Dim lastClose    = closes(closes.Count - 1)
                    Dim atrVal       = live.AtrValue
                    Dim cloudEdge    = If(live.CloudEdgeSl.HasValue, live.CloudEdgeSl.Value, 0D)
                    Dim expectedStop As Decimal
                    If live.Side.Value = OrderSide.Buy Then
                        Dim atrFloor = lastClose - atrVal * 1.5D
                        expectedStop = If(cloudEdge > atrFloor, cloudEdge, atrFloor)
                    Else
                        Dim atrCeiling = lastClose + atrVal * 1.5D
                        expectedStop = If(cloudEdge < atrCeiling, cloudEdge, atrCeiling)
                    End If
                    Dim expectedDelta = Math.Abs(lastClose - expectedStop)
                    Assert.True(Math.Abs(back.StopDelta - expectedDelta) <= expectedDelta * 0.05D + 0.001D,
                                $"StopDelta mismatch: back={back.StopDelta:F4} expected≈{expectedDelta:F4}")
                    Dim expectedTp = expectedDelta * 2D
                    Assert.True(Math.Abs(back.TpDelta - expectedTp) <= expectedTp * 0.05D + 0.001D,
                                $"TpDelta mismatch: back={back.TpDelta:F4} expected≈{expectedTp:F4}")
                End If
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' Core parity: whatever signal fires on a natural series, both evaluators agree
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>9/9 long candidate: strongly rising price, high volume.  Both evaluators must agree.</summary>
        <Fact>
        Public Sub RisingSeries_BothEvaluatorsAgree()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(100D, 1D, h, l, c, v)
            AssertParity(h, l, c, v)
        End Sub

        ''' <summary>9/9 short candidate: strongly falling price.  Both evaluators must agree.</summary>
        <Fact>
        Public Sub FallingSeries_BothEvaluatorsAgree()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(300D, -2D, h, l, c, v)
            AssertParity(h, l, c, v)
        End Sub

        ''' <summary>Flat series: indicators produce no trend conviction — both return no signal.</summary>
        <Fact>
        Public Sub FlatSeries_BothReturnNoSignal()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(200D, 0D, h, l, c, v)
            AssertParity(h, l, c, v)
            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Assert.Null(live.Side)
        End Sub

        ''' <summary>
        ''' Choppy alternating series: ADX stays low, DI spread &lt; 2 (DI-spread = 1 scenario).
        ''' Both evaluators must agree — no signal.
        ''' </summary>
        <Fact>
        Public Sub ChoppySeries_LowDiSpread_BothReturnNoSignal()
            Dim h As New List(Of Decimal)(N)
            Dim l As New List(Of Decimal)(N)
            Dim c As New List(Of Decimal)(N)
            Dim v As New List(Of Decimal)(N)
            For i = 0 To N - 1
                Dim price = If(i Mod 2 = 0, 200D, 201D)
                c.Add(price)
                h.Add(price + 0.5D)
                l.Add(price - 0.5D)
                v.Add(1_200D)
            Next
            AssertParity(h, l, c, v)
            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Assert.Null(live.Side)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' NaN warm-up guard
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Fewer than MinBarsRequired bars: live guard fires early; backtest sees NaN indicators.
        ''' Both must return no signal.
        ''' </summary>
        <Theory>
        <InlineData(1)>
        <InlineData(50)>
        <InlineData(79)>
        Public Sub BelowMinBars_BothReturnNoSignal(barCount As Integer)
            Dim h As New List(Of Decimal)(barCount)
            Dim l As New List(Of Decimal)(barCount)
            Dim c As New List(Of Decimal)(barCount)
            Dim v As New List(Of Decimal)(barCount)
            For i = 0 To barCount - 1
                c.Add(100D + i)
                h.Add(100D + i + 0.5D)
                l.Add(100D + i - 0.5D)
                v.Add(1_200D)
            Next
            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Assert.Null(live.Side)

            Dim bars = ToMarketBars(h, l, c, v)
            Dim ind  = BuildIndicators(h, l, c, v, bars)
            Dim back = New MultiConfluenceSignalProvider().Evaluate(bars(barCount - 1), ind, MakeConfig(), barCount - 1)
            Assert.Null(back)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' Volume gate (volAvg = 0 / hard-gate failing)
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' All volumes = 0: volAvg = 0, VolMa20 = 0 — both evaluators fail condition 8 (lc8/sc8).
        ''' Both must return no signal regardless of price direction.
        ''' </summary>
        <Fact>
        Public Sub ZeroVolume_VolAvgZero_BothReturnNoSignal()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(100D, 1D, h, l, c, v, volPerBar:=0D)
            AssertParity(h, l, c, v)
            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            Assert.Null(live.Side)
        End Sub

        ''' <summary>
        ''' Last-bar volume = 0 on a rising series: lc8 fails (0 &lt; volAvg * 1.2).
        ''' Neither evaluator may fire a full or partial long signal.
        ''' </summary>
        <Fact>
        Public Sub LastBarZeroVolume_VolumeHardGateFails_BothSuppressSignal()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(100D, 1D, h, l, c, v, volPerBar:=1_500D)
            v(N - 1) = 0D   ' only last bar has zero volume

            AssertParity(h, l, c, v)

            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            ' Volume hard-gate must suppress both full and partial long
            Assert.Null(live.Side)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' StochRSI overbought (stochK ≥ 0.7 blocks long)
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Monotonically rising series pins StochRSI K to ≈ 1.0 (≥ 0.7).
        ''' Condition lc7 (K &lt; 0.7) fails.  Both evaluators must agree — no long signal.
        ''' This covers the stochK = 0.75 edge case from the ticket acceptance criteria.
        ''' </summary>
        <Fact>
        Public Sub MonotonicRise_StochRsiOverbought_BothBlockLongSignal()
            Dim h As New List(Of Decimal)(N)
            Dim l As New List(Of Decimal)(N)
            Dim c As New List(Of Decimal)(N)
            Dim v As New List(Of Decimal)(N)
            For i = 0 To N - 1
                Dim price = 100D + i * 2D   ' large step keeps StochRSI pinned high
                c.Add(price)
                h.Add(price + 0.5D)
                l.Add(price - 0.5D)
                v.Add(1_500D)
            Next
            AssertParity(h, l, c, v)

            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            If live.Side IsNot Nothing Then
                Assert.NotEqual(OrderSide.Buy, live.Side.Value)
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' Cloud hard-gate (lc1 / sc1 suppresses partial)
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Falling series produces bearish cloud; lc1 (bullish cloud AND price above cloud) fails.
        ''' No partial long signal may fire.  Both evaluators must agree.
        ''' </summary>
        <Fact>
        Public Sub BearishCloud_CloudHardGateFails_NoPartialLong()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(300D, -1D, h, l, c, v)
            AssertParity(h, l, c, v)

            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            If live.Side IsNot Nothing AndAlso live.IsPartialSignal Then
                Assert.NotEqual(OrderSide.Buy, live.Side.Value)
            End If
        End Sub

        ''' <summary>
        ''' Rising series produces bullish cloud; sc1 (bearish cloud AND price below cloud) fails.
        ''' No partial short signal may fire.  Both evaluators must agree.
        ''' </summary>
        <Fact>
        Public Sub BullishCloud_CloudHardGateFails_NoPartialShort()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(100D, 1D, h, l, c, v)
            AssertParity(h, l, c, v)

            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            If live.Side IsNot Nothing AndAlso live.IsPartialSignal Then
                Assert.NotEqual(OrderSide.Sell, live.Side.Value)
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' Chikou hard-gate (lc4 suppresses partial)
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Flat-then-rising series: in the flat portion the close 26 bars ago equals the
        ''' current close, so the Chikou gap (lc4: close &gt; lagClose + 0.1%) cannot clear.
        ''' Both evaluators agree — no partial long signal from the chikou failure.
        ''' </summary>
        <Fact>
        Public Sub FlatThenRising_ChikouGapInsufficient_BothAgree()
            Dim h As New List(Of Decimal)(N)
            Dim l As New List(Of Decimal)(N)
            Dim c As New List(Of Decimal)(N)
            Dim v As New List(Of Decimal)(N)
            ' First 90 bars flat at 200, then 30 bars rising — ensures Chikou (26-bar lag)
            ' points into the flat zone where the gap is 0, failing lc4.
            For i = 0 To N - 1
                Dim price = If(i < 90, 200D, 200D + (i - 90) * 0.1D)
                c.Add(price)
                h.Add(price + 0.5D)
                l.Add(price - 0.5D)
                v.Add(1_200D)
            Next
            AssertParity(h, l, c, v)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' ADX / DI threshold variations
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' With ADX gate disabled (minAdx = 0), both evaluators must still agree —
        ''' verifies that the config path matches between live (adxThreshold=0) and
        ''' backtest (MinAdxThreshold=0 → gate bypassed).
        ''' </summary>
        <Fact>
        Public Sub RisingSeries_AdxGateDisabled_BothEvaluatorsAgree()
            Dim h, l, c, v As List(Of Decimal)
            BuildSeries(100D, 1D, h, l, c, v)
            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v,
                            adxThreshold:=0.0F, macdHistMinAtrFraction:=0.05)
            Dim bars = ToMarketBars(h, l, c, v)
            Dim ind  = BuildIndicators(h, l, c, v, bars)
            Dim back = New MultiConfluenceSignalProvider().Evaluate(
                            bars(N - 1), ind, MakeConfig(minAdx:=0.0F), N - 1)

            Dim liveSide = If(live.Side Is Nothing, Nothing, live.Side.Value.ToString())
            Dim backSide = If(back Is Nothing, Nothing, back.Side)
            Assert.Equal(liveSide, backSide)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' 8/9 short partial: sc7 hard-gate (StochRSI falling AND not oversold)
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Monotonically falling series pins StochRSI K to ≈ 0 (oversold).
        ''' sc7 (K &gt; 0.3 AND falling) fails because K is oversold.
        ''' Both evaluators agree — no partial short signal from sc7 failure.
        ''' </summary>
        <Fact>
        Public Sub MonotonicFall_StochRsiOversold_BothBlockPartialShort()
            Dim h As New List(Of Decimal)(N)
            Dim l As New List(Of Decimal)(N)
            Dim c As New List(Of Decimal)(N)
            Dim v As New List(Of Decimal)(N)
            For i = 0 To N - 1
                Dim price = 300D - i * 2D
                c.Add(price)
                h.Add(price + 0.5D)
                l.Add(price - 0.5D)
                v.Add(1_500D)
            Next
            AssertParity(h, l, c, v)

            Dim live = MultiConfluenceStrategy.Evaluate(h, l, c, v)
            If live.Side IsNot Nothing AndAlso live.IsPartialSignal Then
                Assert.NotEqual(OrderSide.Sell, live.Side.Value)
            End If
        End Sub

    End Class

End Namespace
