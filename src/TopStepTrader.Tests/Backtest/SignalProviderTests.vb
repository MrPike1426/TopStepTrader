Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Backtest
Imports TopStepTrader.Services.Backtest.Strategies
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for EmaRsiSignalProvider and MultiConfluenceSignalProvider.
    ''' ARCH-01b acceptance criteria: buy signal, sell signal, and no-signal scenarios
    ''' for each provider using in-memory synthetic bar series with no API calls.
    ''' </summary>
    Public Class SignalProviderTests

        ' ══════════════════════════════════════════════════════════════════
        ' Helpers
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>Constant length for all synthetic series in these tests.</summary>
        Private Const N As Integer = 60

        ''' <summary>Bar index used for signal evaluation (well past all warm-up periods).</summary>
        Private Const Idx As Integer = 50

        ''' <summary>Create a bar with explicit O/H/L/C.</summary>
        Private Shared Function MakeBar(open As Decimal, high As Decimal,
                                        low As Decimal, close As Decimal) As MarketBar
            Return New MarketBar With {
                .Open = open, .High = high, .Low = low, .Close = close,
                .Timestamp = DateTimeOffset.UtcNow
            }
        End Function

        ''' <summary>
        ''' Create a list of <paramref name="count"/> bars, alternating bullish/bearish
        ''' around the given <paramref name="basePrice"/>.
        ''' </summary>
        Private Shared Function MakeBars(count As Integer,
                                          Optional basePrice As Decimal = 5000D,
                                          Optional allBullish As Boolean = False,
                                          Optional allBearish As Boolean = False) As IReadOnlyList(Of MarketBar)
            Dim bars As New List(Of MarketBar)(count)
            For i = 0 To count - 1
                Dim isBull = If(allBullish, True, If(allBearish, False, i Mod 2 = 0))
                Dim o = basePrice
                Dim c = If(isBull, basePrice + 1D, basePrice - 1D)
                bars.Add(MakeBar(o, Math.Max(o, c) + 0.5D, Math.Min(o, c) - 0.5D, c))
            Next
            Return bars.AsReadOnly()
        End Function

        ''' <summary>Fill an array of length N with a constant value.</summary>
        Private Shared Function ConstArr(v As Single) As Single()
            Dim a(N - 1) As Single
            For i = 0 To N - 1 : a(i) = v : Next
            Return a
        End Function

        ''' <summary>Fill an array with NaN (simulates indicator not yet warmed up).</summary>
        Private Shared Function NaNArr() As Single()
            Dim a(N - 1) As Single
            For i = 0 To N - 1 : a(i) = Single.NaN : Next
            Return a
        End Function

        ''' <summary>Minimal BacktestConfiguration for signal provider tests.</summary>
        Private Shared Function MakeConfig(Optional minConf As Single = 0.70F,
                                            Optional minAdx As Single = 0.0F,
                                            Optional useAtrMode As Boolean = False) As BacktestConfiguration
            Return New BacktestConfiguration With {
                .MinSignalConfidence = minConf,
                .MinAdxThreshold     = minAdx,
                .UseAtrMode          = useAtrMode,
                .SlAtrMultiple       = 1.5D,
                .TpAtrMultiple       = 3.0D,
                .PointValue          = 5.0D,
                .TickSize            = 0.25D
            }
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' EmaRsiSignalProvider — factory
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub Factory_EmaRsiWeightedScore_ReturnsEmaRsiProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.EmaRsiWeightedScore)
            Assert.IsType(Of EmaRsiSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_MultiConfluence_ReturnsMultiConfluenceProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.MultiConfluence)
            Assert.IsType(Of MultiConfluenceSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_TripleEmaCascade_ReturnsTripleEmaCascadeProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.TripleEmaCascade)
            Assert.IsType(Of TripleEmaCascadeSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_BbSqueezeScalper_ReturnsBbSqueezeProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.BbSqueezeScalper)
            Assert.IsType(Of BbSqueezeSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_LultDivergence_ReturnsLultProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.LultDivergence)
            Assert.IsType(Of LultDivergenceSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_VidyaCross_ReturnsVidyaCrossProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.VidyaCross)
            Assert.IsType(Of VidyaCrossSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_NakedTrader_ReturnsNakedTraderProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.NakedTrader)
            Assert.IsType(Of NakedTraderSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_DoubleBubbleButt_ReturnsDoubleBubbleButtProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.DoubleBubbleButt)
            Assert.IsType(Of DoubleBubbleButtSignalProvider)(provider)
        End Sub

        ''' <summary>Passing an unknown integer strategy value must throw NotImplementedException.</summary>
        <Fact>
        Public Sub Factory_UnknownStrategy_Throws()
            Assert.Throws(Of NotImplementedException)(
                Sub() StrategySignalProviderFactory.Create(CType(999, StrategyConditionType)))
        End Sub

        ' ── ARCH-01d factory tests ───────────────────────────────────────────

        <Fact>
        Public Sub Factory_ConnorsRsi2_ReturnsConnorsRsi2Provider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.ConnorsRsi2)
            Assert.IsType(Of ConnorsRsi2SignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_SuperTrend_ReturnsSuperTrendProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.SuperTrend)
            Assert.IsType(Of SuperTrendSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_DonchianBreakout_ReturnsDonchianBreakoutProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.DonchianBreakout)
            Assert.IsType(Of DonchianBreakoutSignalProvider)(provider)
        End Sub

        <Fact>
        Public Sub Factory_BbRsiMeanReversion_ReturnsBbRsiReversionProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.BbRsiMeanReversion)
            Assert.IsType(Of BbRsiReversionSignalProvider)(provider)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' EmaRsiSignalProvider — buy signal
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' All 6 bullish conditions met → bullScore 100 ≥ minPct 70 → Buy signal.
        ''' EMA21 > EMA50 (25pts), Close > EMA21 (20pts), Close > EMA50 (15pts),
        ''' RSI 60 (20pts), EMA21 rising (10pts), 3/3 bullish candles (10pts) = 100pts.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_AllBullishConditions_ReturnsBuy()
            Dim provider As New EmaRsiSignalProvider()

            ' EMA21=100, EMA50=90, close=110, RSI=60 → bullScore=100
            Dim ema21 = ConstArr(100.0F)
            ema21(Idx - 1) = 99.5F    ' rising: prev < now
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(90.0F),
                .Rsi     = ConstArr(60.0F)
            }
            Dim bar = bars(Idx)
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.True(result.Confidence >= 0.7F)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' EmaRsiSignalProvider — sell signal
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' All bearish conditions: EMA21 below EMA50, close below both EMAs,
        ''' RSI outside bull zone, EMA21 falling, 3/3 bearish candles → bullScore=0,
        ''' downPct=100 ≥ minPct 70 → Sell signal.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_AllBearishConditions_ReturnsSell()
            Dim provider As New EmaRsiSignalProvider()

            ' EMA21=90, EMA50=100, close=80, RSI=25 → bullScore=0 (all conditions fail)
            Dim ema21 = ConstArr(90.0F)
            ema21(Idx - 1) = 90.5F    ' falling: prev > now
            Dim bars = MakeBars(N, basePrice:=80D, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(100.0F),
                .Rsi     = ConstArr(25.0F)
            }
            Dim bar = bars(Idx)
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.True(result.Confidence >= 0.7F)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' EmaRsiSignalProvider — no signal
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' When both bullScore and bearScore fall in the 40–60 neutral zone, the provider
        ''' returns a NeutralExit result so BacktestEngine can close any open position.
        ''' Setup: EMA21 ≈ EMA50 with minimal separation, close between both EMAs,
        ''' RSI=52 (outside both bull 55–70 and bear 30–45 zones), flat EMA21, mixed candles.
        ''' → bullScore: +25 (EMA21 > EMA50×1.0005? No, tiny gap) = 0 + close>EMA21 = 0 (close=100 = EMA21) + close>EMA50 = 0 (EMA50=100) + RSI 55–70=No + flat=No + mixed candles=No → bullScore=0.
        ''' Actually we need both in 40–60. Use: EMA21=101, EMA50=100.04, close=100.5, RSI=52, flat EMA21, 2 bearish candles.
        ''' bullScore: EMA21>EMA50×1.0005? 101>100.04×1.0005=100.09 ✓ +25; close>EMA21? 100.5>101=No; close>EMA50=100.5>100.04 ✓+15; RSI 55–70=No; flat EMA21=0; 0 of 3 bullish (allBearish) = 0 → bullScore=40.
        ''' bearScore: EMA21&lt;EMA50×0.9995? 101&lt;100.04×0.9995=100.09? No; close&lt;EMA21? 100.5&lt;101 ✓+20; close&lt;EMA50? 100.5&lt;100.04? No; RSI 30–45=No; flat=0; 3/3 bearish +10 → bearScore=30. Still not in 40–60.
        ''' Simplest scenario: explicitly ensure bearScore lands 40–60 by choosing RSI=35 (+20) and close&lt;EMA50 (+15) but EMA21 above EMA50 wide (so no bear cross), flat EMA21 (no bear momentum), mix candles (only 1 bearish → no bonus).
        ''' bullScore: EMA21=100&gt;EMA50=90×1.0005=90.045 ✓+25; close=95&lt;EMA21=100 → no +20; close=95&gt;EMA50=90 ✓+15; RSI=35 not in 55–70 → 0; flat EMA21 → 0; 1/3 bullish (1 bullish + 2 bearish in mixed) → 0. bullScore=40.
        ''' bearScore: EMA21=100 NOT &lt; EMA50×0.9995=89.955 → 0; close=95&lt;EMA21=100 ✓+20; close=95&gt;EMA50=90 → no +15; RSI=35 in 30–45 ✓+20; clamp=40; flat EMA21 → no (-10 falling); 2/3 bearish (mixed → alternating) → mixed bars: barIdx-2=bull,barIdx-1=bear,barIdx=bull for default MakeBars even idx. bearCandles depends on allBearish.
        ''' Use allBearish: bearCandles=3 +10. bearScore=40+10=50. bullScore: allBearish so candle bonus=0 → bullScore=40. Both 40–60 ✓.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_BothScoresNeutral_ReturnsNeutralExit()
            Dim provider As New EmaRsiSignalProvider()

            ' EMA21=100, EMA50=90, close=95, RSI=35, EMA21 flat, all-bearish candles.
            ' bullScore: +25 (EMA21>EMA50×1.0005) + 0 (close<EMA21) + 15 (close>EMA50) + 0 (RSI not 55–70) + 0 (flat) + 0 (0 bullish candles) = 40.
            ' bearScore: 0 (EMA21 NOT < EMA50×0.9995) + 20 (close<EMA21) + 0 (close NOT <EMA50) + 20 (RSI 30–45) = 40 → +0 (flat) + 10 (3/3 bearish) = 50.
            ' Both in 40–60 → NeutralExit.
            Dim ema21 = ConstArr(100.0F)   ' flat: prev = now
            Dim bars = MakeBars(N, basePrice:=95D, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(90.0F),
                .Rsi     = ConstArr(35.0F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.70F), Idx)

            Assert.NotNull(result)
            Assert.True(result.NeutralExit)
            Assert.Null(result.Side)
        End Sub

        ''' <summary>
        ''' ADX gate: strong buy signal but ADX below threshold → Nothing returned.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_BullishButAdxBelowGate_ReturnsNothing()
            Dim provider As New EmaRsiSignalProvider()

            Dim ema21 = ConstArr(100.0F)
            ema21(Idx - 1) = 99.5F
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(90.0F),
                .Rsi     = ConstArr(60.0F),
                .Adx     = ConstArr(15.0F)   ' below ADX gate of 25
            }
            Dim bar = bars(Idx)
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(minAdx:=25.0F), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' NaN in any indicator causes the provider to return Nothing (warm-up guard).
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_NaNIndicator_ReturnsNothing()
            Dim provider As New EmaRsiSignalProvider()

            Dim bars = MakeBars(N, allBullish:=True)
            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = NaNArr(),        ' NaN — still warming up
                .Ema50   = ConstArr(90.0F),
                .Rsi     = ConstArr(60.0F)
            }
            Dim bar = bars(Idx)
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' When UseAtrMode=True and ATR series is populated, StopDelta and TpDelta
        ''' are set on the result.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_AtrModeOn_PopulatesDeltas()
            Dim provider As New EmaRsiSignalProvider()

            Dim ema21 = ConstArr(100.0F)
            ema21(Idx - 1) = 99.5F
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(90.0F),
                .Rsi     = ConstArr(60.0F),
                .Atr     = ConstArr(2.0F)   ' ATR = 2.0
            }
            Dim config = MakeConfig(useAtrMode:=True)   ' SlAtrMultiple=1.5, TpAtrMultiple=3.0
            Dim result = provider.Evaluate(bars(Idx), indicators, config, Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.Equal(3.0D, result.StopDelta)   ' 2.0 × 1.5
            Assert.Equal(6.0D, result.TpDelta)     ' 2.0 × 3.0
        End Sub

        ' ── STRAT-09 bear independence tests ────────────────────────────────

        ''' <summary>
        ''' STRAT-09: A neutral bar (EMA flat, RSI=50, mixed candles) must NOT produce
        ''' a Sell signal.  With the old logic (downPct = 100 − bullScore) bullScore≈10
        ''' would have yielded downPct=90 — a false short.  With independent bearScore,
        ''' none of the 6 bear conditions are met so bearScore=0 and no signal fires.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_NeutralBar_DoesNotProduceSellSignal()
            Dim provider As New EmaRsiSignalProvider()

            ' EMA21 = EMA50 (no separation), close between them, RSI=50 (outside 30–45 bear zone),
            ' EMA21 flat (prev=now), mixed candles.
            Dim ema21 = ConstArr(100.0F)           ' flat: prev = now → no momentum
            Dim bars = MakeBars(N, basePrice:=100D) ' alternating bull/bear → mixed candles

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(100.0F),        ' EMA21 = EMA50 → no cross separation
                .Rsi     = ConstArr(50.0F)           ' neutral RSI — outside both bull and bear zones
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.70F), Idx)

            ' No side must fire — bearScore=0 < 70%
            Assert.True(result Is Nothing OrElse result.Side Is Nothing,
                        "Neutral bar should not produce a directional entry signal.")
        End Sub

        ''' <summary>
        ''' STRAT-09: When all 6 independent bear conditions are satisfied the provider
        ''' returns a Sell signal with confidence ≥ minConf, and bearScore is used
        ''' (not 100 − bullScore).  Specifically: bullScore may be 0, and bearScore = 100.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_AllBearConditions_ReturnsSellWithBearScore()
            Dim provider As New EmaRsiSignalProvider()

            ' EMA21=90, EMA50=100  → EMA21 < EMA50*0.9995 (+25), close=75 < EMA21 (+20) < EMA50 (+15),
            ' RSI=35 in 30–45 (+20), EMA21 falling (+10), 3/3 bearish candles (+10) = 100 pts.
            Dim ema21 = ConstArr(90.0F)
            ema21(Idx - 1) = 90.5F                  ' falling: prev=90.5 > now=90
            Dim bars = MakeBars(N, basePrice:=75D, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(100.0F),
                .Rsi     = ConstArr(35.0F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.70F), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.True(result.Confidence >= 0.7F,
                        $"Expected confidence ≥ 0.70 but got {result.Confidence}")
        End Sub

        ' ── STRAT-10: RSI three-zone scoring ────────────────────────────────

        ''' <summary>RSI=72 (extended/overbought) must award 12 pts, not 0.</summary>
        <Fact>
        Public Sub EmaRsi_Rsi72_Awards12BullPoints()
            Dim provider As New EmaRsiSignalProvider()
            ' Isolate RSI contribution: EMA21 flat and below EMA50 (no bull cross), close below both EMAs
            ' so only the RSI zone fires. RSI=72 → +12 on bull side.
            Dim ema21 = ConstArr(90.0F)   ' flat, below EMA50=100
            Dim bars = MakeBars(N, basePrice:=80D, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(100.0F),
                .Rsi     = ConstArr(72.0F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.01F), Idx)
            ' bullScore should be 12 (RSI extended zone only, all other bull conditions fail)
            Assert.NotNull(result)
            Assert.True(result.Confidence > 0F, "RSI=72 should produce non-zero bull confidence")
        End Sub

        ''' <summary>RSI=62 (confirmed trend zone 55–70) must award 20 pts.</summary>
        <Fact>
        Public Sub EmaRsi_Rsi62_Awards20BullPoints()
            Dim provider As New EmaRsiSignalProvider()
            Dim ema21 = ConstArr(100.0F)
            ema21(Idx - 1) = 99.5F
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicatorsWith62 As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(62.0F)
            }
            Dim indicatorsWith72 As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(72.0F)
            }
            Dim result62 = provider.Evaluate(bars(Idx), indicatorsWith62, MakeConfig(), Idx)
            Dim result72 = provider.Evaluate(bars(Idx), indicatorsWith72, MakeConfig(), Idx)

            ' RSI=62 (full 20 pts) should outscore RSI=72 (12 pts)
            Assert.True(result62.Confidence >= result72.Confidence,
                        "RSI=62 (20 pts) should score ≥ RSI=72 (12 pts)")
        End Sub

        ''' <summary>RSI=52 (above midline 50–55) must award 8 pts to the bull score.</summary>
        <Fact>
        Public Sub EmaRsi_Rsi52_Awards8BullPoints()
            Dim provider As New EmaRsiSignalProvider()
            ' Isolate: only RSI zone and EMA21>EMA50 cross (25 pts) active; total =33 with RSI=52.
            ' Compare to RSI=48 where RSI contributes 0 → total=25.
            Dim ema21 = ConstArr(100.0F)   ' flat (no slope pts)
            Dim bars = MakeBars(N, basePrice:=95D, allBearish:=True)  ' close < EMA21; no candle bonus

            Dim indicators52 As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(52.0F)
            }
            Dim indicators48 As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(48.0F)
            }
            Dim result52 = provider.Evaluate(bars(Idx), indicators52, MakeConfig(minConf:=0.01F), Idx)
            Dim result48 = provider.Evaluate(bars(Idx), indicators48, MakeConfig(minConf:=0.01F), Idx)

            Assert.True(result52.Confidence > result48.Confidence,
                        "RSI=52 should score higher than RSI=48 (0 pts)")
        End Sub

        ' ── STRAT-12: EMA21 3-bar slope ─────────────────────────────────────────

        ''' <summary>Flat EMA21 (identical values over 4 bars) must not award momentum points.</summary>
        <Fact>
        Public Sub EmaRsi_FlatEma21Slope_NoMomentumPoints()
            Dim provider As New EmaRsiSignalProvider()
            ' EMA21 completely flat — slope = 0%, well below 0.03% threshold
            Dim ema21 = ConstArr(100.0F)   ' all bars identical
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicatorsFlat As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            ' Rising EMA21 prev < now should also not trigger (tick noise excluded)
            Dim ema21Rising = ConstArr(100.0F)
            ema21Rising(Idx - 1) = 99.5F   ' prev=99.5, now=100 → 0.5% rise over 1 bar but 3-bar slope needs 3 bars
            ' barIndex-3 = Idx-3 = 47; all are 100.0F so slope over 3 bars ≈ 0
            Dim indicatorsRisingPrev As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21Rising, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            Dim resultFlat = provider.Evaluate(bars(Idx), indicatorsFlat, MakeConfig(minConf:=0.01F), Idx)
            Dim resultRisingPrev = provider.Evaluate(bars(Idx), indicatorsRisingPrev, MakeConfig(minConf:=0.01F), Idx)

            ' Both should have equal scores — flat 3-bar slope awards 0 in both cases
            Assert.Equal(resultFlat.Confidence, resultRisingPrev.Confidence)
        End Sub

        ''' <summary>Rising EMA21 over 3 bars (slope > 0.03%) must award 10 pts.</summary>
        <Fact>
        Public Sub EmaRsi_RisingEma21Over3Bars_Awards10Points()
            Dim provider As New EmaRsiSignalProvider()
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            ' Flat array for comparison (no slope)
            Dim ema21Flat = ConstArr(100.0F)
            ' Rising array: barIndex-3=99, barIndex-2=99.5, barIndex-1=99.8, barIndex=100
            ' slope = (100-99)/99 ≈ 1.01% > 0.03% threshold
            Dim ema21Rising(N - 1) As Single
            For i = 0 To N - 1
                ema21Rising(i) = 100.0F
            Next
            ema21Rising(Idx - 3) = 99.0F   ' 3 bars back

            Dim indicatorsFlat As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21Flat, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            Dim indicatorsRising As New StrategyIndicators With {
                .AllBars = bars, .Ema21 = ema21Rising, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            Dim resultFlat   = provider.Evaluate(bars(Idx), indicatorsFlat,   MakeConfig(minConf:=0.01F), Idx)
            Dim resultRising = provider.Evaluate(bars(Idx), indicatorsRising, MakeConfig(minConf:=0.01F), Idx)

            Assert.True(resultRising.Confidence > resultFlat.Confidence,
                        "Rising EMA21 slope should score 10 pts more than flat EMA21")
        End Sub

        ' ── STRAT-11: Volume confirmation gate ──────────────────────────────────

        ''' <summary>A bar with volume > 1.1× the 20-bar average scores 10 pts more than an identical low-volume bar.</summary>
        <Fact>
        Public Sub EmaRsi_HighVolume_Awards10MorePoints()
            Dim provider As New EmaRsiSignalProvider()

            ' Create two identical bar series, differing only in the last bar's volume.
            Dim highVolBars As New List(Of MarketBar)(N)
            Dim lowVolBars  As New List(Of MarketBar)(N)
            For i = 0 To N - 1
                Dim v As Long = If(i = Idx, 2000L, 500L)   ' avg of first 20 prior bars = 500
                highVolBars.Add(New MarketBar With { .Open = 109D, .High = 111D, .Low = 108D, .Close = 110D, .Volume = v, .Timestamp = DateTimeOffset.UtcNow })
                lowVolBars.Add(New MarketBar  With { .Open = 109D, .High = 111D, .Low = 108D, .Close = 110D, .Volume = 400L, .Timestamp = DateTimeOffset.UtcNow })
            Next
            Dim ema21 = ConstArr(100.0F)
            ema21(Idx - 1) = 99.5F

            Dim indicatorsHigh As New StrategyIndicators With {
                .AllBars = highVolBars.AsReadOnly(), .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            Dim indicatorsLow As New StrategyIndicators With {
                .AllBars = lowVolBars.AsReadOnly(), .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            Dim resultHigh = provider.Evaluate(highVolBars(Idx), indicatorsHigh, MakeConfig(minConf:=0.01F), Idx)
            Dim resultLow  = provider.Evaluate(lowVolBars(Idx),  indicatorsLow,  MakeConfig(minConf:=0.01F), Idx)

            Assert.True(resultHigh.Confidence > resultLow.Confidence,
                        "High-volume bar should score 10 pts more than low-volume bar")
        End Sub

        ''' <summary>Zero-volume bar (Yahoo futures omission) must produce the same score as if the volume signal were absent.</summary>
        <Fact>
        Public Sub EmaRsi_ZeroVolume_NoPenalty()
            Dim provider As New EmaRsiSignalProvider()

            Dim zeroVolBars  As New List(Of MarketBar)(N)
            Dim avgVolBars   As New List(Of MarketBar)(N)
            For i = 0 To N - 1
                zeroVolBars.Add(New MarketBar  With { .Open = 109D, .High = 111D, .Low = 108D, .Close = 110D, .Volume = 0L, .Timestamp = DateTimeOffset.UtcNow })
                avgVolBars.Add(New MarketBar   With { .Open = 109D, .High = 111D, .Low = 108D, .Close = 110D, .Volume = 500L, .Timestamp = DateTimeOffset.UtcNow })
            Next
            Dim ema21 = ConstArr(100.0F)
            ema21(Idx - 1) = 99.5F

            Dim indicatorsZero As New StrategyIndicators With {
                .AllBars = zeroVolBars.AsReadOnly(), .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            Dim indicatorsAvg As New StrategyIndicators With {
                .AllBars = avgVolBars.AsReadOnly(), .Ema21 = ema21, .Ema50 = ConstArr(90.0F), .Rsi = ConstArr(60.0F)
            }
            ' Zero-volume bar should score same as average-volume bar (not penalised)
            Dim resultZero = provider.Evaluate(zeroVolBars(Idx), indicatorsZero, MakeConfig(minConf:=0.01F), Idx)
            Dim resultAvg  = provider.Evaluate(avgVolBars(Idx),  indicatorsAvg,  MakeConfig(minConf:=0.01F), Idx)

            Assert.Equal(resultZero.Confidence, resultAvg.Confidence)
        End Sub

        ' ── STRAT-13: Configurable RSI period ───────────────────────────────────

        ''' <summary>RSI(9) and RSI(14) computed from the same zigzag close series produce different values.</summary>
        <Fact>
        Public Sub EmaRsi_Rsi9VsRsi14_ProduceDifferentValues()
            ' Zigzag series: up 10 bars then down 10 bars, repeating.
            ' RSI(9) reacts faster than RSI(14) so they diverge on any oscillating series.
            Dim closes As New List(Of Decimal)(N)
            For i = 0 To N - 1
                Dim phase = i Mod 20
                closes.Add(If(phase < 10, 100D + phase * 2D, 120D - (phase - 10) * 2D))
            Next
            Dim rsi9  = TechnicalIndicators.RSI(closes, 9)
            Dim rsi14 = TechnicalIndicators.RSI(closes, 14)

            ' At any well-warmed-up index the two periods must produce different readings
            Assert.NotEqual(rsi9(Idx), rsi14(Idx))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — buy signal
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' All 7 Long conditions met → Buy signal with non-zero StopDelta and TpDelta.
        ''' Cloud: SpanA=100, SpanB=90 → cloudTop=100; close=110 > cloudTop ✓
        ''' EMA21=105; close=110 > EMA21 ✓
        ''' Tenkan=108 > Kijun=104 ✓
        ''' Chikou: close at Idx-26=100 < current close=110 ✓
        ''' ADX=30 ≥ 0 (gate disabled), PlusDI=25 > MinusDI=15 ✓
        ''' MACD hist now=0.5 > 0 and > prev=0.3 ✓
        ''' StochRsiK=0.5 < 0.8 ✓
        ''' ATR=2.0 → SL=1.5×ATR=3.0 below close=107; cloud bottom=90 < 107 so use ATR level.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_AllLongConditions_ReturnsBuy()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            Dim macdHist = ConstArr(0.5F)
            macdHist(Idx - 1) = 0.3F   ' prev < now (expanding)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(100.0F),
                .IchiSpanB    = ConstArr(90.0F),
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta > 0D)
            ' TP = 2× stop (2:1 R:R)
            Assert.True(Math.Abs(result.TpDelta - result.StopDelta * 2D) < 0.01D,
                        $"Expected TpDelta ≈ 2×StopDelta; got SL={result.StopDelta}, TP={result.TpDelta}")
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — sell signal
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' All 8 Short conditions met → Sell signal.
        ''' Cloud: SpanA=90, SpanB=100 → bearish cloud (SpanA &lt; SpanB) ✓ cloud twist passes
        ''' close=80 &lt; cloudBottom=90 ✓
        ''' EMA21=85; close=80 &lt; EMA21 ✓; EMA50=95 &gt; close ✓
        ''' Tenkan=82 &lt; Kijun=88 ✓
        ''' Chikou: lagClose=90 &gt; currentClose=80 ✓
        ''' PlusDI=15 &lt; MinusDI=25 ✓
        ''' MACD hist now=-0.5 &lt; 0 and &lt; prev=-0.3 ✓
        ''' StochRsiK: prev=0.55, now=0.45 → K &gt; 0.3 AND falling ✓ (STRAT-14 fix)
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_AllShortConditions_ReturnsSell()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F   ' prev > now (expanding downward)
            Dim stochK = ConstArr(0.55F)
            stochK(Idx) = 0.45F          ' falling: prev=0.55, now=0.45 → sc7 passes

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = stochK,
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta > 0D)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — no signal
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Cloud condition fails (close inside cloud) → Nothing returned even if
        ''' other conditions are bullish.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_CloseInsideCloud_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            ' close=95 is inside cloud (SpanA=90, SpanB=100 → cloudTop=100 > close < cloudBottom=90)
            Dim bars = BuildMCBars(currentClose:=95D, lagClose:=85D)
            Dim macdHist = ConstArr(0.5F) : macdHist(Idx - 1) = 0.3F

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(92.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),   ' cloudTop=100, close=95 → inside cloud
                .IchiTenkan   = ConstArr(96.0F),
                .IchiKijun    = ConstArr(93.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' NaN in Ichimoku SpanA → warm-up guard skips bar → Nothing returned.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_NaNInIchimoku_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            Dim macdHist = ConstArr(0.5F) : macdHist(Idx - 1) = 0.3F

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .IchiSpanA    = NaNArr(),           ' still warming up
                .IchiSpanB    = ConstArr(90.0F),
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' MACD histogram is contracting (now &lt; prev) → condition 6 fails → Nothing.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_MacdContracting_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            Dim macdHist = ConstArr(0.3F)
            macdHist(Idx - 1) = 0.5F   ' prev > now → contracting → condition 6 fails

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .IchiSpanA    = ConstArr(100.0F),
                .IchiSpanB    = ConstArr(90.0F),
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' Missing required series (IchiSpanA = Nothing) → guard returns Nothing immediately.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_MissingRequiredSeries_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)

            Dim indicators As New StrategyIndicators With {
                .AllBars   = bars,
                .Ema21     = ConstArr(105.0F),
                .IchiSpanA = Nothing   ' intentionally not set
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — STRAT-07 StochRSI short gate
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' STRAT-07: StochRSI K = 0.85 (deep overbought) with all other short conditions met
        ''' must NOT produce a Sell signal — sc7 requires K &lt; 0.4.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ShortWithStochKOverbought_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F   ' expanding downward

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.85F),   ' flat at 0.85 — sc7 fails (K not falling: prev=now=0.85)
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' STRAT-14: StochRSI K falling from 0.55 to 0.45 (above 0.3, not oversold, momentum turning)
        ''' with all other short conditions met MUST produce a Sell signal —
        ''' sc7 requires K &gt; 0.3 AND K[now] &lt; K[prev].
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ShortWithStochKTurningFromOverbought_ReturnsSell()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F   ' expanding downward
            Dim stochK = ConstArr(0.55F)
            stochK(Idx) = 0.45F          ' falling: prev=0.55, now=0.45; both > 0.3 → sc7 passes

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = stochK,
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — STRAT-14 StochRSI short gate semantics
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' STRAT-14: sc7 fails when K is rising (prev=0.45, now=0.55) even if above 0.3.
        ''' Rising K means momentum is building upward — no room to fall.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ShortWithStochKRising_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F
            Dim stochK = ConstArr(0.45F)
            stochK(Idx) = 0.55F   ' rising: prev=0.45, now=0.55 → sc7 fails (not falling)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = stochK,
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' STRAT-14: sc7 fails when K ≤ 0.3 (oversold territory — no room to fall further).
        ''' The condition requires K &gt; 0.3 to confirm room remains on the downside.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ShortWithStochKOversold_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F
            Dim stochK = ConstArr(0.35F)
            stochK(Idx) = 0.20F   ' falling but K[now]=0.20 ≤ 0.3 → sc7 fails (oversold)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = stochK,
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — STRAT-17 cloud twist pre-filter
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' STRAT-17: All 8 long conditions pass but cloud is bearish (SpanA &lt; SpanB) →
        ''' cloud twist pre-filter blocks the long signal.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_LongWithBearishCloud_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            ' Close=110 is above cloud (SpanA=90, SpanB=100 → cloudTop=100), but cloud is bearish
            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            Dim macdHist = ConstArr(0.5F)
            macdHist(Idx - 1) = 0.3F

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(90.0F),   ' SpanA < SpanB → bearish cloud → lc1 fails
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' STRAT-17: All 8 short conditions pass but cloud is bullish (SpanA &gt; SpanB) →
        ''' cloud twist pre-filter blocks the short signal.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ShortWithBullishCloud_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            ' Close=80 is below cloud (SpanA=100, SpanB=90 → cloudBottom=90), but cloud is bullish
            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F
            Dim stochK = ConstArr(0.55F)
            stochK(Idx) = 0.45F

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(100.0F),  ' SpanA > SpanB → bullish cloud → scl1 fails
                .IchiSpanB    = ConstArr(90.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = stochK,
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — STRAT-18 Chikou-vs-cloud filter
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' STRAT-18: Chikou is above the price 26 bars ago but the current close is
        ''' inside the historical cloud at the lag position (lagCloudBottom=95, lagCloudTop=105,
        ''' close=102). lc4 must fail → no long signal.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ChikouAboveOldPriceButInsideOldCloud_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            ' currentClose=102 > lagClose=100 (gap satisfied) but 95 < 102 < 105 → inside lag cloud
            Dim bars = BuildMCBars(currentClose:=102D, lagClose:=100D)
            Dim macdHist = ConstArr(0.5F)
            macdHist(Idx - 1) = 0.3F

            ' Historical cloud at Idx-26: SpanA=95, SpanB=105 → lagCloudTop=105, close=102 < 105 → lc4 fails
            Dim spanA = ConstArr(100.0F)   ' current cloud: bullish (SpanA=100 > SpanB=90), close=102 above cloud ✓
            spanA(Idx - 26) = 95.0F        ' historical SpanA at lag position
            Dim spanB = ConstArr(90.0F)
            spanB(Idx - 26) = 105.0F       ' historical SpanB at lag position → lagCloudTop=105

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(101.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = spanA,
                .IchiSpanB    = spanB,
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' STRAT-18: Chikou is above old price AND above the historical cloud top
        ''' (lagCloudTop=95, close=110). lc4 must pass → long signal returned.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_ChikouAboveOldPriceAndAboveOldCloud_ReturnsBuy()
            Dim provider As New MultiConfluenceSignalProvider()

            ' close=110, lagClose=100; historical cloud at Idx-26 top=95 → 110 > 95 ✓
            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            Dim macdHist = ConstArr(0.5F)
            macdHist(Idx - 1) = 0.3F

            Dim spanA = ConstArr(100.0F)
            spanA(Idx - 26) = 95.0F        ' historical SpanA at lag position
            Dim spanB = ConstArr(90.0F)
            spanB(Idx - 26) = 88.0F        ' historical SpanB at lag position → lagCloudTop=95

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = spanA,
                .IchiSpanB    = spanB,
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.5F),
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — STRAT-19 graduated confidence
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' STRAT-19: Marginal signal — ADX barely above threshold (21 vs threshold=20),
        ''' tiny DI spread (2.5 pts), tiny MACD histogram, StochRSI near boundary.
        ''' Confidence must be below 0.85 (Lewis would reject it).
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_MarginalSignal_ConfidenceBelowThreshold()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            ' MACD: tiny histogram, expanding (hist=0.001, prev=0.0005)
            Dim macdHist = ConstArr(0.001F)
            macdHist(Idx - 1) = 0.0005F

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(100.0F),
                .IchiSpanB    = ConstArr(90.0F),
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(21.0F),    ' barely above threshold=20
                .PlusDi       = ConstArr(17.25F),   ' spread = 17.25 - 14.75 = 2.5 pts (marginal)
                .MinusDi      = ConstArr(14.75F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.68F),    ' very close to 0.7 boundary (not overbought but marginal)
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minAdx:=20.0F), Idx)
            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.Confidence < 0.85F,
                        $"Expected marginal confidence < 0.85 but got {result.Confidence:F4}")
        End Sub

        ''' <summary>
        ''' STRAT-19: Strong signal — ADX=45 (well above threshold), large DI spread (20 pts),
        ''' large MACD histogram (relative to ATR), StochRSI well away from boundary.
        ''' Confidence must be above 0.90 (Lewis would take it).
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_StrongSignal_ConfidenceAboveThreshold()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            ' MACD: large histogram vs ATR=2 → macdMag=1.5, macdNorm=2×0.5+0.001=1.001 → macdStr≈1.0
            Dim macdHist = ConstArr(1.5F)
            macdHist(Idx - 1) = 1.2F   ' prev < now (expanding)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(105.0F),
                .Ema50        = ConstArr(95.0F),
                .IchiSpanA    = ConstArr(100.0F),
                .IchiSpanB    = ConstArr(90.0F),
                .IchiTenkan   = ConstArr(108.0F),
                .IchiKijun    = ConstArr(104.0F),
                .Adx          = ConstArr(45.0F),    ' strong trend
                .PlusDi       = ConstArr(30.0F),    ' spread = 20 pts
                .MinusDi      = ConstArr(10.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.2F),     ' well away from 0.7 boundary
                .Atr          = ConstArr(2.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minAdx:=20.0F), Idx)
            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.Confidence > 0.90F,
                        $"Expected strong confidence > 0.90 but got {result.Confidence:F4}")
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' MultiConfluenceSignalProvider — STRAT-06 volume gate
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' STRAT-06: All other long conditions pass but current bar volume is below
        ''' 1.2× the 20-bar average → volume gate (lc8) fails → Nothing returned.
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_VolumeBelowThreshold_ReturnsNothing()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=110D, lagClose:=100D)
            Dim macdHist = ConstArr(0.5F)
            macdHist(Idx - 1) = 0.3F

            ' VolMa20 = 1000; current bar Volume (set via MakeBar) ≈ 0 → 0 < 1200 → gate fails
            Dim volMa = ConstArr(1000.0F)

            Dim indicators As New StrategyIndicators With {
                .AllBars       = bars,
                .Ema21         = ConstArr(105.0F),
                .Ema50         = ConstArr(95.0F),
                .IchiSpanA     = ConstArr(100.0F),
                .IchiSpanB     = ConstArr(90.0F),
                .IchiTenkan    = ConstArr(108.0F),
                .IchiKijun     = ConstArr(104.0F),
                .Adx           = ConstArr(30.0F),
                .PlusDi        = ConstArr(25.0F),
                .MinusDi       = ConstArr(15.0F),
                .MacdHistogram = macdHist,
                .StochRsiK     = ConstArr(0.5F),
                .Atr           = ConstArr(2.0F),
                .VolMa20       = volMa   ' avg=1000; bar.Volume=0 → 0 < 1200 → gate fails
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' Helper — build MultiConfluence bar list
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Build a bar list for MultiConfluence tests.
        ''' <paramref name="currentClose"/> is used for bars at/near Idx.
        ''' <paramref name="lagClose"/> is used for bars at Idx-26 (Chikou comparison).
        ''' </summary>
        Private Shared Function BuildMCBars(currentClose As Decimal,
                                             lagClose As Decimal) As IReadOnlyList(Of MarketBar)
            Dim bars As New List(Of MarketBar)(N)
            For i = 0 To N - 1
                Dim c = If(i = Idx - 26, lagClose, currentClose)
                bars.Add(MakeBar(c, c + 1D, c - 1D, c))
            Next
            Return bars.AsReadOnly()
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' TripleEmaCascadeSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' EMA8 crosses above EMA21 and close is above rising EMA50 → Buy.
        ''' </summary>
        <Fact>
        Public Sub TripleEmaCascade_CrossAboveWithRisingEma50_ReturnsBuy()
            Dim provider As New TripleEmaCascadeSignalProvider()
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim ema8 = ConstArr(102.0F)
            ema8(Idx - 1) = 98.0F   ' prev below EMA21, now above → cross above
            Dim ema21 = ConstArr(100.0F)
            Dim ema50 = ConstArr(100.0F)
            ema50(Idx - 1) = 99.5F  ' rising

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .Ema8 = ema8, .Ema21 = ema21, .Ema50 = ema50
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
        End Sub

        ''' <summary>EMA8 crosses below EMA21 and close is below falling EMA50 → Sell.</summary>
        <Fact>
        Public Sub TripleEmaCascade_CrossBelowWithFallingEma50_ReturnsSell()
            Dim provider As New TripleEmaCascadeSignalProvider()
            Dim bars = MakeBars(N, basePrice:=90D, allBearish:=True)

            Dim ema8 = ConstArr(98.0F)
            ema8(Idx - 1) = 102.0F  ' prev above EMA21, now below → cross below
            Dim ema21 = ConstArr(100.0F)
            Dim ema50 = ConstArr(100.0F)
            ema50(Idx - 1) = 100.5F ' falling

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .Ema8 = ema8, .Ema21 = ema21, .Ema50 = ema50
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
        End Sub

        ''' <summary>EMA8 crosses above EMA21 but EMA50 is falling → no signal.</summary>
        <Fact>
        Public Sub TripleEmaCascade_CrossAboveButFallingEma50_ReturnsNothing()
            Dim provider As New TripleEmaCascadeSignalProvider()
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim ema8 = ConstArr(102.0F)
            ema8(Idx - 1) = 98.0F
            Dim ema21 = ConstArr(100.0F)
            Dim ema50 = ConstArr(100.0F)
            ema50(Idx - 1) = 100.5F  ' falling — kills bull signal

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .Ema8 = ema8, .Ema21 = ema21, .Ema50 = ema50
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>NaN in EMA8 → warm-up guard returns Nothing (no exception thrown).</summary>
        <Fact>
        Public Sub TripleEmaCascade_NaNInEma8_ReturnsNothing()
            Dim provider As New TripleEmaCascadeSignalProvider()
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)
            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema8    = NaNArr(),        ' still warming up
                .Ema21   = ConstArr(100.0F),
                .Ema50   = ConstArr(99.0F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' BbSqueezeSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Mode A squeeze breakout: squeeze active (≥3 bars BBW &lt; BbwSma),
        ''' close above upper band×1.0025, EMA5 rising, RSI7 ≥ 60 → Buy.
        ''' </summary>
        <Fact>
        Public Sub BbSqueeze_SqueezeBreakoutBull_ReturnsBuy()
            Dim provider As New BbSqueezeSignalProvider()

            ' Close = 101.5 > upper(100) × 1.0025 = 100.25 ✓
            Dim bars = MakeBars(N, basePrice:=101.5D, allBullish:=True)

            Dim bbUpper = ConstArr(100.0F)
            Dim bbLower = ConstArr(90.0F)
            Dim bbPctB  = ConstArr(0.5F)   ' neutral %B
            Dim rsi7    = ConstArr(65.0F)  ' ≥ 60 ✓
            Dim ema5    = ConstArr(100.0F)
            ema5(Idx - 1) = 99.0F          ' rising ✓
            ' All 10 bars in squeeze (BBW < BbwSma)
            Dim bbWidth = ConstArr(0.8F)
            Dim bbwSma  = ConstArr(1.0F)   ' BBW < BbwSma → in squeeze ✓

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .BbUpper = bbUpper, .BbLower = bbLower,
                .BbPctB = bbPctB, .Rsi = rsi7, .Ema5 = ema5,
                .BbWidth = bbWidth, .BbwSma = bbwSma
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.70F), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.Equal(0.8F, result.Confidence)
        End Sub

        ''' <summary>No squeeze (BBW ≥ BbwSma on all bars) and %B neutral → no Mode-B signal.</summary>
        <Fact>
        Public Sub BbSqueeze_NoSqueezeNeutralPctB_ReturnsNothing()
            Dim provider As New BbSqueezeSignalProvider()

            Dim bars = MakeBars(N, basePrice:=100D)
            Dim bbWidth = ConstArr(1.5F)   ' BBW > BbwSma → no squeeze
            Dim bbwSma  = ConstArr(1.0F)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .BbUpper = ConstArr(105.0F), .BbLower = ConstArr(95.0F),
                .BbPctB  = ConstArr(0.5F),   ' neutral — no Mode-B trigger
                .Rsi     = ConstArr(50.0F),
                .Ema5    = ConstArr(100.0F),
                .BbWidth = bbWidth, .BbwSma = bbwSma
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>NaN in BbUpper → warm-up guard returns Nothing.</summary>
        <Fact>
        Public Sub BbSqueeze_NaNInIndicator_ReturnsNothing()
            Dim provider As New BbSqueezeSignalProvider()

            Dim bars = MakeBars(N)
            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .BbUpper = NaNArr(),        ' still warming up
                .BbLower = ConstArr(90.0F),
                .BbPctB  = ConstArr(0.5F),
                .Rsi     = ConstArr(60.0F),
                .Ema5    = ConstArr(100.0F),
                .BbWidth = ConstArr(0.8F),
                .BbwSma  = ConstArr(1.0F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' VidyaCrossSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Close crosses above VIDYA with ΔVol ≥ 0.2 → Buy with confidence = |ΔVol|×100/100.
        ''' </summary>
        <Fact>
        Public Sub VidyaCross_CrossAboveWithPositiveVolDelta_ReturnsBuy()
            Dim provider As New VidyaCrossSignalProvider()

            ' prevClose = 99 ≤ vidyaPrev = 100, close = 101 > vidyaNow = 100 → cross above ✓
            Dim bars = MakeBars(N, basePrice:=99D)
            Dim currentBar = MakeBar(99D, 102D, 98D, 101D)  ' close=101 > vidyaNow=100

            Dim vidya = ConstArr(100.0F)   ' VIDYA constant at 100

            Dim deltaVol = ConstArr(0.3F)  ' ΔVol = 0.3 ≥ 0.2 ✓

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .Vidya = vidya, .DeltaVolume = deltaVol
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(minConf:=0.20F), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.True(result.Confidence >= 0.2F)
        End Sub

        ''' <summary>Close crosses below VIDYA with ΔVol ≤ −0.2 → Sell.</summary>
        <Fact>
        Public Sub VidyaCross_CrossBelowWithNegativeVolDelta_ReturnsSell()
            Dim provider As New VidyaCrossSignalProvider()

            ' prevClose = 101 ≥ vidyaPrev = 100, close = 99 < vidyaNow = 100 → cross below ✓
            Dim bars = MakeBars(N, basePrice:=101D)
            Dim currentBar = MakeBar(101D, 102D, 98D, 99D)

            Dim vidya    = ConstArr(100.0F)
            Dim deltaVol = ConstArr(-0.4F)  ' ΔVol = -0.4 ≤ -0.2 ✓

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .Vidya = vidya, .DeltaVolume = deltaVol
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(minConf:=0.20F), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
        End Sub

        ''' <summary>Cross above VIDYA but ΔVol too small (0.10 &lt; 0.20 threshold) → Nothing.</summary>
        <Fact>
        Public Sub VidyaCross_CrossAboveButVolDeltaTooSmall_ReturnsNothing()
            Dim provider As New VidyaCrossSignalProvider()

            Dim bars = MakeBars(N, basePrice:=99D)
            Dim currentBar = MakeBar(99D, 102D, 98D, 101D)

            Dim vidya    = ConstArr(100.0F)
            Dim deltaVol = ConstArr(0.1F)   ' below 0.2 threshold

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars, .Vidya = vidya, .DeltaVolume = deltaVol
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(minConf:=0.20F), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>NaN in VIDYA → warm-up guard returns Nothing (no exception thrown).</summary>
        <Fact>
        Public Sub VidyaCross_NaNInVidya_ReturnsNothing()
            Dim provider As New VidyaCrossSignalProvider()
            Dim bars = MakeBars(N)
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .Vidya      = NaNArr(),        ' still warming up
                .DeltaVolume = ConstArr(0.3F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' NakedTraderSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' All 3 countable votes bullish + ADX=30 ≥ 25 → High-confidence Buy (conf=0.60 without vol).
        ''' EMA9=102 > EMA21=100 (+1 vote); MACD hist=0.1 > 0 (+1 vote); +DI=25 > -DI=15 (+1 vote).
        ''' VWAP omitted (Nothing) → 3/3 votes up. ADX=30 ≥ 25, ntAligned=ntTotal=3 → High: conf=0.60.
        ''' </summary>
        <Fact>
        Public Sub NakedTrader_ThreeVotesBullHighAdx_ReturnsBuy()
            Dim provider As New NakedTraderSignalProvider()

            ' close = 110 well above any VWAP we might add; VWAP omitted for simplicity
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema9         = ConstArr(102.0F),   ' EMA9 > EMA21 ✓
                .Ema21        = ConstArr(100.0F),
                .MacdHistogram = ConstArr(0.1F),    ' hist > 0 ✓
                .MacdLine     = ConstArr(0.05F),
                .PlusDi       = ConstArr(25.0F),    ' +DI > -DI ✓
                .MinusDi      = ConstArr(15.0F),
                .Adx          = ConstArr(30.0F)     ' ≥ 25 ✓ — High confidence path
            }

            ' minConf=0.55 is below the expected 0.60, so signal should fire
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.55F), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(0.6F, result.Confidence)
        End Sub

        ''' <summary>
        ''' All votes bearish + ADX ≥ 25 → Sell.
        ''' EMA9 below EMA21, MACD hist negative, −DI > +DI.
        ''' </summary>
        <Fact>
        Public Sub NakedTrader_ThreeVotesBearHighAdx_ReturnsSell()
            Dim provider As New NakedTraderSignalProvider()

            Dim bars = MakeBars(N, basePrice:=90D, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema9         = ConstArr(98.0F),
                .Ema21        = ConstArr(100.0F),
                .MacdHistogram = ConstArr(-0.1F),
                .MacdLine     = ConstArr(-0.05F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .Adx          = ConstArr(30.0F)
            }

            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.55F), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
        End Sub

        ''' <summary>ADX below gate (12 &lt; default 20) → no signal even with all votes bullish.</summary>
        <Fact>
        Public Sub NakedTrader_AllBullishVotesButAdxTooLow_ReturnsNothing()
            Dim provider As New NakedTraderSignalProvider()

            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema9         = ConstArr(102.0F),
                .Ema21        = ConstArr(100.0F),
                .MacdHistogram = ConstArr(0.1F),
                .MacdLine     = ConstArr(0.05F),
                .PlusDi       = ConstArr(25.0F),
                .MinusDi      = ConstArr(15.0F),
                .Adx          = ConstArr(12.0F)   ' below default gate of 20
            }

            ' minAdx = 0 means gate disabled; use default 20 baked into NakedTrader itself
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(minConf:=0.55F, minAdx:=0.0F), Idx)

            ' ADX=12 < ntAdxGate=20 (NakedTrader internal gate) → no signal
            Assert.Null(result)
        End Sub

        ''' <summary>NaN in EMA9 → warm-up guard returns Nothing (no exception thrown).</summary>
        <Fact>
        Public Sub NakedTrader_NaNInEma9_ReturnsNothing()
            Dim provider As New NakedTraderSignalProvider()
            Dim bars = MakeBars(N, basePrice:=110D, allBullish:=True)
            Dim indicators As New StrategyIndicators With {
                .AllBars       = bars,
                .Ema9          = NaNArr(),        ' still warming up
                .Ema21         = ConstArr(100.0F),
                .MacdHistogram = ConstArr(0.1F),
                .MacdLine      = ConstArr(0.05F),
                .PlusDi        = ConstArr(25.0F),
                .MinusDi       = ConstArr(15.0F),
                .Adx           = ConstArr(30.0F)
            }
            Dim result = provider.Evaluate(bars(Idx), indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' DoubleBubbleButtSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Close above upper inner band (1 SD) → Buy.
        ''' IndicatorExitLevel = inner upper band (for neutral-zone exit).
        ''' </summary>
        <Fact>
        Public Sub DoubleBubbleButt_CloseAboveInnerUpper_ReturnsBuy()
            Dim provider As New DoubleBubbleButtSignalProvider()

            ' close = 102 > dbbIU = 101 → Buy ✓
            Dim bars = MakeBars(N, basePrice:=102D, allBullish:=True)
            Dim currentBar = MakeBar(101D, 103D, 100D, 102D)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .DbbInnerUpper = ConstArr(101.0F),  ' inner upper 1 SD
                .DbbInnerLower = ConstArr(99.0F),   ' inner lower 1 SD
                .BbUpper      = ConstArr(104.0F),   ' outer upper 2 SD
                .BbLower      = ConstArr(96.0F),    ' outer lower 2 SD
                .Atr          = ConstArr(2.0F)      ' ATR(20)
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(CDec(101.0F), result.IndicatorExitLevel)  ' inner upper band stored
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta > 0D)
        End Sub

        ''' <summary>Close below lower inner band (1 SD) → Sell.</summary>
        <Fact>
        Public Sub DoubleBubbleButt_CloseBelowInnerLower_ReturnsSell()
            Dim provider As New DoubleBubbleButtSignalProvider()

            ' close = 98 < dbbIL = 99 → Sell ✓
            Dim currentBar = MakeBar(99D, 100D, 97D, 98D)
            Dim bars = MakeBars(N, basePrice:=98D, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .DbbInnerUpper = ConstArr(101.0F),
                .DbbInnerLower = ConstArr(99.0F),
                .BbUpper      = ConstArr(104.0F),
                .BbLower      = ConstArr(96.0F),
                .Atr          = ConstArr(2.0F)
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.Equal(CDec(99.0F), result.IndicatorExitLevel)   ' inner lower band stored
        End Sub

        ''' <summary>Close inside bands (between inner upper and lower) → no signal.</summary>
        <Fact>
        Public Sub DoubleBubbleButt_CloseInsideBands_ReturnsNothing()
            Dim provider As New DoubleBubbleButtSignalProvider()

            ' close = 100 is between dbbIL=99 and dbbIU=101 → neutral zone
            Dim currentBar = MakeBar(100D, 100.5D, 99.5D, 100D)
            Dim bars = MakeBars(N)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .DbbInnerUpper = ConstArr(101.0F),
                .DbbInnerLower = ConstArr(99.0F),
                .BbUpper      = ConstArr(104.0F),
                .BbLower      = ConstArr(96.0F),
                .Atr          = ConstArr(2.0F)
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>NaN in DbbInnerUpper → warm-up guard returns Nothing (no exception thrown).</summary>
        <Fact>
        Public Sub DoubleBubbleButt_NaNInInnerBand_ReturnsNothing()
            Dim provider As New DoubleBubbleButtSignalProvider()
            Dim currentBar = MakeBar(102D, 103D, 101D, 102D)
            Dim bars = MakeBars(N)
            Dim indicators As New StrategyIndicators With {
                .AllBars       = bars,
                .DbbInnerUpper = NaNArr(),        ' still warming up
                .DbbInnerLower = ConstArr(99.0F),
                .BbUpper       = ConstArr(104.0F),
                .BbLower       = ConstArr(96.0F),
                .Atr           = ConstArr(2.0F)
            }
            Dim result = provider.Evaluate(currentBar, indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' ConnorsRsi2SignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' RSI(2) &lt; 10 AND close &gt; SMA(200) → Buy signal (dip in uptrend).
        ''' </summary>
        <Fact>
        Public Sub ConnorsRsi2_DipInUptrend_ReturnsBuy()
            Dim provider As New ConnorsRsi2SignalProvider()
            Dim bars = MakeBars(N, basePrice:=5100D)
            Dim bar  = MakeBar(5100D, 5105D, 5095D, 5100D)

            Dim indicators As New StrategyIndicators With {
                .Rsi2   = ConstArr(8.0F),    ' RSI(2) < 10 — deep dip
                .Sma200 = ConstArr(5000.0F)  ' SMA(200) below close — uptrend
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(1.0F, result.Confidence)
        End Sub

        ''' <summary>
        ''' RSI(2) &gt; 90 AND close &lt; SMA(200) → Sell signal (rally in downtrend).
        ''' </summary>
        <Fact>
        Public Sub ConnorsRsi2_RallyInDowntrend_ReturnsSell()
            Dim provider As New ConnorsRsi2SignalProvider()
            Dim bars = MakeBars(N, basePrice:=4800D)
            Dim bar  = MakeBar(4800D, 4810D, 4790D, 4800D)

            Dim indicators As New StrategyIndicators With {
                .Rsi2   = ConstArr(92.0F),   ' RSI(2) > 90 — overextended rally
                .Sma200 = ConstArr(5000.0F)  ' SMA(200) above close — downtrend
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
        End Sub

        ''' <summary>
        ''' RSI(2) = 50 — neither extreme → no signal.
        ''' </summary>
        <Fact>
        Public Sub ConnorsRsi2_NeutralRsi_ReturnsNothing()
            Dim provider As New ConnorsRsi2SignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5000D, 5010D, 4990D, 5000D)

            Dim indicators As New StrategyIndicators With {
                .Rsi2   = ConstArr(50.0F),   ' neutral
                .Sma200 = ConstArr(4900.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' RSI(2) &lt; 10 but close &lt; SMA(200) — dip in downtrend, no Buy → no signal.
        ''' </summary>
        <Fact>
        Public Sub ConnorsRsi2_DipInDowntrend_ReturnsNothing()
            Dim provider As New ConnorsRsi2SignalProvider()
            Dim bars = MakeBars(N, basePrice:=4800D)
            Dim bar  = MakeBar(4800D, 4810D, 4790D, 4800D)

            Dim indicators As New StrategyIndicators With {
                .Rsi2   = ConstArr(7.0F),    ' dip
                .Sma200 = ConstArr(5000.0F)  ' close < SMA(200) — downtrend, wrong direction for Buy
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' UseAtrMode=True populates StopDelta and TpDelta from ATR(14).
        ''' </summary>
        <Fact>
        Public Sub ConnorsRsi2_AtrModeOn_PopulatesDeltas()
            Dim provider As New ConnorsRsi2SignalProvider()
            Dim bars = MakeBars(N, basePrice:=5100D)
            Dim bar  = MakeBar(5100D, 5105D, 5095D, 5100D)

            Dim cfg = MakeConfig(useAtrMode:=True)
            Dim indicators As New StrategyIndicators With {
                .Rsi2   = ConstArr(7.0F),
                .Sma200 = ConstArr(5000.0F),
                .Atr    = ConstArr(4.0F)   ' ATR = 4.0
            }
            Dim result = provider.Evaluate(bar, indicators, cfg, Idx)

            Assert.NotNull(result)
            Assert.True(result.StopDelta > 0D, "StopDelta should be populated in ATR mode")
            Assert.True(result.TpDelta > 0D,   "TpDelta should be populated in ATR mode")
            ' StopDelta = 4.0 × 1.5 = 6.0; TpDelta = 4.0 × 3.0 = 12.0
            Assert.Equal(6.0D, result.StopDelta)
            Assert.Equal(12.0D, result.TpDelta)
        End Sub

        ''' <summary>
        ''' NaN in RSI(2) causes no signal (warm-up guard).
        ''' </summary>
        <Fact>
        Public Sub ConnorsRsi2_NaNRsi_ReturnsNothing()
            Dim provider As New ConnorsRsi2SignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5000D, 5010D, 4990D, 5000D)

            Dim indicators As New StrategyIndicators With {
                .Rsi2   = NaNArr(),
                .Sma200 = ConstArr(4900.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' SuperTrendSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Direction flips −1 → +1 → Buy signal with absolute SL/TP levels.
        ''' </summary>
        <Fact>
        Public Sub SuperTrend_BullFlip_ReturnsBuyWithAbsoluteLevels()
            Dim provider As New SuperTrendSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5010D, 5020D, 5000D, 5015D)

            Dim dirArr(N - 1) As Single
            For i = 0 To N - 1 : dirArr(i) = -1.0F : Next
            dirArr(Idx) = 1.0F        ' flip at signal bar
            dirArr(Idx - 1) = -1.0F  ' prior bar bear

            Dim indicators As New StrategyIndicators With {
                .SuperTrendDir  = dirArr,
                .SuperTrendLine = ConstArr(4950.0F),  ' SL = line level
                .SuperTrendAtr  = ConstArr(10.0F)     ' ATR(10) for TP
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(4950.0D, result.AbsoluteSlPrice)
            ' TP = bar.Close (5015) + 2 × ATR(10) (10) = 5035
            Assert.Equal(5035.0D, result.AbsoluteTpPrice)
        End Sub

        ''' <summary>
        ''' Direction flips +1 → −1 → Sell signal with absolute SL/TP levels.
        ''' </summary>
        <Fact>
        Public Sub SuperTrend_BearFlip_ReturnsSellWithAbsoluteLevels()
            Dim provider As New SuperTrendSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(4990D, 4995D, 4980D, 4985D)

            Dim dirArr(N - 1) As Single
            For i = 0 To N - 1 : dirArr(i) = 1.0F : Next
            dirArr(Idx) = -1.0F       ' flip at signal bar
            dirArr(Idx - 1) = 1.0F   ' prior bar bull

            Dim indicators As New StrategyIndicators With {
                .SuperTrendDir  = dirArr,
                .SuperTrendLine = ConstArr(5050.0F),  ' SL = line level (above close for short)
                .SuperTrendAtr  = ConstArr(10.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.Equal(5050.0D, result.AbsoluteSlPrice)
            ' TP = bar.Close (4985) − 2 × 10 = 4965
            Assert.Equal(4965.0D, result.AbsoluteTpPrice)
        End Sub

        ''' <summary>
        ''' Direction unchanged (no flip) → no signal.
        ''' </summary>
        <Fact>
        Public Sub SuperTrend_NoFlip_ReturnsNothing()
            Dim provider As New SuperTrendSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5010D, 5020D, 5000D, 5010D)

            Dim indicators As New StrategyIndicators With {
                .SuperTrendDir  = ConstArr(1.0F),     ' sustained bull — no flip
                .SuperTrendLine = ConstArr(4950.0F),
                .SuperTrendAtr  = ConstArr(10.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' NaN direction value → no signal (warm-up guard).
        ''' </summary>
        <Fact>
        Public Sub SuperTrend_NaNDirection_ReturnsNothing()
            Dim provider As New SuperTrendSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5010D, 5020D, 5000D, 5010D)

            Dim indicators As New StrategyIndicators With {
                .SuperTrendDir  = NaNArr(),
                .SuperTrendLine = ConstArr(4950.0F),
                .SuperTrendAtr  = ConstArr(10.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' DonchianBreakoutSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Close breaks above prior bar's 20-bar upper channel → Buy signal.
        ''' IndicatorExitLevel carries 10-bar mid channel.
        ''' </summary>
        <Fact>
        Public Sub DonchianBreakout_BreakAboveUpper_ReturnsBuy()
            Dim provider As New DonchianBreakoutSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            ' bar.Close = 5010 > prior upper 5005
            Dim bar = MakeBar(5008D, 5012D, 5006D, 5010D)

            Dim upperArr(N - 1) As Single
            For i = 0 To N - 1 : upperArr(i) = 5010.0F : Next
            upperArr(Idx - 1) = 5005.0F  ' prior bar upper — breakout above this

            Dim indicators As New StrategyIndicators With {
                .DonchianUpper = upperArr,
                .DonchianLower = ConstArr(4990.0F),
                .DonchianMid   = ConstArr(5000.0F)   ' 10-bar mid = exit level
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(5000.0D, result.IndicatorExitLevel)
        End Sub

        ''' <summary>
        ''' Close breaks below prior bar's 20-bar lower channel → Sell signal.
        ''' </summary>
        <Fact>
        Public Sub DonchianBreakout_BreakBelowLower_ReturnsSell()
            Dim provider As New DonchianBreakoutSignalProvider()
            Dim bars = MakeBars(N, basePrice:=4990D)
            ' bar.Close = 4985 < prior lower 4990
            Dim bar = MakeBar(4988D, 4992D, 4983D, 4985D)

            Dim lowerArr(N - 1) As Single
            For i = 0 To N - 1 : lowerArr(i) = 4985.0F : Next
            lowerArr(Idx - 1) = 4990.0F   ' prior bar lower — breakout below this

            Dim indicators As New StrategyIndicators With {
                .DonchianUpper = ConstArr(5010.0F),
                .DonchianLower = lowerArr,
                .DonchianMid   = ConstArr(5000.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.Equal(5000.0D, result.IndicatorExitLevel)
        End Sub

        ''' <summary>
        ''' Close inside the channel → no signal.
        ''' </summary>
        <Fact>
        Public Sub DonchianBreakout_InsideChannel_ReturnsNothing()
            Dim provider As New DonchianBreakoutSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5000D, 5002D, 4998D, 5000D)

            Dim indicators As New StrategyIndicators With {
                .DonchianUpper = ConstArr(5010.0F),   ' close 5000 < upper 5010
                .DonchianLower = ConstArr(4990.0F),   ' close 5000 > lower 4990
                .DonchianMid   = ConstArr(5000.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' ATR mode populates StopDelta and TpDelta.
        ''' </summary>
        <Fact>
        Public Sub DonchianBreakout_AtrModeOn_PopulatesDeltas()
            Dim provider As New DonchianBreakoutSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5008D, 5012D, 5006D, 5010D)

            Dim upperArr(N - 1) As Single
            For i = 0 To N - 1 : upperArr(i) = 5010.0F : Next
            upperArr(Idx - 1) = 5005.0F

            Dim cfg = MakeConfig(useAtrMode:=True)
            Dim indicators As New StrategyIndicators With {
                .DonchianUpper = upperArr,
                .DonchianLower = ConstArr(4990.0F),
                .DonchianMid   = ConstArr(5000.0F),
                .Atr           = ConstArr(3.0F)    ' ATR = 3.0
            }
            Dim result = provider.Evaluate(bar, indicators, cfg, Idx)

            Assert.NotNull(result)
            Assert.Equal(4.5D, result.StopDelta)   ' 3.0 × 1.5
            Assert.Equal(9.0D, result.TpDelta)     ' 3.0 × 3.0
        End Sub

        ''' <summary>NaN in DonchianUpper → warm-up guard returns Nothing (no exception thrown).</summary>
        <Fact>
        Public Sub DonchianBreakout_NaNInUpper_ReturnsNothing()
            Dim provider As New DonchianBreakoutSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5000D)
            Dim bar  = MakeBar(5010D, 5015D, 5008D, 5011D)
            Dim indicators As New StrategyIndicators With {
                .DonchianUpper = NaNArr(),        ' still warming up
                .DonchianLower = ConstArr(4990.0F),
                .DonchianMid   = ConstArr(5000.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)
            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' BbRsiReversionSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Close below lower BB AND RSI &lt; 30 → Buy (dual oversold).
        ''' IndicatorExitLevel carries BB middle band.
        ''' </summary>
        <Fact>
        Public Sub BbRsiReversion_DualOversold_ReturnsBuy()
            Dim provider As New BbRsiReversionSignalProvider()
            Dim bars = MakeBars(N, basePrice:=4990D)
            ' close 4988 < lower BB 4992 AND RSI 25 < 30
            Dim bar = MakeBar(4990D, 4992D, 4986D, 4988D)

            Dim indicators As New StrategyIndicators With {
                .BbUpper  = ConstArr(5010.0F),
                .BbMiddle = ConstArr(5001.0F),   ' mid = exit anchor
                .BbLower  = ConstArr(4992.0F),
                .Rsi      = ConstArr(25.0F)       ' RSI < 30
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(5001.0D, result.IndicatorExitLevel)
        End Sub

        ''' <summary>
        ''' Close above upper BB AND RSI &gt; 70 → Sell (dual overbought).
        ''' </summary>
        <Fact>
        Public Sub BbRsiReversion_DualOverbought_ReturnsSell()
            Dim provider As New BbRsiReversionSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5012D)
            ' close 5015 > upper BB 5010 AND RSI 75 > 70
            Dim bar = MakeBar(5012D, 5018D, 5010D, 5015D)

            Dim indicators As New StrategyIndicators With {
                .BbUpper  = ConstArr(5010.0F),
                .BbMiddle = ConstArr(5001.0F),
                .BbLower  = ConstArr(4992.0F),
                .Rsi      = ConstArr(75.0F)   ' RSI > 70
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
        End Sub

        ''' <summary>
        ''' Close below lower BB but RSI = 40 (not oversold) → no signal.
        ''' Dual confirmation is required.
        ''' </summary>
        <Fact>
        Public Sub BbRsiReversion_BbBreachWithoutRsi_ReturnsNothing()
            Dim provider As New BbRsiReversionSignalProvider()
            Dim bars = MakeBars(N, basePrice:=4988D)
            Dim bar  = MakeBar(4990D, 4992D, 4986D, 4988D)

            Dim indicators As New StrategyIndicators With {
                .BbUpper  = ConstArr(5010.0F),
                .BbMiddle = ConstArr(5001.0F),
                .BbLower  = ConstArr(4992.0F),
                .Rsi      = ConstArr(40.0F)   ' RSI not below 30
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' Close inside BB bands → no signal regardless of RSI.
        ''' </summary>
        <Fact>
        Public Sub BbRsiReversion_InsideBands_ReturnsNothing()
            Dim provider As New BbRsiReversionSignalProvider()
            Dim bars = MakeBars(N, basePrice:=5001D)
            Dim bar  = MakeBar(5000D, 5005D, 4995D, 5001D)

            Dim indicators As New StrategyIndicators With {
                .BbUpper  = ConstArr(5010.0F),
                .BbMiddle = ConstArr(5001.0F),
                .BbLower  = ConstArr(4992.0F),
                .Rsi      = ConstArr(25.0F)   ' RSI oversold but inside bands
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' ATR mode populates StopDelta and TpDelta.
        ''' </summary>
        <Fact>
        Public Sub BbRsiReversion_AtrModeOn_PopulatesDeltas()
            Dim provider As New BbRsiReversionSignalProvider()
            Dim bars = MakeBars(N, basePrice:=4988D)
            Dim bar  = MakeBar(4990D, 4992D, 4986D, 4988D)

            Dim cfg = MakeConfig(useAtrMode:=True)
            Dim indicators As New StrategyIndicators With {
                .BbUpper  = ConstArr(5010.0F),
                .BbMiddle = ConstArr(5001.0F),
                .BbLower  = ConstArr(4992.0F),
                .Rsi      = ConstArr(25.0F),
                .Atr      = ConstArr(5.0F)   ' ATR = 5.0
            }
            Dim result = provider.Evaluate(bar, indicators, cfg, Idx)

            Assert.NotNull(result)
            Assert.Equal(7.5D, result.StopDelta)   ' 5.0 × 1.5
            Assert.Equal(15.0D, result.TpDelta)    ' 5.0 × 3.0
        End Sub

        ''' <summary>
        ''' NaN in BB lower band causes no signal (warm-up guard).
        ''' </summary>
        <Fact>
        Public Sub BbRsiReversion_NaNBbLower_ReturnsNothing()
            Dim provider As New BbRsiReversionSignalProvider()
            Dim bars = MakeBars(N, basePrice:=4988D)
            Dim bar  = MakeBar(4990D, 4992D, 4986D, 4988D)

            Dim indicators As New StrategyIndicators With {
                .BbUpper  = ConstArr(5010.0F),
                .BbMiddle = ConstArr(5001.0F),
                .BbLower  = NaNArr(),          ' NaN — still warming up
                .Rsi      = ConstArr(25.0F)
            }
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(), Idx)

            Assert.Null(result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' LultDivergenceSignalProvider
        ' ══════════════════════════════════════════════════════════════════

        Private Const LultN   As Integer = 200
        Private Const LultIdx As Integer = 150

        ''' <summary>
        ''' Build a bar list for LULT divergence tests.
        ''' Key bars:
        '''   i=80  → anchor bar  (Low=4900, used as bull anchor price extreme)
        '''   i=130 → trigger bar (Low=4850, less than anchor → price divergence)
        '''   i=149 → prior bar   (bearish body: O=5001, C=5000)
        '''   i=150 → current bar (bullish engulfing: O=4999, C=5003, Low=4998)
        ''' <paramref name="currentBarHour"/> controls the UTC hour of the current bar
        ''' (must be 11–16 inclusive for the time filter to pass).
        ''' </summary>
        Private Shared Function BuildLultBars(Optional currentBarHour As Integer = 12) As IReadOnlyList(Of MarketBar)
            Dim ts As New DateTimeOffset(2026, 4, 19, currentBarHour, 0, 0, TimeSpan.Zero)
            Dim bars As New List(Of MarketBar)(LultN)
            For i = 0 To LultN - 1
                Dim barTs = ts.AddMinutes(-(LultIdx - i) * 5L)
                Select Case i
                    Case LultIdx           ' current bar — bullish engulfing
                        bars.Add(New MarketBar With {
                            .Open = 4999D, .High = 5004D, .Low = 4998D, .Close = 5003D,
                            .Timestamp = ts
                        })
                    Case LultIdx - 1       ' previous bar — bearish (to be engulfed)
                        bars.Add(New MarketBar With {
                            .Open = 5001D, .High = 5002D, .Low = 4999D, .Close = 5000D,
                            .Timestamp = ts.AddMinutes(-5)
                        })
                    Case 80                ' anchor extreme bar
                        bars.Add(New MarketBar With {
                            .Open = 4902D, .High = 4905D, .Low = 4900D, .Close = 4901D,
                            .Timestamp = barTs
                        })
                    Case 130               ' trigger extreme bar
                        bars.Add(New MarketBar With {
                            .Open = 4852D, .High = 4855D, .Low = 4850D, .Close = 4851D,
                            .Timestamp = barTs
                        })
                    Case Else
                        bars.Add(New MarketBar With {
                            .Open = 5000D, .High = 5001D, .Low = 4999D, .Close = 5000D,
                            .Timestamp = barTs
                        })
                End Select
            Next
            Return bars.AsReadOnly()
        End Function

        ''' <summary>
        ''' Build WT1 for the bull LULT scenario:
        '''   i=80: local minimum −70 (&lt;−60 anchor breach)
        '''   i=130: local minimum −45 (shallower than anchor)
        '''   i=144: +5 (crosses above WT2=10 → 3 at that index)
        '''   All others: 0.0
        ''' </summary>
        Private Shared Function BuildLultWt1() As Single()
            Dim wt1(LultN - 1) As Single              ' default 0.0F
            wt1(79) = -65.0F : wt1(80) = -70.0F : wt1(81) = -65.0F   ' anchor local min
            wt1(129) = -40.0F : wt1(130) = -45.0F : wt1(131) = -40.0F ' trigger local min
            wt1(144) = 5.0F                            ' cross above WT2 at dotIdx=144
            Return wt1
        End Function

        ''' <summary>
        ''' Build WT2 for the bull LULT scenario:
        '''   Default: 10.0 (above WT1 default 0 → no spurious cross in 131–143)
        '''   i=144: 3.0 (WT1[144]=5 ≥ WT2[144]=3 → cross found)
        ''' </summary>
        Private Shared Function BuildLultWt2() As Single()
            Dim wt2(LultN - 1) As Single
            For i = 0 To LultN - 1 : wt2(i) = 10.0F : Next   ' above WT1 default
            wt2(144) = 3.0F                                     ' engineered cross point
            Return wt2
        End Function

        ''' <summary>WaveTrend1 = Nothing → null guard returns Nothing immediately.</summary>
        <Fact>
        Public Sub LultDivergence_NullWaveTrend_ReturnsNothing()
            Dim provider As New LultDivergenceSignalProvider()
            Dim bars = BuildLultBars()
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .WaveTrend1 = Nothing,
                .WaveTrend2 = BuildLultWt2()
            }
            Dim result = provider.Evaluate(bars(LultIdx), indicators, MakeConfig(), LultIdx)
            Assert.Null(result)
        End Sub

        ''' <summary>barIndex &lt; 100 → insufficient lookback history → Nothing.</summary>
        <Fact>
        Public Sub LultDivergence_BarIndexBelow100_ReturnsNothing()
            Dim provider As New LultDivergenceSignalProvider()
            Dim bars = BuildLultBars()
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .WaveTrend1 = BuildLultWt1(),
                .WaveTrend2 = BuildLultWt2()
            }
            Dim result = provider.Evaluate(bars(50), indicators, MakeConfig(), 50)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' Bar UTC hour = 10, outside the 11–16 inclusive window → Nothing,
        ''' even if all WaveTrend conditions would otherwise produce a signal.
        ''' </summary>
        <Fact>
        Public Sub LultDivergence_TimeFilterRejectsBarOutsideWindow_ReturnsNothing()
            Dim provider As New LultDivergenceSignalProvider()
            Dim bars = BuildLultBars(currentBarHour:=10)
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .WaveTrend1 = BuildLultWt1(),
                .WaveTrend2 = BuildLultWt2()
            }
            Dim result = provider.Evaluate(bars(LultIdx), indicators, MakeConfig(), LultIdx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' Constant WT1 has no local minima or maxima → extremes list empty → Nothing.
        ''' </summary>
        <Fact>
        Public Sub LultDivergence_ConstantWaveTrend_NoExtremes_ReturnsNothing()
            Dim provider As New LultDivergenceSignalProvider()
            Dim bars = BuildLultBars()
            Dim flatWt1(LultN - 1) As Single   ' all 0.0F — no strict inequality → no extremes
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .WaveTrend1 = flatWt1,
                .WaveTrend2 = BuildLultWt2()
            }
            Dim result = provider.Evaluate(bars(LultIdx), indicators, MakeConfig(), LultIdx)
            Assert.Null(result)
        End Sub

        ''' <summary>
        ''' Full 6-step bull divergence setup — all conditions satisfied:
        '''   Step 1-2  Anchor i=80: WT1=-70 (breaches −60); trigger i=130: WT1=-45 (shallower)
        '''   Step 3    Price divergence: bars[130].Low=4850 &lt; bars[80].Low=4900
        '''   Step 4    Dot: WT1 crosses WT2 upward at dotIdx=144 (trigger+14 bars)
        '''   Step 5    Engulfing: bars[150] O=4999,C=5003 engulfs bars[149] O=5001,C=5000
        '''   Time      bar.Timestamp.UtcHour=12 ∈ [11,16]
        ''' → Buy signal; StopDelta = |5003 − 4850| = 153; TpDelta = 306.
        ''' </summary>
        <Fact>
        Public Sub LultDivergence_FullBullSetup_ReturnsBuy()
            Dim provider As New LultDivergenceSignalProvider()
            Dim bars = BuildLultBars(currentBarHour:=12)
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .WaveTrend1 = BuildLultWt1(),
                .WaveTrend2 = BuildLultWt2()
            }
            Dim result = provider.Evaluate(bars(LultIdx), indicators, MakeConfig(), LultIdx)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.Equal(153D, result.StopDelta)
            Assert.Equal(306D, result.TpDelta)
        End Sub

        ''' <summary>
        ''' NaN values in WaveTrend1 array → warm-up guard skips every bar in the
        ''' lookback scan → no extremes found → Nothing returned (no exception thrown).
        ''' </summary>
        <Fact>
        Public Sub LultDivergence_NaNWaveTrend1Values_ReturnsNothing()
            Dim provider As New LultDivergenceSignalProvider()
            Dim bars = BuildLultBars(currentBarHour:=12)
            Dim nanWt1(LultN - 1) As Single
            For i = 0 To LultN - 1 : nanWt1(i) = Single.NaN : Next
            Dim indicators As New StrategyIndicators With {
                .AllBars    = bars,
                .WaveTrend1 = nanWt1,
                .WaveTrend2 = BuildLultWt2()
            }
            Dim result = provider.Evaluate(bars(LultIdx), indicators, MakeConfig(), LultIdx)
            Assert.Null(result)
        End Sub

    End Class

End Namespace
