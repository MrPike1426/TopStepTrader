Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.VwapMeanReversion
Imports Xunit

Namespace TopStepTrader.Tests.Services.VwapMeanReversion

    ''' <summary>
    ''' FEAT-75 F5: Detector replay tests against synthetic bar fixtures via the Friend
    ''' <c>ComputeFromBars</c> seam (no Sqlite / HTTP). Fixture design:
    '''
    '''   • 5-min: 30 bars alternating tightly around 100 (small session SD), then a final
    '''     excursion bar whose close lands ~2.5σ (trigger), ~7σ (too far), or ~0σ (no setup)
    '''     from the session VWAP.
    '''   • 15-min: 40 flat bars (ADX ≈ 0 — chop) or 40 steadily-trending bars (ADX ≈ 100 — veto).
    '''   • 1-min: a reversal candle (close &gt; open and &gt; prior high) or a continuation bar.
    '''
    ''' All timestamps sit inside one CME session (winter date, well before the 19:40 UTC
    ''' entry cutoff) unless a test overrides them.
    ''' </summary>
    Public Class VwapMeanReversionSignalDetectorTests

        ' Tuesday 2026-01-13. Bars run 14:00 → 16:35 UTC (08:00 → 10:35 CST) — one session.
        Private Shared ReadOnly BaseUtc As New DateTimeOffset(2026, 1, 13, 14, 0, 0, TimeSpan.Zero)

        Private Shared Function MakeBar(ts As DateTimeOffset, tf As BarTimeframe,
                                        o As Decimal, h As Decimal, l As Decimal, c As Decimal,
                                        Optional vol As Long = 100) As MarketBar
            Return New MarketBar With {
                .ContractId = "TEST", .Timestamp = ts, .Timeframe = tf,
                .Open = o, .High = h, .Low = l, .Close = c, .Volume = vol
            }
        End Function

        Private Shared Function MakeDetector(cfg As VwapMeanReversionConfig) As VwapMeanReversionSignalDetector
            Return New VwapMeanReversionSignalDetector(
                barIngestion:=Nothing, barCollection:=Nothing,
                config:=cfg, logger:=NullLogger(Of VwapMeanReversionSignalDetector).Instance)
        End Function

        ''' <summary>30 alternating 5-min bars around 100 plus one final excursion bar.</summary>
        Private Shared Function FiveMinFixture(finalClose As Decimal,
                                               Optional startUtc As DateTimeOffset? = Nothing) As List(Of MarketBar)
            Dim start = If(startUtc, BaseUtc)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 29
                Dim c As Decimal = If(i Mod 2 = 0, 100.5D, 99.5D)
                bars.Add(MakeBar(start.AddMinutes(i * 5), BarTimeframe.FiveMinute,
                                 c, c + 0.2D, c - 0.2D, c))
            Next
            bars.Add(MakeBar(start.AddMinutes(30 * 5), BarTimeframe.FiveMinute,
                             99.5D, finalClose + 0.2D, finalClose - 0.2D, finalClose))
            Return bars
        End Function

        ''' <summary>40 flat 15-min bars — zero directional movement, ADX ≈ 0 (chop regime).</summary>
        Private Shared Function ChopFifteenMinFixture() As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 39
                bars.Add(MakeBar(BaseUtc.AddMinutes(-600 + i * 15), BarTimeframe.FifteenMinute,
                                 100D, 100D, 100D, 100D))
            Next
            Return bars
        End Function

        ''' <summary>40 steadily-rising 15-min bars — ADX saturates near 100 (trend regime).</summary>
        Private Shared Function TrendFifteenMinFixture() As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 39
                Dim c As Decimal = 100D + i
                bars.Add(MakeBar(BaseUtc.AddMinutes(-600 + i * 15), BarTimeframe.FifteenMinute,
                                 c - 1D, c + 0.5D, c - 1.5D, c))
            Next
            Return bars
        End Function

        ''' <summary>1-min pair: bullish reversal (close &gt; open AND close &gt; prior 1m high).</summary>
        Private Shared Function BullishConfirmFixture() As List(Of MarketBar)
            Return New List(Of MarketBar) From {
                MakeBar(BaseUtc.AddMinutes(148), BarTimeframe.OneMinute, 98.6D, 98.7D, 98.4D, 98.5D),
                MakeBar(BaseUtc.AddMinutes(149), BarTimeframe.OneMinute, 98.5D, 99.0D, 98.4D, 98.9D)
            }
        End Function

        ''' <summary>1-min pair: bearish continuation — no bullish reversal pattern.</summary>
        Private Shared Function NoConfirmFixture() As List(Of MarketBar)
            Return New List(Of MarketBar) From {
                MakeBar(BaseUtc.AddMinutes(148), BarTimeframe.OneMinute, 98.8D, 98.9D, 98.5D, 98.6D),
                MakeBar(BaseUtc.AddMinutes(149), BarTimeframe.OneMinute, 98.6D, 98.7D, 98.3D, 98.4D)
            }
        End Function

        ''' <summary>1-min pair: bearish reversal (close &lt; open AND close &lt; prior 1m low).</summary>
        Private Shared Function BearishConfirmFixture() As List(Of MarketBar)
            Return New List(Of MarketBar) From {
                MakeBar(BaseUtc.AddMinutes(148), BarTimeframe.OneMinute, 101.4D, 101.6D, 101.2D, 101.5D),
                MakeBar(BaseUtc.AddMinutes(149), BarTimeframe.OneMinute, 101.5D, 101.6D, 101.0D, 101.1D)
            }
        End Function

        ' ─── Warmup gates ──────────────────────────────────────────────────────

        <Fact>
        Public Sub EmptySeries_ReturnsNoSignal_WithReason()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", New List(Of MarketBar)(),
                                           ChopFifteenMinFixture(), BullishConfirmFixture())
            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.Equal("5m bars warmup", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub InsufficientAdxBars_ReturnsWarmupReason()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(98.6D),
                                           ChopFifteenMinFixture().Take(10).ToList(),
                                           BullishConfirmFixture())
            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.Equal("15m ADX warmup", eval.RejectionReason)
        End Sub

        ' ─── Long / short triggers ─────────────────────────────────────────────

        <Fact>
        Public Sub LongTrigger_FiresWhenAllConditionsMet()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(98.6D),
                                           ChopFifteenMinFixture(), BullishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.Bullish, eval.Signal)
            Assert.True(eval.BeyondEntryBand)
            Assert.False(eval.TooFarGone)
            Assert.True(eval.AdxVetoPassed)
            Assert.True(eval.ConfirmationCandle)
            Assert.InRange(eval.DeviationSd, -5.0, -2.0)

            ' Trade plan: T1 is the session VWAP; stop below entry; T2 between entry and +1σ band.
            Assert.Equal(eval.Vwap, eval.T1Price)
            Assert.True(eval.SuggestedInitialStopPrice < eval.LastClose)
            Assert.True(eval.T2Price > eval.LastClose)
            Assert.True(eval.T2Price <= eval.Vwap + eval.Sd + 0.01D)
        End Sub

        <Fact>
        Public Sub ShortTrigger_FiresOnUpsideExcursion()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(101.4D),
                                           ChopFifteenMinFixture(), BearishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.Bearish, eval.Signal)
            Assert.InRange(eval.DeviationSd, 2.0, 5.0)
            Assert.True(eval.SuggestedInitialStopPrice > eval.LastClose)
            Assert.True(eval.T2Price < eval.LastClose)
        End Sub

        ' ─── Vetoes ────────────────────────────────────────────────────────────

        <Fact>
        Public Sub AdxVeto_SuppressesSignal_InTrendRegime()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(98.6D),
                                           TrendFifteenMinFixture(), BullishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.False(eval.AdxVetoPassed)
            Assert.Contains("Trend veto", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub TooFarGoneVeto_TreatsExtremeExcursionAsBreakout()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(90D),
                                           ChopFifteenMinFixture(), BullishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.True(eval.TooFarGone)
            Assert.Contains("Too far gone", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub NoConfirmationCandle_SkipsEntry()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(98.6D),
                                           ChopFifteenMinFixture(), NoConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.False(eval.ConfirmationCandle)
            Assert.Equal("No 1m reversal confirmation yet", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub InsideEntryBand_NoSetup()
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(100.1D),
                                           ChopFifteenMinFixture(), BullishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.False(eval.BeyondEntryBand)
        End Sub

        ' ─── Session-time gates ────────────────────────────────────────────────

        <Fact>
        Public Sub EntryCutoff_BlocksLateEntries()
            ' Shift the whole 5-min series so the final bar closes at 20:00 UTC —
            ' inside the 90-min cutoff before the 21:10 UTC close.
            Dim lateStart = New DateTimeOffset(2026, 1, 13, 17, 30, 0, TimeSpan.Zero)
            Dim det = MakeDetector(New VwapMeanReversionConfig())
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(98.6D, lateStart),
                                           ChopFifteenMinFixture(), BullishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.False(eval.InEntryWindow)
            Assert.Equal("Inside pre-close entry cutoff", eval.RejectionReason)
        End Sub

        <Fact>
        Public Sub EnableLongFalse_BlocksLongSide()
            Dim cfg As New VwapMeanReversionConfig() With {.EnableLong = False}
            Dim det = MakeDetector(cfg)
            Dim eval = det.ComputeFromBars("MES", FiveMinFixture(98.6D),
                                           ChopFifteenMinFixture(), BullishConfirmFixture())

            Assert.Equal(VwapMeanReversionSignalSide.None, eval.Signal)
            Assert.Equal("Long entries disabled", eval.RejectionReason)
        End Sub

        ' ─── Persona defaults (FEAT-75 F1) ─────────────────────────────────────

        <Fact>
        Public Sub PersonaDefaults_SetAdxVetoThreshold()
            Assert.Equal(30.0, VwapMeanReversionConfig.PersonaAdxVetoThreshold("Lewis"))
            Assert.Equal(25.0, VwapMeanReversionConfig.PersonaAdxVetoThreshold("Damian"))
            Assert.Equal(20.0, VwapMeanReversionConfig.PersonaAdxVetoThreshold("Joe"))

            Dim cfg As New VwapMeanReversionConfig()
            cfg.ApplyPersona("Joe")
            Assert.Equal("Joe", cfg.ActivePersona)
            Assert.Equal(20.0, cfg.AdxVetoThreshold)
        End Sub

        <Fact>
        Public Sub EntryCutoff_WindowMath()
            Dim cfg As New VwapMeanReversionConfig()   ' 90 min before 21:10 UTC
            Assert.False(cfg.IsInsideEntryCutoff(New DateTime(2026, 1, 13, 19, 39, 0, DateTimeKind.Utc)))
            Assert.True(cfg.IsInsideEntryCutoff(New DateTime(2026, 1, 13, 19, 40, 0, DateTimeKind.Utc)))
            Assert.True(cfg.IsInsideEntryCutoff(New DateTime(2026, 1, 13, 21, 30, 0, DateTimeKind.Utc)))
            Assert.False(cfg.IsInsideEntryCutoff(New DateTime(2026, 1, 13, 22, 0, 0, DateTimeKind.Utc)))
        End Sub

    End Class

End Namespace
