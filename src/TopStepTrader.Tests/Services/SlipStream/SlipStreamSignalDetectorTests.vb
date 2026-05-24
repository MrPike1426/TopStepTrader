Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.SlipStream
Imports Xunit

Namespace TopStepTrader.Tests.Services.SlipStream

    ''' <summary>
    ''' FEAT-70: Detector replay tests against synthetic 5-minute bar fixtures. These
    ''' bypass the bar-I/O layer by calling the Friend <c>ComputeFromBars</c> seam,
    ''' so tests run in-process with no Sqlite or HTTP dependencies. Fixture math
    ''' favors verifiable invariants (warmup gates, session windows, enable-flag respect)
    ''' over hand-tuned multi-indicator confluence — the latter is left to manual
    ''' replay of recorded live bars in a follow-up ticket.
    ''' </summary>
    Public Class SlipStreamSignalDetectorTests

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

        Private Shared Function MakeDetector(cfg As SlipStreamConfig) As SlipStreamSignalDetector
            Return New SlipStreamSignalDetector(
                barIngestion:=Nothing, barCollection:=Nothing,
                config:=cfg, logger:=NullLogger(Of SlipStreamSignalDetector).Instance)
        End Function

        Private Shared Function FixtureConfig() As SlipStreamConfig
            Return New SlipStreamConfig() With {
                .UseSession = False,
                .UseHtfFilter = False
            }
        End Function

        ' ─── Warmup / empty series gates ──────────────────────────────────────

        <Fact>
        Public Sub EmptySeries_ReturnsNoSignal_WithReason()
            Dim cfg = FixtureConfig()
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", New List(Of MarketBar)(), Nothing)
            Assert.Equal(SlipStreamSignalSide.None, eval.Signal)
            Assert.False(eval.IsWarm)
            Assert.Equal("No bars available", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub TooFewBars_ReturnsWarmup()
            Dim cfg = FixtureConfig()
            Dim det = MakeDetector(cfg)
            Dim bars As New List(Of MarketBar)
            Dim ts = New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            ' Only 50 bars — far short of the EmaSlowLength + AtrPercentLookback warmup floor.
            For i = 0 To 49
                bars.Add(MakeBar(ts, 4500D + i))
                ts = ts.AddMinutes(5)
            Next
            Dim eval = det.ComputeFromBars("MES", bars, Nothing)
            Assert.Equal(SlipStreamSignalSide.None, eval.Signal)
            Assert.False(eval.IsWarm)
            Assert.Equal("Warmup", eval.RejectionReason)
        End Sub

        ' ─── EnableLong/Short flags ───────────────────────────────────────────

        <Fact>
        Public Sub DisableLong_NeverFiresBullish_EvenOnTrendUpSeries()
            Dim cfg = FixtureConfig()
            cfg.EnableLong = False
            cfg.EnableShort = True
            Dim det = MakeDetector(cfg)
            ' 350 bars of slow uptrend with a tiny pullback at the end.
            Dim bars As New List(Of MarketBar)
            Dim ts = New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            For i = 0 To 348
                bars.Add(MakeBar(ts, 4500D + CDec(i)))
                ts = ts.AddMinutes(5)
            Next
            ' Final bar dips to tag the fast EMA.
            bars.Add(MakeBar(ts, 4820D))
            Dim eval = det.ComputeFromBars("MES", bars, Nothing)
            ' The signal would never be Bullish regardless of other gates because EnableLong is false.
            Assert.NotEqual(SlipStreamSignalSide.Bullish, eval.Signal)
        End Sub

        <Fact>
        Public Sub DisableShort_NeverFiresBearish_EvenOnTrendDownSeries()
            Dim cfg = FixtureConfig()
            cfg.EnableLong = True
            cfg.EnableShort = False
            Dim det = MakeDetector(cfg)
            ' 350 bars of slow downtrend.
            Dim bars As New List(Of MarketBar)
            Dim ts = New DateTimeOffset(2026, 5, 20, 13, 0, 0, TimeSpan.Zero)
            For i = 0 To 348
                bars.Add(MakeBar(ts, 4900D - CDec(i)))
                ts = ts.AddMinutes(5)
            Next
            bars.Add(MakeBar(ts, 4555D))
            Dim eval = det.ComputeFromBars("MES", bars, Nothing)
            Assert.NotEqual(SlipStreamSignalSide.Bearish, eval.Signal)
        End Sub

        ' ─── Session window parser ────────────────────────────────────────────

        <Fact>
        Public Sub IsTimestampInWindow_InsideDaytimeWindow_ReturnsTrue()
            ' 2026-05-20 16:00 UTC = 11:00 Central (CDT, UTC-5). Window 0830-1500 → inside.
            Dim utcTs = New DateTimeOffset(2026, 5, 20, 16, 0, 0, TimeSpan.Zero)
            Assert.True(SlipStreamSignalDetector.IsTimestampInWindow(utcTs, "0830-1500"))
        End Sub

        <Fact>
        Public Sub IsTimestampInWindow_BeforeWindow_ReturnsFalse()
            ' 2026-05-20 12:00 UTC = 07:00 Central. Window 0830-1500 → before.
            Dim utcTs = New DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero)
            Assert.False(SlipStreamSignalDetector.IsTimestampInWindow(utcTs, "0830-1500"))
        End Sub

        <Fact>
        Public Sub IsTimestampInWindow_AfterWindow_ReturnsFalse()
            ' 2026-05-20 21:00 UTC = 16:00 Central. Window 0830-1500 → after.
            Dim utcTs = New DateTimeOffset(2026, 5, 20, 21, 0, 0, TimeSpan.Zero)
            Assert.False(SlipStreamSignalDetector.IsTimestampInWindow(utcTs, "0830-1500"))
        End Sub

        <Fact>
        Public Sub IsTimestampInWindow_OvernightWrap_HandlesCorrectly()
            ' Window 2300-0500 wraps midnight.
            ' 2026-05-21 06:00 UTC = 01:00 Central → inside the wrap window.
            Dim insideWrap = New DateTimeOffset(2026, 5, 21, 6, 0, 0, TimeSpan.Zero)
            Assert.True(SlipStreamSignalDetector.IsTimestampInWindow(insideWrap, "2300-0500"))
            ' 2026-05-21 15:00 UTC = 10:00 Central → outside.
            Dim outsideWrap = New DateTimeOffset(2026, 5, 21, 15, 0, 0, TimeSpan.Zero)
            Assert.False(SlipStreamSignalDetector.IsTimestampInWindow(outsideWrap, "2300-0500"))
        End Sub

        <Fact>
        Public Sub IsTimestampInWindow_MalformedWindow_ReturnsFalse()
            Dim utcTs = New DateTimeOffset(2026, 5, 20, 16, 0, 0, TimeSpan.Zero)
            Assert.False(SlipStreamSignalDetector.IsTimestampInWindow(utcTs, ""))
            Assert.False(SlipStreamSignalDetector.IsTimestampInWindow(utcTs, "0830"))
            Assert.False(SlipStreamSignalDetector.IsTimestampInWindow(utcTs, "abc-def"))
        End Sub

        ' ─── Timeframe parser ─────────────────────────────────────────────────

        <Fact>
        Public Sub ParseTimeframe_RecognisedLabels_ReturnExpectedEnum()
            Assert.Equal(BarTimeframe.OneMinute, SlipStreamSignalDetector.ParseTimeframe("1min"))
            Assert.Equal(BarTimeframe.FiveMinute, SlipStreamSignalDetector.ParseTimeframe("5min"))
            Assert.Equal(BarTimeframe.FifteenMinute, SlipStreamSignalDetector.ParseTimeframe("15min"))
            Assert.Equal(BarTimeframe.OneHour, SlipStreamSignalDetector.ParseTimeframe("60min"))
            Assert.Equal(BarTimeframe.OneHour, SlipStreamSignalDetector.ParseTimeframe("1hour"))
            Assert.Equal(BarTimeframe.FourHour, SlipStreamSignalDetector.ParseTimeframe("4h"))
            Assert.Equal(BarTimeframe.Daily, SlipStreamSignalDetector.ParseTimeframe("daily"))
        End Sub

        <Fact>
        Public Sub ParseTimeframe_UnknownLabel_DefaultsToHourly()
            Assert.Equal(BarTimeframe.OneHour, SlipStreamSignalDetector.ParseTimeframe(""))
            Assert.Equal(BarTimeframe.OneHour, SlipStreamSignalDetector.ParseTimeframe("nonsense"))
        End Sub

    End Class

End Namespace
