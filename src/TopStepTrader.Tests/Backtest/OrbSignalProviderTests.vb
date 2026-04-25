Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports TopStepTrader.Services.Backtest.Strategies
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for OrbSignalProvider (STRAT-29 acceptance criteria).
    ''' Covers: range construction phase, long breakout, short breakout,
    ''' wide-range no-trade filter, and late-session no-trade filter.
    ''' </summary>
    Public Class OrbSignalProviderTests

        ' ── Constants ───────────���────────────────────���─────────────────────────
        Private Const BasePrice As Decimal = 5000D
        Private Const ORHigh As Decimal = 5015D   ' session open range high
        Private Const ORLow As Decimal = 4985D    ' session open range low (width = 30)
        Private Const ORWidth As Decimal = 30D
        Private Const TickSz As Decimal = 0.25D
        Private Const PointVal As Decimal = 5.0D

        ' orbBarCount for 5-min timeframe = 30/5 = 6 bars
        Private Const OrbBarCount As Integer = 6

        ' ── Helpers ────────────────────────────────────────────────────────────

        Private Shared Function MakeBar(open As Decimal, high As Decimal,
                                        low As Decimal, close As Decimal,
                                        ts As DateTimeOffset,
                                        Optional volume As Decimal = 1000D) As MarketBar
            Return New MarketBar With {
                .Open = open, .High = high, .Low = low, .Close = close,
                .Volume = volume, .Timestamp = ts
            }
        End Function

        ''' <summary>
        ''' Build a bar series:
        ''' - First <paramref name="orbBarCount"/> bars: session open-range bars (high/low spanning ORHigh/ORLow).
        ''' - Subsequent bars: flat bars at <paramref name="signalClose"/>.
        ''' All bars are on the same calendar date.
        ''' </summary>
        Private Shared Function BuildBars(totalBars As Integer,
                                          signalClose As Decimal,
                                          Optional sessionDate As DateTime = Nothing) As IReadOnlyList(Of MarketBar)
            If sessionDate = DateTime.MinValue Then sessionDate = New DateTime(2025, 1, 13)
            Dim bars As New List(Of MarketBar)(totalBars)
            For i = 0 To totalBars - 1
                Dim ts = New DateTimeOffset(sessionDate, TimeSpan.Zero).AddMinutes(i * 5)
                If i < OrbBarCount Then
                    ' Opening range bar spanning ORHigh/ORLow
                    bars.Add(MakeBar(BasePrice, ORHigh, ORLow, BasePrice, ts, 1000D))
                Else
                    Dim h = Math.Max(signalClose, BasePrice) + 1D
                    Dim l = Math.Min(signalClose, BasePrice) - 1D
                    bars.Add(MakeBar(BasePrice, h, l, signalClose, ts, 1500D))  ' high volume for signal
                End If
            Next
            Return bars.AsReadOnly()
        End Function

        Private Shared Function MakeIndicators(bars As IReadOnlyList(Of MarketBar),
                                                Optional atrOverride As Single = 20.0F,
                                                Optional volMa20Override As Single = 1000.0F) As StrategyIndicators
            Dim n = bars.Count
            Dim atr(n - 1) As Single
            Dim volMa(n - 1) As Single
            For i = 0 To n - 1
                atr(i) = atrOverride
                volMa(i) = volMa20Override
            Next
            Return New StrategyIndicators With {
                .AllBars = bars,
                .Atr = atr,
                .VolMa20 = volMa
            }
        End Function

        Private Shared Function MakeConfig(Optional timeframe As Integer = 5) As BacktestConfiguration
            Return New BacktestConfiguration With {
                .Timeframe = timeframe,
                .TickSize = TickSz,
                .PointValue = PointVal,
                .MinStopDollars = 0D,
                .MinSignalConfidence = 0.6F
            }
        End Function

        ' ── Factory test ───────────────────────────────────────────────────────

        <Fact>
        Public Sub Factory_OpeningRangeBreakout_ReturnsOrbProvider()
            Dim provider = StrategySignalProviderFactory.Create(Core.Enums.StrategyConditionType.OpeningRangeBreakout)
            Assert.IsType(Of OrbSignalProvider)(provider)
        End Sub

        ' ── Test 1: Building opening range → no signal ─────────────────────────

        <Fact>
        Public Sub Evaluate_DuringRangePhase_ReturnsNothing()
            Dim bars = BuildBars(30, signalClose:=ORHigh + 5D)
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New OrbSignalProvider()

            ' Evaluate bar at index 3 (still in opening range phase: 0..5)
            Dim result = sut.Evaluate(bars(3), indicators, config, barIndex:=3)

            Assert.Null(result)
        End Sub

        ' ── Test 2: Long breakout signal ──────────────────────────────────────

        <Fact>
        Public Sub Evaluate_LongBreakout_ReturnsBuySignal()
            ' Close above OR high at bar index 7 (past range phase 0..5, before midpoint)
            Dim longClose = ORHigh + 5D
            Dim bars = BuildBars(20, signalClose:=longClose)
            Dim indicators = MakeIndicators(bars, atrOverride:=20.0F, volMa20Override:=1000.0F)
            Dim config = MakeConfig()
            Dim sut As New OrbSignalProvider()

            Dim result = sut.Evaluate(bars(7), indicators, config, barIndex:=7)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta > 0D)
            ' TP should be 1.5× OR width (approx — may be wider if stop was clamped)
            Assert.True(result.TpDelta >= ORWidth * 1.5D * 0.99D)
        End Sub

        ' ── Test 3: Short breakout signal ──────────────────────────────���──────

        <Fact>
        Public Sub Evaluate_ShortBreakout_ReturnsSellSignal()
            Dim shortClose = ORLow - 5D
            Dim bars = BuildBars(20, signalClose:=shortClose)
            Dim indicators = MakeIndicators(bars, atrOverride:=20.0F, volMa20Override:=1000.0F)
            Dim config = MakeConfig()
            Dim sut As New OrbSignalProvider()

            Dim result = sut.Evaluate(bars(7), indicators, config, barIndex:=7)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta >= ORWidth * 1.5D * 0.99D)
        End Sub

        ' ── Test 4: No-trade filter — range too wide (> 2× ATR) ───────────────

        <Fact>
        Public Sub Evaluate_WideOpeningRange_ReturnsNothing()
            ' OR width = 30, ATR = 10 → 30 > 2×10 = no trade
            Dim bars = BuildBars(20, signalClose:=ORHigh + 5D)
            Dim indicators = MakeIndicators(bars, atrOverride:=10.0F)   ' ATR=10, OR=30 > 20
            Dim config = MakeConfig()
            Dim sut As New OrbSignalProvider()

            Dim result = sut.Evaluate(bars(7), indicators, config, barIndex:=7)

            Assert.Null(result)
        End Sub

        ' ── Test 5: No-trade filter — past session midpoint ────────────────────

        <Fact>
        Public Sub Evaluate_PastSessionMidpoint_ReturnsNothing()
            ' Build 40 bars (all same day); midpoint = bar 20; evaluate at bar 25
            Dim bars = BuildBars(40, signalClose:=ORHigh + 5D)
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New OrbSignalProvider()

            ' Bar 25 is past the midpoint (session 0..39, midpoint at 19)
            Dim result = sut.Evaluate(bars(25), indicators, config, barIndex:=25)

            Assert.Null(result)
        End Sub

        ' ── Test 6: No signal when close is inside OR (no breakout) ───────────

        <Fact>
        Public Sub Evaluate_CloseInsideRange_ReturnsNothing()
            ' Close exactly at midpoint of OR — no breakout
            Dim insideClose = (ORHigh + ORLow) / 2D
            Dim bars = BuildBars(20, signalClose:=insideClose)
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New OrbSignalProvider()

            Dim result = sut.Evaluate(bars(7), indicators, config, barIndex:=7)

            Assert.Null(result)
        End Sub

    End Class

End Namespace
