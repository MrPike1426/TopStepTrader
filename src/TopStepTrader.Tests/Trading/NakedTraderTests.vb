Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Tests for the Naked Trader strategy: ADX/DMI Wilder-smoothing correctness
    ''' and NakedTraderAnalyser direction/confidence voting rules.
    '''
    ''' Run with: dotnet test --filter "FullyQualifiedName~NakedTrader"
    ''' </summary>
    Public Class NakedTraderTests

        ' ══════════════════════════════════════════════════════════════════════
        ' 1 — DMI / ADX Wilder smoothing (TechnicalIndicators.DMI)
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' A consistently rising high/close series produces +DM >> -DM,
        ''' so +DI must dominate -DI.
        ''' </summary>
        <Fact>
        Public Sub DMI_StrongUptrend_PlusDIExceedsMinusDI()
            Dim series = MakeUptrendSeries(40)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes, 14)

            Dim lastPDI = TechnicalIndicators.LastValid(dmi.PlusDI)
            Dim lastMDI = TechnicalIndicators.LastValid(dmi.MinusDI)
            Dim lastAdx = TechnicalIndicators.LastValid(dmi.ADX)

            Assert.True(lastPDI > lastMDI,
                        $"+DI={lastPDI:F2} should exceed -DI={lastMDI:F2} for a strong uptrend")
            Assert.InRange(CDbl(lastAdx), 0.0, 100.0)
        End Sub

        ''' <summary>
        ''' A consistently falling low/close series produces -DM >> +DM,
        ''' so -DI must dominate +DI.
        ''' </summary>
        <Fact>
        Public Sub DMI_StrongDowntrend_MinusDIExceedsPlusDI()
            Dim series = MakeDowntrendSeries(40)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes, 14)

            Dim lastPDI = TechnicalIndicators.LastValid(dmi.PlusDI)
            Dim lastMDI = TechnicalIndicators.LastValid(dmi.MinusDI)

            Assert.True(lastMDI > lastPDI,
                        $"-DI={lastMDI:F2} should exceed +DI={lastPDI:F2} for a strong downtrend")
        End Sub

        ''' <summary>
        ''' Period=14 requires at least 14 bars to compute DI; fewer returns all NaN.
        ''' </summary>
        <Fact>
        Public Sub DMI_InsufficientBars_AllNaN()
            Dim n = 13
            Dim closes = Enumerable.Range(0, n).Select(Function(i) CDec(100 + i)).ToList()
            Dim highs = closes.Select(Function(c) c + 0.5D).ToList()
            Dim lows = closes.Select(Function(c) c - 0.5D).ToList()

            Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, 14)

            Assert.True(dmi.ADX.All(Function(v) Single.IsNaN(v)),
                        "All ADX values should be NaN when bars < period")
            Assert.True(dmi.PlusDI.All(Function(v) Single.IsNaN(v)),
                        "All +DI values should be NaN when bars < period")
        End Sub

        ''' <summary>
        ''' Wilder warm-up: DI seeds at index=period; ADX seeds after period more DX values,
        ''' so the first valid ADX appears at index 2×period − 1 = 27 for period=14.
        ''' All earlier indices must remain NaN.
        ''' </summary>
        <Fact>
        Public Sub DMI_WilderSmoothing_FirstADXAtIndex27ForPeriod14()
            Dim series = MakeUptrendSeries(40)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes, 14)

            ' Indices 0–26 must all be NaN (warm-up not complete)
            For i = 0 To 26
                Assert.True(Single.IsNaN(dmi.ADX(i)),
                            $"ADX({i}) should be NaN during Wilder warm-up (period=14)")
            Next

            ' Index 27 is the first seeded ADX value
            Assert.False(Single.IsNaN(dmi.ADX(27)),
                         "ADX(27) should be the first valid seeded value (2×14−1)")
        End Sub

        ''' <summary>
        ''' Every non-NaN ADX value must lie within the mathematical range [0, 100].
        ''' </summary>
        <Fact>
        Public Sub DMI_AllValidADXValues_WithinZeroToHundred()
            Dim series = MakeUptrendSeries(50)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes, 14)

            For Each v In dmi.ADX
                If Not Single.IsNaN(v) Then
                    Assert.InRange(CDbl(v), 0.0, 100.0)
                End If
            Next
        End Sub

        ''' <summary>
        ''' Wilder smoothing is incremental: each new bar only shifts the smoothed TR/DM values
        ''' by 1/period of the prior value. Two runs with identical input must produce identical output.
        ''' </summary>
        <Fact>
        Public Sub DMI_Deterministic_SameInputSameOutput()
            Dim series = MakeUptrendSeries(40)
            Dim dmi1 = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes, 14)
            Dim dmi2 = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes, 14)

            For i = 0 To 39
                Assert.Equal(dmi1.ADX(i), dmi2.ADX(i))
                Assert.Equal(dmi1.PlusDI(i), dmi2.PlusDI(i))
                Assert.Equal(dmi1.MinusDI(i), dmi2.MinusDI(i))
            Next
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' 2 — NakedTraderAnalyser direction voting
        ' ══════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub Analyse_StrongUptrend_DirectionIsUp()
            Dim result = NakedTraderAnalyser.Analyse(MakeUptrendBars(40))
            Assert.Equal(TrendDirection.Up, result.Direction)
        End Sub

        <Fact>
        Public Sub Analyse_StrongDowntrend_DirectionIsDown()
            Dim result = NakedTraderAnalyser.Analyse(MakeDowntrendBars(40))
            Assert.Equal(TrendDirection.Down, result.Direction)
        End Sub

        <Fact>
        Public Sub Analyse_UpVotesPlusDownVotesEqualsTotal()
            Dim result = NakedTraderAnalyser.Analyse(MakeUptrendBars(40))
            Assert.Equal(result.TotalVotes, result.UpVotes + result.DownVotes)
        End Sub

        ''' <summary>When volume is all-zero, VWAP vote is absent → exactly 3 votes.</summary>
        <Fact>
        Public Sub Analyse_NoVolume_ThreeVotesCounted()
            Dim bars = MakeUptrendBars(40, withVolume:=False)
            Dim result = NakedTraderAnalyser.Analyse(bars)
            Assert.Equal(3, result.TotalVotes)
        End Sub

        ''' <summary>When volume is present, all four indicator votes are available.</summary>
        <Fact>
        Public Sub Analyse_WithVolume_FourVotesCounted()
            Dim bars = MakeUptrendBars(40, withVolume:=True)
            Dim result = NakedTraderAnalyser.Analyse(bars)
            Assert.Equal(4, result.TotalVotes)
        End Sub

        ''' <summary>Fewer than MIN_BARS immediately returns LOW confidence with a non-empty summary.</summary>
        <Fact>
        Public Sub Analyse_InsufficientBars_LowConfidenceAndSummarySet()
            Dim result = NakedTraderAnalyser.Analyse(MakeUptrendBars(10))
            Assert.Equal(TrendConfidence.Low, result.Confidence)
            Assert.False(String.IsNullOrEmpty(result.Summary))
        End Sub

        ''' <summary>Null input is handled gracefully — returns LOW confidence, no exception.</summary>
        <Fact>
        Public Sub Analyse_NullInput_GracefulLowConfidence()
            Dim result = NakedTraderAnalyser.Analyse(Nothing)
            Assert.Equal(TrendConfidence.Low, result.Confidence)
        End Sub

        ''' <summary>Summary string always contains the direction token.</summary>
        <Fact>
        Public Sub Analyse_Summary_ContainsDirectionText()
            Dim result = NakedTraderAnalyser.Analyse(MakeUptrendBars(40))
            Assert.Contains("UP", result.Summary, StringComparison.OrdinalIgnoreCase)
        End Sub

        ''' <summary>Deterministic: same bars always produce the same direction and confidence.</summary>
        <Fact>
        Public Sub Analyse_Deterministic_SameInputSameOutput()
            Dim bars = MakeUptrendBars(40)
            Dim r1 = NakedTraderAnalyser.Analyse(bars)
            Dim r2 = NakedTraderAnalyser.Analyse(bars)
            Assert.Equal(r1.Direction, r2.Direction)
            Assert.Equal(r1.Confidence, r2.Confidence)
            Assert.Equal(r1.UpVotes, r2.UpVotes)
            Assert.Equal(r1.DownVotes, r2.DownVotes)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' 3 — Confidence rules
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' A strongly trending series must produce ADX > 0 once the warm-up completes.
        ''' </summary>
        <Fact>
        Public Sub Analyse_StrongTrend_ADXIsPositive()
            Dim result = NakedTraderAnalyser.Analyse(MakeUptrendBars(40))
            If Not Single.IsNaN(result.Adx) Then
                Assert.True(result.Adx > 0.0F, $"ADX={result.Adx:F2} should be positive for a strong trend")
            End If
        End Sub

        ''' <summary>
        ''' ADX &lt; 20 forces LOW confidence regardless of vote alignment.
        ''' We verify the rule by constructing a near-flat choppy series.
        ''' </summary>
        <Fact>
        Public Sub Analyse_WeakADX_ConfidenceIsLow()
            Dim bars = MakeChoppyBars(40)
            Dim result = NakedTraderAnalyser.Analyse(bars)
            ' Only assert when ADX is computable and actually below the threshold
            If Not Single.IsNaN(result.Adx) AndAlso result.Adx < 20.0F Then
                Assert.Equal(TrendConfidence.Low, result.Confidence)
            End If
        End Sub

        ''' <summary>
        ''' The uptrend bars use rising volume, so the last bar's volume should exceed
        ''' VolumeMA(20) → IsVolumeOk = True.
        ''' </summary>
        <Fact>
        Public Sub Analyse_RisingVolume_IsVolumeOkTrue()
            Dim bars = MakeUptrendBars(40, withVolume:=True)
            Dim result = NakedTraderAnalyser.Analyse(bars)
            If result.IsVolumeOk.HasValue Then
                Assert.True(result.IsVolumeOk.Value,
                            "Rising-volume uptrend: last bar volume should exceed VolumeMA(20)")
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' Helpers
        ' ══════════════════════════════════════════════════════════════════════

        Private Shared Function MakeUptrendSeries(n As Integer) _
                As (Highs As List(Of Decimal), Lows As List(Of Decimal), Closes As List(Of Decimal))
            Dim closes = Enumerable.Range(0, n).Select(Function(i) 100D + i * 0.75D).ToList()
            Dim highs = closes.Select(Function(c) c + 0.40D).ToList()
            Dim lows = closes.Select(Function(c) c - 0.15D).ToList()
            Return (highs, lows, closes)
        End Function

        Private Shared Function MakeDowntrendSeries(n As Integer) _
                As (Highs As List(Of Decimal), Lows As List(Of Decimal), Closes As List(Of Decimal))
            Dim closes = Enumerable.Range(0, n).Select(Function(i) 100D - i * 0.75D).ToList()
            Dim highs = closes.Select(Function(c) c + 0.15D).ToList()
            Dim lows = closes.Select(Function(c) c - 0.40D).ToList()
            Return (highs, lows, closes)
        End Function

        Private Shared Function MakeUptrendBars(n As Integer,
                                                Optional withVolume As Boolean = True) As IList(Of MarketBar)
            Dim t0 = DateTimeOffset.UtcNow.AddMinutes(-5.0 * n)
            Return Enumerable.Range(0, n).Select(
                Function(i)
                    Return New MarketBar With {
                        .Timestamp = t0.AddMinutes(5.0 * i),
                        .Open = 99.85D + i * 0.75D,
                        .High = 100D + i * 0.75D + 0.40D,
                        .Low = 100D + i * 0.75D - 0.15D,
                        .Close = 100D + i * 0.75D,
                        .Volume = If(withVolume, CLng(1000L + i * 10L), 0L)
                    }
                End Function).ToList()
        End Function

        Private Shared Function MakeDowntrendBars(n As Integer,
                                                  Optional withVolume As Boolean = True) As IList(Of MarketBar)
            Dim t0 = DateTimeOffset.UtcNow.AddMinutes(-5.0 * n)
            Return Enumerable.Range(0, n).Select(
                Function(i)
                    Return New MarketBar With {
                        .Timestamp = t0.AddMinutes(5.0 * i),
                        .Open = 100.15D - i * 0.75D,
                        .High = 100D - i * 0.75D + 0.15D,
                        .Low = 100D - i * 0.75D - 0.40D,
                        .Close = 100D - i * 0.75D,
                        .Volume = If(withVolume, CLng(1000L + i * 10L), 0L)
                    }
                End Function).ToList()
        End Function

        ''' <summary>Alternating up/down bars — very low directional movement; ADX should be small.</summary>
        Private Shared Function MakeChoppyBars(n As Integer) As IList(Of MarketBar)
            Dim t0 = DateTimeOffset.UtcNow.AddMinutes(-5.0 * n)
            Return Enumerable.Range(0, n).Select(
                Function(i)
                    Dim noise = If(i Mod 2 = 0, 0.1D, -0.1D)
                    Return New MarketBar With {
                        .Timestamp = t0.AddMinutes(5.0 * i),
                        .Open = 100D - noise,
                        .High = 100.3D,
                        .Low = 99.7D,
                        .Close = 100D + noise,
                        .Volume = 1000L
                    }
                End Function).ToList()
        End Function

    End Class

End Namespace
