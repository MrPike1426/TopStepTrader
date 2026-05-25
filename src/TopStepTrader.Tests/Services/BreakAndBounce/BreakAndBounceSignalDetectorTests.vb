Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.BreakAndBounce
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Services.BreakAndBounce

    ''' <summary>
    ''' FEAT-62: Replay tests against the Friend <c>ComputeFromBars</c> seam.
    ''' Covers session-window gating, dir-state requirement, retest gate, candle
    ''' pattern gate, happy-path firing, and the SL-floor winner selection.
    ''' </summary>
    Public Class BreakAndBounceSignalDetectorTests

        Private Const Symbol As String = "MES"
        Private Const TickSize As Decimal = 0.25D
        Private Const PrevHigh As Decimal = 4500D
        Private Const PrevLow As Decimal = 4480D

        Private Shared Function FixtureConfig() As BreakAndBounceConfig
            Return New BreakAndBounceConfig() With {
                .EntryWindow = "0000-2400",
                .FlatWindow = "2330-2400",
                .MinimumStopDistanceTicks = 4,
                .MinimumStopAtrFraction = 0.5,
                .AtrLength = 14,
                .EnableLong = True,
                .EnableShort = True
            }
        End Function

        Private Shared Function MakeDetector(cfg As BreakAndBounceConfig, tracker As BreakoutStateTracker) As BreakAndBounceSignalDetector
            Return New BreakAndBounceSignalDetector(
                barIngestion:=Nothing,
                barCollection:=Nothing,
                dailyRange:=Nothing,
                config:=cfg,
                tracker:=tracker,
                logger:=NullLogger(Of BreakAndBounceSignalDetector).Instance)
        End Function

        Private Shared Function Bar(ts As DateTimeOffset, o As Decimal, h As Decimal, l As Decimal, c As Decimal) As MarketBar
            Return New MarketBar With {
                .ContractId = Symbol,
                .Timestamp = ts,
                .Timeframe = BarTimeframe.FiveMinute,
                .Open = o, .High = h, .Low = l, .Close = c
            }
        End Function

        Private Shared Function BuildSteadyRetestSeries(lastBarOverride As MarketBar,
                                                         secondLastOverride As MarketBar,
                                                         Optional baseClose As Decimal = 4495D) As List(Of MarketBar)
            ' 20 prior bars with a constant ~1-point range so the Wilder ATR(14) ≈ 1.0
            ' (well above the tick floor for MES). This keeps the SL floor predictable.
            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            Dim out As New List(Of MarketBar)
            For i = 0 To 19
                out.Add(Bar(ts, baseClose, baseClose + 0.5D, baseClose - 0.5D, baseClose))
                ts = ts.AddMinutes(5)
            Next
            out.Add(secondLastOverride)
            out.Add(lastBarOverride)
            Return out
        End Function

        Private Shared Function DailyRangeStruct() As DailyRange
            Return New DailyRange With {
                .PrevHigh = PrevHigh,
                .PrevLow = PrevLow,
                .SourceBarDate = New DateOnly(2026, 5, 19)
            }
        End Function

        ' ─── Rejection branches ──────────────────────────────────────────────

        <Fact>
        Public Sub OutsideEntryWindow_RejectsWithReason()
            Dim cfg = FixtureConfig()
            cfg.EntryWindow = "1900-2100"  ' deliberately not covering the fixture timestamp
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            Dim lastBar = Bar(ts, 4490D, 4502D, 4499D, 4501D)
            Dim prevBar = Bar(ts.AddMinutes(-5), 4490D, 4491D, 4489D, 4490D)
            Dim retest = BuildSteadyRetestSeries(lastBar, prevBar)
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4496D, 4505D, 4494D, 4504D)
            }

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.None, eval.Signal)
            Assert.Equal("Outside entry window", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub NoBreakoutBias_RejectsWithReason()
            Dim cfg = FixtureConfig()
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            ' 15m bar closes inside the prev-day range — no fresh breakout.
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4490D, 4498D, 4485D, 4492D)
            }
            Dim retest = BuildSteadyRetestSeries(
                Bar(ts, 4498D, 4500D, 4497D, 4499D),
                Bar(ts.AddMinutes(-5), 4498D, 4499D, 4497D, 4498D))

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.None, eval.Signal)
            Assert.Equal("No 15m breakout bias yet", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub NoRetest_RejectsWithReason()
            Dim cfg = FixtureConfig()
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            ' 15m breakout sets long bias…
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4496D, 4510D, 4495D, 4508D)
            }
            ' …but the 5m bar never tags prev high.
            Dim lastBar = Bar(ts, 4505D, 4510D, 4503D, 4509D)
            Dim prevBar = Bar(ts.AddMinutes(-5), 4504D, 4506D, 4503D, 4505D)
            Dim retest = BuildSteadyRetestSeries(lastBar, prevBar, baseClose:=4505D)

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.None, eval.Signal)
            Assert.Equal("No 5m retest of prev high", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub NoCandlePattern_RejectsAfterRetest()
            Dim cfg = FixtureConfig()
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4496D, 4510D, 4495D, 4508D)
            }
            ' 5m bar tags prev high and closes above it but is a wide-bodied bar — not a hammer,
            ' and curr.Open (4499) is NOT < prev.Low (4498) so strict engulfing also fails.
            Dim lastBar = Bar(ts, 4499D, 4506D, 4498D, 4505D)
            Dim prevBar = Bar(ts.AddMinutes(-5), 4499D, 4500D, 4498D, 4499D)
            Dim retest = BuildSteadyRetestSeries(lastBar, prevBar)

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.None, eval.Signal)
            Assert.Equal("No qualifying candle pattern", eval.RejectionReason)
            Assert.True(eval.RetestTagged)  ' confirms the retest predicate did pass
        End Sub

        <Fact>
        Public Sub LongEntriesDisabled_RejectsEvenWithFullChain()
            Dim cfg = FixtureConfig()
            cfg.EnableLong = False
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4496D, 4510D, 4495D, 4508D)
            }
            ' Hammer that retests prev high.
            Dim lastBar = Bar(ts, 4502D, 4502.5D, 4495D, 4501.5D)
            Dim prevBar = Bar(ts.AddMinutes(-5), 4500D, 4501D, 4499D, 4500D)
            Dim retest = BuildSteadyRetestSeries(lastBar, prevBar)

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.None, eval.Signal)
            Assert.Equal("Long entries disabled", eval.RejectionReason)
        End Sub

        ' ─── Happy paths ─────────────────────────────────────────────────────

        <Fact>
        Public Sub LongHappyPath_HammerOnRetestFiresBullish()
            Dim cfg = FixtureConfig()
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4496D, 4510D, 4495D, 4508D)
            }
            ' Strict hammer: body = 0.5, lower wick = 6.5 (>1), upper wick = 0.5 (≥ body) — fails.
            ' Make a sharper hammer: open ≈ close, deep lower wick, tiny upper wick.
            Dim lastBar = Bar(ts, o:=4501.75D, h:=4502D, l:=4495D, c:=4502D)
            ' body=0.25, lower=6.25 (>0.5), upper=0 (<0.25) → hammer.
            Dim prevBar = Bar(ts.AddMinutes(-5), 4500D, 4501D, 4499D, 4500D)
            Dim retest = BuildSteadyRetestSeries(lastBar, prevBar)

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.Bullish, eval.Signal)
            Assert.Equal("Hammer", eval.PatternHit)
            Assert.True(eval.SuggestedInitialStopPrice > 0D)
            Assert.True(eval.SuggestedInitialStopPrice < lastBar.Close,
                        "Long initial stop must sit below entry")
        End Sub

        <Fact>
        Public Sub ShortHappyPath_InvertedHammerOnRetestFiresBearish()
            Dim cfg = FixtureConfig()
            Dim tracker As New BreakoutStateTracker(cfg)
            Dim det = MakeDetector(cfg, tracker)

            Dim ts As New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            ' 15m closes below prev low → short bias.
            Dim breakout = New List(Of MarketBar) From {
                Bar(ts.AddMinutes(-30), 4485D, 4486D, 4470D, 4472D)
            }
            ' 5m bar tags prev low from below then closes back below it; inverted hammer
            ' (body small, upper wick long, lower wick small).
            Dim lastBar = Bar(ts, o:=4478.25D, h:=4485D, l:=4478D, c:=4478D)
            ' body=0.25, upper=6.75 (>0.5), lower=0 (<0.25) → inv-hammer.
            Dim prevBar = Bar(ts.AddMinutes(-5), 4480D, 4481D, 4479D, 4480D)
            Dim retest = BuildSteadyRetestSeries(lastBar, prevBar)

            Dim eval = det.ComputeFromBars(Symbol, TickSize, DailyRangeStruct(), retest, breakout)
            Assert.Equal(BreakAndBounceSignalSide.Bearish, eval.Signal)
            Assert.Equal("InvertedHammer", eval.PatternHit)
            Assert.True(eval.SuggestedInitialStopPrice > lastBar.Close,
                        "Short initial stop must sit above entry")
        End Sub

        ' ─── SL-floor selection (ComputeInitialStop) ────────────────────────

        <Fact>
        Public Sub StopFloor_LongTakesFurthestOfThreeFloors()
            Dim cfg = FixtureConfig()
            cfg.MinimumStopDistanceTicks = 2
            cfg.MinimumStopAtrFraction = 0.5
            ' Retest bar: range = 10 (tiny PDF cushion: range×0.2 = 2 below prevHigh = 4498)
            Dim retestBar = New MarketBar With {.Open = 4499D, .High = 4506D, .Low = 4496D, .Close = 4505D}
            Dim range = New DailyRange With {.PrevHigh = 4500D, .PrevLow = 4480D}

            ' Ticks floor: close - 2*0.25 = 4504.5  (closest to entry)
            ' ATR floor: close - 0.5*ATR — with ATR=10 → 4500 (mid)
            ' PDF stop: 4500 - 10*0.2 = 4498 (furthest)
            Dim atr14 As Decimal = 10D
            Dim result = BreakAndBounceSignalDetector.ComputeInitialStop(
                1, retestBar, range, atr14, 0.25D, cfg)
            Assert.Equal(4498D, result.StopPrice)
            Assert.Equal("PDF", result.Source)
        End Sub

        <Fact>
        Public Sub StopFloor_LongTicksFloorWinsWhenPdfTooClose()
            Dim cfg = FixtureConfig()
            cfg.MinimumStopDistanceTicks = 80      ' very wide ticks floor
            cfg.MinimumStopAtrFraction = 0.1       ' ATR floor too narrow
            Dim retestBar = New MarketBar With {.Open = 4500D, .High = 4501D, .Low = 4499D, .Close = 4501D}
            Dim range = New DailyRange With {.PrevHigh = 4500D, .PrevLow = 4480D}
            Dim atr14 As Decimal = 1D
            ' Ticks floor: close - 80*0.25 = 4481 (way below)
            ' PDF: 4500 - 2*0.2 = 4499.6 (very close)
            ' ATR: close - 0.1*1 = 4500.9
            Dim result = BreakAndBounceSignalDetector.ComputeInitialStop(
                1, retestBar, range, atr14, 0.25D, cfg)
            Assert.Equal(4481D, result.StopPrice)
            Assert.Equal("TicksFloor", result.Source)
        End Sub

        <Fact>
        Public Sub StopFloor_LongAtrFloorWinsWhenAtrLargest()
            Dim cfg = FixtureConfig()
            cfg.MinimumStopDistanceTicks = 4
            cfg.MinimumStopAtrFraction = 5.0
            Dim retestBar = New MarketBar With {.Open = 4500D, .High = 4502D, .Low = 4498D, .Close = 4501D}
            Dim range = New DailyRange With {.PrevHigh = 4500D, .PrevLow = 4480D}
            Dim atr14 As Decimal = 10D
            ' PDF: 4500 - 4*0.2 = 4499.2
            ' Ticks: 4501 - 4*0.25 = 4500
            ' ATR: 4501 - 5*10 = 4451  (furthest below)
            Dim result = BreakAndBounceSignalDetector.ComputeInitialStop(
                1, retestBar, range, atr14, 0.25D, cfg)
            Assert.Equal(4451D, result.StopPrice)
            Assert.Equal("AtrFloor", result.Source)
        End Sub

        <Fact>
        Public Sub StopFloor_ShortMirrorTakesFurthestAboveEntry()
            Dim cfg = FixtureConfig()
            cfg.MinimumStopDistanceTicks = 2
            cfg.MinimumStopAtrFraction = 0.5
            Dim retestBar = New MarketBar With {.Open = 4481D, .High = 4484D, .Low = 4474D, .Close = 4475D}
            Dim range = New DailyRange With {.PrevHigh = 4500D, .PrevLow = 4480D}
            Dim atr14 As Decimal = 10D
            ' PDF: 4480 + 10*0.2 = 4482  (furthest above entry 4475)
            ' Ticks: 4475 + 2*0.25 = 4475.5
            ' ATR: 4475 + 0.5*10 = 4480
            Dim result = BreakAndBounceSignalDetector.ComputeInitialStop(
                -1, retestBar, range, atr14, 0.25D, cfg)
            Assert.Equal(4482D, result.StopPrice)
            Assert.Equal("PDF", result.Source)
        End Sub

        ' ─── Wilder ATR sanity ───────────────────────────────────────────────

        <Fact>
        Public Sub ComputeWilderAtr_ReturnsZeroOnInsufficientData()
            Dim bars As New List(Of MarketBar) From {
                New MarketBar With {.High = 10D, .Low = 9D, .Close = 9.5D}
            }
            Assert.Equal(0D, BreakAndBounceSignalDetector.ComputeWilderAtr(bars, 14))
        End Sub

        <Fact>
        Public Sub ComputeWilderAtr_PositiveOnFlatSeries()
            Dim bars As New List(Of MarketBar)
            Dim baseClose As Decimal = 4500D
            For i = 0 To 20
                bars.Add(New MarketBar With {.High = baseClose + 1D, .Low = baseClose - 1D, .Close = baseClose})
            Next
            Dim atr = BreakAndBounceSignalDetector.ComputeWilderAtr(bars, 14)
            Assert.True(atr > 0D)
            Assert.True(atr <= 2D)
        End Sub

    End Class

End Namespace
