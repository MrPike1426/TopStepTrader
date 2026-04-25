Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports TopStepTrader.Services.Backtest.Strategies
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for PumpNDumpSignalProvider (TEST-10 acceptance criteria).
    ''' </summary>
    Public Class PumpNDumpSignalProviderTests

        Private Shared Function MakeBar(open As Decimal, close As Decimal) As MarketBar
            Return New MarketBar With {
                .Open = open, .Close = close,
                .High = Math.Max(open, close) + 0.25D,
                .Low  = Math.Min(open, close) - 0.25D,
                .Volume = 500D,
                .Timestamp = DateTimeOffset.UtcNow
            }
        End Function

        Private Shared Function MakeIndicators(bars As IReadOnlyList(Of MarketBar),
                                               Optional atr As Single = 2.0F) As StrategyIndicators
            Dim n = bars.Count
            Dim atrArr(n - 1) As Single
            For i = 0 To n - 1
                atrArr(i) = atr
            Next
            Return New StrategyIndicators With {.AllBars = bars, .Atr = atrArr}
        End Function

        Private Shared Function MakeConfig() As BacktestConfiguration
            Return New BacktestConfiguration With {
                .UseAtrMode    = True,
                .SlAtrMultiple = 1.5D,
                .TpAtrMultiple = 3.0D,
                .TickSize      = 0.25D,
                .PointValue    = 5.0D
            }
        End Function

        ' ── Factory registration ───────────────────────────────────────────────

        <Fact>
        Public Sub Factory_PumpNDump_ReturnsPumpNDumpProvider()
            Dim provider = StrategySignalProviderFactory.Create(StrategyConditionType.PumpNDump)
            Assert.IsType(Of PumpNDumpSignalProvider)(provider)
        End Sub

        ' ── Test 1: 3 green bars → Buy ─────────────────────────────────────────

        <Fact>
        Public Sub PumpNDump_3GreenBars_ReturnsBuySignal()
            Dim bars As IReadOnlyList(Of MarketBar) = New List(Of MarketBar) From {
                MakeBar(100D, 101D),
                MakeBar(101D, 102D),
                MakeBar(102D, 103D)
            }
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New PumpNDumpSignalProvider()

            Dim result = sut.Evaluate(bars(2), indicators, config, barIndex:=2)

            Assert.NotNull(result)
            Assert.Equal("Buy", result.Side)
            Assert.True(result.IsLong)
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta > result.StopDelta)
        End Sub

        ' ── Test 2: 3 red bars → Sell ──────────────────────────────────────────

        <Fact>
        Public Sub PumpNDump_3RedBars_ReturnsSellSignal()
            Dim bars As IReadOnlyList(Of MarketBar) = New List(Of MarketBar) From {
                MakeBar(103D, 102D),
                MakeBar(102D, 101D),
                MakeBar(101D, 100D)
            }
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New PumpNDumpSignalProvider()

            Dim result = sut.Evaluate(bars(2), indicators, config, barIndex:=2)

            Assert.NotNull(result)
            Assert.Equal("Sell", result.Side)
            Assert.False(result.IsLong)
            Assert.True(result.StopDelta > 0D)
            Assert.True(result.TpDelta > result.StopDelta)
        End Sub

        ' ── Test 3: mixed bars → no signal ─────────────────────────────────────

        <Fact>
        Public Sub PumpNDump_MixedBars_ReturnsNoSignal()
            Dim bars As IReadOnlyList(Of MarketBar) = New List(Of MarketBar) From {
                MakeBar(100D, 101D),   ' green
                MakeBar(101D, 100D),   ' red
                MakeBar(100D, 101D)    ' green
            }
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New PumpNDumpSignalProvider()

            Dim result = sut.Evaluate(bars(2), indicators, config, barIndex:=2)

            Assert.Null(result)
        End Sub

        ' ── Test 4: fewer than 3 bars → no signal ──────────────────────────────

        <Fact>
        Public Sub PumpNDump_FewerThan3Bars_ReturnsNoSignal()
            Dim bars As IReadOnlyList(Of MarketBar) = New List(Of MarketBar) From {
                MakeBar(100D, 101D),
                MakeBar(101D, 102D)
            }
            Dim indicators = MakeIndicators(bars)
            Dim config = MakeConfig()
            Dim sut As New PumpNDumpSignalProvider()

            ' barIndex=1 means only 2 bars available (indices 0..1), barIndex < 2 → Nothing
            Dim result = sut.Evaluate(bars(1), indicators, config, barIndex:=1)

            Assert.Null(result)
        End Sub

        ' ── Test 5: ATR sizing — StopDelta = ATR × SlAtrMultiple ───────────────

        <Fact>
        Public Sub PumpNDump_AtrMode_StopAndTpScaleWithAtr()
            Dim bars As IReadOnlyList(Of MarketBar) = New List(Of MarketBar) From {
                MakeBar(100D, 101D),
                MakeBar(101D, 102D),
                MakeBar(102D, 103D)
            }
            Dim indicators = MakeIndicators(bars, atr:=4.0F)
            Dim config = MakeConfig()   ' SlAtrMultiple=1.5, TpAtrMultiple=3.0
            Dim sut As New PumpNDumpSignalProvider()

            Dim result = sut.Evaluate(bars(2), indicators, config, barIndex:=2)

            Assert.NotNull(result)
            Assert.Equal(6.0D, result.StopDelta)   ' 4 × 1.5
            Assert.Equal(12.0D, result.TpDelta)    ' 4 × 3.0
        End Sub

    End Class

End Namespace
