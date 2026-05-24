Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Scalper
Imports Xunit

Namespace TopStepTrader.Tests.Services.Scalper

    ''' <summary>
    ''' FEAT-64: Detector replay tests against synthetic 5-minute bar fixtures. These
    ''' bypass the bar-I/O layer by calling the Friend <c>ComputeFromBars</c> seam,
    ''' so tests run in-process with no Sqlite or HTTP dependencies.
    ''' </summary>
    Public Class UltimateScalperSignalDetectorTests

        Private Shared Function MakeBar(ts As DateTimeOffset, c As Decimal,
                                        Optional spread As Decimal = 1D,
                                        Optional vol As Long = 1000) As MarketBar
            Return New MarketBar With {
                .ContractId = "TEST",
                .Timestamp = ts,
                .Timeframe = BarTimeframe.FiveMinute,
                .Open = c,
                .High = c + spread,
                .Low = c - spread,
                .Close = c,
                .Volume = vol
            }
        End Function

        Private Shared Function MakeDetector(cfg As UltimateScalperConfig) As UltimateScalperSignalDetector
            Return New UltimateScalperSignalDetector(
                barIngestion:=Nothing, barCollection:=Nothing,
                config:=cfg, logger:=NullLogger(Of UltimateScalperSignalDetector).Instance)
        End Function

        ''' <summary>
        ''' Hand-computed 208-bar series that drives:
        '''   bars   0..188 (189 bars): smooth +2 climb 100 -> 476 — keeps RSI ≈ 100 throughout
        '''   bars 189..193 (5 bars):   -10 each (sharp dip) — drives RSI down through 50 (cross below)
        '''   bars 194..199 (6 bars):   +10 each (rally back) — drives RSI back above 50 (cross above ~bar 196)
        '''   bars 200..207 (8 bars):   -12 each (sharp pullback) — drives RSI below 30 by bar 207
        ''' Final close = 390. Computed MA200 ≈ 310 (close > MA). Cross-above at bar 196 → 11 bars
        ''' from the final signal bar (207), which requires <c>MaxBarsSinceMidlineCross >= 11</c>
        ''' to fire — the test config sets that to 15 for fixture constructibility.
        ''' </summary>
        Private Shared Function BuildBullishFixture() As List(Of MarketBar)
            Dim bars = New List(Of MarketBar)()
            Dim ts = New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)

            ' Phase 1: smooth +2 climb.
            For i = 0 To 188
                bars.Add(MakeBar(ts, 100D + CDec(i) * 2D))
                ts = ts.AddMinutes(5)
            Next
            ' Phase 2A: -10 each (5 bars). Previous close = 476; first bar = 466.
            Dim px As Decimal = 476D
            For i = 1 To 5
                px -= 10D
                bars.Add(MakeBar(ts, px))
                ts = ts.AddMinutes(5)
            Next
            ' Phase 2B: +10 each (6 bars). Previous close = 426; first bar = 436.
            For i = 1 To 6
                px += 10D
                bars.Add(MakeBar(ts, px))
                ts = ts.AddMinutes(5)
            Next
            ' Phase 3: -12 each (8 bars). Previous close = 486; first bar = 474; final = 390.
            For i = 1 To 8
                px -= 12D
                bars.Add(MakeBar(ts, px))
                ts = ts.AddMinutes(5)
            Next
            Return bars
        End Function

        <Fact>
        Public Sub WarmupSeries_ReturnsNoSignal_AndIsNotWarm()
            Dim cfg = New UltimateScalperConfig() With {.MaLength = 200, .RsiLength = 14}
            Dim det = MakeDetector(cfg)
            ' Only 50 bars - far short of MA200 warmup.
            Dim bars As New List(Of MarketBar)
            Dim ts = New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            For i = 0 To 49
                bars.Add(MakeBar(ts, 4500D + i))
                ts = ts.AddMinutes(5)
            Next
            Dim eval = det.ComputeFromBars("MES", bars)
            Assert.Equal(UltimateScalperSignalSide.None, eval.Signal)
            Assert.False(eval.IsWarm)
            Assert.Equal("Warmup", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub EmptySeries_ReturnsNoSignal_WithReason()
            Dim cfg = New UltimateScalperConfig()
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", New List(Of MarketBar)())
            Assert.Equal(UltimateScalperSignalSide.None, eval.Signal)
            Assert.Equal("No bars available", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub BullishFixture_FiresWhenFullConfluenceAligns()
            ' Recency widened to 15 so the hand-computed 11-bar gap (cross above @ bar 196,
            ' signal bar @ bar 207) qualifies. All other thresholds are production defaults.
            Dim cfg = New UltimateScalperConfig() With {.MaLength = 200, .RsiLength = 14, .MaxBarsSinceMidlineCross = 15}
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", BuildBullishFixture())
            Dim diag = $"close={eval.LastClose} ma={eval.Ma200} vwap={eval.Vwap} rsi={eval.Rsi:F2} " &
                       $"crossAbove={eval.BarsSinceCrossAbove} crossBelow={eval.BarsSinceCrossBelow} " &
                       $"signal={eval.Signal} reason={eval.RejectionReason}"
            Assert.True(eval.IsWarm, $"Series must be warm: {diag}")
            Assert.True(eval.LastClose > eval.Ma200, $"price must remain above MA200: {diag}")
            Assert.True(eval.LastClose > eval.Vwap, $"price must remain above VWAP: {diag}")
            Assert.True(eval.Rsi < cfg.RsiOversold, $"RSI must be oversold: {diag}")
            Assert.True(eval.BarsSinceCrossAbove <= cfg.MaxBarsSinceMidlineCross,
                        $"recency must satisfy <= {cfg.MaxBarsSinceMidlineCross}: {diag}")
            Assert.Equal(UltimateScalperSignalSide.Bullish, eval.Signal)
            ' FEAT-69: PrimedSide mirrors Signal — Bullish fire means Bullish prime.
            Assert.Equal(UltimateScalperSignalSide.Bullish, eval.PrimedSide)
        End Sub

        <Fact>
        Public Sub BullishFixture_NearMiss_PrimedSideIsNone()
            ' FEAT-69: when Signal does not fire, PrimedSide must also stay None
            ' (v1 definition: prime = signal). Force RSI threshold so signal misses.
            Dim cfg = New UltimateScalperConfig() With {.MaLength = 200, .RsiLength = 14, .MaxBarsSinceMidlineCross = 15, .RsiOversold = 1.0}
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", BuildBullishFixture())
            Assert.Equal(UltimateScalperSignalSide.None, eval.Signal)
            Assert.Equal(UltimateScalperSignalSide.None, eval.PrimedSide)
        End Sub

        <Fact>
        Public Sub NearMiss_RsiNotOversold_RejectionReasonExplains()
            ' Force the oversold threshold to 1 so the bullish bar's sub-30 RSI cannot qualify.
            Dim cfg = New UltimateScalperConfig() With {.MaLength = 200, .RsiLength = 14, .MaxBarsSinceMidlineCross = 15, .RsiOversold = 1.0}
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", BuildBullishFixture())
            Assert.Equal(UltimateScalperSignalSide.None, eval.Signal)
            Assert.Contains("RSI", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub NearMiss_RecencyExpired_RejectionReasonExplains()
            ' Tight recency (5 bars) cannot accommodate the 11-bar cross-above → signal gap.
            Dim cfg = New UltimateScalperConfig() With {.MaLength = 200, .RsiLength = 14, .MaxBarsSinceMidlineCross = 5}
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", BuildBullishFixture())
            Assert.Equal(UltimateScalperSignalSide.None, eval.Signal)
            Assert.Contains("cross above 50", eval.RejectionReason)
        End Sub

    End Class

End Namespace
