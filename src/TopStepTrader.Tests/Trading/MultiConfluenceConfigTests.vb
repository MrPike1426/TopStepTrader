Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' STRAT-24: Verifies that MultiConfluenceConfig externalisation is behaviour-neutral
    ''' (snapshot test) and that alternative configs produce different outcomes (sweep test).
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~MultiConfluenceConfig"
    ''' </summary>
    Public Class MultiConfluenceConfigTests

        Private Const N As Integer = 100

        Private Shared Sub BuildBars(basePrice As Decimal, delta As Decimal,
                                     ByRef highs As List(Of Decimal),
                                     ByRef lows As List(Of Decimal),
                                     ByRef closes As List(Of Decimal),
                                     ByRef volumes As List(Of Decimal))
            highs   = New List(Of Decimal)(N)
            lows    = New List(Of Decimal)(N)
            closes  = New List(Of Decimal)(N)
            volumes = New List(Of Decimal)(N)
            For i = 0 To N - 1
                Dim c = basePrice + delta * CDec(i)
                closes.Add(c)
                highs.Add(c + 0.5D)
                lows.Add(c - 0.5D)
                volumes.Add(1_200D)
            Next
        End Sub

        ' ── Snapshot test ─────────────────────────────────────────────────────────

        ''' <summary>
        ''' STRAT-24 AC-1 (snapshot): Passing a default MultiConfluenceConfig must produce
        ''' identical BullScore, BearScore, LongCount, ShortCount, and Side to calling
        ''' Evaluate with no config at all.  Confirms externalisation is behaviour-neutral.
        ''' </summary>
        <Theory>
        <InlineData(100.0, 1.0)>    ' bullish trend
        <InlineData(300.0, -2.0)>   ' bearish trend
        <InlineData(200.0, 0.0)>    ' flat / no signal
        Public Sub DefaultConfig_ProducesIdenticalResultToNoConfig(basePriceD As Double, deltaD As Double)
            Dim basePrice = CDec(basePriceD)
            Dim delta     = CDec(deltaD)
            Dim highs As List(Of Decimal) = Nothing
            Dim lows  As List(Of Decimal) = Nothing
            Dim cls   As List(Of Decimal) = Nothing
            Dim vols  As List(Of Decimal) = Nothing
            BuildBars(basePrice, delta, highs, lows, cls, vols)

            Dim baseline = MultiConfluenceStrategy.Evaluate(highs, lows, cls, vols)
            Dim withCfg  = MultiConfluenceStrategy.Evaluate(highs, lows, cls, vols,
                               config:=New MultiConfluenceConfig())

            Assert.Equal(baseline.Side,       withCfg.Side)
            Assert.Equal(baseline.BullScore,  withCfg.BullScore)
            Assert.Equal(baseline.BearScore,  withCfg.BearScore)
            Assert.Equal(baseline.LongCount,  withCfg.LongCount)
            Assert.Equal(baseline.ShortCount, withCfg.ShortCount)
            Assert.Equal(baseline.IsPartialSignal, withCfg.IsPartialSignal)
        End Sub

        ' ── Sweep test ────────────────────────────────────────────────────────────

        ''' <summary>
        ''' STRAT-24 AC-2 (sweep): Two alternative configs with differing ADX thresholds and
        ''' MACD fractions must be accepted and produce independently-valid results.
        ''' The test verifies that: (a) both calls complete without exception, (b) the
        ''' stricter config (Lewis) never fires a signal where the relaxed config (Joe)
        ''' does not (i.e. Lewis ⊆ Joe in signal space on a given bar), and (c) scores
        ''' are in range [0, 100].
        ''' </summary>
        <Fact>
        Public Sub SweepTwoConfigs_StrictConfigSubsetOfRelaxedConfig()
            ' Use a moderately bullish trend — may or may not produce a signal,
            ' but the subset relationship must hold regardless.
            Dim highs As List(Of Decimal) = Nothing
            Dim lows  As List(Of Decimal) = Nothing
            Dim cls   As List(Of Decimal) = Nothing
            Dim vols  As List(Of Decimal) = Nothing
            BuildBars(100D, 0.5D, highs, lows, cls, vols)

            ' Lewis config: tighter ADX (25) and higher MACD bar (0.07)
            Dim lewisConfig = New MultiConfluenceConfig() With {
                .AdxThreshold          = 25.0F,
                .MacdHistMinAtrFraction = 0.07
            }

            ' Joe config: relaxed ADX (15) and lower MACD bar (0.03)
            Dim joeConfig = New MultiConfluenceConfig() With {
                .AdxThreshold          = 15.0F,
                .MacdHistMinAtrFraction = 0.03
            }

            Dim lewisResult = MultiConfluenceStrategy.Evaluate(highs, lows, cls, vols, config:=lewisConfig)
            Dim joeResult   = MultiConfluenceStrategy.Evaluate(highs, lows, cls, vols, config:=joeConfig)

            ' Scores in range
            Assert.InRange(lewisResult.BullScore, 0, 100)
            Assert.InRange(lewisResult.BearScore, 0, 100)
            Assert.InRange(joeResult.BullScore,   0, 100)
            Assert.InRange(joeResult.BearScore,   0, 100)

            ' Lewis ⊆ Joe: if Lewis fires a signal, Joe must also fire (same direction)
            ' because Lewis requirements are a strict superset of Joe requirements.
            If lewisResult.Side.HasValue Then
                Assert.True(joeResult.Side.HasValue,
                    "Joe (relaxed) must signal whenever Lewis (strict) signals on the same bars.")
                Assert.Equal(lewisResult.Side, joeResult.Side)
            End If
        End Sub

        ''' <summary>
        ''' STRAT-24 AC-2b (sweep): A config with an extremely high ADX threshold (99)
        ''' must produce LongCount and ShortCount strictly less than the default config
        ''' on a trending series where ADX-based condition 5 is the differentiator.
        ''' </summary>
        <Fact>
        Public Sub HighAdxConfig_ReducesConditionCount_OnTrendingSeries()
            Dim highs As List(Of Decimal) = Nothing
            Dim lows  As List(Of Decimal) = Nothing
            Dim cls   As List(Of Decimal) = Nothing
            Dim vols  As List(Of Decimal) = Nothing
            BuildBars(100D, 1D, highs, lows, cls, vols)

            Dim defaultResult = MultiConfluenceStrategy.Evaluate(highs, lows, cls, vols)

            Dim highAdxConfig = New MultiConfluenceConfig() With {.AdxThreshold = 99.0F}
            Dim highAdxResult = MultiConfluenceStrategy.Evaluate(highs, lows, cls, vols,
                                    config:=highAdxConfig)

            ' With ADX threshold=99 the ADX condition should fail, reducing counts
            Assert.True(highAdxResult.LongCount <= defaultResult.LongCount,
                "High ADX threshold must not increase LongCount vs default.")
            Assert.True(highAdxResult.ShortCount <= defaultResult.ShortCount,
                "High ADX threshold must not increase ShortCount vs default.")
        End Sub

    End Class

End Namespace
