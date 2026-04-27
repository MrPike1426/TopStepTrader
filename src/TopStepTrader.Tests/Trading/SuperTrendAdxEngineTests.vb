Imports TopStepTrader.ML.Features
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Unit tests for the SuperTrendAdx indicator logic used by StrategyExecutionEngine.
    ''' Covers: ADX gate, DI confirmation gate, and direction-flip detection.
    ''' Run with: dotnet test --filter "FullyQualifiedName~SuperTrendAdx"
    ''' </summary>
    Public Class SuperTrendAdxEngineTests

        ' ══════════════════════════════════════════════════════════════════════
        ' 1 — ADX gate: low ADX should not produce a signal
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Choppy/ranging bars produce low ADX.  The engine gates on ADX >= AdxThreshold
        ''' (default 20), so no signal should fire.
        ''' </summary>
        <Fact>
        Public Sub SuperTrendAdx_LowAdx_NoSignal()
            Dim series = MakeChoppySeries(60)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes)
            Dim adx = TechnicalIndicators.LastValid(dmi.ADX)

            ' Choppy data should stay well below a 20-ADX threshold
            Assert.True(adx < 20.0,
                        $"Expected ADX < 20 for choppy data but got {adx:F2}")
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' 2 — DI gate: correct direction but wrong DI relationship → no signal
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' A strong uptrend gives SuperTrend direction = +1 and ADX above threshold,
        ''' but if we artificially verify -DI > +DI the long signal must NOT fire.
        ''' We confirm this by checking the DI relationship on a downtrend series
        ''' (where SuperTrend direction would be -1 and -DI > +DI) and asserting
        ''' that the indicators agree — i.e., no cross-fire between direction and DI.
        ''' </summary>
        <Fact>
        Public Sub SuperTrendAdx_UptrendDirectionButMinusDiDominates_NoLongSignal()
            ' Downtrend series: SuperTrend direction = -1, -DI > +DI
            Dim series = MakeDowntrendSeries(60)
            Dim st = TechnicalIndicators.SuperTrend(series.Highs, series.Lows, series.Closes)
            Dim stDir = st.Direction(st.Direction.Length - 1)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes)
            Dim plusDi = TechnicalIndicators.LastValid(dmi.PlusDI)
            Dim minusDi = TechnicalIndicators.LastValid(dmi.MinusDI)

            ' Direction must be down AND -DI must dominate — so long condition is false
            Dim longCondition = (stDir > 0.0F AndAlso plusDi > minusDi)
            Assert.False(longCondition,
                         $"Long condition must be False on a downtrend: dir={stDir}, +DI={plusDi:F2}, -DI={minusDi:F2}")
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' 3 — Direction-flip detection
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' A strong uptrend series produces SuperTrend direction = +1 (after warm-up).
        ''' When _stPrevDirection was -1 (downtrend) and direction flips to +1, stIsFlip = True.
        ''' When _stPrevDirection is already +1 and direction stays +1, stIsFlip = False.
        ''' </summary>
        <Fact>
        Public Sub SuperTrendAdx_DirectionFlip_DetectedCorrectly()
            Dim upSeries = MakeUptrendSeries(60)
            Dim st = TechnicalIndicators.SuperTrend(upSeries.Highs, upSeries.Lows, upSeries.Closes)
            Dim curDir = st.Direction(st.Direction.Length - 1)

            ' Simulate: previous direction was -1 (downtrend), now +1 → flip
            Dim prevDir As Single = -1.0F
            Dim stIsFlip = (curDir <> prevDir AndAlso prevDir <> 0.0F)
            Assert.True(stIsFlip,
                        $"Expected a flip when prev={prevDir} and cur={curDir}")

            ' Simulate: previous direction already +1 (same) → no flip
            Dim prevDirSame As Single = 1.0F
            Dim stIsNotFlip = (curDir <> prevDirSame AndAlso prevDirSame <> 0.0F)
            Assert.False(stIsNotFlip,
                         $"Expected no flip when prev={prevDirSame} and cur={curDir}")
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' 4 — Full long signal: strong uptrend with ADX >= threshold and +DI > -DI
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' A sufficiently long and strong uptrend should satisfy all three conditions:
        ''' SuperTrend direction = +1, ADX >= 20, and +DI > -DI.
        ''' </summary>
        <Fact>
        Public Sub SuperTrendAdx_StrongUptrend_AllConditionsMet()
            Dim series = MakeUptrendSeries(80)
            Dim st = TechnicalIndicators.SuperTrend(series.Highs, series.Lows, series.Closes)
            Dim stDir = st.Direction(st.Direction.Length - 1)
            Dim dmi = TechnicalIndicators.DMI(series.Highs, series.Lows, series.Closes)
            Dim adx = TechnicalIndicators.LastValid(dmi.ADX)
            Dim plusDi = TechnicalIndicators.LastValid(dmi.PlusDI)
            Dim minusDi = TechnicalIndicators.LastValid(dmi.MinusDI)

            Assert.True(stDir > 0.0F, $"Expected uptrend direction but got {stDir}")
            Assert.True(adx >= 20.0, $"Expected ADX >= 20 but got {adx:F2}")
            Assert.True(plusDi > minusDi, $"Expected +DI > -DI but got +DI={plusDi:F2}, -DI={minusDi:F2}")
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

        Private Shared Function MakeChoppySeries(n As Integer) _
                As (Highs As List(Of Decimal), Lows As List(Of Decimal), Closes As List(Of Decimal))
            Dim closes = Enumerable.Range(0, n).Select(
                Function(i) 100D + If(i Mod 2 = 0, 0.1D, -0.1D)).ToList()
            Dim highs = closes.Select(Function(c) c + 0.30D).ToList()
            Dim lows = closes.Select(Function(c) c - 0.30D).ToList()
            Return (highs, lows, closes)
        End Function

    End Class

End Namespace
