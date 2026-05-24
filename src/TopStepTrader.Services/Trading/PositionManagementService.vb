Imports System.Linq
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' ARCH-20: strategy-agnostic per-tick position management pipeline extracted from
    ''' <c>SuperTrendPlusViewModel.HandleOpenPositionAsync</c>. See
    ''' <see cref="IPositionManagementService"/> for the contract.
    '''
    ''' Singleton lifetime: the alternating-tick snapshot-skip set is per-service-instance
    ''' state and should outlive any single strategy ViewModel so a second strategy
    ''' joining the same instrument inherits the same cadence rather than burning a
    ''' redundant REST call on its first tick.
    ''' </summary>
    Public Class PositionManagementService
        Implements IPositionManagementService

        Private Const BarsToFetch As Integer = 60
        Private Const SyncMissThreshold As Integer = 3
        Private Const SnapshotStaleMinutes As Integer = 5

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _barService As IBarIngestionService
        Private ReadOnly _contractResolver As IContractResolutionService
        Private ReadOnly _tradeRecordService As ITradeRecordService
        Private ReadOnly _exitEngine As ExitSignalEngine
        Private ReadOnly _livePnL As ILivePnLService
        Private ReadOnly _debugCapture As IDebugTradeCaptureService
        Private ReadOnly _logger As ILogger(Of PositionManagementService)

        ''' <summary>
        ''' API-budget optimisation (BUG-72): slot indices whose live-position snapshot REST
        ''' call should be skipped on the next tick (alternating-tick cadence in steady state).
        ''' Was <c>SuperTrendPlusViewModel._skipSnapshotNextTick</c>; moved here so the per-
        ''' strategy snapshot cadence survives across ViewModels (singleton service).
        ''' </summary>
        Private ReadOnly _skipSnapshotNextTick As New HashSet(Of Integer)()

        Public Sub New(orderService As IOrderService,
                       barService As IBarIngestionService,
                       contractResolver As IContractResolutionService,
                       tradeRecordService As ITradeRecordService,
                       exitEngine As ExitSignalEngine,
                       logger As ILogger(Of PositionManagementService),
                       Optional livePnL As ILivePnLService = Nothing,
                       Optional debugCapture As IDebugTradeCaptureService = Nothing)
            _orderService = orderService
            _barService = barService
            _contractResolver = contractResolver
            _tradeRecordService = tradeRecordService
            _exitEngine = exitEngine
            _livePnL = livePnL
            _debugCapture = debugCapture
            _logger = logger
        End Sub

        Public Async Function UpdateAsync(slot As PositionSlot,
                                          tickContext As PositionManagementTickContext,
                                          ct As CancellationToken) _
            As Task(Of PositionManagementResult) _
            Implements IPositionManagementService.UpdateAsync

            Dim result As New PositionManagementResult()

            ' Default the "current close" / "latest pnl" trackers; populated once the
            ' tick reaches the indicator-evaluation stage.
            Dim latestPnl As Decimal = slot.UnrealizedPnl
            Dim currentClose As Decimal = If(slot.LivePrice > 0D, slot.LivePrice, 0D)

            ' API-budget optimisation: skip the per-slot REST snapshot on alternating ticks
            ' once the slot is in steady state (entry resolved, healthy, no recent miss).
            ' UserHub pushes via LivePnLService keep displayed P&L fresh in the gap.
            Dim mustSnapshot As Boolean =
                slot.EntryPrice = 0D OrElse
                Not slot.PositionId.HasValue OrElse
                slot.MissCount > 0 OrElse
                slot.Health <> SlotHealth.Healthy OrElse
                tickContext.ReleasedThisTick OrElse
                tickContext.ForceSnapshot
            Dim skipSnapshot As Boolean = False
            If Not mustSnapshot Then
                If _skipSnapshotNextTick.Contains(slot.SlotIndex) Then
                    skipSnapshot = True
                    _skipSnapshotNextTick.Remove(slot.SlotIndex)
                Else
                    _skipSnapshotNextTick.Add(slot.SlotIndex)
                End If
            Else
                _skipSnapshotNextTick.Remove(slot.SlotIndex)
            End If

            Dim snapshot As LivePositionSnapshot = Nothing
            If skipSnapshot Then
                snapshot = New LivePositionSnapshot With {
                    .PositionId = If(slot.PositionId, 0L),
                    .OpenRate = slot.EntryPrice,
                    .Units = slot.Contracts,
                    .Amount = slot.Contracts,
                    .IsBuy = (slot.Side = "Buy"),
                    .UnrealizedPnlUsd = slot.UnrealizedPnl,
                    .PositionCount = 1
                }
            Else
                Try
                    snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                        slot.AccountId, slot.Instrument, slot.PositionId)
                Catch ex As Exception
                    snapshot = Nothing
                    _logger.LogWarning(ex, "PosMgmt GetLivePositionSnapshotAsync failed for [Slot {Idx}] on {Contract}",
                                       slot.SlotIndex, slot.Instrument)
                End Try
            End If

            ' BUG-90 F2: a non-null snapshot with zero/negative Units or Amount is the
            ' degenerate "successful call but position is actually flat" shape.
            If Not skipSnapshot AndAlso snapshot IsNot Nothing AndAlso
               Not LivePositionSnapshotValidator.IsConfirmedOpen(snapshot) Then
                _logger.LogWarning(
                    "PosMgmt [Slot {Idx}] {Contract} snapshot returned degenerate shape (Units={Units}, Amount={Amount}) — treating as miss (BUG-90 F2)",
                    slot.SlotIndex, slot.Instrument, snapshot.Units, snapshot.Amount)
                snapshot = Nothing
            End If

            If snapshot Is Nothing Then
                slot.MissCount += 1
                If slot.MissCount >= SyncMissThreshold Then
                    Return ExitRequested("Closed by Broker", "miss", latestPnl, currentClose)
                End If

                ' BUG-79: defensive last-resort timeout.
                If SnapshotStalenessGuard.IsStale(
                       slot.LastSnapshotOkUtc, tickContext.AsOfUtc,
                       TimeSpan.FromMinutes(SnapshotStaleMinutes)) Then
                    _logger.LogWarning(
                        "PosMgmt [Slot {Idx}] {Contract} snapshot stale for {Mins:F1} min — force-releasing slot",
                        slot.SlotIndex, slot.Instrument,
                        (tickContext.AsOfUtc - slot.LastSnapshotOkUtc).TotalMinutes)
                    Return ExitRequested("Closed by Broker (snapshot stale)", "staleness", latestPnl, currentClose)
                End If

                ' Miss path without an exit: nothing else to do this tick.
                result.LatestPnl = latestPnl
                result.CurrentClose = currentClose
                Return result
            End If

            ' Live snapshot is valid.
            slot.MissCount = 0
            ' BUG-79 + BUG-90 F3: stamp only on confirmed-open real snapshots.
            If Not skipSnapshot Then
                slot.LastSnapshotOkUtc = tickContext.AsOfUtc
                slot.NetPosLastSeen = CInt(Math.Round(snapshot.Units))
            End If
            If Not slot.PositionId.HasValue AndAlso snapshot.PositionId <> 0 Then
                slot.PositionId = snapshot.PositionId
                _logger.LogInformation("PosMgmt [Slot {Idx}] {Contract} PositionId resolved from snapshot: {PosId}",
                                       slot.SlotIndex, slot.Instrument, slot.PositionId)
            End If
            If snapshot.OpenRate <> 0D AndAlso slot.EntryPrice = 0D Then
                Await BackfillEntryAndStopAsync(slot, snapshot)
            End If

            ' BUG-80: per-tick bracket-presence verification.
            If Not skipSnapshot AndAlso slot.IsOpen AndAlso slot.EntryPrice <> 0D Then
                Dim verifyResult = Await VerifyBracketStopAsync(slot, ct)
                If verifyResult.Outcome = PositionManagementOutcome.ExitRequested Then
                    verifyResult.LatestPnl = latestPnl
                    verifyResult.CurrentClose = currentClose
                    Return verifyResult
                End If
                If Not slot.IsOpen Then
                    result.LatestPnl = latestPnl
                    result.CurrentClose = currentClose
                    Return result
                End If
            End If

            latestPnl = snapshot.UnrealizedPnlUsd
            slot.UnrealizedPnl = latestPnl

            Dim bars As IList(Of MarketBar) = tickContext.Bars
            If bars Is Nothing OrElse bars.Count < 14 Then
                Try
                    ' BUG-72: paper feed for indicator alignment.
                    bars = Await _barService.GetLiveBarsAsync(slot.Instrument, tickContext.StrategyTimeframe, BarsToFetch, live:=False)
                Catch ex As Exception
                    _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] {Contract} strategy-TF bar fetch failed (tf={Tf})",
                                       slot.SlotIndex, slot.Instrument, tickContext.StrategyTimeframe)
                    slot.PriceStaleCount += 1
                    If slot.PriceStaleCount >= 2 AndAlso slot.Health = SlotHealth.Healthy Then
                        slot.Health = SlotHealth.Warning
                    End If
                    result.LatestPnl = latestPnl
                    result.CurrentClose = currentClose
                    Return result
                End Try
            End If
            If bars Is Nothing OrElse bars.Count < 14 Then
                result.LatestPnl = latestPnl
                result.CurrentClose = currentClose
                Return result
            End If

            ' BUG-81: stale strategy-bar guard with live fallback.
            Dim tfMinutes As Integer = Math.Max(1, CInt(tickContext.StrategyTimeframe))
            Dim latestBarAge As TimeSpan = DateTimeOffset.UtcNow - bars(bars.Count - 1).Timestamp
            If latestBarAge.TotalMinutes > 2.0 * tfMinutes Then
                _logger.LogWarning(
                    "PosMgmt [Slot {Idx}] {Contract} stale strategy bars (latest={Ts:o} age={AgeMin:F1}m tf={TfMin}m) — falling back to live feed",
                    slot.SlotIndex, slot.Instrument, bars(bars.Count - 1).Timestamp, latestBarAge.TotalMinutes, tfMinutes)
                Try
                    Dim liveBars = Await _barService.GetLiveBarsAsync(slot.Instrument, tickContext.StrategyTimeframe, BarsToFetch, live:=True)
                    If liveBars IsNot Nothing AndAlso liveBars.Count >= 14 Then
                        bars = liveBars
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] {Contract} live-fallback bar fetch failed", slot.SlotIndex, slot.Instrument)
                End Try
                If slot.Health = SlotHealth.Healthy Then slot.Health = SlotHealth.Warning
            End If

            Dim highs = bars.Select(Function(b) b.High).ToList()
            Dim lows = bars.Select(Function(b) b.Low).ToList()
            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim n = bars.Count - 1

            currentClose = If(slot.LivePrice > 0D, slot.LivePrice, CDec(closes(n)))

            ' FEAT-54: stale-price detection from ILivePnLService diagnostics.
            If _livePnL IsNot Nothing Then
                Dim diag = _livePnL.GetDiagnostics(slot.Instrument)
                If diag.BarFetchZeroCount >= 5 AndAlso slot.Health = SlotHealth.Healthy Then
                    slot.Health = SlotHealth.Warning
                End If
            End If

            ' Derive P&L locally from the freshest price. The TopStepX REST snapshot always
            ' returns openPnl=0, so the local calculation is the sole source of truth for
            ' the display. FEAT-58: also track MAE/MFE.
            If slot.EntryPrice <> 0D Then
                Dim fc3 = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                If fc3 IsNot Nothing AndAlso fc3.PxTickSize > 0D AndAlso fc3.PxTickValue > 0D Then
                    Dim priceDiff As Decimal = If(slot.Side = "Buy",
                        currentClose - slot.EntryPrice,
                        slot.EntryPrice - currentClose)
                    Dim ticks As Decimal = priceDiff / fc3.PxTickSize
                    latestPnl = Math.Round(ticks * fc3.PxTickValue * slot.Contracts, 2)
                    slot.UnrealizedPnl = latestPnl
                    If latestPnl < slot.MaxAdverseExcursionUsd Then slot.MaxAdverseExcursionUsd = latestPnl
                    If latestPnl > slot.MaxFavorableExcursionUsd Then slot.MaxFavorableExcursionUsd = latestPnl
                    If slot.InitialRiskDollars = 0D AndAlso slot.InitialRisk <> 0D Then
                        Dim riskTicks = slot.InitialRisk / fc3.PxTickSize
                        slot.InitialRiskDollars = Math.Round(riskTicks * fc3.PxTickValue * slot.Contracts, 2)
                    End If
                End If
            End If

            ' ── P&L Guard override ──
            ' BUG-78: aggregate unrealised P&L across all open slots on the same instrument.
            ' The caller supplies the aggregated value via TickContext.AggregatedInstrumentPnl.
            If slot.IsOpen AndAlso slot.EntryPrice <> 0D AndAlso
               tickContext.PnLGuard IsNot Nothing AndAlso tickContext.PnLGuard.IsActive Then
                If tickContext.PnLGuard.ShouldFlatten(tickContext.AggregatedInstrumentPnl) Then
                    _logger.LogInformation(
                        "PosMgmt [Slot {Idx}] {Contract} P&L Guard breach — aggregated={Agg:F2} (slot={Slot:F2}) tp={Tp} sl={Sl}",
                        slot.SlotIndex, slot.Instrument, tickContext.AggregatedInstrumentPnl, latestPnl,
                        tickContext.PnLGuard.TakeProfitThreshold, tickContext.PnLGuard.StopLossThreshold)
                    Return ExitRequested(Core.Settings.PnLGuardSettings.ExitReasonText, "pnl-guard", latestPnl, currentClose)
                End If
            End If

            Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=tickContext.StMultiplier)
            Dim dmiForExit = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
            Dim atr14Exit = TechnicalIndicators.ATR(highs, lows, closes, period:=14)
            Dim stLine = CDec(st.Line(n))

            ' BUG-49: clear early-mode grace once ST direction confirms slot side.
            If slot.IsEarlyModeEntry Then
                Dim stDirN = st.Direction(n)
                If (slot.Side = "Buy" AndAlso stDirN > 0) OrElse (slot.Side = "Sell" AndAlso stDirN < 0) Then
                    slot.IsEarlyModeEntry = False
                    _logger.LogInformation("PosMgmt [Slot {Idx}] Early entry confirmed — E1 now active", slot.SlotIndex)
                ElseIf tickContext.EarlyModeMaxAgeMinutes > 0 AndAlso slot.EntryTime <> DateTime.MinValue Then
                    ' BUG-81: auto-clear early-mode grace once the configured cap elapses.
                    Dim graceAge = DateTime.Now - slot.EntryTime
                    If graceAge.TotalMinutes >= tickContext.EarlyModeMaxAgeMinutes Then
                        slot.IsEarlyModeEntry = False
                        _logger.LogWarning(
                            "PosMgmt [Slot {Idx}] Early-mode grace expired after {AgeMin:F1}m (cap={CapMin}m) — E1 now active without ST confirmation",
                            slot.SlotIndex, graceAge.TotalMinutes, tickContext.EarlyModeMaxAgeMinutes)
                    End If
                End If
            End If

            ' BUG-81: per-tick ST direction trace for diagnosis.
            Dim stDirNow = st.Direction(n)
            Dim stDirPrev = If(n > 0, st.Direction(n - 1), stDirNow)
            _logger.LogInformation(
                "PosMgmt [Slot {Idx}] {Contract} barTs={Ts:o} stDir(n-1)={Prev} stDir(n)={Curr} side={Side} earlyMode={Early}",
                slot.SlotIndex, slot.Instrument, bars(n).Timestamp, stDirPrev, stDirNow, slot.Side, slot.IsEarlyModeEntry)

            Dim volumes = bars.Select(Function(b) b.Volume).ToList()
            Dim vwapExit = TechnicalIndicators.VWAP(highs, lows, closes, volumes)
            Dim rsiExit = TechnicalIndicators.RSI(closes, 14)

            ' BUG-75 / ARCH-15: floor InitialRisk at the entry-bar ATR.
            If slot.InitialRisk = 0D AndAlso slot.EntryPrice <> 0D AndAlso slot.StopPrice <> 0D Then
                Dim rawRisk As Decimal = Math.Abs(slot.EntryPrice - slot.StopPrice)
                slot.InitialRisk = If(slot.EntryAtr > 0D, Math.Max(rawRisk, slot.EntryAtr), rawRisk)
                _logger.LogInformation(
                    "PosMgmt [Slot {Idx}] InitialRisk stamped: raw={Raw:F4} atr={Atr:F4} final={Final:F4} (entry={Entry:F4} stop={Stop:F4})",
                    slot.SlotIndex, rawRisk, slot.EntryAtr, slot.InitialRisk, slot.EntryPrice, slot.StopPrice)
            End If

            Dim adxNow = dmiForExit.ADX(n)
            If Not Single.IsNaN(adxNow) Then slot.CurrentAdx = adxNow

            Dim diPlusNow = dmiForExit.PlusDI(n)
            Dim diMinusNow = dmiForExit.MinusDI(n)
            Dim priceToStNow = If(slot.EntryPrice <> 0D AndAlso stLine <> 0D,
                                  CSng(Math.Abs(currentClose - stLine)), 0F)

            ' STRAT-31: scale in as ADX strengthens. Strategy supplies the band classifier.
            ' Band delta is multiplied by the leverage multiplier so a 3× user adding +1 band
            ' adds +3 contracts; total caps at 3 × leverage to mirror the entry-time math.
            If Not slot.IsEarlyModeEntry AndAlso slot.EntryPrice <> 0D AndAlso
               tickContext.BandForAdx IsNot Nothing Then
                If Not Single.IsNaN(adxNow) Then
                    Dim currentBand = tickContext.BandForAdx(adxNow)
                    Dim lev As Integer = Math.Max(1, tickContext.LeverageMultiplier)
                    Dim maxContracts As Integer = 3 * lev
                    If currentBand > slot.LastAdxBand AndAlso slot.Contracts < maxContracts Then
                        Dim addContracts = Math.Min((currentBand - slot.LastAdxBand) * lev,
                                                    maxContracts - slot.Contracts)
                        If addContracts > 0 AndAlso tickContext.OnScaleInRequested IsNot Nothing Then
                            Await tickContext.OnScaleInRequested(slot, addContracts)
                        End If
                    End If
                    slot.LastAdxBand = Math.Max(slot.LastAdxBand, currentBand)
                End If
            End If

            ' ARCH-15: ExitSignalEngine is the single source of truth.
            Dim closesDec = closes.Select(Function(c) CDec(c)).ToList()
            Dim highsDec = highs.Select(Function(h) CDec(h)).ToList()
            Dim lowsDec = lows.Select(Function(l) CDec(l)).ToList()
            Dim plusDIArr = Enumerable.Range(0, bars.Count).Select(Function(i) dmiForExit.PlusDI(i)).ToArray()
            Dim minusDIArr = Enumerable.Range(0, bars.Count).Select(Function(i) dmiForExit.MinusDI(i)).ToArray()
            Dim adxArr = Enumerable.Range(0, bars.Count).Select(Function(i) dmiForExit.ADX(i)).ToArray()
            Dim fc = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
            Dim breakevenMinTicks As Integer = If(fc IsNot Nothing, fc.PhasedTrailBreakevenMinTicks, 0)
            Dim tickSize As Decimal = If(fc IsNot Nothing, fc.PxTickSize, 0D)
            Dim eval = _exitEngine.Evaluate(slot,
                                            highsDec, lowsDec, closesDec,
                                            st.Line, st.Direction,
                                            plusDIArr, minusDIArr, adxArr,
                                            atr14Exit,
                                            vwapExit, rsiExit,
                                            breakevenMinTicks, tickSize)

            ' BUG-88 / FEAT-63: two-bar consecutive-score exit gate. Suppressed entirely
            ' when the ladder is active — in that mode the laddered SL is the sole exit
            ' trigger; E1 flip and E2–E9 degradation are both ignored.
            Dim currentBarTs = bars(n).Timestamp
            Dim ladderActive As Boolean = tickContext.LadderTpDollars > 0D
            If Not ladderActive Then
                Dim gateAction = ExitGate.ApplyExitGate(slot, eval, currentBarTs, tickContext.ExitScoreThreshold)
                Select Case gateAction
                    Case ExitGateAction.ImmediateExit
                        Return ExitRequested("ExitEngine: SuperTrend flip", "exit-engine", latestPnl, currentClose,
                                              adxNow, diPlusNow, diMinusNow, priceToStNow, ranEngine:=True)
                    Case ExitGateAction.ConsecutiveExit
                        Return ExitRequested(
                            $"ExitEngine: score={eval.Score} (bar {slot.ConsecutiveExitBars}/2) signals=[{String.Join(",", eval.ContributingSignals)}]",
                            "exit-engine", latestPnl, currentClose,
                            adxNow, diPlusNow, diMinusNow, priceToStNow, ranEngine:=True)
                    Case ExitGateAction.WarningIncremented
                        _logger.LogInformation(
                            "PosMgmt [Slot {Idx}] {Contract} exit-warning bar {N}/2 — score={Score} signals=[{Sigs}]",
                            slot.SlotIndex, slot.Instrument, slot.ConsecutiveExitBars, eval.Score,
                            String.Join(",", eval.ContributingSignals))
                    Case ExitGateAction.CounterReset
                        _logger.LogInformation(
                            "PosMgmt [Slot {Idx}] {Contract} exit-warning cleared — score={Score}",
                            slot.SlotIndex, slot.Instrument, eval.Score)
                End Select
            End If

            ' Phased-stop ratchet (skipped during early-mode grace).
            Dim stopAdjusted As Boolean = False
            If Not slot.IsEarlyModeEntry Then
                Dim newStop As Decimal = eval.PhasedStopPrice
                Dim oldStopPhase = slot.StopPhase
                slot.StopPhase = eval.StopPhase

                ' FEAT-63: merge laddered SL (if ladder mode active) — whichever stop
                ' is more conservative wins. Ladder math is stateless: rung is recomputed
                ' from current $ P&L each tick, so scale-in profit jumps advance the SL
                ' to the highest cleared rung in one step. Existing eval.PhasedStopPrice
                ' is already ratcheted against slot.StopPrice, so the Max/Min merge here
                ' preserves the never-moves-backward invariant.
                Dim ladderSlForLog As Decimal? = Nothing
                Dim ladderRungForLog As Integer = 0
                If ladderActive AndAlso slot.EntryPrice <> 0D Then
                    Dim fcLadder = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                    If fcLadder IsNot Nothing Then
                        ladderRungForLog = LadderStopCalculator.ComputeRung(latestPnl, tickContext.LadderTpDollars)
                        Dim ladderSlOpt = LadderStopCalculator.ComputeLadderStopPrice(
                            ladderRungForLog, tickContext.LadderTpDollars,
                            slot.EntryPrice, slot.Side = "Buy", slot.Contracts,
                            fcLadder.PxTickSize, fcLadder.PxTickValue)
                        If ladderSlOpt.HasValue Then
                            ladderSlForLog = ladderSlOpt
                            If slot.Side = "Buy" Then
                                newStop = Math.Max(newStop, ladderSlOpt.Value)
                            Else
                                newStop = Math.Min(newStop, ladderSlOpt.Value)
                            End If
                        End If
                    End If
                End If

                ' FEAT-58: count a ratchet step on phase or price change; stamp FreeRide activation.
                If oldStopPhase <> slot.StopPhase OrElse newStop <> slot.StopPrice Then
                    slot.SlRatchetCount += 1
                End If
                If slot.StopPhase = StopPhase.FreeRide AndAlso slot.FreeRideActivatedAtMinutes = 0F AndAlso
                   slot.EntryTime <> DateTime.MinValue Then
                    slot.FreeRideActivatedAtMinutes = CSng((tickContext.AsOfUtc - slot.EntryTime).TotalMinutes)
                End If

                Dim isPrimaryForEdit As Boolean = tickContext.IsPrimaryForBracketEdit
                Dim primaryEditSucceeded As Boolean = False
                If isPrimaryForEdit AndAlso slot.PositionId.HasValue AndAlso newStop <> slot.StopPrice Then
                    Dim tpArg As Decimal? = If(slot.TakeProfitPrice <> 0D, CType(slot.TakeProfitPrice, Decimal?), Nothing)
                    ' FEAT-63: emit a marker line when the ladder is the binding constraint on this move,
                    ' so the post-mortem can distinguish ladder-driven rolls from phased-stop rolls.
                    If ladderActive AndAlso ladderSlForLog.HasValue AndAlso ladderSlForLog.Value = newStop AndAlso
                       ladderRungForLog >= 1 Then
                        Dim lockedPnl As Decimal = ladderRungForLog * tickContext.LadderTpDollars
                        _logger.LogInformation(
                            "PosMgmt LADDER advanced to rung {Rung} — SL→{Price:F2} (locks ${Locked:F2} P&L; pnl={Pnl:F2}, tp={Tp:F2}) for [Slot {Idx}] on {Contract}",
                            ladderRungForLog, newStop, lockedPnl, latestPnl, tickContext.LadderTpDollars,
                            slot.SlotIndex, slot.Instrument)
                    End If
                    Try
                        Dim editOk = Await _orderService.EditPositionSlTpAsync(slot.PositionId.Value, newStop, tpArg)
                        If editOk Then
                            primaryEditSucceeded = True
                            If tickContext.IsDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
                               Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                                Dim slSnap As New DebugSnapshotRecord With {
                                    .TradeId = slot.DebugTradeId,
                                    .Timestamp = DateTime.UtcNow.ToString("O"),
                                    .EventType = "SlAdjust",
                                    .CurrentSL = newStop,
                                    .Notes = $"{oldStopPhase}->{slot.StopPhase}"
                                }
                                _debugCapture.RecordSnapshot(slSnap)
                                _debugCapture.RecordAction(New DebugTradeAction With {
                                    .TradeId = slot.DebugTradeId,
                                    .TimestampUtc = DateTime.UtcNow.ToString("O"),
                                    .ActionType = "StopLossModified",
                                    .OldValue = slot.StopPrice,
                                    .NewValue = newStop,
                                    .Reason = $"Phase {oldStopPhase} -> {slot.StopPhase}",
                                    .Source = "Local"
                                })
                            End If
                            _logger.LogInformation("PosMgmt ExitEngine SL phase={Phase} trail->{Price} (TP={Tp}) for [Slot {Idx}] on {Contract}",
                                                   slot.StopPhase, newStop,
                                                   If(tpArg.HasValue, tpArg.Value.ToString("F2"), "none"),
                                                   slot.SlotIndex, slot.Instrument)
                        Else
                            _logger.LogWarning("PosMgmt EditPositionSlTpAsync returned False for [Slot {Idx}] on {Contract} — will retry next tick",
                                               slot.SlotIndex, slot.Instrument)
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "PosMgmt EditPositionSlTpAsync failed for [Slot {Idx}] on {Contract}", slot.SlotIndex, slot.Instrument)
                    End Try
                ElseIf Not isPrimaryForEdit Then
                    _logger.LogInformation("PosMgmt ExitEngine SL ratchet deferred for scale-in [Slot {Idx}] on {Contract} — primary slot owns bracket",
                                           slot.SlotIndex, slot.Instrument)
                End If

                If newStop <> slot.StopPrice AndAlso (Not isPrimaryForEdit OrElse primaryEditSucceeded) Then
                    If slot.TradeRecordId > 0 Then
                        Dim rid = slot.TradeRecordId
                        Dim prevStop = slot.StopPrice
                        Dim ns = newStop
                        Dim ph = slot.StopPhase.ToString()
                        Dim svc = _tradeRecordService
                        Dim log = _logger
#Disable Warning BC42358
                        Task.Run(Async Function()
                                     Try
                                         Await svc.LogStopAdjustmentAsync(rid, DateTimeOffset.UtcNow, prevStop, ns, ph)
                                     Catch ex As Exception
                                         log.LogWarning(ex, "PosMgmt LogStopAdjustmentAsync failed for record {Id}", rid)
                                     End Try
                                 End Function)
#Enable Warning BC42358
                    End If
                    slot.StopPrice = newStop
                    stopAdjusted = True
                End If
            End If

            ' FEAT-59: per-closed-bar tick snapshot for unconditional replay.
            If slot.TradeRecordId > 0 Then
                Dim barTs = bars(n).Timestamp
                If barTs > slot.LastTickSnapshotBarTime Then
                    slot.LastTickSnapshotBarTime = barTs
                    Dim tickSnap As New TradeTickSnapshot With {
                        .LiveTradeRecordId = slot.TradeRecordId,
                        .BarTimestamp = barTs,
                        .BarOpen = bars(n).Open,
                        .BarHigh = bars(n).High,
                        .BarLow = bars(n).Low,
                        .BarClose = bars(n).Close,
                        .BarVolume = bars(n).Volume,
                        .SuperTrendLine = stLine,
                        .SuperTrendDirection = If(st.Direction(n) >= 0, 1, -1),
                        .Atr = atr14Exit(n),
                        .Adx = dmiForExit.ADX(n),
                        .PlusDi = dmiForExit.PlusDI(n),
                        .MinusDi = dmiForExit.MinusDI(n),
                        .CurrentStopPrice = slot.StopPrice,
                        .CurrentTakeProfitPrice = slot.TakeProfitPrice,
                        .UnrealisedPnlDollars = latestPnl,
                        .MaxAdverseExcursionDollars = slot.MaxAdverseExcursionUsd,
                        .MaxFavorableExcursionDollars = slot.MaxFavorableExcursionUsd,
                        .StopPhase = slot.StopPhase.ToString(),
                        .ExitScore = eval.Score
                    }
                    Dim svcTick = _tradeRecordService
                    Dim logTick = _logger
                    Dim ridTick = slot.TradeRecordId
#Disable Warning BC42358
                    Task.Run(Async Function()
                                 Try
                                     Await svcTick.LogTickSnapshotAsync(ridTick, tickSnap)
                                 Catch ex As Exception
                                     logTick.LogWarning(ex, "PosMgmt LogTickSnapshotAsync failed for record {Id}", ridTick)
                                 End Try
                             End Function)
#Enable Warning BC42358
                End If
            End If

            result.Outcome = PositionManagementOutcome.Continue
            result.StopAdjusted = stopAdjusted
            result.RanExitEngine = True
            result.LatestPnl = latestPnl
            result.CurrentClose = currentClose
            result.AdxSample = adxNow
            result.PlusDiSample = diPlusNow
            result.MinusDiSample = diMinusNow
            result.PriceToStSample = priceToStNow
            Return result
        End Function

        ''' <summary>
        ''' BUG-80: Verifies a resting Stop bracket order (type=4) exists on the broker for
        ''' this slot's contract. When absent, attempts to re-create the protective stop
        ''' from the last known <c>StopPrice</c>; if the protective stop is still missing
        ''' on the next tick, the slot is flattened with reason "Bracket SL missing" to
        ''' prevent unmanaged risk. Slot health is degraded to Warning while missing.
        ''' </summary>
        Public Async Function VerifyBracketStopAsync(slot As PositionSlot,
                                                       ct As CancellationToken) _
            As Task(Of PositionManagementResult) _
            Implements IPositionManagementService.VerifyBracketStopAsync

            Dim ok As New PositionManagementResult()

            Dim bracketStop As Decimal? = Nothing
            Try
                bracketStop = Await _orderService.TryGetBracketStopPriceAsync(slot.AccountId, slot.Instrument)
            Catch ex As Exception
                _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] BracketState verify lookup failed for {Contract}",
                                   slot.SlotIndex, slot.Instrument)
                Return ok
            End Try

            If bracketStop.HasValue AndAlso bracketStop.Value > 0D Then
                If slot.BracketMissingTickCount > 0 Then
                    _logger.LogInformation(
                        "PosMgmt [Slot {Idx}] {Contract} BracketState=Restored stop={Stop:F2}",
                        slot.SlotIndex, slot.Instrument, bracketStop.Value)
                End If
                slot.BracketMissingTickCount = 0
                Return ok
            End If

            slot.BracketMissingTickCount += 1
            _logger.LogWarning(
                "PosMgmt [Slot {Idx}] {Contract} BracketState=Missing tickCount={Count} positionId={PosId} entryOrderId={OId} cachedStop={Stop:F2}",
                slot.SlotIndex, slot.Instrument, slot.BracketMissingTickCount,
                If(slot.PositionId.HasValue, slot.PositionId.Value.ToString(), "n/a"),
                If(slot.EntryOrderId.HasValue, slot.EntryOrderId.Value.ToString(), "n/a"),
                slot.StopPrice)

            If slot.Health = SlotHealth.Healthy Then
                slot.Health = SlotHealth.Warning
            End If

            Dim restored As Boolean = False
            Dim restoredVia As String = Nothing
            If slot.PositionId.HasValue AndAlso slot.StopPrice <> 0D Then
                Try
                    restored = Await _orderService.EditPositionSlTpAsync(slot.PositionId.Value, slot.StopPrice, Nothing)
                    If restored Then restoredVia = "edit"
                Catch ex As Exception
                    _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] BracketState=Missing edit-restore failed for {Contract}",
                                       slot.SlotIndex, slot.Instrument)
                End Try
            End If

            If restored AndAlso restoredVia = "edit" Then
                If _debugCapture IsNot Nothing AndAlso Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                    _debugCapture.RecordAction(New DebugTradeAction With {
                        .TradeId = slot.DebugTradeId,
                        .TimestampUtc = DateTime.UtcNow.ToString("O"),
                        .ActionType = "StopLossModified",
                        .OldValue = Nothing,
                        .NewValue = slot.StopPrice,
                        .Reason = "Bracket re-protect (edit)",
                        .Source = "Local"
                    })
                End If
            End If

            If Not restored AndAlso slot.StopPrice <> 0D Then
                Try
                    Dim protectSide As OrderSide = If(slot.Side = "Buy", OrderSide.Sell, OrderSide.Buy)
                    Dim stopOrder As New Order With {
                        .AccountId = slot.AccountId,
                        .ContractId = slot.Instrument,
                        .Side = protectSide,
                        .Quantity = slot.Contracts,
                        .OrderType = OrderType.StopOrder,
                        .StopPrice = slot.StopPrice
                    }
                    Dim placed = Await _orderService.PlaceOrderAsync(stopOrder)
                    restored = placed IsNot Nothing AndAlso
                               (placed.Status = OrderStatus.Working OrElse placed.Status = OrderStatus.Filled)
                    _logger.LogWarning(
                        "PosMgmt [Slot {Idx}] {Contract} BracketState=Missing stand-alone Stop submit ok={Ok} stop={Stop:F2}",
                        slot.SlotIndex, slot.Instrument, restored, slot.StopPrice)
                    If restored AndAlso _debugCapture IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                        _debugCapture.RecordAction(New DebugTradeAction With {
                            .TradeId = slot.DebugTradeId,
                            .TimestampUtc = DateTime.UtcNow.ToString("O"),
                            .ActionType = "StopLossPlaced",
                            .Price = slot.StopPrice,
                            .Quantity = slot.Contracts,
                            .OrderId = placed?.ExternalOrderId,
                            .Reason = "Bracket re-protect (stand-alone Stop)",
                            .Source = "Local"
                        })
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] BracketState=Missing stand-alone Stop submit failed for {Contract}",
                                       slot.SlotIndex, slot.Instrument)
                End Try
            End If

            If slot.BracketMissingTickCount >= 2 Then
                _logger.LogError(
                    "PosMgmt [Slot {Idx}] {Contract} BracketState=Missing for {Count} ticks — flattening (Bracket SL missing)",
                    slot.SlotIndex, slot.Instrument, slot.BracketMissingTickCount)
                Return New PositionManagementResult With {
                    .Outcome = PositionManagementOutcome.ExitRequested,
                    .ExitReason = "Bracket SL missing",
                    .ExitTrigger = "bracket-missing"
                }
            End If

            Return ok
        End Function

        ''' <summary>
        ''' Backfills <c>slot.EntryPrice</c> + <c>slot.StopPrice</c> from the first confirmed
        ''' broker snapshot. Prefers the order's execute price over the position snapshot
        ''' averagePrice for entry; resyncs the broker bracket SL to the ST-line stored at
        ''' entry time to absorb any gap-open divergence.
        ''' </summary>
        Private Async Function BackfillEntryAndStopAsync(slot As PositionSlot,
                                                          snapshot As LivePositionSnapshot) As Task
            Dim confirmedEntry As Decimal = 0D
            If slot.EntryOrderId.HasValue Then
                Try
                    Dim fillPx = Await _orderService.TryGetOrderFillPriceAsync(
                        slot.EntryOrderId.Value, slot.AccountId)
                    If fillPx.HasValue AndAlso fillPx.Value > 0D Then
                        confirmedEntry = fillPx.Value
                        _logger.LogInformation(
                            "PosMgmt [Slot {Idx}] {Contract} EntryPrice from execute price: {Price:F2} (order {OId})",
                            slot.SlotIndex, slot.Instrument, confirmedEntry, slot.EntryOrderId.Value)
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] TryGetOrderFillPriceAsync failed for {Contract}", slot.SlotIndex, slot.Instrument)
                End Try
            End If
            If confirmedEntry = 0D Then confirmedEntry = snapshot.OpenRate
            slot.EntryPrice = confirmedEntry

            ' Pull the initial stop price from the live Stop Market bracket order (BUG-82 F1).
            Dim priorBracketStop As Decimal? = Nothing
            Try
                Dim bracketStop = Await _orderService.TryGetBracketStopPriceAsync(slot.AccountId, slot.Instrument)
                If bracketStop.HasValue AndAlso bracketStop.Value > 0D Then
                    _logger.LogInformation(
                        "PosMgmt [Slot {Idx}] {Contract} StopPrice from bracket order: {Stop:F2}",
                        slot.SlotIndex, slot.Instrument, bracketStop.Value)
                    priorBracketStop = bracketStop.Value
                    slot.StopPrice = bracketStop.Value
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] TryGetBracketStopPriceAsync failed for {Contract}", slot.SlotIndex, slot.Instrument)
            End Try

            ' One-time sync: drive the broker SL to the ST-line value stored at entry time.
            If slot.PositionId.HasValue AndAlso slot.StopPrice <> 0D Then
                Try
                    Dim syncOk = Await _orderService.EditPositionSlTpAsync(slot.PositionId.Value, slot.StopPrice, Nothing)
                    _logger.LogInformation("PosMgmt [Slot {Idx}] initial SL sync → {Stop:F2} ok={Ok}",
                                           slot.SlotIndex, slot.StopPrice, syncOk)
                    If syncOk AndAlso _debugCapture IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                        _debugCapture.RecordAction(New DebugTradeAction With {
                            .TradeId = slot.DebugTradeId,
                            .TimestampUtc = DateTime.UtcNow.ToString("O"),
                            .ActionType = "StopLossModified",
                            .OldValue = priorBracketStop,
                            .NewValue = slot.StopPrice,
                            .Reason = If(priorBracketStop.HasValue,
                                         "Initial SL sync to ST-line after fill",
                                         "Initial SL sync to ST-line after fill (prior bracket SL unknown)"),
                            .Source = "Local"
                        })
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "PosMgmt [Slot {Idx}] initial SL sync failed for {Contract}", slot.SlotIndex, slot.Instrument)
                End Try
            End If

            If slot.TradeRecordId > 0 Then
                Dim svc = _tradeRecordService
                Dim rid = slot.TradeRecordId
                Dim entryForUpdate = confirmedEntry
                Dim log = _logger
#Disable Warning BC42358
                Task.Run(Async Function() As Task
                             Try
                                 Await svc.UpdateEntryPriceAsync(rid, entryForUpdate)
                             Catch ex As Exception
                                 log.LogWarning(ex, "PosMgmt UpdateEntryPriceAsync failed for record {Id}", rid)
                             End Try
                         End Function)
#Enable Warning BC42358
            End If

            If _debugCapture IsNot Nothing AndAlso Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                _debugCapture.UpdateFill(slot.DebugTradeId, confirmedEntry, DateTime.UtcNow)
                _debugCapture.RecordAction(New DebugTradeAction With {
                    .TradeId = slot.DebugTradeId,
                    .TimestampUtc = DateTime.UtcNow.ToString("O"),
                    .ActionType = "EntryFilled",
                    .Price = confirmedEntry,
                    .Quantity = slot.Contracts,
                    .OrderId = slot.EntryOrderId,
                    .Source = "Local"
                })
            End If
        End Function

        Private Shared Function ExitRequested(reason As String,
                                              trigger As String,
                                              latestPnl As Decimal,
                                              currentClose As Decimal,
                                              Optional adxSample As Single = Single.NaN,
                                              Optional plusDiSample As Single = Single.NaN,
                                              Optional minusDiSample As Single = Single.NaN,
                                              Optional priceToStSample As Single = 0F,
                                              Optional ranEngine As Boolean = False) As PositionManagementResult
            Return New PositionManagementResult With {
                .Outcome = PositionManagementOutcome.ExitRequested,
                .ExitReason = reason,
                .ExitTrigger = trigger,
                .LatestPnl = latestPnl,
                .CurrentClose = currentClose,
                .AdxSample = adxSample,
                .PlusDiSample = plusDiSample,
                .MinusDiSample = minusDiSample,
                .PriceToStSample = priceToStSample,
                .RanExitEngine = ranEngine
            }
        End Function

    End Class

End Namespace
