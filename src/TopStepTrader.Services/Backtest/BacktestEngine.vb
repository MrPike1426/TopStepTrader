Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Walk-forward backtest engine. Replays historical bars through the same EMA/RSI
    ''' weighted-scoring algorithm used by StrategyExecutionEngine (live trading), so that
    ''' backtest results represent what live trading will actually produce.
    '''
    ''' Signal algorithm (mirrors StrategyExecutionEngine.EmaRsiWeightedScore):
    '''   Six signals scored 0â€“100; fire Long when bullScore â‰¥ threshold, Short when bearScore â‰¥ threshold.
    '''   1. EMA21 > EMA50 crossover â€” 25 pts
    '''   2. Close > EMA21 â€” 20 pts
    '''   3. Close > EMA50 â€” 15 pts
    '''   4. RSI14 trending zone (50â€“70 = +20 pts, else 0 pts) â€” up to 20 pts
    '''   5. EMA21 momentum (rising since prior bar) â€” 10 pts
    '''   6. 2+ of last 3 candles bullish â€” 10 pts
    '''
    ''' Exit rules (EmaRsiWeightedScore):
    '''   TP / SL intrabar fills (price-level triggers via bar.High/Low).
    '''   Neutral confidence exit: score 40â€“60% â†’ close at bar close (mirrors live engine priority).
    '''
    ''' Pure calculation logic lives in <see cref="BacktestMetrics"/> (Friend module)
    ''' so it can be unit-tested independently.
    ''' </summary>
    Public Class BacktestEngine
        Implements IBacktestService

        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _backtestRepository As BacktestRepository
        Private ReadOnly _logger As ILogger(Of BacktestEngine)

        Public Event ProgressUpdated As EventHandler(Of BacktestProgressEventArgs) _
            Implements IBacktestService.ProgressUpdated

        Public Sub New(barRepository As BarRepository,
                       backtestRepository As BacktestRepository,
                       logger As ILogger(Of BacktestEngine))
            _barRepository = barRepository
            _backtestRepository = backtestRepository
            _logger = logger
        End Sub

        Public Async Function RunBacktestAsync(config As BacktestConfiguration,
                                                cancel As CancellationToken) _
            As Task(Of BacktestResult) Implements IBacktestService.RunBacktestAsync

            _logger.LogInformation("Starting backtest '{Name}' from {Start} to {End}",
                                   config.RunName, config.StartDate, config.EndDate)

            Dim from As DateTimeOffset = New DateTimeOffset(DateTime.SpecifyKind(config.StartDate, DateTimeKind.Unspecified), TimeSpan.Zero)
            Dim [to] As DateTimeOffset = New DateTimeOffset(DateTime.SpecifyKind(config.EndDate, DateTimeKind.Unspecified), TimeSpan.Zero).AddDays(1)
            Dim filteredBars = Await _barRepository.GetBarsAsync(
                config.ContractId, CType(config.Timeframe, BarTimeframe), from, [to], cancel)

            If filteredBars.Count < 50 Then
                Throw New InvalidOperationException(
                    $"Insufficient bars for backtest: {filteredBars.Count}. Need at least 50.")
            End If

            ' ── Train/test split (FEAT-13) ──────────────────────────────────────
            If config.TrainTestSplit > 0.0 AndAlso config.TrainTestSplit < 1.0 Then
                Dim splitIdx = CInt(Math.Floor(filteredBars.Count * config.TrainTestSplit))
                If splitIdx >= 50 AndAlso (filteredBars.Count - splitIdx) >= 50 Then
                    _logger.LogInformation(
                        "Train/test split {Split:P0}: {Train} train bars / {Test} test bars",
                        config.TrainTestSplit, splitIdx, filteredBars.Count - splitIdx)
                    Dim trainBars = filteredBars.Take(splitIdx).ToList()
                    Dim testBars  = filteredBars.Skip(splitIdx).ToList()
                    Dim trainResult = RunReplay(config, trainBars, cancel)
                    Dim testResult  = RunReplay(config, testBars,  cancel)
                    trainResult.OutOfSampleResult = testResult
                    Try
                        Await PersistResultAsync(trainResult, splitIdx, config.Timeframe)
                    Catch ex As Exception
                        _logger.LogError(ex, "Failed to persist backtest result")
                    End Try
                    _logger.LogInformation(
                        "Train/test complete: Train PnL={TrainPnL:C}, Test PnL={TestPnL:C}",
                        trainResult.TotalPnL, testResult.TotalPnL)
                    Return trainResult
                End If
            End If

            _logger.LogInformation("Replaying {N} bars", filteredBars.Count)
            Dim result = RunReplay(config, filteredBars, cancel)

            Try
                Await PersistResultAsync(result, filteredBars.Count, config.Timeframe)
            Catch ex As Exception
                _logger.LogError(ex, "Failed to persist backtest result")
            End Try

            _logger.LogInformation(
                "Backtest complete: {Trades} trades, PnL={PnL:C}, WinRate={WR:P1}",
                result.TotalTrades, result.TotalPnL, result.WinRate)

            Return result
        End Function

        ''' <summary>
        ''' Pure replay loop -- pre-loaded bars only, no I/O.
        ''' Exposed as Friend so unit tests can inject synthetic bar lists and verify engine
        ''' behaviour (e.g. DonchianBreakout de-bounce, NaN warm-up guard) without a
        ''' database or bar-repository dependency.
        ''' </summary>
        Friend Function RunReplay(config As BacktestConfiguration,
                                  filteredBars As IReadOnlyList(Of MarketBar),
                                  cancel As CancellationToken) As BacktestResult

            ' -- Pre-calculate all indicator series and warm-up period via helper.
            Dim warmUp As Integer = 0
            Dim indicators = BuildIndicators(config, filteredBars, warmUp)

            ' Validate critical configuration before replay begins.
            If config.PointValue <= 0D Then
                Throw New InvalidOperationException(
                    $"BacktestConfiguration.PointValue must be > 0 for contract '{config.ContractId}'. " &
                    $"Got {config.PointValue}. Set the correct point value (e.g. MES=$5, MGC=$10, MCL=$100).")
            End If

            ' State: open DoubleBubbleButt exit level
            Dim dbbIsLong As Boolean = True          ' DoubleBubbleButt position direction
            Dim dbbInner1SdExit As Decimal = 0D     ' inner 1-SD band level at entry (neutral-zone exit trigger)

            ' State: SuperTrendAdx flip-exit tracking (STRAT-32)
            ' Stores the ST direction (+1 or -1) at the time the position was opened.
            ' Reset to 0 when the position closes.
            Dim stAdxEntryDir As Single = 0.0F

            ' Resolve provider once before the replay loop.
            Dim provider = StrategySignalProviderFactory.Create(config.StrategyCondition)

            Dim trades As New List(Of BacktestTrade)()
            Dim capital = config.InitialCapital
            Dim peakCapital = capital
            Dim maxDrawdown = 0D
            ' openLegs holds all entry/scale-in legs for the currently open position.
            ' All legs share the same PositionGroupId and exit together.
            Dim openLegs As New List(Of BacktestTrade)()
            Dim positionGroupCounter As Integer = 0
            ' Tracks the bar index of the most recent ForceClose exit.
            ' Used to suppress re-entry for 3 bars after a profit-cap close,
            ' preventing artificial churn where a position is closed and immediately re-entered.
            ' Sentinel is -1000 (not Integer.MinValue) to avoid Int32 overflow in the
            ' (i - lastForceCloseBarIndex) subtraction at the cooldown check below.
            Dim lastForceCloseBarIndex As Integer = -1000
            ' Tracks the bar index of the most recent EndOfDay forced-close.
            ' Used to suppress re-entry for 1 bar after a session-end close.
            Dim lastEndOfDayBarIndex As Integer = -1000
            ' MultiConfluence ATR-based SL/TP prices â€” set at entry, cleared on exit.
            ' Used instead of tick-based config values for MultiConfluence positions.
            Dim mcOpenSlPrice As Decimal = 0D
            Dim mcOpenTpPrice As Decimal = 0D
            Dim mcIsLong As Boolean = True

            ' Dynamic exit tracking â€” shared across all strategies when any of the three
            ' dynamic-exit config flags is True.  Reset to 0D on position close.
            '   dynStop      â€” current SL price level (may trail or advance to break-even)
            '   dynTp        â€” current TP price level (may extend on close beyond initial target)
            '   dynStopDelta â€” initial price-unit distance from entry to SL (held constant)
            '   dynTpDelta   â€” initial price-unit distance from entry to TP (held constant)
            ' When all three flags are False these stay at 0D and are not used.
            ' UseAtrMode needs price-level dynStop/dynTp initialised at entry even when
            ' trailing/BE/extend are off, so the price-level CheckExit path is used.
            Dim dynEnabled = config.UseAtrMode OrElse
                             config.TrailingStopEnabled OrElse
                             config.BreakEvenOnHalfTpEnabled OrElse
                             config.ExtendTpEnabled OrElse
                             config.StrategyCondition = StrategyConditionType.SuperTrendPlus
            Dim dynStop As Decimal = 0D
            Dim dynTp As Decimal = 0D
            Dim dynStopDelta As Decimal = 0D
            Dim dynTpDelta As Decimal = 0D

            ' â”€â”€ Next-bar entry state (Items 2+3) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            ' Signal fires at bar[i]; position fills at bar[i+1].Open Â± 1 tick slippage.
            ' Pending state is cleared each bar regardless of whether the fill succeeded.
            ' All pending fields bundled; setting to Nothing clears atomically.
            Dim pending As PendingEntry = Nothing

            ' MinSignalConfidence is stored as 0.0â€“1.0 (e.g. 0.75 = 75%).
            ' The EMA/RSI score produces 0â€“100, so convert once here.
            Dim minPct As Double = config.MinSignalConfidence * 100.0

            Dim progressStep = Math.Max(1, CInt(filteredBars.Count / 20))

            For i = warmUp To filteredBars.Count - 1
                cancel.ThrowIfCancellationRequested()

                ' Progress events every ~5%
                If i Mod progressStep = 0 Then
                    Dim pct = CInt((i / CDbl(filteredBars.Count)) * 100)
                    RaiseEvent ProgressUpdated(Me, New BacktestProgressEventArgs(
                        pct, filteredBars(i).Timestamp.Date, trades.Count))
                End If

                Dim bar = filteredBars(i)

                ' End-of-day forced close
                ' TopStepX force-closes all futures positions at the end of each daily
                ' session. When the date changes and a position is open, close all legs
                ' at the prior bar's Close with SL-equivalent slippage.
                If openLegs.Count > 0 AndAlso i > warmUp Then
                    Dim prevBar = filteredBars(i - 1)
                    If bar.Timestamp.Date <> prevBar.Timestamp.Date Then
                        Dim eodExitPrice = prevBar.Close
                        If config.SlippageTicks > 0 AndAlso config.TickSize > 0D Then
                            Dim slipDelta = config.SlippageTicks * config.TickSize
                            eodExitPrice += If(openLegs(0).Side = "Buy", -slipDelta, slipDelta)
                        End If
                        Dim eodPositionPnL = 0D
                        For Each leg In openLegs
                            leg.ExitTime = prevBar.Timestamp
                            leg.ExitPrice = eodExitPrice
                            leg.ExitReason = "EndOfDay"
                            Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                            leg.PnL = pnl
                            eodPositionPnL += pnl
                            trades.Add(leg)
                        Next
                        capital += eodPositionPnL
                        If capital > peakCapital Then peakCapital = capital
                        Dim eodDd = peakCapital - capital
                        If eodDd > maxDrawdown Then maxDrawdown = eodDd
                        openLegs.Clear()
                        mcOpenSlPrice = 0D : mcOpenTpPrice = 0D
                        dbbInner1SdExit = 0D
                        stAdxEntryDir = 0.0F
                        dynStop = 0D : dynTp = 0D : dynStopDelta = 0D : dynTpDelta = 0D
                        lastEndOfDayBarIndex = i
                        pending = Nothing
                    End If
                End If

                ' Entry fills at bar.Open Â± 1 tick adverse slippage (Items 2+3).
                ' Pending entries generated during a ForceClose cooldown are dropped.
                If pending IsNot Nothing Then
                    If (i - lastForceCloseBarIndex) <= 3 OrElse (i - lastEndOfDayBarIndex) <= 1 Then
                        pending = Nothing   ' cooldown — discard stale signal
                    Else
                        Dim canFillLeg = (openLegs.Count = 0) OrElse
                                         (pending.IsScaleIn AndAlso openLegs.Count > 0 AndAlso
                                          openLegs.Count < config.MaxScaleIns AndAlso
                                          openLegs(0).Side = pending.Side)
                        If canFillLeg Then
                            Dim isBuyFill = (pending.Side = “Buy”)
                            ' Base 1-tick adverse slippage + STRAT-25 spread cost (half-spread penalty at entry).
                            Dim fillSlip = If(config.TickSize > 0D, config.TickSize, 0D)
                            Dim spreadCost = If(config.TickSize > 0D, config.SpreadTicks * config.TickSize, 0D)
                            Dim fillPrice = bar.Open + If(isBuyFill, fillSlip + spreadCost, -(fillSlip + spreadCost))
                            ' STRAT-16: partial-conviction entries use half quantity
                            Dim fillQty = If(pending.IsPartialSignal,
                                            Math.Max(1, config.Quantity \ 2),
                                            config.Quantity)
                            Dim newLeg As New BacktestTrade With {
                                .PositionGroupId = If(pending.IsScaleIn, openLegs(0).PositionGroupId, pending.GroupId),
                                .EntryTime = bar.Timestamp,
                                .EntryPrice = fillPrice,
                                .Side = pending.Side,
                                .Quantity = fillQty,
                                .SignalConfidence = pending.Confidence
                            }
                            openLegs.Add(newLeg)
                            ' Record ST direction at entry for flip-exit tracking (STRAT-32 / FEAT-35)
                            If (config.StrategyCondition = StrategyConditionType.SuperTrendAdx OrElse
                                config.StrategyCondition = StrategyConditionType.SuperTrendPlus) AndAlso
                               Not pending.IsScaleIn AndAlso
                               indicators.StDirectionSeries IsNot Nothing AndAlso i > 0 Then
                                stAdxEntryDir = indicators.StDirectionSeries(i - 1)
                            End If
                            ' Initialise dynamic exit levels for initial entry
                            If dynEnabled AndAlso Not pending.IsScaleIn Then
                                If pending.StopDelta > 0D Then
                                    ' ATR-relative exits: anchor SL/TP to fillPrice
                                    dynStopDelta = pending.StopDelta
                                    dynTpDelta = pending.TpDelta
                                    dynStop = If(isBuyFill, fillPrice - dynStopDelta, fillPrice + dynStopDelta)
                                    dynTp = If(isBuyFill, fillPrice + dynTpDelta, fillPrice - dynTpDelta)
                                    If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                                        mcOpenSlPrice = dynStop : mcOpenTpPrice = dynTp
                                        mcIsLong = isBuyFill
                                    End If
                                ElseIf config.StrategyCondition = StrategyConditionType.SuperTrendPlus AndAlso
                                       pending.AbsoluteSlPrice > 0D Then
                                    ' SuperTrend line as initial SL; TP is initial-risk × TpMultiple
                                    dynStop = pending.AbsoluteSlPrice
                                    dynStopDelta = Math.Abs(fillPrice - dynStop)
                                    dynTpDelta = pending.TpDelta
                                    dynTp = If(isBuyFill, fillPrice + dynTpDelta, fillPrice - dynTpDelta)
                                End If
                            End If
                            ' Indicator-channel exits (set at signal time; independent of fill price)
                            If pending.DbbInner <> 0D Then
                                dbbInner1SdExit = pending.DbbInner : dbbIsLong = pending.DbbIsLong
                            End If
                        End If
                    End If
                    ' Clear all pending state atomically (whether fill succeeded or not)
                    pending = Nothing
                End If

                ' â”€â”€ Check exit for open position â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ' TP/SL levels are anchored to the first leg's entry price; all legs exit together.
                ' MultiConfluence uses ATR-based price-level checks.
                ' All others use tick-based config.
                '
                ' IMPORTANT â€” exit checks intentionally run BEFORE UpdateDynamicExits.
                ' UpdateDynamicExits (trailing stop, break-even, extend TP) runs at the END
                ' of this block only when the trade survives the bar (exitReason Is Nothing).
                ' Running it first would cause extend-TP to raise dynTp on the same bar that
                ' bar.High first reaches the original TP, making CheckExit test against the
                ' extended level and skip the natural exit.  OHLC guarantees bar.Close â‰¥ TP
                ' â†’ bar.High â‰¥ TP, so extend-TP before CheckExit always defeats the TP exit.
                If openLegs.Count > 0 Then
                    Dim exitReason As String = Nothing
                    Dim exitPrice As Decimal = bar.Close

                    ' â”€â”€ Force Close profit cap â€” per-position P&L check â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    ' If the sum of unrealised P&L across all open legs â‰¥ ForceCloseAmount,
                    ' close everything at bar.Close. Only positive P&L triggers â€” losses
                    ' are managed by SL/TP brackets.
                    If config.ForceCloseEnabled AndAlso config.ForceCloseAmount > 0D Then
                        Dim fcPnl As Decimal = 0D
                        For Each leg In openLegs
                            Dim legPnl = BacktestMetrics.CalculatePnL(
                                New BacktestTrade With {
                                    .EntryPrice = leg.EntryPrice,
                                    .ExitPrice = bar.Close,
                                    .Side = leg.Side,
                                    .Quantity = leg.Quantity
                                }, config)
                            fcPnl += legPnl
                        Next
                        If fcPnl >= config.ForceCloseAmount Then
                            Dim fcPositionPnL = 0D
                            For Each leg In openLegs
                                leg.ExitTime = bar.Timestamp
                                leg.ExitPrice = bar.Close
                                leg.ExitReason = "ForceClose"
                                Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                                leg.PnL = pnl
                                fcPositionPnL += pnl
                                trades.Add(leg)
                            Next
                            capital += fcPositionPnL
                            If capital > peakCapital Then peakCapital = capital
                            Dim fcDd = peakCapital - capital
                            If fcDd > maxDrawdown Then maxDrawdown = fcDd
                            openLegs.Clear()
                            mcOpenSlPrice = 0D : mcOpenTpPrice = 0D
                            dbbInner1SdExit = 0D
                            stAdxEntryDir = 0.0F
                            dynStop = 0D : dynTp = 0D : dynStopDelta = 0D : dynTpDelta = 0D
                            lastForceCloseBarIndex = i  ' arm 3-bar re-entry cooldown
                            pending = Nothing            ' discard any pending entry from this bar
                            Continue For  ' ForceClose handled exit â€” skip remaining checks for this bar
                        End If
                    End If
                    ' â”€â”€ DoubleBubbleButt neutral-zone exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    ' Exit Long  when close drops back below the upper 1.0 SD band (neutral zone).
                    ' Exit Short when close rises back above the lower 1.0 SD band (neutral zone).
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.DoubleBubbleButt AndAlso
                       dbbInner1SdExit <> 0D Then
                        If dbbIsLong AndAlso bar.Close < dbbInner1SdExit Then
                            exitReason = "NeutralExit"
                        ElseIf Not dbbIsLong AndAlso bar.Close > dbbInner1SdExit Then
                            exitReason = "NeutralExit"
                        End If
                    End If

                    ' ── SuperTrendAdx / SuperTrendPlus flip exit (STRAT-32 / FEAT-35) ────
                    ' Exit when SuperTrend direction reverses relative to the entry direction.
                    ' Flip exit price = bar.Close of the flip bar (ST direction changes on close).
                    If exitReason Is Nothing AndAlso
                       (config.StrategyCondition = StrategyConditionType.SuperTrendAdx OrElse
                        config.StrategyCondition = StrategyConditionType.SuperTrendPlus) AndAlso
                       stAdxEntryDir <> 0.0F AndAlso
                       indicators.StDirectionSeries IsNot Nothing Then
                        Dim curStDir = indicators.StDirectionSeries(i)
                        If curStDir <> 0.0F AndAlso Not Single.IsNaN(curStDir) AndAlso
                           curStDir <> stAdxEntryDir Then
                            exitReason = "StFlip"
                            exitPrice = bar.Close
                        End If
                    End If

                    If config.StrategyCondition = StrategyConditionType.MultiConfluence AndAlso mcOpenSlPrice <> 0D Then
                        exitReason = BacktestMetrics.CheckFixedExit(openLegs(0).Side, bar, mcOpenSlPrice, mcOpenTpPrice)
                        If exitReason IsNot Nothing Then
                            exitPrice = BacktestMetrics.GetExitPrice(openLegs(0), bar, exitReason, mcOpenSlPrice, mcOpenTpPrice)
                        End If
                    ElseIf exitReason Is Nothing Then
                        ' Use dynamic price levels when any dynamic-exit flag is on;
                        ' fall back to config-derived fixed levels otherwise.
                        If dynEnabled AndAlso dynStop <> 0D Then
                            exitReason = BacktestMetrics.CheckExit(openLegs(0), bar, dynStop, dynTp)
                            If exitReason IsNot Nothing Then
                                exitPrice = BacktestMetrics.GetExitPrice(openLegs(0), bar, exitReason, dynStop, dynTp)
                            End If
                        End If
                    End If
                    If exitReason IsNot Nothing Then
                        ' Apply stop-loss slippage: SL fills degrade by SlippageTicks in the
                        ' adverse direction (long fills lower, short fills higher).
                        ' TP and other exits fill at target â€” no slippage applied.
                        If exitReason = "StopLoss" AndAlso config.SlippageTicks > 0 AndAlso
                           openLegs.Count > 0 AndAlso config.TickSize > 0D Then
                            Dim slipDelta = config.SlippageTicks * config.TickSize
                            exitPrice += If(openLegs(0).Side = "Buy", -slipDelta, slipDelta)
                        End If
                        Dim positionPnL = 0D
                        For Each leg In openLegs
                            leg.ExitTime = bar.Timestamp
                            leg.ExitPrice = exitPrice
                            leg.ExitReason = exitReason
                            Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                            leg.PnL = pnl
                            positionPnL += pnl
                            trades.Add(leg)
                        Next
                        capital += positionPnL
                        If capital > peakCapital Then peakCapital = capital
                        Dim dd = peakCapital - capital
                        If dd > maxDrawdown Then maxDrawdown = dd
                        openLegs.Clear()
                        mcOpenSlPrice = 0D
                        mcOpenTpPrice = 0D
                        dbbInner1SdExit = 0D
                        stAdxEntryDir = 0.0F
                        ' Clear dynamic exit state
                        dynStop = 0D
                        dynTp = 0D
                        dynStopDelta = 0D
                        dynTpDelta = 0D
                    Else
                        ' No exit this bar â€” advance trailing stop / break-even / extend TP
                        ' ready for the NEXT bar's exit check.  Must run AFTER all exit checks
                        ' so it never raises dynTp before CheckExit has had a chance to test it.
                        If dynEnabled AndAlso dynStopDelta > 0D Then
                            If config.StrategyCondition <> StrategyConditionType.SuperTrendPlus Then
                                BacktestMetrics.UpdateDynamicExits(
                                    openLegs(0), bar, config,
                                    dynStopDelta, dynTpDelta,
                                    dynStop, dynTp)
                                ' Keep strategy-specific price-level variables in sync.
                                If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                                    mcOpenSlPrice = dynStop
                                    mcOpenTpPrice = dynTp
                                End If
                            Else
                                ' SuperTrendPlus: advance stop to current SuperTrend line each bar.
                                ' For longs the ST line rises as price advances; take max to ratchet up.
                                ' For shorts the ST line falls; take min to ratchet down.
                                If indicators.StLineSeries IsNot Nothing Then
                                    Dim curStLine = indicators.StLineSeries(i)
                                    If Not Single.IsNaN(curStLine) AndAlso curStLine > 0.0F Then
                                        Dim stLineD = CDec(curStLine)
                                        If openLegs(0).Side = “Buy” Then
                                            If stLineD > dynStop Then dynStop = stLineD
                                        Else
                                            If stLineD < dynStop Then dynStop = stLineD
                                        End If
                                    End If
                                End If
                            End If
                        End If
                    End If
                End If

                ' â”€â”€ Signal evaluation â€” provider-based â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ' ForceClose re-entry cooldown: suppress entry signals for 3 bars after a
                ' profit-cap exit (only when flat; open-position exits are unaffected).
                If openLegs.Count = 0 AndAlso (i - lastForceCloseBarIndex) <= 3 Then Continue For

                ' EndOfDay re-entry cooldown: suppress entry signals for 1 bar after a
                ' session-end forced close.
                If openLegs.Count = 0 AndAlso (i - lastEndOfDayBarIndex) <= 1 Then Continue For

                ' EmaRsi: evaluate even when a position is open (neutral-zone exit fires on
                ' open positions).  All other strategies: evaluate only when flat.
                Dim signal As SignalResult = Nothing
                If config.StrategyCondition = StrategyConditionType.EmaRsiWeightedScore Then
                    signal = provider.Evaluate(bar, indicators, config, i)
                    ' Neutral-zone exit: score 40â€“60 with open legs â†’ close at bar.Close
                    If signal IsNot Nothing AndAlso signal.NeutralExit AndAlso openLegs.Count > 0 Then
                        Dim neutralPositionPnL = 0D
                        For Each leg In openLegs
                            leg.ExitTime = bar.Timestamp
                            leg.ExitPrice = bar.Close
                            leg.ExitReason = "NeutralExit"
                            Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                            leg.PnL = pnl
                            neutralPositionPnL += pnl
                            trades.Add(leg)
                        Next
                        capital += neutralPositionPnL
                        If capital > peakCapital Then peakCapital = capital
                        Dim neutralDd = peakCapital - capital
                        If neutralDd > maxDrawdown Then maxDrawdown = neutralDd
                        openLegs.Clear()
                        dynStop = 0D : dynTp = 0D
                        dynStopDelta = 0D : dynTpDelta = 0D
                    End If
                ElseIf openLegs.Count = 0 Then
                    signal = provider.Evaluate(bar, indicators, config, i)
                End If

                ' Monday morning 1H SuperTrend gate (FEAT-37)
                If signal IsNot Nothing AndAlso signal.Side IsNot Nothing AndAlso
                   indicators.HtfStDirectionSeries IsNot Nothing Then
                    Dim ukTz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")
                    Dim ukBarTime = TimeZoneInfo.ConvertTimeFromUtc(bar.Timestamp.UtcDateTime, ukTz)
                    If ukBarTime.DayOfWeek = DayOfWeek.Monday AndAlso ukBarTime.Hour < 8 Then
                        Dim htfDir = indicators.HtfStDirectionSeries(i)
                        If Not Single.IsNaN(htfDir) AndAlso htfDir <> 0.0F Then
                            Dim signalIsLong = signal.Side = "Buy"
                            If (signalIsLong AndAlso htfDir < 0) OrElse (Not signalIsLong AndAlso htfDir > 0) Then
                                signal = Nothing
                            End If
                        End If
                    End If
                End If

                ' â”€â”€ Map SignalResult to pending entry state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                If signal IsNot Nothing AndAlso signal.Side IsNot Nothing Then
                    If openLegs.Count = 0 Then
                        positionGroupCounter += 1
                        pending = New PendingEntry With {
                            .GroupId = positionGroupCounter,
                            .Side = signal.Side,
                            .Confidence = signal.Confidence,
                            .StopDelta = signal.StopDelta,
                            .TpDelta = signal.TpDelta,
                            .AbsoluteSlPrice = signal.AbsoluteSlPrice,
                            .IsPartialSignal = signal.IsPartialSignal
                        }
                        ' Indicator-channel exit level — strategy-specific pending field
                        If signal.IndicatorExitLevel <> 0D Then
                            Select Case config.StrategyCondition
                                Case StrategyConditionType.DoubleBubbleButt
                                    pending.DbbInner = signal.IndicatorExitLevel
                                    pending.DbbIsLong = signal.IsLong
                            End Select
                        End If
                    ElseIf config.StrategyCondition = StrategyConditionType.EmaRsiWeightedScore AndAlso
                           openLegs.Count < config.MaxScaleIns AndAlso
                           openLegs(0).Side = signal.Side Then
                        ' Scale-in — same direction, up to MaxScaleIns additional legs
                        pending = New PendingEntry With {
                            .Side = signal.Side,
                            .Confidence = signal.Confidence,
                            .IsScaleIn = True
                        }
                    End If
                End If
                ' â”€â”€ End of signal evaluation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            Next

            ' Close any open position at end of data
            If openLegs.Count > 0 Then
                Dim lastBar = filteredBars.Last()
                For Each leg In openLegs
                    leg.ExitTime = lastBar.Timestamp
                    leg.ExitPrice = lastBar.Close
                    leg.ExitReason = "EndOfData"
                    leg.PnL = BacktestMetrics.CalculatePnL(leg, config)
                    capital += leg.PnL.GetValueOrDefault()
                    trades.Add(leg)
                Next
            End If


            ' Calculate metrics
            Dim result = BacktestMetrics.BuildResult(config, trades, capital, maxDrawdown)

            Return result
        End Function

        Public Async Function GetBacktestRunsAsync() As Task(Of IEnumerable(Of BacktestResult)) _
            Implements IBacktestService.GetBacktestRunsAsync
            Dim entities = Await _backtestRepository.GetRecentRunsAsync()
            Return entities.Select(Function(e) MapRunToResult(e))
        End Function

        ' â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        Private Async Function PersistResultAsync(result As BacktestResult, barCount As Integer, timeframe As Integer) As Task
            Dim entity = New BacktestRunEntity With {
                .RunName = result.RunName,
                .ContractId = result.ContractId,
                .Timeframe = timeframe,
                .StartDate = result.StartDate,
                .EndDate = result.EndDate,
                .InitialCapital = result.InitialCapital,
                .FinalCapital = result.FinalCapital,
                .TotalTrades = result.TotalTrades,
                .WinningTrades = result.WinningTrades,
                .LosingTrades = result.LosingTrades,
                .TotalPnL = result.TotalPnL,
                .MaxDrawdown = result.MaxDrawdown,
                .WinRate = result.WinRate,
                .SharpeRatio = result.SharpeRatio,
                .AveragePnLPerTrade = result.AveragePnLPerTrade,
                .ParametersJson = $"{{""roundTripFeeUsd"":{result.RoundTripFeeUsd:F2}}}",
                .ModelVersion = "EMA/RSI-Rule-Based",
                .CompletedAt = DateTimeOffset.UtcNow,
                .Trades = result.Trades.Select(Function(t) New BacktestTradeEntity With {
                    .EntryTime = t.EntryTime,
                    .ExitTime = t.ExitTime,
                    .Side = t.Side,
                    .EntryPrice = t.EntryPrice,
                    .ExitPrice = t.ExitPrice,
                    .Quantity = t.Quantity,
                    .PnL = t.PnL,
                    .ExitReason = t.ExitReason,
                    .SignalConfidence = t.SignalConfidence,
                    .PositionGroupId = t.PositionGroupId
                }).ToList()
            }
            result.Id = Await _backtestRepository.SaveRunAsync(entity)
        End Function

        Private Shared Function MapRunToResult(e As BacktestRunEntity) As BacktestResult
            Return New BacktestResult With {
                .Id = e.Id,
                .RunName = e.RunName,
                .ContractId = e.ContractId,
                .StartDate = e.StartDate,
                .EndDate = e.EndDate,
                .InitialCapital = e.InitialCapital,
                .FinalCapital = e.FinalCapital,
                .TotalTrades = e.TotalTrades,
                .WinningTrades = e.WinningTrades,
                .LosingTrades = e.LosingTrades,
                .TotalPnL = e.TotalPnL,
                .MaxDrawdown = e.MaxDrawdown,
                .WinRate = e.WinRate.GetValueOrDefault(),
                .SharpeRatio = e.SharpeRatio,
                .AveragePnLPerTrade = e.AveragePnLPerTrade
            }
        End Function

        ''' <summary>
        ''' Safe CDec for Single values: returns 0D when the value is NaN or Infinity,
        ''' preventing OverflowException from CDec(Single.PositiveInfinity).
        ''' </summary>
        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function



        ''' <summary>Pre-calculates all indicator series for a backtest run.</summary>
        ''' <remarks>Called once before the replay loop; moves indicator math out of RunBacktestAsync.</remarks>
        Private Shared Function BuildIndicators(
                config As BacktestConfiguration,
                filteredBars As IReadOnlyList(Of MarketBar),
                ByRef warmUp As Integer) As StrategyIndicators

            Dim allCloses = filteredBars.Select(Function(b) b.Close).ToList()
            Dim allHighs = filteredBars.Select(Function(b) b.High).ToList()
            Dim allLows = filteredBars.Select(Function(b) b.Low).ToList()

            ' -- Shared series (EMA21/50, RSI14, ADX14 used by most strategies)
            Dim ema21Series = TechnicalIndicators.EMA(allCloses, 21)
            Dim ema50Series = TechnicalIndicators.EMA(allCloses, 50)
            Dim rsi14Series = TechnicalIndicators.RSI(allCloses, 14)
            Dim adx14Series = TechnicalIndicators.DMI(allHighs, allLows, allCloses).ADX

            Dim universalAtr14 As Single() = Nothing
            If config.UseAtrMode AndAlso
               config.StrategyCondition <> StrategyConditionType.MultiConfluence Then
                universalAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' -- DoubleBubbleButt: BB(20,1.0) inner + BB(20,2.0) outer + ATR(20)
            Dim dbbInnerUpper As Single() = Nothing
            Dim dbbInnerLower As Single() = Nothing
            Dim dbbOuterUpper As Single() = Nothing
            Dim dbbOuterLower As Single() = Nothing
            Dim dbbAtr20 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.DoubleBubbleButt Then
                Dim dbbInner = TechnicalIndicators.BollingerBands(allCloses, 20, 1.0)
                dbbInnerUpper = dbbInner.Upper
                dbbInnerLower = dbbInner.Lower
                Dim dbbOuter = TechnicalIndicators.BollingerBands(allCloses, 20, 2.0)
                dbbOuterUpper = dbbOuter.Upper
                dbbOuterLower = dbbOuter.Lower
                dbbAtr20 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 20)
            End If

            ' -- MultiConfluence: Ichimoku + DMI + MACD + StochRSI + ATR(14)
            Dim mcIchiTenkan As Single() = Nothing
            Dim mcIchiKijun As Single() = Nothing
            Dim mcIchiSpanA As Single() = Nothing
            Dim mcIchiSpanB As Single() = Nothing
            Dim mcPlusDI As Single() = Nothing
            Dim mcMinusDI As Single() = Nothing
            Dim mcAdxSeries As Single() = Nothing
            Dim mcMacdHist As Single() = Nothing
            Dim mcStochRsiK As Single() = Nothing
            Dim mcAtr14 As Single() = Nothing
            Dim mcVolMa20 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                Dim ichi = TechnicalIndicators.IchimokuCloud(allHighs, allLows, allCloses, 9, 26, 52, 26)
                mcIchiTenkan = ichi.Tenkan
                mcIchiKijun = ichi.Kijun
                mcIchiSpanA = ichi.SpanA
                mcIchiSpanB = ichi.SpanB
                Dim dmiMc = TechnicalIndicators.DMI(allHighs, allLows, allCloses, 14)
                mcPlusDI = dmiMc.PlusDI
                mcMinusDI = dmiMc.MinusDI
                mcAdxSeries = dmiMc.ADX
                mcMacdHist = TechnicalIndicators.MACD(allCloses, fastPeriod:=8, slowPeriod:=17, signalPeriod:=9).Histogram
                mcStochRsiK = TechnicalIndicators.StochasticRSI(allCloses).K
                mcAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
                Dim mcAllVols = filteredBars.Select(Function(b) CDec(b.Volume)).ToList()
                mcVolMa20 = TechnicalIndicators.SMA(mcAllVols, 20)
            End If

            ' -- VIDYA Cross: VIDYA(14,9), CMO(9), DeltaVolume(6), ATR(14)
            Dim vcVidyaSeries As Single() = Nothing
            Dim vcCmoSeries As Single() = Nothing
            Dim vcDeltaVolSeries As Single() = Nothing
            Dim vcAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.VidyaCross Then
                vcVidyaSeries = TechnicalIndicators.VIDYA(allCloses, 14, 9)
                vcCmoSeries = TechnicalIndicators.CMO(allCloses, 9)
                Dim allOpens = filteredBars.Select(Function(b) b.Open).ToList()
                Dim allVols = filteredBars.Select(Function(b) b.Volume).ToList()
                vcDeltaVolSeries = TechnicalIndicators.DeltaVolume(allCloses, allOpens, allVols, 6)
                vcAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' -- Naked Trader: EMA(9/21), MACD(8,17,9), DMI(14), VWAP, VolMA(20), ATR(14)
            Dim ntEma9 As Single() = Nothing
            Dim ntEma21 As Single() = Nothing
            Dim ntMacdHist As Single() = Nothing
            Dim ntMacdLine As Single() = Nothing
            Dim ntPlusDI As Single() = Nothing
            Dim ntMinusDI As Single() = Nothing
            Dim ntAdxSeries As Single() = Nothing
            Dim ntVwapSeries As Single() = Nothing
            Dim ntVolMa20 As Single() = Nothing
            Dim ntAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.NakedTrader Then
                ntEma9 = TechnicalIndicators.EMA(allCloses, 9)
                ntEma21 = TechnicalIndicators.EMA(allCloses, 21)
                Dim macdNt = TechnicalIndicators.MACD(allCloses, 8, 17, 9)
                ntMacdHist = macdNt.Histogram
                ntMacdLine = macdNt.Line
                Dim dmiNt = TechnicalIndicators.DMI(allHighs, allLows, allCloses, 14)
                ntPlusDI = dmiNt.PlusDI
                ntMinusDI = dmiNt.MinusDI
                ntAdxSeries = dmiNt.ADX
                Dim allVols = filteredBars.Select(Function(b) b.Volume).ToList()
                ntVwapSeries = TechnicalIndicators.VWAP(allHighs, allLows, allCloses, allVols)
                Dim volDecimals = allVols.Select(Function(v) CDec(v)).ToList()
                ntVolMa20 = TechnicalIndicators.SMA(volDecimals, 20)
                ntAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' -- LULT Divergence: WaveTrend(10,21,4) + ATR(14)
            Dim lultWt1 As Single() = Nothing
            Dim lultWt2 As Single() = Nothing
            Dim lultAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.LultDivergence Then
                Dim wt = TechnicalIndicators.WaveTrend(allHighs, allLows, allCloses, 10, 21, 4)
                lultWt1 = wt.Wt1
                lultWt2 = wt.Wt2
                lultAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' -- BB Squeeze Scalper: BB(12,2), BBW, %B, RSI(7), EMA(5), BBW SMA(20), ATR(10)
            Dim bbsBands As (Upper As Single(), Middle As Single(), Lower As Single()) = (Nothing, Nothing, Nothing)
            Dim bbsBbwArr As Single() = Nothing
            Dim bbsPctBArr As Single() = Nothing
            Dim bbsRsi7 As Single() = Nothing
            Dim bbsEma5 As Single() = Nothing
            Dim bbsBbwSma As Single() = Nothing
            Dim bbsAtr10 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.BbSqueezeScalper Then
                bbsBands = TechnicalIndicators.BollingerBands(allCloses, 12, 2.0)
                bbsBbwArr = TechnicalIndicators.BollingerBandWidth(allCloses, 12, 2.0)
                bbsPctBArr = TechnicalIndicators.BollingerPercentB(allCloses, 12, 2.0)
                bbsRsi7 = TechnicalIndicators.RSI(allCloses, 7)
                bbsEma5 = TechnicalIndicators.EMA(allCloses, 5)
                bbsBbwSma = TechnicalIndicators.SMA(bbsBbwArr.Select(Function(v) SafeD(v)).ToList(), 20)
                bbsAtr10 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 10)
            End If

            ' -- SuperTrendAdx: SuperTrend(10,3.0) Direction + DMI/ADX(14)
            Dim stAdxDirectionSeries As Single() = Nothing
            Dim stAdxPlusDI As Single() = Nothing
            Dim stAdxMinusDI As Single() = Nothing
            Dim stAdxAdxSeries As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.SuperTrendAdx Then
                Dim stResult = TechnicalIndicators.SuperTrend(allHighs, allLows, allCloses, period:=10, multiplier:=3.0)
                stAdxDirectionSeries = stResult.Direction
                Dim dmiSt = TechnicalIndicators.DMI(allHighs, allLows, allCloses)
                stAdxPlusDI = dmiSt.PlusDI
                stAdxMinusDI = dmiSt.MinusDI
                stAdxAdxSeries = dmiSt.ADX
            End If

            ' -- SuperTrendPlus (FEAT-35): SuperTrend(10,3.0) Line + Direction + DMI/ADX(14)
            Dim stPlusLineSeries As Single() = Nothing
            Dim stPlusDirectionSeries As Single() = Nothing
            Dim stPlusPlusDI As Single() = Nothing
            Dim stPlusMinusDI As Single() = Nothing
            Dim stPlusAdxSeries As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.SuperTrendPlus Then
                Dim stPlusResult = TechnicalIndicators.SuperTrend(allHighs, allLows, allCloses, period:=10, multiplier:=3.0)
                stPlusLineSeries = stPlusResult.Line
                stPlusDirectionSeries = stPlusResult.Direction
                Dim dmiStPlus = TechnicalIndicators.DMI(allHighs, allLows, allCloses)
                stPlusPlusDI = dmiStPlus.PlusDI
                stPlusMinusDI = dmiStPlus.MinusDI
                stPlusAdxSeries = dmiStPlus.ADX
            End If

            ' -- Opening Range Breakout: ATR(14), VolMa20
            Dim orbVolMa20 As Single() = Nothing
            Dim orbAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.OpeningRangeBreakout Then
                Dim allVols = filteredBars.Select(Function(b) CDec(b.Volume)).ToList()
                orbVolMa20 = TechnicalIndicators.SMA(allVols, 20)
                orbAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' -- VWAP Mean Reversion: session-anchored VWAP, rolling stddev bands (1.5 SD and 2.0 SD), ATR(14)
            Dim vmrVwap As Single() = Nothing
            Dim vmrUpper2Sd As Single() = Nothing
            Dim vmrLower2Sd As Single() = Nothing
            Dim vmrUpper15Sd As Single() = Nothing
            Dim vmrLower15Sd As Single() = Nothing
            Dim vmrAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.VwapMeanReversion Then
                Dim allVols = filteredBars.Select(Function(b) b.Volume).ToList()
                vmrVwap = TechnicalIndicators.VWAP(allHighs, allLows, allCloses, allVols)
                vmrAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
                ' Rolling 20-bar standard deviation of (close − VWAP) to set band widths
                Const VmrPeriod As Integer = 20
                Dim n = allCloses.Count
                vmrUpper2Sd = New Single(n - 1) {}
                vmrLower2Sd = New Single(n - 1) {}
                vmrUpper15Sd = New Single(n - 1) {}
                vmrLower15Sd = New Single(n - 1) {}
                For i = 0 To n - 1
                    vmrUpper2Sd(i) = Single.NaN
                    vmrLower2Sd(i) = Single.NaN
                    vmrUpper15Sd(i) = Single.NaN
                    vmrLower15Sd(i) = Single.NaN
                Next
                For i = VmrPeriod - 1 To n - 1
                    If Single.IsNaN(vmrVwap(i)) Then Continue For
                    Dim vwapD = CDbl(vmrVwap(i))
                    Dim variance As Double = 0
                    For j = i - VmrPeriod + 1 To i
                        Dim diff = CDbl(allCloses(j)) - vwapD
                        variance += diff * diff
                    Next
                    Dim sd = Math.Sqrt(variance / VmrPeriod)
                    vmrUpper2Sd(i) = CSng(vwapD + 2.0 * sd)
                    vmrLower2Sd(i) = CSng(vwapD - 2.0 * sd)
                    vmrUpper15Sd(i) = CSng(vwapD + 1.5 * sd)
                    vmrLower15Sd(i) = CSng(vwapD - 1.5 * sd)
                Next
            End If
            Select Case config.StrategyCondition
                Case StrategyConditionType.MultiConfluence : warmUp = 80
                Case StrategyConditionType.DoubleBubbleButt : warmUp = 25
                Case StrategyConditionType.LultDivergence : warmUp = 100
                Case StrategyConditionType.VwapMeanReversion : warmUp = 20
                Case Else : warmUp = 55
            End Select

            ' -- Populate StrategyIndicators from computed series
            Dim indicators As New StrategyIndicators With {
                .AllBars = filteredBars,
                .Ema21 = ema21Series,
                .Ema50 = ema50Series
            }
            Select Case config.StrategyCondition
                Case StrategyConditionType.MultiConfluence
                    indicators.IchiTenkan = mcIchiTenkan
                    indicators.IchiKijun = mcIchiKijun
                    indicators.IchiSpanA = mcIchiSpanA
                    indicators.IchiSpanB = mcIchiSpanB
                    indicators.PlusDi = mcPlusDI
                    indicators.MinusDi = mcMinusDI
                    indicators.Adx = mcAdxSeries
                    indicators.MacdHistogram = mcMacdHist
                    indicators.StochRsiK = mcStochRsiK
                    indicators.Atr = mcAtr14
                    indicators.VolMa20 = mcVolMa20
                Case StrategyConditionType.DoubleBubbleButt
                    indicators.DbbInnerUpper = dbbInnerUpper
                    indicators.DbbInnerLower = dbbInnerLower
                    indicators.BbUpper = dbbOuterUpper
                    indicators.BbLower = dbbOuterLower
                    indicators.Atr = dbbAtr20
                Case StrategyConditionType.VidyaCross
                    indicators.Vidya = vcVidyaSeries
                    indicators.DeltaVolume = vcDeltaVolSeries
                    indicators.Atr = vcAtr14
                Case StrategyConditionType.NakedTrader
                    indicators.Ema9 = ntEma9
                    indicators.MacdHistogram = ntMacdHist
                    indicators.MacdLine = ntMacdLine
                    indicators.PlusDi = ntPlusDI
                    indicators.MinusDi = ntMinusDI
                    indicators.Adx = ntAdxSeries
                    indicators.Vwap = ntVwapSeries
                    indicators.VolMa20 = ntVolMa20
                    indicators.Atr = ntAtr14
                Case StrategyConditionType.LultDivergence
                    indicators.WaveTrend1 = lultWt1
                    indicators.WaveTrend2 = lultWt2
                    indicators.Atr = lultAtr14
                Case StrategyConditionType.BbSqueezeScalper
                    indicators.BbUpper = bbsBands.Upper
                    indicators.BbMiddle = bbsBands.Middle
                    indicators.BbLower = bbsBands.Lower
                    indicators.BbWidth = bbsBbwArr
                    indicators.BbPctB = bbsPctBArr
                    indicators.Rsi = bbsRsi7
                    indicators.Ema5 = bbsEma5
                    indicators.BbwSma = bbsBbwSma
                    indicators.Atr = bbsAtr10
                Case StrategyConditionType.SuperTrendAdx
                    indicators.StDirectionSeries = stAdxDirectionSeries
                    indicators.PlusDi = stAdxPlusDI
                    indicators.MinusDi = stAdxMinusDI
                    indicators.Adx = stAdxAdxSeries
                Case StrategyConditionType.SuperTrendPlus
                    indicators.StLineSeries = stPlusLineSeries
                    indicators.StDirectionSeries = stPlusDirectionSeries
                    indicators.PlusDi = stPlusPlusDI
                    indicators.MinusDi = stPlusMinusDI
                    indicators.Adx = stPlusAdxSeries
                Case StrategyConditionType.EmaRsiWeightedScore
                    Dim emaRsiPeriod = If(config.IndicatorPeriod > 0, config.IndicatorPeriod, 14)
                    indicators.Rsi = If(emaRsiPeriod = 14, rsi14Series, TechnicalIndicators.RSI(allCloses, emaRsiPeriod))
                    indicators.Adx = adx14Series
                    indicators.Atr = universalAtr14
                Case StrategyConditionType.OpeningRangeBreakout
                    indicators.Atr    = orbAtr14
                    indicators.VolMa20 = orbVolMa20
                Case StrategyConditionType.VwapMeanReversion
                    indicators.Vwap    = vmrVwap
                    indicators.BbUpper  = vmrUpper2Sd
                    indicators.BbLower  = vmrLower2Sd
                    indicators.BbMiddle = vmrUpper15Sd
                    indicators.BbWidth  = vmrLower15Sd
                    indicators.Atr     = vmrAtr14
                Case Else
                    indicators.Rsi = rsi14Series
                    indicators.Adx = adx14Series
                    indicators.Atr = universalAtr14
            End Select

            ' -- Universal: 1H HTF SuperTrend direction for Monday morning gate (FEAT-37)
            indicators.HtfStDirectionSeries = ComputeHtfStDirectionSeries(filteredBars)

            Return indicators
        End Function

        ''' <summary>
        ''' Aggregates chart-timeframe bars into 1-hour OHLC buckets, computes SuperTrend(10, 3.0),
        ''' and returns an array (same length as <paramref name="bars"/>) where each element is the
        ''' direction of the last *completed* 1H bar before the corresponding chart bar.
        ''' NaN when insufficient 1H history exists for the warm-up period.
        ''' Used by the Monday morning HTF gate (FEAT-37).
        ''' </summary>
        Private Shared Function ComputeHtfStDirectionSeries(bars As IReadOnlyList(Of MarketBar)) As Single()
            Dim result(bars.Count - 1) As Single
            For i = 0 To result.Length - 1
                result(i) = Single.NaN
            Next

            If bars.Count < 12 Then Return result

            ' Group into 1H UTC buckets (floor each bar timestamp to the hour)
            Dim buckets = bars.GroupBy(
                Function(b) New DateTime(b.Timestamp.UtcDateTime.Year,
                                        b.Timestamp.UtcDateTime.Month,
                                        b.Timestamp.UtcDateTime.Day,
                                        b.Timestamp.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc)) _
                          .OrderBy(Function(g) g.Key) _
                          .ToList()

            If buckets.Count < 12 Then Return result

            Dim h1Times As New List(Of DateTime)(buckets.Count)
            Dim h1Highs As New List(Of Decimal)(buckets.Count)
            Dim h1Lows As New List(Of Decimal)(buckets.Count)
            Dim h1Closes As New List(Of Decimal)(buckets.Count)
            For Each bucket In buckets
                h1Times.Add(bucket.Key)
                h1Highs.Add(bucket.Max(Function(b) b.High))
                h1Lows.Add(bucket.Min(Function(b) b.Low))
                h1Closes.Add(bucket.OrderBy(Function(b) b.Timestamp).Last().Close)
            Next

            Dim st1H = TechnicalIndicators.SuperTrend(h1Highs, h1Lows, h1Closes, period:=10, multiplier:=3.0)

            For i = 0 To bars.Count - 1
                ' Current UTC hour start for this bar
                Dim barHourStart = New DateTime(bars(i).Timestamp.UtcDateTime.Year,
                                               bars(i).Timestamp.UtcDateTime.Month,
                                               bars(i).Timestamp.UtcDateTime.Day,
                                               bars(i).Timestamp.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc)
                ' Last completed 1H bucket is the one whose key < barHourStart
                Dim htfIdx = h1Times.FindLastIndex(Function(t) t < barHourStart)
                If htfIdx >= 10 Then
                    result(i) = st1H.Direction(htfIdx)
                End If
            Next

            Return result
        End Function

        End Class
    End Namespace
