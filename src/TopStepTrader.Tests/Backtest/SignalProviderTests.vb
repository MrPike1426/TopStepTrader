Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
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
        ''' Mixed conditions produce bullScore=60, downPct=40 — neither meets the 70% threshold.
        ''' Score is in the 40–60 neutral zone, so the provider returns a NeutralExit signal
        ''' (not Nothing) so BacktestEngine can close any open position at bar.Close.
        ''' EMA21=100 > EMA50=90 (+25), close=105 > EMA21 (+20) > EMA50 (+15), RSI=80 outside 55–70 (0pts),
        ''' EMA21 flat (0pts), all-bearish candles so 0 of 3 bullish (0pts) → bullScore=60, downPct=40.
        ''' </summary>
        <Fact>
        Public Sub EmaRsi_NeutralScore_ReturnsNeutralExit()
            Dim provider As New EmaRsiSignalProvider()

            ' EMA21 flat (prev=now=100), all-bearish bars → candle bonus = 0 pts
            ' Final: 25+20+15+0+0+0 = 60 → neutral zone, no entry side fires
            Dim ema21 = ConstArr(100.0F)   ' flat: prev = now
            Dim bars = MakeBars(N, basePrice:=105D, allBullish:=False, allBearish:=True)

            Dim indicators As New StrategyIndicators With {
                .AllBars = bars,
                .Ema21   = ema21,
                .Ema50   = ConstArr(90.0F),
                .Rsi     = ConstArr(80.0F)
            }
            Dim bar = bars(Idx)
            Dim result = provider.Evaluate(bar, indicators, MakeConfig(minConf:=0.70F), Idx)

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
        ''' All 7 Short conditions met → Sell signal.
        ''' Cloud: SpanA=90, SpanB=100 → cloudBottom=90; close=80 < cloudBottom ✓
        ''' EMA21=85; close=80 < EMA21 ✓
        ''' Tenkan=82 < Kijun=88 ✓
        ''' Chikou: lagClose=90 > currentClose=80 ✓
        ''' PlusDI=15 < MinusDI=25 ✓
        ''' MACD hist now=-0.5 < 0 and < prev=-0.3 ✓
        ''' StochRsiK=0.7 > 0.2 ✓
        ''' </summary>
        <Fact>
        Public Sub MultiConfluence_AllShortConditions_ReturnsSell()
            Dim provider As New MultiConfluenceSignalProvider()

            Dim bars = BuildMCBars(currentClose:=80D, lagClose:=90D)
            Dim macdHist = ConstArr(-0.5F)
            macdHist(Idx - 1) = -0.3F   ' prev > now (expanding downward)

            Dim indicators As New StrategyIndicators With {
                .AllBars      = bars,
                .Ema21        = ConstArr(85.0F),
                .IchiSpanA    = ConstArr(90.0F),
                .IchiSpanB    = ConstArr(100.0F),
                .IchiTenkan   = ConstArr(82.0F),
                .IchiKijun    = ConstArr(88.0F),
                .Adx          = ConstArr(30.0F),
                .PlusDi       = ConstArr(15.0F),
                .MinusDi      = ConstArr(25.0F),
                .MacdHistogram = macdHist,
                .StochRsiK    = ConstArr(0.7F),
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

    End Class

End Namespace
