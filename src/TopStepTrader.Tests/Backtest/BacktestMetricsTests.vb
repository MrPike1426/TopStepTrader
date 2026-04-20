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
            ' SL=10 ticks → stopDelta = 10 × 0.25 = 2.5 pts
            ' Buy at 5000 → SL triggers when bar.Low ≤ 4997.5
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4997.5D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.CheckExit(trade, bar, config)

            Assert.Equal("StopLoss", result)
        End Sub

        <Fact>
        Public Sub CheckExit_BuyHitsTakeProfit_ReturnsTakeProfit()
            ' TP=20 ticks → tpDelta = 20 × 0.25 = 5 pts
            ' Buy at 5000 → TP triggers when bar.High ≥ 5005
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5005D, low:=4999D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.CheckExit(trade, bar, config)

            Assert.Equal("TakeProfit", result)
        End Sub

        <Fact>
        Public Sub CheckExit_BuyNeitherLevelHit_ReturnsNothing()
            ' Bar stays comfortably inside SL/TP range — no exit
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5002D, low:=4999D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.CheckExit(trade, bar, config)

            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub CheckExit_SellHitsStopLoss_ReturnsStopLoss()
            ' SL=10 ticks → stopDelta = 2.5 pts
            ' Sell at 5000 → SL triggers when bar.High ≥ 5002.5
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5002.5D, low:=4999D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.CheckExit(trade, bar, config)

            Assert.Equal("StopLoss", result)
        End Sub

        <Fact>
        Public Sub CheckExit_SellHitsTakeProfit_ReturnsTakeProfit()
            ' TP=20 ticks → tpDelta = 5 pts
            ' Sell at 5000 → TP triggers when bar.Low ≤ 4995
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4995D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.CheckExit(trade, bar, config)

            Assert.Equal("TakeProfit", result)
        End Sub

        <Fact>
        Public Sub CheckExit_SellNeitherLevelHit_ReturnsNothing()
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4999D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.CheckExit(trade, bar, config)

            Assert.Null(result)
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
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", config)

            Assert.Equal(4997.5D, result)
        End Sub

        <Fact>
        Public Sub GetExitPrice_BuyTakeProfit_ReturnsEntryPlusTpDelta()
            ' TP=20 ticks → tpDelta=5 pts; Buy entry 5000 → TP fill = 5005
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5006D, low:=4999D, close:=5004D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.GetExitPrice(trade, bar, "TakeProfit", config)

            Assert.Equal(5005D, result)
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellStopLoss_ReturnsEntryPlusStopDelta()
            ' SL=10 ticks → stopDelta=2.5 pts; Sell entry 5000 → SL fill = 5002.5 (loss)
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5003D, low:=4999D, close:=5001D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", config)

            Assert.Equal(5002.5D, result)
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellTakeProfit_ReturnsEntryMinusTpDelta()
            ' TP=20 ticks → tpDelta=5 pts; Sell entry 5000 → TP fill = 4995 (profit)
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4994D, close:=4996D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.GetExitPrice(trade, bar, "TakeProfit", config)

            Assert.Equal(4995D, result)
        End Sub

        <Fact>
        Public Sub GetExitPrice_EndOfData_ReturnsBarClose()
            ' EndOfData exits at bar.Close — no fixed level was hit
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4999D, close:=5000.5D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim result = BacktestMetrics.GetExitPrice(trade, bar, "EndOfData", config)

            Assert.Equal(5000.5D, result)
        End Sub

        <Fact>
        Public Sub GetExitPrice_BuyStopLoss_IsAlwaysBelowEntry()
            ' Core invariant: a Buy StopLoss fill must be below entry (always a loss)
            Dim trade = MakeTrade("Buy", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5001D, low:=4997D, close:=4999D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim exitPrice = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", config)

            Assert.True(exitPrice < trade.EntryPrice,
                        $"Buy StopLoss exit ({exitPrice}) must be below entry ({trade.EntryPrice})")
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellStopLoss_IsAlwaysAboveEntry()
            ' Core invariant: a Sell StopLoss fill must be above entry (always a loss)
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            Dim bar = MakeBar(high:=5003D, low:=4999D, close:=5001D)
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim exitPrice = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", config)

            Assert.True(exitPrice > trade.EntryPrice,
                        $"Sell StopLoss exit ({exitPrice}) must be above entry ({trade.EntryPrice})")
        End Sub

        <Fact>
        Public Sub GetExitPrice_SellStopLoss_ProducesNegativePnL()
            ' UAT-BUG-006 core regression: Sell StopLoss must produce a LOSS, never a profit.
            ' Before the fix: exit used bar.Close; if bar.High hit SL but bar.Close was below
            ' entry, CalculatePnL returned a positive number for a "StopLoss" trade.
            Dim trade = MakeTrade("Sell", entryPrice:=5000D)
            ' Bar that wicks above SL level but closes below entry (the original failure scenario)
            Dim bar = MakeBar(high:=5003D, low:=4990D, close:=4995D)   ' close < entry = "profitable" for Sell
            Dim config = MakeConfig(slTicks:=10, tpTicks:=20)

            Dim exitPrice = BacktestMetrics.GetExitPrice(trade, bar, "StopLoss", config)
            trade.ExitPrice = exitPrice
            Dim pnl = BacktestMetrics.CalculatePnL(trade, config)

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

        ''' <summary>Build a minimal BacktestConfiguration for metric tests.
        ''' <paramref name="slTicks"/> and <paramref name="tpTicks"/> are in MES ticks (0.25 pts each).
        ''' BacktestMetrics uses dollar amounts: dollars = ticks × TickSize(0.25) × PointValue(5) × Quantity(1).
        ''' </summary>
        Private Shared Function MakeConfig(Optional slTicks As Decimal = 10D,
                                            Optional tpTicks As Decimal = 20D) As BacktestConfiguration
            ' Convert tick counts to dollar amounts for the dollar-based BacktestMetrics convention.
            ' MES defaults: TickSize=0.25, PointValue=5, Quantity=1
            ' → slDollars = slTicks × 0.25 × 5 = slTicks × 1.25
            Return New BacktestConfiguration With {
                .InitialSlAmount = slTicks * 0.25D * 5D,
                .InitialTpAmount = tpTicks * 0.25D * 5D,
                .RunName = "Unit Test",
                .ContractId = "CON.F.US.MES.H26",
                .StartDate = Date.Today.AddDays(-7),
                .EndDate = Date.Today,
                .InitialCapital = 50000D,
                .MinSignalConfidence = 0.65F
            }
        End Function

    End Class

End Namespace
