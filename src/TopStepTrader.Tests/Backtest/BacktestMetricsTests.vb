Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for BacktestMetrics — pure calculation functions with no external dependencies.
    ''' TICKET-006 Phase 5.
    ''' </summary>
    Public Class BacktestMetricsTests

        ' ══════════════════════════════════════════════════════════════════
        ' CalculatePnL
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CalculatePnL_BuyTrade_Profit_ReturnsPositive()
            ' MES: Buy at 5000, exit at 5010, qty=1 → (5010-5000) × 1 × $5/pt = $50
            ' (Old code used $50/pt ES multiplier — 10× too large for MES)
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5010D, qty:=1)
            Dim config = MakeConfig()  ' PointValue defaults to $5 (MES correct)

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(50D, result)
        End Sub

        <Fact>
        Public Sub CalculatePnL_BuyTrade_Loss_ReturnsNegative()
            ' MES: Buy at 5000, exit at 4990, qty=1 → (4990-5000) × 1 × $5/pt = -$50
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=4990D, qty:=1)
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(-50D, result)
        End Sub

        <Fact>
        Public Sub CalculatePnL_SellTrade_Profit_ReturnsPositive()
            ' MES: Sell at 5000, exit at 4990 (price dropped = profit for short)
            ' → -(4990-5000) × 1 × $5/pt = +$50
            Dim trade = MakeTrade("Sell", entryPrice:=5000D, exitPrice:=4990D, qty:=1)
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(50D, result)
        End Sub

        <Fact>
        Public Sub CalculatePnL_SellTrade_Loss_ReturnsNegative()
            ' MES: Sell at 5000, exit at 5010 (price rose = loss for short)
            ' → -(5010-5000) × 1 × $5/pt = -$50
            Dim trade = MakeTrade("Sell", entryPrice:=5000D, exitPrice:=5010D, qty:=1)
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(-50D, result)
        End Sub

        <Fact>
        Public Sub CalculatePnL_MultipleContracts_ScalesWithQuantity()
            ' MES: Buy at 5000, exit at 5004, qty=3 → (5004-5000) × 3 × $5/pt = $60
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5004D, qty:=3)
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(60D, result)
        End Sub

        <Fact>
        Public Sub CalculatePnL_NoExitPrice_ReturnsZero()
            ' Open trade (no exit recorded) should return 0
            Dim trade = New BacktestTrade With {
                .EntryPrice = 5000D,
                .Side = "Buy",
                .Quantity = 1,
                .ExitPrice = Nothing
            }
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(0D, result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' CheckExit
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CheckExit_BuyHitsStopLoss_ReturnsStopLoss()
            ' SL=10 ticks → stopDelta=2.5 pts; Buy at 5000 → SL triggers when bar.Low ≤ 4997.5
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4997.5D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal("StopLoss", BacktestMetrics.CheckExit(trade, bar, sl, tp))
        End Sub

        <Fact>
        Public Sub CheckExit_BuyHitsTakeProfit_ReturnsTakeProfit()
            ' TP=20 ticks → tpDelta=5 pts; Buy at 5000 → TP triggers when bar.High ≥ 5005
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5005D, low:=4999D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal("TakeProfit", BacktestMetrics.CheckExit(trade, bar, sl, tp))
        End Sub

        <Fact>
        Public Sub CheckExit_BuyNeitherLevelHit_ReturnsNothing()
            ' Bar stays inside SL/TP range — no exit
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5002D, low:=4999D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Null(BacktestMetrics.CheckExit(trade, bar, sl, tp))
        End Sub

        <Fact>
        Public Sub CheckExit_SellHitsStopLoss_ReturnsStopLoss()
            ' SL=10 ticks → stopDelta=2.5 pts; Sell at 5000 → SL triggers when bar.High ≥ 5002.5
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5002.5D, low:=4999D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal("StopLoss", BacktestMetrics.CheckExit(trade, bar, sl, tp))
        End Sub

        <Fact>
        Public Sub CheckExit_SellHitsTakeProfit_ReturnsTakeProfit()
            ' TP=20 ticks → tpDelta=5 pts; Sell at 5000 → TP triggers when bar.Low ≤ 4995
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4995D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal("TakeProfit", BacktestMetrics.CheckExit(trade, bar, sl, tp))
        End Sub

        <Fact>
        Public Sub CheckExit_SellNeitherLevelHit_ReturnsNothing()
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4999D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Null(BacktestMetrics.CheckExit(trade, bar, sl, tp))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' CalculateSharpe
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CalculateSharpe_ZeroTrades_ReturnsNothing()
            Dim trades As New List(Of BacktestTrade)()

            Dim result = BacktestMetrics.CalculateSharpe(trades)

            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub CalculateSharpe_OneTrade_ReturnsNothing()
            ' Need ≥ 2 trades for a meaningful Sharpe
            Dim trades = New List(Of BacktestTrade) From {MakeClosedTrade(500D)}

            Dim result = BacktestMetrics.CalculateSharpe(trades)

            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub CalculateSharpe_AllSamePnL_ReturnsNothing()
            ' StdDev = 0 → Sharpe is undefined
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(100D), MakeClosedTrade(100D), MakeClosedTrade(100D)
            }

            Dim result = BacktestMetrics.CalculateSharpe(trades)

            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub CalculateSharpe_MixedPnL_ReturnsNonNull()
            ' Alternating wins and losses → stddev > 0 → valid Sharpe
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(500D), MakeClosedTrade(-250D),
                MakeClosedTrade(300D), MakeClosedTrade(-100D)
            }

            Dim result = BacktestMetrics.CalculateSharpe(trades)

            Assert.NotNull(result)
        End Sub

        <Fact>
        Public Sub CalculateSharpe_KnownValues_MatchFormula()
            ' avg([100,-100]) = 0, stddev = 100, Sharpe = 0/100 × √252 = 0
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(100D), MakeClosedTrade(-100D)
            }

            Dim result = BacktestMetrics.CalculateSharpe(trades)

            Assert.NotNull(result)
            Assert.Equal(0.0F, result.Value, 4)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' BuildResult
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub BuildResult_NoTrades_ReturnsZeroMetrics()
            Dim trades As New List(Of BacktestTrade)()
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.BuildResult(config, trades,
                                                      finalCapital:=50000D, maxDrawdown:=0D)

            Assert.Equal(0, result.TotalTrades)
            Assert.Equal(0F, result.WinRate)
            Assert.Equal(0D, result.TotalPnL)
            Assert.Equal(0D, result.AveragePnLPerTrade)
            Assert.Null(result.SharpeRatio)
        End Sub

        <Fact>
        Public Sub BuildResult_TwoWinnersOneLoss_WinRateAndTotalsCorrect()
            ' W: +$500, W: +$250, L: -$200  → 2 wins, 1 loss, total $550
            ' Each trade gets a distinct PositionGroupId so BuildResult counts 3 positions.
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(500D,  positionGroupId:=1),
                MakeClosedTrade(250D,  positionGroupId:=2),
                MakeClosedTrade(-200D, positionGroupId:=3)
            }
            Dim config = MakeConfig()
            config.InitialCapital = 50000D

            Dim result = BacktestMetrics.BuildResult(config, trades,
                                                      finalCapital:=50550D, maxDrawdown:=200D)

            Assert.Equal(3, result.TotalTrades)
            Assert.Equal(2, result.WinningTrades)
            Assert.Equal(1, result.LosingTrades)
            Assert.Equal(550D, result.TotalPnL)
            Assert.Equal(200D, result.MaxDrawdown)
            ' WinRate = 2/3 ≈ 0.6667
            Assert.True(Math.Abs(result.WinRate - CSng(2) / 3) < 0.0001F,
                        $"Expected WinRate ≈ 0.667, got {result.WinRate}")
            Assert.Equal(550D / 3D, result.AveragePnLPerTrade)
        End Sub

        <Fact>
        Public Sub BuildResult_AllLosers_WinRateIsZero()
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(-100D), MakeClosedTrade(-200D)
            }
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.BuildResult(config, trades,
                                                      finalCapital:=49700D, maxDrawdown:=300D)

            Assert.Equal(0F, result.WinRate)
            Assert.Equal(-300D, result.TotalPnL)
        End Sub

        <Fact>
        Public Sub BuildResult_PropagatesConfigMetadata()
            Dim trades As New List(Of BacktestTrade)()
            Dim config = MakeConfig()
            config.RunName = "Regression Test Run"
            config.ContractId = "CON.F.US.MES.H26"
            config.InitialCapital = 75000D

            Dim result = BacktestMetrics.BuildResult(config, trades, 75000D, 0D)

            Assert.Equal("Regression Test Run", result.RunName)
            Assert.Equal("CON.F.US.MES.H26", result.ContractId)
            Assert.Equal(75000D, result.InitialCapital)
            Assert.Equal(75000D, result.FinalCapital)
        End Sub

        <Fact>
        Public Sub BuildResult_ScaleInLegs_MetricsArePositionGroupBased()
            ' Position 1: initial entry +$300, scale-in +$150 → group P&L = +$450 (winner)
            ' Position 2: single entry -$100 → group P&L = -$100 (loser)
            ' Expected: TotalTrades=2 (positions), WinRate=50%, TotalPnL=$350, AvgPnL=$175
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(300D,  positionGroupId:=1),   ' initial entry, group 1
                MakeClosedTrade(150D,  positionGroupId:=1),   ' scale-in,      group 1
                MakeClosedTrade(-100D, positionGroupId:=2)    ' separate position
            }
            Dim config = MakeConfig()

            Dim result = BacktestMetrics.BuildResult(config, trades,
                                                      finalCapital:=50350D, maxDrawdown:=100D)

            ' Metrics are per position (group), not per individual entry row
            Assert.Equal(2, result.TotalTrades)      ' 2 unique position groups
            Assert.Equal(1, result.WinningTrades)    ' group 1 (+$450 > 0)
            Assert.Equal(1, result.LosingTrades)     ' group 2 (-$100 ≤ 0)
            Assert.Equal(0.5F, result.WinRate, 4)
            Assert.Equal(350D, result.TotalPnL)      ' all legs summed
            Assert.Equal(175D, result.AveragePnLPerTrade)  ' 350 / 2 positions
            ' Raw trade rows are all preserved for display
            Assert.Equal(3, result.Trades.Count)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' GetExitPrice — UAT-BUG-006 regression
        ' (StopLoss must never produce a profit; TakeProfit must never produce a loss)
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub GetExitPrice_BuyStopLoss_ReturnsEntryMinusStopDelta()
            ' SL=10 ticks → stopDelta=2.5 pts; Buy entry 5000 → SL fill = 4997.5
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4997D, close:=4998D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal(4997.5D, BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", sl, tp))
        End Sub

        <Fact>
        Public Sub GetExitPrice_BuyTakeProfit_ReturnsEntryPlusTpDelta()
            ' TP=20 ticks → tpDelta=5 pts; Buy entry 5000 → TP fill = 5005
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5006D, low:=4999D, close:=5004D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal(5005D, BacktestMetrics.GetExitPrice(trade, bar, "TakeProfit", sl, tp))
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellStopLoss_ReturnsEntryPlusStopDelta()
            ' SL=10 ticks → stopDelta=2.5 pts; Sell entry 5000 → SL fill = 5002.5 (loss)
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5003D, low:=4999D, close:=5001D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal(5002.5D, BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", sl, tp))
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellTakeProfit_ReturnsEntryMinusTpDelta()
            ' TP=20 ticks → tpDelta=5 pts; Sell entry 5000 → TP fill = 4995 (profit)
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4994D, close:=4996D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal(4995D, BacktestMetrics.GetExitPrice(trade, bar, "TakeProfit", sl, tp))
        End Sub

        <Fact>
        Public Sub GetExitPrice_EndOfData_ReturnsBarClose()
            ' EndOfData exits at bar.Close — no fixed level was hit
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4999D, close:=5000.5D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Assert.Equal(5000.5D, BacktestMetrics.GetExitPrice(trade, bar, "EndOfData", sl, tp))
        End Sub

        <Fact>
        Public Sub GetExitPrice_BuyStopLoss_IsAlwaysBelowEntry()
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4997D, close:=4999D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Dim exitPrice = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", sl, tp)
            Assert.True(exitPrice < trade.EntryPrice,
                        $"Buy StopLoss exit ({exitPrice}) must be below entry ({trade.EntryPrice})")
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellStopLoss_IsAlwaysAboveEntry()
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5003D, low:=4999D, close:=5001D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Dim exitPrice = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", sl, tp)
            Assert.True(exitPrice > trade.EntryPrice,
                        $"Sell StopLoss exit ({exitPrice}) must be above entry ({trade.EntryPrice})")
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellStopLoss_ProducesNegativePnL()
            ' UAT-BUG-006 regression: Sell StopLoss must produce a LOSS, never a profit.
            ' Before the fix: bar.High triggered SL but bar.Close was below entry, producing phantom profit.
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5003D, low:=4990D, close:=4995D)
            Dim sl As Decimal, tp As Decimal
            CalcLevels(trade, 10D, 20D, sl, tp)

            Dim exitPrice = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", sl, tp)
            trade.ExitPrice = exitPrice
            Dim pnl = BacktestMetrics.CalculatePnL(trade, MakeConfig())

            Assert.True(pnl < 0D, $"StopLoss P&L must be negative; got {pnl}")
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' BUG-04: TickSize > 0 guard in CalculatePnL
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CalculatePnL_TickSizeZero_ThrowsInvalidOperationException()
            ' A misconfigured TickSize=0 must produce a clear error, not DivideByZeroException.
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5010D, qty:=1)
            Dim config = MakeConfig()
            config.TickSize = 0D   ' simulate misconfigured instrument

            Dim ex = Assert.Throws(Of InvalidOperationException)(
                Sub() BacktestMetrics.CalculatePnL(trade, config))
            Assert.Contains("TickSize must be > 0", ex.Message)
        End Sub

        <Fact>
        Public Sub CalculatePnL_TickSizeNegative_ThrowsInvalidOperationException()
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5010D, qty:=1)
            Dim config = MakeConfig()
            config.TickSize = -0.25D

            Dim ex = Assert.Throws(Of InvalidOperationException)(
                Sub() BacktestMetrics.CalculatePnL(trade, config))
            Assert.Contains("TickSize must be > 0", ex.Message)
        End Sub

        <Fact>
        Public Sub CalculatePnL_TickSizePositive_DoesNotThrow()
            ' A valid TickSize should work normally — no exception
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5010D, qty:=1)
            Dim config = MakeConfig()
            config.TickSize = 0.25D   ' MES default

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(50D, result)  ' (5010-5000) × 1 × $5/pt = $50
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' BUG-04: MaxScaleIns ≥ 0 guard in BacktestConfiguration
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub BacktestConfiguration_MaxScaleInsNegative_ThrowsArgumentOutOfRange()
            Dim config As New BacktestConfiguration()

            Dim ex = Assert.Throws(Of ArgumentOutOfRangeException)(
                Sub() config.MaxScaleIns = -1)
            Assert.Equal("MaxScaleIns", ex.ParamName)
        End Sub

        <Fact>
        Public Sub BacktestConfiguration_MaxScaleInsZero_IsValid()
            ' Zero scale-ins is a valid configuration (no pyramid entries allowed)
            Dim config As New BacktestConfiguration()
            config.MaxScaleIns = 0
            Assert.Equal(0, config.MaxScaleIns)
        End Sub

        <Fact>
        Public Sub BacktestConfiguration_MaxScaleInsPositive_IsValid()
            Dim config As New BacktestConfiguration()
            config.MaxScaleIns = 3   ' Joe persona
            Assert.Equal(3, config.MaxScaleIns)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' Test helpers
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>Build a trade with optional exit price for P&amp;L / exit tests.</summary>
        Private Shared Function MakeTrade(side As String,
                                           entryPrice As Decimal,
                                           Optional exitPrice As Decimal? = Nothing,
                                           Optional qty As Integer = 1) As BacktestTrade
            Return New BacktestTrade With {
                .Side = side,
                .EntryPrice = entryPrice,
                .ExitPrice = exitPrice,
                .Quantity = qty,
                .EntryTime = DateTimeOffset.UtcNow,
                .ExitTime = If(exitPrice.HasValue,
                               CType(DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset?),
                               Nothing)
            }
        End Function

        ''' <summary>Build a closed trade with PnL pre-set (for Sharpe / BuildResult tests).
        ''' <paramref name="positionGroupId"/> defaults to 0 — pass a unique value per trade in BuildResult tests
        ''' so each row is treated as its own position group.</summary>
        Private Shared Function MakeClosedTrade(pnl As Decimal,
                                                 Optional positionGroupId As Integer = 0) As BacktestTrade
            Return New BacktestTrade With {
                .Side = "Buy",
                .EntryPrice = 5000D,
                .ExitPrice = 5000D,   ' price irrelevant — PnL set directly
                .Quantity = 1,
                .PnL = pnl,
                .PositionGroupId = positionGroupId,
                .EntryTime = DateTimeOffset.UtcNow,
                .ExitTime = DateTimeOffset.UtcNow.AddMinutes(5)
            }
        End Function

        ''' <summary>Build a bar with specific High/Low for exit-check tests.
        ''' Optional <paramref name="close"/> defaults to the midpoint when not provided.</summary>
        Private Shared Function MakeBar(high As Decimal, low As Decimal,
                                        Optional close As Decimal? = Nothing) As MarketBar
            Dim closePrice = If(close.HasValue, close.Value, (high + low) / 2D)
            Return New MarketBar With {
                .High = high,
                .Low = low,
                .Open = (high + low) / 2D,
                .Close = closePrice,
                .Timestamp = DateTimeOffset.UtcNow
            }
        End Function

        ''' <summary>Build a minimal BacktestConfiguration for metric tests (MES defaults).</summary>
        Private Shared Function MakeConfig() As BacktestConfiguration
            Return New BacktestConfiguration With {
                .RunName = "Unit Test",
                .ContractId = "CON.F.US.MES.H26",
                .StartDate = Date.Today.AddDays(-7),
                .EndDate = Date.Today,
                .InitialCapital = 50000D,
                .MinSignalConfidence = 0.65F
            }
        End Function

        ''' <summary>
        ''' Convert tick counts to absolute SL/TP price levels for CheckExit/GetExitPrice tests.
        ''' MES convention: TickSize=0.25 pts/tick.  Buy SL below entry; Sell SL above entry.
        ''' </summary>
        Private Shared Sub CalcLevels(trade As BacktestTrade, slTicks As Decimal, tpTicks As Decimal,
                                       ByRef slPrice As Decimal, ByRef tpPrice As Decimal)
            Dim slDelta = slTicks * 0.25D
            Dim tpDelta = tpTicks * 0.25D
            If trade.Side = "Buy" Then
                slPrice = trade.EntryPrice - slDelta
                tpPrice = trade.EntryPrice + tpDelta
            Else
                slPrice = trade.EntryPrice + slDelta
                tpPrice = trade.EntryPrice - tpDelta
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' BUG-27: Commission erosion — symmetry, consistency, and CommissionPaid tracking
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CalculatePnL_CommissionDeductedSymmetrically_EntryAndExit()
            ' Entry + exit commission must both be charged.
            ' Buy at 5000, exit at 5002 → gross = 2pts × $5 = $10.
            ' CommissionPerSideUsd = $2 → total commission = $2 × 2 = $4.
            ' Net = $10 − $4 = $6.
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5002D, qty:=1)
            Dim config = MakeConfig()
            config.CommissionPerSideUsd = 2D

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.Equal(6D, result)
        End Sub

        <Fact>
        Public Sub CalculatePnL_CommissionExceedsGrossProfit_ReturnsNegative()
            ' A thin winner (gross = $2) with high commission ($5/side = $10 round trip)
            ' must produce a net loss — verifies commission is not silently dropped.
            Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5000.4D, qty:=1)
            Dim config = MakeConfig()
            config.CommissionPerSideUsd = 5D   ' $5/side = $10 round trip

            Dim result = BacktestMetrics.CalculatePnL(trade, config)

            Assert.True(result < 0D, $"Net P&L ({result}) must be negative when commission exceeds gross profit")
        End Sub

        <Fact>
        Public Sub BuildResult_CommissionPaid_EqualsTotalRoundTripTimesTradeCount()
            ' CommissionPaid must equal CommissionPerSideUsd × 2 × total qty across all trades.
            ' 3 trades, qty=1 each, $0.37/side → CommissionPaid = $0.74 × 3 = $2.22.
            Dim trades = New List(Of BacktestTrade) From {
                MakeClosedTrade(10D,  positionGroupId:=1),
                MakeClosedTrade(-5D,  positionGroupId:=2),
                MakeClosedTrade(8D,   positionGroupId:=3)
            }
            Dim config = MakeConfig()
            config.CommissionPerSideUsd = 0.37D

            Dim result = BacktestMetrics.BuildResult(config, trades, finalCapital:=50013D, maxDrawdown:=5D)

            Assert.Equal(0.37D * 2D * 3D, result.CommissionPaid)
        End Sub

        <Fact>
        Public Sub CalculatePnL_HighFrequencyLowEdge_CommissionErodesProfit()
            ' A thin-edge strategy with 38% win rate where total commission > total gross profit.
            ' Win: gross = $1.50; Loss: gross = $0.90 (1.67:1 risk:reward, 38% win rate).
            ' Commission = $0.74/trade (MES round trip = $0.37/side × 2).
            ' Per-trade gross EV = 0.38 × 1.50 − 0.62 × 0.90 = +$0.012 gross (barely positive).
            ' Net EV = $0.012 − $0.74 = −$0.728/trade → sum over 312 trades must be negative.
            Dim config = MakeConfig()
            config.CommissionPerSideUsd = 0.37D  ' MES round trip = $0.74
            ' MES: $5/pt, TickSize=0.25 → 0.3pt win = $1.50 gross; 0.18pt loss = $0.90 gross
            Dim totalNet = 0D
            Dim groupId = 0
            Dim rng As New Random(42)
            For i = 1 To 312
                groupId += 1
                Dim isWin = (rng.NextDouble() < 0.38)
                Dim exitOffset = If(isWin, 0.3D, -0.18D)   ' pts: win=+0.3, loss=−0.18
                Dim trade = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5000D + exitOffset, qty:=1)
                trade.PositionGroupId = groupId
                Dim pnl = BacktestMetrics.CalculatePnL(trade, config)
                trade.PnL = pnl
                totalNet += pnl
            Next

            Assert.True(totalNet < 0D,
                        $"312-trade thin-edge strategy must be net-negative after commission; got {totalNet:C2}")
        End Sub

        <Fact>
        Public Sub BuildResult_CommissionPaid_IsSymmetric_BothTrainAndTest()
            ' Verifies CommissionPaid is the same formula regardless of split source.
            ' Two identical trade lists should produce the same CommissionPaid.
            Dim trades1 = New List(Of BacktestTrade) From {
                MakeClosedTrade(10D, positionGroupId:=1),
                MakeClosedTrade(-5D, positionGroupId:=2)
            }
            Dim trades2 = New List(Of BacktestTrade) From {
                MakeClosedTrade(10D, positionGroupId:=1),
                MakeClosedTrade(-5D, positionGroupId:=2)
            }
            Dim config = MakeConfig()
            config.CommissionPerSideUsd = 0.52D  ' OIL round trip = $1.04

            Dim r1 = BacktestMetrics.BuildResult(config, trades1, 50005D, 5D)
            Dim r2 = BacktestMetrics.BuildResult(config, trades2, 50005D, 5D)

            Assert.Equal(r1.CommissionPaid, r2.CommissionPaid)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' TEST-01: UpdateDynamicExits — Trailing Stop, Break-Even, Extend TP
        ' ══════════════════════════════════════════════════════════════════

        ' ── Trailing stop ─────────────────────────────────────────────────

        ''' <summary>
        ''' Long position: price rises, so SL candidate (close − stopDelta) exceeds current SL.
        ''' SL must ratchet up to lock in profit.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_TrailingStop_Long_PriceRises_SlAdvances()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5012D, low:=5008D, close:=5010D)
            Dim config = MakeConfig()
            config.TrailingStopEnabled = True

            Dim currentStop = 4995D   ' initial SL: 5 pts below entry
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=5D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' candidate = 5010 − 5 = 5005 > 4995 → advances
            Assert.Equal(5005D, currentStop)
        End Sub

        ''' <summary>
        ''' Long position: price dips back. SL must NOT retreat — it only moves in trade's favour.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_TrailingStop_Long_PriceFalls_SlDoesNotRetreat()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5006D, low:=5001D, close:=5001D)
            Dim config = MakeConfig()
            config.TrailingStopEnabled = True

            Dim currentStop = 5005D   ' SL already ratcheted to 5005
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=5D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' candidate = 5001 − 5 = 4996 < 5005 → stays at 5005
            Assert.Equal(5005D, currentStop)
        End Sub

        ''' <summary>
        ''' Short position: price falls, so SL candidate (close + stopDelta) is below current SL.
        ''' SL must ratchet down.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_TrailingStop_Short_PriceFalls_SlAdvances()
            Dim trade  = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=4992D, low:=4988D, close:=4990D)
            Dim config = MakeConfig()
            config.TrailingStopEnabled = True

            Dim currentStop = 5005D   ' initial SL: 5 pts above entry
            Dim currentTp   = 4990D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=5D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' candidate = 4990 + 5 = 4995 < 5005 → advances (ratchets down)
            Assert.Equal(4995D, currentStop)
        End Sub

        ''' <summary>
        ''' Short position: price bounces up. SL must NOT widen upward.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_TrailingStop_Short_PriceRises_SlDoesNotRetreat()
            Dim trade  = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5002D, low:=4999D, close:=5001D)
            Dim config = MakeConfig()
            config.TrailingStopEnabled = True

            Dim currentStop = 4995D   ' SL already ratcheted to 4995
            Dim currentTp   = 4990D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=5D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' candidate = 5001 + 5 = 5006 > 4995 → stays at 4995
            Assert.Equal(4995D, currentStop)
        End Sub

        ' ── Break-even ────────────────────────────────────────────────────

        ''' <summary>
        ''' Long position: close reaches exactly entry + halfTp → SL advances to entry.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_BreakEven_Long_ClosesAtHalfTp_SlMovesToEntry()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            ' tpDelta=10 → halfTp=5 → trigger at close ≥ 5005
            Dim bar    = MakeBar(high:=5006D, low:=5004D, close:=5005D)
            Dim config = MakeConfig()
            config.BreakEvenOnHalfTpEnabled = True

            Dim currentStop = 4990D   ' SL below entry
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            Assert.Equal(5000D, currentStop)   ' moved to entry
        End Sub

        ''' <summary>
        ''' Long position: close is 1 tick below the half-TP trigger → SL must stay put.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_BreakEven_Long_ClosesBelowHalfTp_SlUnchanged()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            ' halfTp=5 → trigger at 5005; bar closes at 5004 (below trigger)
            Dim bar    = MakeBar(high:=5005D, low:=5003D, close:=5004D)
            Dim config = MakeConfig()
            config.BreakEvenOnHalfTpEnabled = True

            Dim currentStop = 4990D
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            Assert.Equal(4990D, currentStop)   ' unchanged
        End Sub

        ''' <summary>
        ''' Break-even is idempotent: once SL = entry, subsequent bars above the trigger
        ''' must not retreat SL (condition `currentStop &lt; entryPrice` becomes False).
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_BreakEven_Long_AlreadyAtEntry_SlNotRetreated()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5008D, low:=5006D, close:=5007D)
            Dim config = MakeConfig()
            config.BreakEvenOnHalfTpEnabled = True

            Dim currentStop = 5000D   ' already at break-even
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' currentStop (5000) is NOT < entryPrice (5000) → condition is False → no change
            Assert.Equal(5000D, currentStop)
        End Sub

        ''' <summary>
        ''' Short position: close reaches entry − halfTp → SL advances to entry.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_BreakEven_Short_ClosesAtHalfTp_SlMovesToEntry()
            Dim trade  = MakeTrade("Sell", entryPrice:=5000D)
            ' tpDelta=10 → halfTp=5 → trigger at close ≤ 4995
            Dim bar    = MakeBar(high:=4996D, low:=4994D, close:=4995D)
            Dim config = MakeConfig()
            config.BreakEvenOnHalfTpEnabled = True

            Dim currentStop = 5010D   ' SL above entry for short
            Dim currentTp   = 4990D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            Assert.Equal(5000D, currentStop)   ' moved to entry
        End Sub

        ' ── Extend TP ─────────────────────────────────────────────────────

        ''' <summary>
        ''' Long position: bar closes at TP level → TP advances by one tpDelta.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_ExtendTp_Long_CloseBeyondTarget_TpAdvances()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5012D, low:=5009D, close:=5010D)
            Dim config = MakeConfig()
            config.ExtendTpEnabled = True

            Dim currentStop = 4990D
            Dim currentTp   = 5010D   ' initial TP = entry + 1×tpDelta
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' close(5010) ≥ currentTp(5010) → extended = 5020 ≤ cap(5000+30=5030) → advances
            Assert.Equal(5020D, currentTp)
        End Sub

        ''' <summary>
        ''' Long position: bar closes one tick below TP → TP must remain unchanged.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_ExtendTp_Long_ClosesBelowTarget_TpUnchanged()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5010D, low:=5008D, close:=5009D)
            Dim config = MakeConfig()
            config.ExtendTpEnabled = True

            Dim currentStop = 4990D
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            Assert.Equal(5010D, currentTp)   ' unchanged
        End Sub

        ''' <summary>
        ''' Long position: TP is already at the hard cap (entry + 3×tpDelta).
        ''' A close at the cap must NOT advance TP beyond it.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_ExtendTp_Long_AtCap_TpDoesNotExceedCap()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            ' cap = entry(5000) + 3×tpDelta(10) = 5030
            Dim bar    = MakeBar(high:=5032D, low:=5029D, close:=5030D)
            Dim config = MakeConfig()
            config.ExtendTpEnabled = True

            Dim currentStop = 4990D
            Dim currentTp   = 5030D   ' already at cap
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' extended = 5040 > cap(5030) → blocked
            Assert.Equal(5030D, currentTp)
        End Sub

        ''' <summary>
        ''' Short position: bar closes at or below TP level → TP advances downward by tpDelta.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_ExtendTp_Short_CloseBeyondTarget_TpAdvances()
            Dim trade  = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=4992D, low:=4989D, close:=4990D)
            Dim config = MakeConfig()
            config.ExtendTpEnabled = True

            Dim currentStop = 5010D
            Dim currentTp   = 4990D   ' initial TP = entry − 1×tpDelta
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=10D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' close(4990) ≤ currentTp(4990) → extended = 4980 ≥ cap(5000−30=4970) → advances
            Assert.Equal(4980D, currentTp)
        End Sub

        ''' <summary>
        ''' stopDelta = 0 → early return; neither SL nor TP are touched.
        ''' </summary>
        <Fact>
        Public Sub UpdateDynamicExits_StopDeltaZero_EarlyReturn_NothingChanged()
            Dim trade  = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar    = MakeBar(high:=5020D, low:=5015D, close:=5018D)
            Dim config = MakeConfig()
            config.TrailingStopEnabled       = True
            config.BreakEvenOnHalfTpEnabled  = True
            config.ExtendTpEnabled           = True

            Dim currentStop = 4990D
            Dim currentTp   = 5010D
            BacktestMetrics.UpdateDynamicExits(trade, bar, config,
                                               stopDelta:=0D, tpDelta:=10D,
                                               currentStop, currentTp)

            ' stopDelta = 0 → function returns immediately, nothing changes
            Assert.Equal(4990D, currentStop)
            Assert.Equal(5010D, currentTp)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' TEST-02: Scale-In Multi-Leg Exit Tests
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' 2-leg Buy: initial entry at 5000, scale-in at 5002, both exit at TP 5010.
        ''' Individual leg P&amp;Ls sum correctly; BuildResult counts 1 position (winner).
        ''' MES: $5/pt.  Leg1 = (5010-5000)×$5 = $50.  Leg2 = (5010-5002)×$5 = $40.
        ''' </summary>
        <Fact>
        Public Sub ScaleIn_TwoLegBuy_TakeProfit_AggregatePnLIsCorrect()
            Dim config = MakeConfig()
            ' Leg 1 — initial entry
            Dim leg1 = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=5010D, qty:=1)
            leg1.PositionGroupId = 1
            leg1.PnL = BacktestMetrics.CalculatePnL(leg1, config)
            ' Leg 2 — scale-in at higher entry price
            Dim leg2 = MakeTrade("Buy", entryPrice:=5002D, exitPrice:=5010D, qty:=1)
            leg2.PositionGroupId = 1   ' same group as initial entry
            leg2.PnL = BacktestMetrics.CalculatePnL(leg2, config)

            ' Individual legs
            Assert.Equal(50D, leg1.PnL.Value)    ' (5010-5000) × 1 × $5
            Assert.Equal(40D, leg2.PnL.Value)    ' (5010-5002) × 1 × $5

            ' Aggregate via BuildResult
            Dim trades = New List(Of BacktestTrade) From {leg1, leg2}
            Dim result = BacktestMetrics.BuildResult(config, trades, finalCapital:=50090D, maxDrawdown:=0D)

            Assert.Equal(1, result.TotalTrades)       ' 1 position (both legs share group 1)
            Assert.Equal(1, result.WinningTrades)
            Assert.Equal(0, result.LosingTrades)
            Assert.Equal(90D, result.TotalPnL)         ' 50 + 40
            Assert.Equal(90D, result.AveragePnLPerTrade)
            Assert.Equal(2, result.Trades.Count)       ' both raw rows preserved
        End Sub

        ''' <summary>
        ''' 2-leg Buy: initial entry at 5000, scale-in at 5002, both hit SL at 4997.5.
        ''' A 2-leg position hitting SL produces the correct aggregate loss.
        ''' Leg1 = (4997.5-5000)×$5 = -$12.50.  Leg2 = (4997.5-5002)×$5 = -$22.50.
        ''' </summary>
        <Fact>
        Public Sub ScaleIn_TwoLegBuy_StopLoss_AggregateLossIsCorrect()
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)
            ' SL is 10 ticks = 2.5 pts below entry; both legs exit at the same SL fill price
            Dim slFill = 4997.5D   ' entry(5000) − 10 ticks × 0.25

            Dim leg1 = MakeTrade("Buy", entryPrice:=5000D, exitPrice:=slFill, qty:=1)
            leg1.PositionGroupId = 1
            leg1.ExitReason = "StopLoss"
            leg1.PnL = BacktestMetrics.CalculatePnL(leg1, config)

            Dim leg2 = MakeTrade("Buy", entryPrice:=5002D, exitPrice:=slFill, qty:=1)
            leg2.PositionGroupId = 1
            leg2.ExitReason = "StopLoss"
            leg2.PnL = BacktestMetrics.CalculatePnL(leg2, config)

            ' Individual leg losses
            Assert.Equal(-12.5D, leg1.PnL.Value)   ' (4997.5-5000) × 1 × $5
            Assert.Equal(-22.5D, leg2.PnL.Value)   ' (4997.5-5002) × 1 × $5

            ' Aggregate — both are losses so position is a loser
            Dim trades = New List(Of BacktestTrade) From {leg1, leg2}
            Dim result = BacktestMetrics.BuildResult(config, trades, finalCapital:=49965D, maxDrawdown:=35D)

            Assert.Equal(1, result.TotalTrades)
            Assert.Equal(0, result.WinningTrades)
            Assert.Equal(1, result.LosingTrades)
            Assert.Equal(-35D, result.TotalPnL)         ' -12.5 + -22.5
            Assert.True(result.TotalPnL < 0D, "Aggregate P&L of a SL exit must be negative")
        End Sub

        ''' <summary>
        ''' PositionGroupId consistency: 3 legs share group 1; a 4th leg belongs to group 2.
        ''' BuildResult must count exactly 2 positions — not 4 individual entries.
        ''' Group-1 aggregate P&amp;L: +$50 + $40 + $30 = +$120 (winner).
        ''' Group-2 P&amp;L: -$20 (loser).
        ''' </summary>
        <Fact>
        Public Sub ScaleIn_PositionGroupId_Consistent_BuildResultCountsPositions()
            Dim config = MakeConfig()

            ' Group 1 — three legs, all profitable
            Dim leg1 = MakeClosedTrade(50D,  positionGroupId:=1)
            Dim leg2 = MakeClosedTrade(40D,  positionGroupId:=1)
            Dim leg3 = MakeClosedTrade(30D,  positionGroupId:=1)
            ' Group 2 — single losing position
            Dim leg4 = MakeClosedTrade(-20D, positionGroupId:=2)

            Dim trades = New List(Of BacktestTrade) From {leg1, leg2, leg3, leg4}
            Dim result = BacktestMetrics.BuildResult(config, trades, finalCapital:=50120D, maxDrawdown:=20D)

            Assert.Equal(2, result.TotalTrades)          ' 2 unique groups, not 4 rows
            Assert.Equal(1, result.WinningTrades)        ' group 1 sum = +120 > 0
            Assert.Equal(1, result.LosingTrades)         ' group 2 sum = -20 ≤ 0
            Assert.Equal(100D, result.TotalPnL)          ' 120 + (-20)
            Assert.Equal(50D, result.AveragePnLPerTrade) ' 100 / 2 positions
            Assert.Equal(4, result.Trades.Count)         ' all raw rows preserved
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' CheckFixedExit
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CheckFixedExit_Buy_SlHit_ReturnsStopLoss()
            ' Long SL fires when bar.Low touches or crosses below sl.
            Dim bar = MakeBar(high:=5005D, low:=4994D, close:=4995D)
            Dim result = BacktestMetrics.CheckFixedExit("Buy", bar, sl:=4995D, tp:=5010D)
            Assert.Equal("StopLoss", result)
        End Sub

        <Fact>
        Public Sub CheckFixedExit_Buy_TpHit_ReturnsTakeProfit()
            ' Long TP fires when bar.High touches or crosses above tp.
            Dim bar = MakeBar(high:=5011D, low:=5001D, close:=5008D)
            Dim result = BacktestMetrics.CheckFixedExit("Buy", bar, sl:=4990D, tp:=5010D)
            Assert.Equal("TakeProfit", result)
        End Sub

        <Fact>
        Public Sub CheckFixedExit_Sell_SlHit_ReturnsStopLoss()
            ' Short SL fires when bar.High touches or crosses above sl.
            Dim bar = MakeBar(high:=5006D, low:=4998D, close:=5000D)
            Dim result = BacktestMetrics.CheckFixedExit("Sell", bar, sl:=5005D, tp:=4990D)
            Assert.Equal("StopLoss", result)
        End Sub

        <Fact>
        Public Sub CheckFixedExit_Sell_TpHit_ReturnsTakeProfit()
            ' Short TP fires when bar.Low touches or crosses below tp.
            Dim bar = MakeBar(high:=5001D, low:=4989D, close:=4992D)
            Dim result = BacktestMetrics.CheckFixedExit("Sell", bar, sl:=5010D, tp:=4990D)
            Assert.Equal("TakeProfit", result)
        End Sub

        <Fact>
        Public Sub CheckFixedExit_NoExit_ReturnsNothing()
            ' Bar stays entirely between sl and tp — no exit triggered.
            Dim bar = MakeBar(high:=5005D, low:=4997D, close:=5001D)
            Dim result = BacktestMetrics.CheckFixedExit("Buy", bar, sl:=4990D, tp:=5010D)
            Assert.Null(result)
        End Sub

    End Class

End Namespace
