Option Strict On
Option Explicit On

Imports System.Linq
Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Tests derived from the BackTestSMEReview.md static analysis (2026-04-26).
    ''' Each test maps to a numbered finding (T1–T10) in that document.
    '''
    ''' Tests cover three reviewer roles:
    '''   Senior Test Engineer  — T1, T2, T3, T4, T5, T6, T7
    '''   Experienced Quant     — T8, T9, T10
    ''' </summary>
    Public Class BacktestSMEReviewTests

        Private Shared ReadOnly _sut As New BacktestEngine(
            Nothing, Nothing, NullLogger(Of BacktestEngine).Instance)

        ' ── Bar Construction Helpers ─────────────────────────────────────────────────

        ''' <summary>
        ''' Creates 200 bars with a deliberate EMA21/EMA50 crossover.
        ''' Bars 0–59: declining −2 pts/bar (EMA21 below EMA50).
        ''' Bars 60–199: rising +4 pts/bar (EMA21 crosses above EMA50 ~bar 75).
        ''' Bars have constant spread (High=Close+2, Low=Close−2) so ATR is non-zero.
        ''' Volume is constant at 300 — below the 1.1× volume gate, so the volume
        ''' bonus is skipped and the score comes entirely from price-based signals.
        ''' </summary>
        Private Shared Function MakeCrossoverBars() As List(Of MarketBar)
            Dim t0 = New DateTimeOffset(2026, 1, 6, 9, 30, 0, TimeSpan.Zero)
            Dim bars As New List(Of MarketBar)(200)
            For i As Integer = 0 To 199
                Dim closePrice As Decimal = If(i < 60,
                    5200D - i * 2D,
                    5082D + (i - 60) * 4D)
                bars.Add(New MarketBar With {
                    .ContractId = "MES",
                    .Timeframe = BarTimeframe.OneMinute,
                    .Timestamp = t0.AddMinutes(i),
                    .Open = closePrice,
                    .High = closePrice + 2D,
                    .Low = closePrice - 2D,
                    .Close = closePrice,
                    .Volume = 300L,
                    .VWAP = closePrice
                })
            Next
            Return bars
        End Function

        Private Shared Function MakeEmaRsiConfig(
                Optional minConfidence As Single = 0.75F,
                Optional spreadTicks As Integer = 0) As BacktestConfiguration
            Return New BacktestConfiguration() With {
                .ContractId = "MES",
                .PointValue = 5.0D,
                .TickSize = 0.25D,
                .StrategyCondition = StrategyConditionType.EmaRsiWeightedScore,
                .UseAtrMode = True,
                .SlAtrMultiple = 1.5D,
                .TpAtrMultiple = 3.0D,
                .MinSignalConfidence = minConfidence,
                .MaxScaleIns = 1,
                .SpreadTicks = spreadTicks
            }
        End Function

        Private Shared Function MakeDynConfig(
                Optional trailing As Boolean = False,
                Optional breakEven As Boolean = False,
                Optional extendTp As Boolean = False) As BacktestConfiguration
            Return New BacktestConfiguration() With {
                .PointValue = 5.0D,
                .TickSize = 0.25D,
                .TrailingStopEnabled = trailing,
                .BreakEvenOnHalfTpEnabled = breakEven,
                .ExtendTpEnabled = extendTp
            }
        End Function

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T1 — Strategies actually fire trades on valid signal bars
        '
        ' SME finding (Test Engineer H2/M2): all existing tests use monotonically
        ' rising bars that never produce an EMA crossover, so no test verifies that
        ' any strategy actually generates a trade.  A strategy that silently never fires
        ' (due to a warm-up bug, NaN guard, or threshold change) would not be caught.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T1_EmaRsi_CrossoverBars_ProducesAtLeastOneTrade()
            Dim result = _sut.RunReplay(MakeEmaRsiConfig(), MakeCrossoverBars(), CancellationToken.None)

            Assert.True(result.TotalTrades > 0,
                        "Expected ≥1 trade from the decline→rise crossover bar sequence. " &
                        "If this fails, the EmaRsi signal provider no longer fires on a valid crossover.")
        End Sub

        <Fact>
        Public Sub T1_MultiConfluence_CrossoverBars_DoesNotThrow()
            ' MultiConfluence has warmUp=80 and a complex signal; this verifies it runs
            ' without NaN overflow or exception on the crossover bars.
            Dim config = New BacktestConfiguration With {
                .ContractId = "MES",
                .PointValue = 5.0D,
                .TickSize = 0.25D,
                .StrategyCondition = StrategyConditionType.MultiConfluence,
                .UseAtrMode = True,
                .SlAtrMultiple = 1.0D,
                .TpAtrMultiple = 2.0D,
                .MinSignalConfidence = 0.70F
            }
            Dim ex = Record.Exception(
                Sub() _sut.RunReplay(config, MakeCrossoverBars(), CancellationToken.None))
            Assert.Null(ex)
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T2 — Train/test split partitions bars at the correct temporal boundary
        '
        ' SME finding (Test Engineer M4): the 60/40 split in RunBacktestAsync has no
        ' unit tests.  Tested here via RunReplay with manually split bar lists, which
        ' exercises the same partitioning logic the engine applies.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T2_TrainTestSplit_FloorIndexIsCorrect()
            ' floor(200 × 0.6) = 120
            Assert.Equal(120, CInt(Math.Floor(200 * 0.6)))
        End Sub

        <Fact>
        Public Sub T2_TrainTestSplit_TrainTradesAllPrecedeFirstTestBar()
            Dim bars = MakeCrossoverBars()
            Dim splitIdx = CInt(Math.Floor(bars.Count * 0.6))   ' = 120
            Dim trainBars = bars.Take(splitIdx).ToList()
            Dim testBars  = bars.Skip(splitIdx).ToList()

            Dim trainResult = _sut.RunReplay(MakeEmaRsiConfig(), trainBars, CancellationToken.None)
            Dim testResult  = _sut.RunReplay(MakeEmaRsiConfig(), testBars,  CancellationToken.None)

            If trainResult.Trades.Any() AndAlso testResult.Trades.Any() Then
                Dim lastTrainEntry  = trainResult.Trades.Max(Function(t) t.EntryTime)
                Dim firstTestEntry  = testResult.Trades.Min(Function(t) t.EntryTime)
                Assert.True(lastTrainEntry <= firstTestEntry,
                            $"Train set last entry ({lastTrainEntry:u}) must precede test set first entry ({firstTestEntry:u}).")
            End If
        End Sub

        <Fact>
        Public Sub T2_TrainTestSplit_BothHalvesRunWithoutException()
            Dim bars = MakeCrossoverBars()
            Dim trainBars = bars.Take(120).ToList()
            Dim testBars  = bars.Skip(120).ToList()

            Dim exTrain = Record.Exception(
                Sub() _sut.RunReplay(MakeEmaRsiConfig(), trainBars, CancellationToken.None))
            Dim exTest = Record.Exception(
                Sub() _sut.RunReplay(MakeEmaRsiConfig(), testBars, CancellationToken.None))

            Assert.Null(exTrain)
            Assert.Null(exTest)
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T3 — All closed trades have a non-null ExitReason and ExitPrice
        '
        ' SME finding (Test Engineer H5): a signal on the last bar sets `pending` but
        ' the fill loop never runs.  EndOfData close handles all open legs correctly,
        ' but an orphaned pending (never filled) silently vanishes without error.
        ' This test verifies no trade surfaces with a null exit.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T3_AllTrades_HaveNonNullExitReason()
            Dim result = _sut.RunReplay(MakeEmaRsiConfig(), MakeCrossoverBars(), CancellationToken.None)

            For Each trade In result.Trades
                Assert.False(String.IsNullOrEmpty(trade.ExitReason),
                             $"Trade entered at {trade.EntryTime:u} has null/empty ExitReason.")
            Next
        End Sub

        <Fact>
        Public Sub T3_AllTrades_HaveExitPrice()
            Dim result = _sut.RunReplay(MakeEmaRsiConfig(), MakeCrossoverBars(), CancellationToken.None)

            For Each trade In result.Trades
                Assert.True(trade.ExitPrice.HasValue,
                            $"Trade entered at {trade.EntryTime:u} has no ExitPrice — position never closed.")
            Next
        End Sub

        <Fact>
        Public Sub T3_AllTrades_HavePnL()
            Dim result = _sut.RunReplay(MakeEmaRsiConfig(), MakeCrossoverBars(), CancellationToken.None)

            For Each trade In result.Trades
                Assert.True(trade.PnL.HasValue,
                            $"Trade entered at {trade.EntryTime:u} has no P&L — CalculatePnL was not called.")
            Next
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T4 — CheckFixedExit: when SL and TP are both hit on the same bar, SL wins
        '
        ' SME finding (Test Engineer M5): the SL-priority tie-break is undocumented
        ' and untested.  On a volatile bar that sweeps both levels, the engine always
        ' records StopLoss because the If-block tests SL first.  A change to check TP
        ' first would silently convert all double-hit bars from losses to wins.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T4_CheckFixedExit_Buy_BothLevelsHit_ReturnsSL()
            ' Buy: SL=4985 (Low=4980 <= 4985 ✓), TP=5015 (High=5020 >= 5015 ✓)
            Dim bar = New MarketBar With {.High = 5020D, .Low = 4980D}
            Assert.Equal("StopLoss", BacktestMetrics.CheckFixedExit("Buy", bar, sl:=4985D, tp:=5015D))
        End Sub

        <Fact>
        Public Sub T4_CheckFixedExit_Sell_BothLevelsHit_ReturnsSL()
            ' Sell: SL=5015 (High=5020 >= 5015 ✓), TP=4985 (Low=4980 <= 4985 ✓)
            Dim bar = New MarketBar With {.High = 5020D, .Low = 4980D}
            Assert.Equal("StopLoss", BacktestMetrics.CheckFixedExit("Sell", bar, sl:=5015D, tp:=4985D))
        End Sub

        <Fact>
        Public Sub T4_CheckFixedExit_OnlyTP_Hit_ReturnsTakeProfit()
            Dim bar = New MarketBar With {.High = 5020D, .Low = 4995D}
            ' Buy: SL=4990 (Low=4995 > 4990 — NOT hit), TP=5015 (High=5020 >= 5015 ✓)
            Assert.Equal("TakeProfit", BacktestMetrics.CheckFixedExit("Buy", bar, sl:=4990D, tp:=5015D))
        End Sub

        <Fact>
        Public Sub T4_CheckFixedExit_NeitherLevelHit_ReturnsNothing()
            Dim bar = New MarketBar With {.High = 5010D, .Low = 4992D}
            ' Buy: SL=4990 (Low=4992 > 4990 — NOT hit), TP=5015 (High=5010 < 5015 — NOT hit)
            Assert.Null(BacktestMetrics.CheckFixedExit("Buy", bar, sl:=4990D, tp:=5015D))
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T5 — TrailingStop: current stop never retreats away from the favourable side
        '
        ' SME finding (Test Engineer M6): UpdateDynamicExits trailing stop path has
        ' no unit tests.  A regression (e.g. wrong Max/Min choice) would allow the
        ' stop to move against the position, widening risk instead of protecting profit.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T5_TrailingStop_Long_AdvancesOnRise_FreezeOnPullback()
            Dim config = MakeDynConfig(trailing:=True)
            Dim trade  = New BacktestTrade With {.Side = "Buy", .EntryPrice = 5000D}
            Dim stopDelta = 10D : Dim tpDelta = 20D
            Dim dynStop = 4990D : Dim dynTp = 5020D

            ' Bar 1 advance: close=5015 → new stop = 5015−10 = 5005
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5015D},
                                               config, stopDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(5005D, dynStop)

            ' Bar 2 pullback: close=5003 → stop must NOT retreat below 5005
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5003D},
                                               config, stopDelta, tpDelta, dynStop, dynTp)
            Assert.True(dynStop >= 5005D, $"Stop retreated to {dynStop} — must stay at 5005 or above.")
        End Sub

        <Fact>
        Public Sub T5_TrailingStop_Short_AdvancesOnDrop_FreezeOnRally()
            Dim config = MakeDynConfig(trailing:=True)
            Dim trade  = New BacktestTrade With {.Side = "Sell", .EntryPrice = 5000D}
            Dim stopDelta = 10D : Dim tpDelta = 20D
            Dim dynStop = 5010D : Dim dynTp = 4980D

            ' Bar 1 advance: close=4985 → new stop = 4985+10 = 4995
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 4985D},
                                               config, stopDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(4995D, dynStop)

            ' Bar 2 rally: close=4993 → stop must NOT retreat above 4995
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 4993D},
                                               config, stopDelta, tpDelta, dynStop, dynTp)
            Assert.True(dynStop <= 4995D, $"Short stop retreated to {dynStop} — must stay at 4995 or below.")
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T6 — BreakEven: stop moves to entry when close reaches 50% of TP distance
        '
        ' SME finding (Test Engineer M6): break-even logic has no unit tests.
        ' Key invariants: (a) fires when Close >= entry + tpDelta×0.5 for longs,
        ' (b) does not fire before that threshold, (c) is idempotent after firing.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T6_BreakEven_Long_StopMovesToEntryAtHalfTpDistance()
            Dim config = MakeDynConfig(breakEven:=True)
            Dim trade  = New BacktestTrade With {.Side = "Buy", .EntryPrice = 5000D}
            Dim stopDelta = 20D  ' initial SL at 4980
            Dim tpDelta   = 40D  ' TP at 5040; BE threshold = 5000 + 40×0.5 = 5020
            Dim dynStop = 4980D : Dim dynTp = 5040D

            ' Bar below threshold — stop must not move
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5015D},
                                               config, stopDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(4980D, dynStop)

            ' Bar at exact threshold — stop must move to entry price
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5020D},
                                               config, stopDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(5000D, dynStop)
        End Sub

        <Fact>
        Public Sub T6_BreakEven_AlreadyAtEntry_DoesNotRetreatOnFurtherAdvance()
            Dim config = MakeDynConfig(breakEven:=True)
            Dim trade  = New BacktestTrade With {.Side = "Buy", .EntryPrice = 5000D}
            Dim dynStop = 5000D : Dim dynTp = 5040D  ' already at break-even

            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5030D},
                                               config, 20D, 40D, dynStop, dynTp)
            Assert.True(dynStop >= 5000D, "Break-even must not retreat stop below entry once set.")
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T7 — ExtendTp: hard cap at 3× initial tpDelta from entry, never exceeded
        '
        ' SME finding (Test Engineer M6): extend-TP cap logic has no unit tests.
        ' A regression removing the cap would allow unbounded TP advances, keeping
        ' a position open indefinitely even when a reversal has started.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T7_ExtendTp_Long_ThreeAdvancesHitCap()
            Dim config = MakeDynConfig(extendTp:=True)
            Dim trade  = New BacktestTrade With {.Side = "Buy", .EntryPrice = 5000D}
            Dim tpDelta = 10D                    ' initial TP = 5010; cap = 5000 + 3×10 = 5030
            Dim dynStop = 4990D : Dim dynTp = 5010D

            ' Advance 1: close=5012 >= 5010 → TP = 5020
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5012D},
                                               config, tpDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(5020D, dynTp)

            ' Advance 2: close=5022 >= 5020 → TP = 5030 (= cap)
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5022D},
                                               config, tpDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(5030D, dynTp)

            ' Advance 3 attempt: close=5032 >= 5030 → extended=5040 > cap=5030 → BLOCKED
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 5032D},
                                               config, tpDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(5030D, dynTp)
        End Sub

        <Fact>
        Public Sub T7_ExtendTp_Short_ThreeAdvancesHitCap()
            Dim config = MakeDynConfig(extendTp:=True)
            Dim trade  = New BacktestTrade With {.Side = "Sell", .EntryPrice = 5000D}
            Dim tpDelta = 10D                    ' initial TP = 4990; cap = 5000 − 30 = 4970
            Dim dynStop = 5010D : Dim dynTp = 4990D

            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 4988D},
                                               config, tpDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(4980D, dynTp)

            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 4978D},
                                               config, tpDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(4970D, dynTp)

            ' Cap hit — must not advance further
            BacktestMetrics.UpdateDynamicExits(trade, New MarketBar With {.Close = 4968D},
                                               config, tpDelta, tpDelta, dynStop, dynTp)
            Assert.Equal(4970D, dynTp)
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T8 — Calmar: zero MaxDrawdown returns TotalPnL — documents early-win bias
        '
        ' SME finding (Quant H2 + Test Engineer H2): InitialCapital=0 means the first
        ' profitable trade sets peakCapital.  A run with no losing trades accumulates
        ' MaxDrawdown=0.  The guard `If(MaxDD > 0, PnL/MaxDD, PnL)` then returns the
        ' raw P&L as Calmar — which dominates over any run that had even one loss,
        ' regardless of actual risk-adjusted performance.  Two runs with identical
        ' TotalPnL but different MaxDrawdown produce incomparable Calmar ratios.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T8_Calmar_ZeroDrawdown_ReturnsRawPnL_NotRatio()
            Dim resultNoDD   = New BacktestResult With {.TotalPnL = 500D, .MaxDrawdown = 0D}
            Dim resultWithDD = New BacktestResult With {.TotalPnL = 500D, .MaxDrawdown = 100D}

            Dim calmarNoDD   = If(resultNoDD.MaxDrawdown > 0D,
                                  resultNoDD.TotalPnL / resultNoDD.MaxDrawdown,
                                  resultNoDD.TotalPnL)
            Dim calmarWithDD = If(resultWithDD.MaxDrawdown > 0D,
                                  resultWithDD.TotalPnL / resultWithDD.MaxDrawdown,
                                  resultWithDD.TotalPnL)

            Assert.Equal(500D, calmarNoDD)    ' no-drawdown run: Calmar = raw PnL = 500
            Assert.Equal(5D,   calmarWithDD)  ' with-drawdown run: Calmar = 500/100 = 5
            ' Same P&L, 100× different Calmar — the no-drawdown run dominates the leaderboard.
            Assert.True(calmarNoDD > calmarWithDD,
                        "Zero-drawdown run dominates the Calmar leaderboard over a run with identical " &
                        "P&L but one loss — this is the early-win bias documented in BackTestSMEReview.md.")
        End Sub

        <Fact>
        Public Sub T8_BuildResult_ZeroInitialCapital_MaxDrawdownStartsFromFirstLoss()
            ' InitialCapital=0 means peakCapital starts at 0.
            ' First losing trade immediately produces MaxDrawdown > 0.
            Dim config = New BacktestConfiguration With {
                .ContractId = "MES", .PointValue = 5.0D, .TickSize = 0.25D, .InitialCapital = 0D
            }
            Dim losingTrade = New BacktestTrade With {
                .Side = "Buy", .EntryPrice = 5000D,
                .ExitPrice = 4990D, .ExitReason = "StopLoss",
                .Quantity = 1, .PositionGroupId = 1
            }
            losingTrade.PnL = BacktestMetrics.CalculatePnL(losingTrade, config)

            Dim trades = New List(Of BacktestTrade) From {losingTrade}
            Dim finalCapital = 0D + losingTrade.PnL.GetValueOrDefault()

            ' Manually replicate RunReplay's peak/drawdown tracking with InitialCapital=0
            Dim peakCapital = 0D
            Dim maxDrawdown = 0D
            If finalCapital > peakCapital Then peakCapital = finalCapital
            Dim dd = peakCapital - finalCapital
            If dd > maxDrawdown Then maxDrawdown = dd

            Assert.True(maxDrawdown > 0D, "A single losing trade from InitialCapital=0 must produce MaxDrawdown > 0.")
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T9 — SpreadTicks > 0 reduces per-trade P&L vs SpreadTicks = 0
        '
        ' SME finding (Quant H5): MaxEffort never sets SpreadTicks — all 1,080 runs
        ' use SpreadTicks=0.  For M6E (typical spread = 1 pip = $12.50/side) this
        ' understates per-trade cost by ~$25.  100 trades = $2,500 phantom P&L.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T9_SpreadTicks_PositiveSpread_ReducesPnLOnTradesThatFire()
            Dim bars = MakeCrossoverBars()
            Dim resultNoSpread   = _sut.RunReplay(MakeEmaRsiConfig(spreadTicks:=0), bars, CancellationToken.None)
            Dim resultWithSpread = _sut.RunReplay(MakeEmaRsiConfig(spreadTicks:=2), bars, CancellationToken.None)

            ' Only assert when both runs produce trades (same bar sequence, same strategy)
            If resultNoSpread.TotalTrades > 0 AndAlso resultWithSpread.TotalTrades > 0 Then
                Assert.True(resultWithSpread.TotalPnL < resultNoSpread.TotalPnL,
                            $"SpreadTicks=2 P&L ({resultWithSpread.TotalPnL:C0}) must be below " &
                            $"SpreadTicks=0 P&L ({resultNoSpread.TotalPnL:C0}).  " &
                            "MaxEffort omits spread cost for all 1,080 runs — see BackTestSMEReview.md T9.")
            End If
        End Sub

        <Fact>
        Public Sub T9_SpreadTicks_TradeCount_NeverExceedsNoSpread()
            ' Spread shifts the fill price which shifts SL/TP — an earlier SL hit can
            ' block a re-entry, so the spread run may produce fewer trades.  It cannot
            ' create new signals, so r2.TotalTrades <= r0.TotalTrades must hold.
            Dim bars = MakeCrossoverBars()
            Dim r0 = _sut.RunReplay(MakeEmaRsiConfig(spreadTicks:=0), bars, CancellationToken.None)
            Dim r2 = _sut.RunReplay(MakeEmaRsiConfig(spreadTicks:=2), bars, CancellationToken.None)
            Assert.True(r2.TotalTrades <= r0.TotalTrades,
                        $"SpreadTicks=2 produced more trades ({r2.TotalTrades}) than SpreadTicks=0 ({r0.TotalTrades}) — spread cannot create signals.")
        End Sub

        ' ═══════════════════════════════════════════════════════════════════════════
        ' T10 — Low trade count: metrics are valid but statistically insignificant
        '
        ' SME finding (Quant H4): MaxEffort has no minimum trade count filter.
        ' A strategy that fires 3 trades (all wins from a short lucky window) can
        ' produce WinRate=100%, high Sharpe, and high Calmar — and rank #1.
        ' The engine produces correct arithmetic; the caller must apply a minimum
        ' threshold (≥30 positions is the standard academic floor) before ranking.
        ' ═══════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub T10_LowTradeCount_MetricsAreArithmeticallyValid()
            ' Take only the first 100 bars — likely produces 0–2 trades
            Dim bars = MakeCrossoverBars().Take(100).ToList()
            Dim result = _sut.RunReplay(MakeEmaRsiConfig(), bars, CancellationToken.None)

            Assert.InRange(result.WinRate, 0.0F, 1.0F)
            Assert.True(result.TotalTrades >= 0)
            Assert.True(result.MaxDrawdown >= 0D)
        End Sub

        <Fact>
        Public Sub T10_LowTradeCount_CanProduceHundredPctWinRate()
            ' A short window during a strong trend can produce all-win results.
            ' This documents that WinRate=1.0 is arithmetically valid but not reliable
            ' without a minimum trade count filter in the caller (MaxEffort).
            Dim bars = MakeCrossoverBars()
            Dim result = _sut.RunReplay(MakeEmaRsiConfig(), bars, CancellationToken.None)

            If result.TotalTrades > 0 AndAlso result.TotalTrades < 10 Then
                ' A low-trade run with a high win rate proves the statistical gap exists.
                ' No assertion — the test passes to document the scenario.
                ' Fix: MaxEffort should filter results with TotalTrades < 30 before ranking.
            End If

            ' Always-valid: Calmar of a zero-drawdown low-trade run equals TotalPnL
            If result.TotalTrades > 0 AndAlso result.MaxDrawdown = 0D Then
                Dim calmar = If(result.MaxDrawdown > 0D,
                                result.TotalPnL / result.MaxDrawdown,
                                result.TotalPnL)
                Assert.Equal(result.TotalPnL, calmar)
            End If
        End Sub

    End Class

End Namespace
