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

            ' State: open SuperTrend SL/TP prices (price-level exit, like MultiConfluence)
            Dim qlStOpenSlPrice As Decimal = 0D
            Dim qlStOpenTpPrice As Decimal = 0D
            Dim qlStIsLong As Boolean = True

            ' State: open Donchian / BbRsi / DoubleBubbleButt exit levels
            Dim qlDonOpenMidExit As Decimal = 0D    ' adverse mid-channel level at entry
            Dim qlDonIsLong As Boolean = True
            Dim qlBbOpenMidExit As Decimal = 0D     ' BB middle band (SMA20) at entry
            Dim qlBbIsLong As Boolean = True
            Dim dbbIsLong As Boolean = True          ' DoubleBubbleButt position direction
            Dim dbbInner1SdExit As Decimal = 0D     ' inner 1-SD band level at entry (neutral-zone exit trigger)

            ' Resolve provider once before the replay loop.
            Dim provider = StrategySignalProviderFactory.Create(config.StrategyCondition)

            ' Validate critical configuration before replay begins.
            If config.PointValue <= 0D Then
                Throw New InvalidOperationException(
                    $"BacktestConfiguration.PointValue must be > 0 for contract '{config.ContractId}'. " &
                    $"Got {config.PointValue}. Set the correct point value (e.g. MES=$5, MGC=$10, MCL=$100).")
            End If

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
            ' Tracks the bar index of the most recent DonchianBreakout mid-cross (NeutralExit).
            ' Suppresses re-entry for 3 bars after a mid-cross exit to prevent oscillation churn.
            Dim lastDonchianExitBarIndex As Integer = -1000

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
                             config.ExtendTpEnabled
            Dim dynStop      As Decimal = 0D
            Dim dynTp        As Decimal = 0D
            Dim dynStopDelta As Decimal = 0D
            Dim dynTpDelta   As Decimal = 0D

            ' â”€â”€ Next-bar entry state (Items 2+3) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            ' Signal fires at bar[i]; position fills at bar[i+1].Open Â± 1 tick slippage.
            ' Pending state is cleared each bar regardless of whether the fill succeeded.
            ‘ All pending fields bundled; setting to Nothing clears atomically.
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

                ' â”€â”€ Fill pending entry from previous bar's signal â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ' Entry fills at bar.Open Â± 1 tick adverse slippage (Items 2+3).
                ' Pending entries generated during a ForceClose cooldown are dropped.
                If pending IsNot Nothing Then
                    If (i - lastForceCloseBarIndex) <= 3 Then
                        pending = Nothing   ' cooldown — discard stale signal
                    Else
                        Dim canFillLeg = (openLegs.Count = 0) OrElse
                                         (pending.IsScaleIn AndAlso openLegs.Count > 0 AndAlso
                                          openLegs.Count < config.MaxScaleIns AndAlso
                                          openLegs(0).Side = pending.Side)
                        If canFillLeg Then
                            Dim isBuyFill  = (pending.Side = “Buy”)
                            Dim fillSlip   = If(config.TickSize > 0D, config.TickSize, 0D)
                            Dim fillPrice  = bar.Open + If(isBuyFill, fillSlip, -fillSlip)
                            ' STRAT-16: partial-conviction entries use half quantity
                            Dim fillQty = If(pending.IsPartialSignal,
                                            Math.Max(1, config.Quantity \ 2),
                                            config.Quantity)
                            Dim newLeg As New BacktestTrade With {
                                .PositionGroupId = If(pending.IsScaleIn, openLegs(0).PositionGroupId, pending.GroupId),
                                .EntryTime       = bar.Timestamp,
                                .EntryPrice      = fillPrice,
                                .Side            = pending.Side,
                                .Quantity        = fillQty,
                                .SignalConfidence = pending.Confidence
                            }
                            openLegs.Add(newLeg)
                            ' Initialise dynamic exit levels for initial entry
                            If dynEnabled AndAlso Not pending.IsScaleIn Then
                                If pending.AbsStSl <> 0D Then
                                    ' SuperTrend: SL = indicator line (absolute); TP = stored absolute
                                    qlStOpenSlPrice = pending.AbsStSl
                                    qlStOpenTpPrice = pending.AbsStTp
                                    qlStIsLong      = pending.StIsLong
                                    dynStopDelta = Math.Abs(fillPrice - qlStOpenSlPrice)
                                    dynTpDelta   = Math.Abs(qlStOpenTpPrice - fillPrice)
                                    dynStop = qlStOpenSlPrice
                                    dynTp   = qlStOpenTpPrice
                                ElseIf pending.StopDelta > 0D Then
                                    ' ATR-relative exits: anchor SL/TP to fillPrice
                                    dynStopDelta = pending.StopDelta
                                    dynTpDelta   = pending.TpDelta
                                    dynStop = If(isBuyFill, fillPrice - dynStopDelta, fillPrice + dynStopDelta)
                                    dynTp   = If(isBuyFill, fillPrice + dynTpDelta,  fillPrice - dynTpDelta)
                                    If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                                        mcOpenSlPrice = dynStop : mcOpenTpPrice = dynTp
                                        mcIsLong      = isBuyFill
                                    End If
                                End If
                            End If
                            ' Indicator-channel exits (set at signal time; independent of fill price)
                            If pending.DonMid <> 0D Then
                                qlDonOpenMidExit = pending.DonMid : qlDonIsLong = pending.DonIsLong
                            End If
                            If pending.BbMid <> 0D Then
                                qlBbOpenMidExit = pending.BbMid : qlBbIsLong = pending.BbIsLong
                            End If
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
                ' MultiConfluence / SuperTrend use ATR-based price-level checks.
                ' DonchianBreakout / BbRsiMeanReversion use indicator-level exits.
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
                            qlStOpenSlPrice = 0D : qlStOpenTpPrice = 0D
                            qlDonOpenMidExit = 0D : qlBbOpenMidExit = 0D
                            dbbInner1SdExit = 0D
                            dynStop = 0D : dynTp = 0D : dynStopDelta = 0D : dynTpDelta = 0D
                            lastForceCloseBarIndex = i  ' arm 3-bar re-entry cooldown
                            pending = Nothing            ' discard any pending entry from this bar
                            Continue For  ' ForceClose handled exit â€” skip remaining checks for this bar
                        End If
                    End If

                    ' â”€â”€ SuperTrend price-level exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    If config.StrategyCondition = StrategyConditionType.SuperTrend AndAlso qlStOpenSlPrice <> 0D Then
                        exitReason = BacktestMetrics.CheckFixedExit(openLegs(0).Side, bar, qlStOpenSlPrice, qlStOpenTpPrice)
                        If exitReason IsNot Nothing Then
                            exitPrice = BacktestMetrics.GetExitPrice(openLegs(0), bar, exitReason, qlStOpenSlPrice, qlStOpenTpPrice)
                        End If
                    End If

                    ' â”€â”€ DonchianBreakout indicator-level exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.DonchianBreakout AndAlso
                       qlDonOpenMidExit <> 0D Then
                        If qlDonIsLong AndAlso bar.Close < qlDonOpenMidExit Then
                            exitReason = "NeutralExit"
                        ElseIf Not qlDonIsLong AndAlso bar.Close > qlDonOpenMidExit Then
                            exitReason = "NeutralExit"
                        End If
                    End If

                    ' â”€â”€ BbRsiMeanReversion indicator-level exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    ' Exit Long when close >= BB middle (SMA20) or RSI crosses back above 50.
                    ' Exit Short when close <= BB middle (SMA20) or RSI crosses back below 50.
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.BbRsiMeanReversion AndAlso
                       qlBbOpenMidExit <> 0D Then
                        Dim rsiExitVal = If(qlBbIsLong,
                                            indicators.Rsi(i) <= 50.0F,   ' long: exit when RSI reverses â‰¤ 50
                                            indicators.Rsi(i) >= 50.0F)   ' short: exit when RSI reverses â‰¥ 50
                        If qlBbIsLong AndAlso
                           (bar.Close >= qlBbOpenMidExit OrElse
                            (Not Single.IsNaN(indicators.Rsi(i)) AndAlso rsiExitVal)) Then
                            exitReason = "TakeProfit"
                        ElseIf Not qlBbIsLong AndAlso
                               (bar.Close <= qlBbOpenMidExit OrElse
                                (Not Single.IsNaN(indicators.Rsi(i)) AndAlso rsiExitVal)) Then
                            exitReason = "TakeProfit"
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

                    ' â”€â”€ ConnorsRsi2 RSI-based exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    ' Exit Long when close > SMA(5) or RSI(2) > 65.
                    ' Exit Short when close < SMA(5) or RSI(2) < 35.
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.ConnorsRsi2 AndAlso
                       indicators.Rsi2 IsNot Nothing AndAlso indicators.Sma5 IsNot Nothing Then
                        If Not (Single.IsNaN(indicators.Rsi2(i)) OrElse Single.IsNaN(indicators.Sma5(i))) Then
                            If openLegs(0).Side = "Buy" Then
                                If bar.Close > SafeD(indicators.Sma5(i)) OrElse indicators.Rsi2(i) > 65.0F Then
                                    exitReason = "TakeProfit"
                                End If
                            Else
                                If bar.Close < SafeD(indicators.Sma5(i)) OrElse indicators.Rsi2(i) < 35.0F Then
                                    exitReason = "TakeProfit"
                                End If
                            End If
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
                        ' Clear QuantLab state
                        qlStOpenSlPrice = 0D
                        qlStOpenTpPrice = 0D
                        qlDonOpenMidExit = 0D
                        qlBbOpenMidExit = 0D
                        dbbInner1SdExit = 0D
                        ' Clear dynamic exit state
                        dynStop      = 0D
                        dynTp        = 0D
                        dynStopDelta = 0D
                        dynTpDelta   = 0D
                        ' Arm Donchian de-bounce: suppress re-entry for 3 bars after a mid-cross exit
                        If exitReason = "NeutralExit" AndAlso
                           config.StrategyCondition = StrategyConditionType.DonchianBreakout Then
                            lastDonchianExitBarIndex = i
                        End If
                    Else
                        ' No exit this bar â€” advance trailing stop / break-even / extend TP
                        ' ready for the NEXT bar's exit check.  Must run AFTER all exit checks
                        ' so it never raises dynTp before CheckExit has had a chance to test it.
                        If dynEnabled AndAlso dynStopDelta > 0D Then
                            BacktestMetrics.UpdateDynamicExits(
                                openLegs(0), bar, config,
                                dynStopDelta, dynTpDelta,
                                dynStop, dynTp)
                            ' Keep strategy-specific price-level variables in sync.
                            If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                                mcOpenSlPrice = dynStop
                                mcOpenTpPrice = dynTp
                            ElseIf config.StrategyCondition = StrategyConditionType.SuperTrend Then
                                qlStOpenSlPrice = dynStop
                                qlStOpenTpPrice = dynTp
                            End If
                        End If
                    End If
                End If

                ' â”€â”€ Signal evaluation â€” provider-based â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ' ForceClose re-entry cooldown: suppress entry signals for 3 bars after a
                ' profit-cap exit (only when flat; open-position exits are unaffected).
                If openLegs.Count = 0 AndAlso (i - lastForceCloseBarIndex) <= 3 Then Continue For

                ' DonchianBreakout de-bounce: suppress re-entry for 3 bars after a mid-cross
                ' (NeutralExit) to prevent oscillation churn around the 10-bar mid-channel.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.DonchianBreakout AndAlso
                   (i - lastDonchianExitBarIndex) <= 3 Then Continue For

                ' EmaRsi: evaluate even when a position is open (neutral-zone exit fires on
                ' open positions).  All other strategies: evaluate only when flat.
                Dim signal As SignalResult = Nothing
                If config.StrategyCondition = StrategyConditionType.EmaRsiWeightedScore Then
                    signal = provider.Evaluate(bar, indicators, config, i)
                    ' Neutral-zone exit: score 40â€“60 with open legs â†’ close at bar.Close
                    If signal IsNot Nothing AndAlso signal.NeutralExit AndAlso openLegs.Count > 0 Then
                        Dim neutralPositionPnL = 0D
                        For Each leg In openLegs
                            leg.ExitTime   = bar.Timestamp
                            leg.ExitPrice  = bar.Close
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

                ' â”€â”€ Map SignalResult to pending entry state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                If signal IsNot Nothing AndAlso signal.Side IsNot Nothing Then
                    If openLegs.Count = 0 Then
                        positionGroupCounter += 1
                        pending = New PendingEntry With {
                            .GroupId    = positionGroupCounter,
                            .Side       = signal.Side,
                            .Confidence = signal.Confidence,
                            .StopDelta  = signal.StopDelta,
                            .TpDelta    = signal.TpDelta,
                            .AbsStSl    = signal.AbsoluteSlPrice,
                            .AbsStTp    = signal.AbsoluteTpPrice,
                            .StIsLong   = signal.IsLong,
                            .IsPartialSignal = signal.IsPartialSignal
                        }
                        ‘ Indicator-channel exit level — strategy-specific pending field
                        If signal.IndicatorExitLevel <> 0D Then
                            Select Case config.StrategyCondition
                                Case StrategyConditionType.DonchianBreakout
                                    pending.DonMid    = signal.IndicatorExitLevel
                                    pending.DonIsLong = signal.IsLong
                                Case StrategyConditionType.BbRsiMeanReversion
                                    pending.BbMid    = signal.IndicatorExitLevel
                                    pending.BbIsLong = signal.IsLong
                                Case StrategyConditionType.DoubleBubbleButt
                                    pending.DbbInner  = signal.IndicatorExitLevel
                                    pending.DbbIsLong = signal.IsLong
                            End Select
                        End If
                    ElseIf config.StrategyCondition = StrategyConditionType.EmaRsiWeightedScore AndAlso
                           openLegs.Count < config.MaxScaleIns AndAlso
                           openLegs(0).Side = signal.Side Then
                        ‘ Scale-in — same direction, up to MaxScaleIns additional legs
                        pending = New PendingEntry With {
                            .Side       = signal.Side,
                            .Confidence = signal.Confidence,
                            .IsScaleIn  = True
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
            Dim allHighs  = filteredBars.Select(Function(b) b.High).ToList()
            Dim allLows   = filteredBars.Select(Function(b) b.Low).ToList()

            ' -- Shared series (EMA21/50, RSI14, ADX14 used by most strategies)
            Dim ema21Series = TechnicalIndicators.EMA(allCloses, 21)
            Dim ema50Series = TechnicalIndicators.EMA(allCloses, 50)
            Dim rsi14Series = TechnicalIndicators.RSI(allCloses, 14)
            Dim adx14Series = TechnicalIndicators.DMI(allHighs, allLows, allCloses).ADX

            Dim ema8Series As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.TripleEmaCascade Then
                ema8Series = TechnicalIndicators.EMA(allCloses, 8)
            End If

            Dim universalAtr14 As Single() = Nothing
            If config.UseAtrMode AndAlso
               config.StrategyCondition <> StrategyConditionType.MultiConfluence AndAlso
               config.StrategyCondition <> StrategyConditionType.SuperTrend Then
                universalAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' -- ConnorsRsi2: RSI(2), SMA(5), SMA(200)
            Dim qlRsi2   As Single() = Nothing
            Dim qlSma5   As Single() = Nothing
            Dim qlSma200 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.ConnorsRsi2 Then
                qlRsi2   = TechnicalIndicators.Rsi2(allCloses)
                qlSma5   = TechnicalIndicators.SMA(allCloses, 5)
                qlSma200 = TechnicalIndicators.SMA(allCloses, 200)
            End If

            ' -- SuperTrend: ATR(10) x 3.0 multiplier
            Dim qlStLine  As Single() = Nothing
            Dim qlStDir   As Single() = Nothing
            Dim qlStAtr10 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.SuperTrend Then
                Dim stResult  = TechnicalIndicators.SuperTrend(allHighs, allLows, allCloses, 10, 3.0)
                qlStLine  = stResult.Line
                qlStDir   = stResult.Direction
                qlStAtr10 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 10)
            End If

            ' -- DonchianBreakout: 20-bar entry channel + 10-bar exit channel
            Dim qlDonUpper20 As Single() = Nothing
            Dim qlDonLower20 As Single() = Nothing
            Dim qlDonMid10   As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.DonchianBreakout Then
                Dim don20    = TechnicalIndicators.DonchianChannel(allHighs, allLows, 20)
                qlDonUpper20 = don20.Upper
                qlDonLower20 = don20.Lower
                Dim don10    = TechnicalIndicators.DonchianChannel(allHighs, allLows, 10)
                qlDonMid10   = don10.Middle
            End If

            ' -- BbRsiMeanReversion: BB(20,2) + RSI(14)
            Dim qlBbUpper  As Single() = Nothing
            Dim qlBbMiddle As Single() = Nothing
            Dim qlBbLower  As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.BbRsiMeanReversion Then
                Dim bb20   = TechnicalIndicators.BollingerBands(allCloses, 20, 2.0)
                qlBbUpper  = bb20.Upper
                qlBbMiddle = bb20.Middle
                qlBbLower  = bb20.Lower
            End If

            ' -- DoubleBubbleButt: BB(20,1.0) inner + BB(20,2.0) outer + ATR(20)
            Dim dbbInnerUpper As Single() = Nothing
            Dim dbbInnerLower As Single() = Nothing
            Dim dbbOuterUpper As Single() = Nothing
            Dim dbbOuterLower As Single() = Nothing
            Dim dbbAtr20      As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.DoubleBubbleButt Then
                Dim dbbInner  = TechnicalIndicators.BollingerBands(allCloses, 20, 1.0)
                dbbInnerUpper = dbbInner.Upper
                dbbInnerLower = dbbInner.Lower
                Dim dbbOuter  = TechnicalIndicators.BollingerBands(allCloses, 20, 2.0)
                dbbOuterUpper = dbbOuter.Upper
                dbbOuterLower = dbbOuter.Lower
                dbbAtr20      = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 20)
            End If

            ' -- MultiConfluence: Ichimoku + DMI + MACD + StochRSI + ATR(14)
            Dim mcIchiTenkan As Single() = Nothing
            Dim mcIchiKijun  As Single() = Nothing
            Dim mcIchiSpanA  As Single() = Nothing
            Dim mcIchiSpanB  As Single() = Nothing
            Dim mcPlusDI     As Single() = Nothing
            Dim mcMinusDI    As Single() = Nothing
            Dim mcAdxSeries  As Single() = Nothing
            Dim mcMacdHist   As Single() = Nothing
            Dim mcStochRsiK  As Single() = Nothing
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

            ' -- Warm-up periods (bars to skip before signalling)
            Select Case config.StrategyCondition
                Case StrategyConditionType.MultiConfluence : warmUp = 80
                Case StrategyConditionType.ConnorsRsi2 : warmUp = 205
                Case StrategyConditionType.DoubleBubbleButt : warmUp = 25
                Case StrategyConditionType.LultDivergence : warmUp = 100
                Case Else : warmUp = 55
            End Select

            ' -- Populate StrategyIndicators from computed series
            Dim indicators As New StrategyIndicators With {
                .AllBars = filteredBars,
                .Ema21 = ema21Series,
                .Ema50 = ema50Series
            }
            If ema8Series IsNot Nothing Then indicators.Ema8 = ema8Series

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
                Case StrategyConditionType.ConnorsRsi2
                    indicators.Rsi2   = qlRsi2
                    indicators.Sma5   = qlSma5
                    indicators.Sma200 = qlSma200
                    indicators.Adx    = adx14Series
                    indicators.Atr    = universalAtr14
                Case StrategyConditionType.SuperTrend
                    indicators.SuperTrendLine = qlStLine
                    indicators.SuperTrendDir  = qlStDir
                    indicators.SuperTrendAtr  = qlStAtr10
                Case StrategyConditionType.DonchianBreakout
                    indicators.DonchianUpper = qlDonUpper20
                    indicators.DonchianLower = qlDonLower20
                    indicators.DonchianMid   = qlDonMid10
                    indicators.Atr           = universalAtr14
                Case StrategyConditionType.BbRsiMeanReversion
                    indicators.BbUpper  = qlBbUpper
                    indicators.BbMiddle = qlBbMiddle
                    indicators.BbLower  = qlBbLower
                    indicators.Rsi      = rsi14Series
                    indicators.Atr      = universalAtr14
                Case StrategyConditionType.DoubleBubbleButt
                    indicators.DbbInnerUpper = dbbInnerUpper
                    indicators.DbbInnerLower = dbbInnerLower
                    indicators.BbUpper       = dbbOuterUpper
                    indicators.BbLower       = dbbOuterLower
                    indicators.Atr           = dbbAtr20
                Case StrategyConditionType.VidyaCross
                    indicators.Vidya       = vcVidyaSeries
                    indicators.DeltaVolume = vcDeltaVolSeries
                    indicators.Atr         = vcAtr14
                Case StrategyConditionType.NakedTrader
                    indicators.Ema9          = ntEma9
                    indicators.MacdHistogram = ntMacdHist
                    indicators.MacdLine      = ntMacdLine
                    indicators.PlusDi        = ntPlusDI
                    indicators.MinusDi       = ntMinusDI
                    indicators.Adx           = ntAdxSeries
                    indicators.Vwap          = ntVwapSeries
                    indicators.VolMa20       = ntVolMa20
                    indicators.Atr           = ntAtr14
                Case StrategyConditionType.LultDivergence
                    indicators.WaveTrend1 = lultWt1
                    indicators.WaveTrend2 = lultWt2
                    indicators.Atr        = lultAtr14
                Case StrategyConditionType.BbSqueezeScalper
                    indicators.BbUpper  = bbsBands.Upper
                    indicators.BbMiddle = bbsBands.Middle
                    indicators.BbLower  = bbsBands.Lower
                    indicators.BbWidth  = bbsBbwArr
                    indicators.BbPctB   = bbsPctBArr
                    indicators.Rsi      = bbsRsi7
                    indicators.Ema5     = bbsEma5
                    indicators.BbwSma   = bbsBbwSma
                    indicators.Atr      = bbsAtr10
                Case StrategyConditionType.EmaRsiWeightedScore
                    Dim emaRsiPeriod = If(config.IndicatorPeriod > 0, config.IndicatorPeriod, 14)
                    indicators.Rsi = If(emaRsiPeriod = 14, rsi14Series, TechnicalIndicators.RSI(allCloses, emaRsiPeriod))
                    indicators.Adx = adx14Series
                    indicators.Atr = universalAtr14
                Case Else
                    indicators.Rsi = rsi14Series
                    indicators.Adx = adx14Series
                    indicators.Atr = universalAtr14
            End Select

            Return indicators
        End Function

        End Class
    End Namespace
