Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.SlipStream

    ''' <summary>
    ''' FEAT-70: Singleton scanner-orchestrator for the SlipStream trend-pullback strategy.
    '''
    ''' Scans the watchlist every <see cref="ScanIntervalSeconds"/> seconds while
    ''' <see cref="IsEnabled"/> is True; emits <c>WatchlistTick</c> + <c>SignalDetected</c>.
    ''' On a confluence signal while flat, opens a single live position via
    ''' <see cref="IEntryExecutionService"/> (reusing the ARCH-19 pipeline for AI veto,
    ''' bracket placement, and persistence), then drives a quote-based exit policy:
    '''
    '''   • At entry: protective SL at <c>entry ± atrSLmult × ATR</c>.
    '''   • On every quote: track peak favorable price; once price has moved
    '''     <c>trailOffMult × ATR</c> in favor, ratchet a runner trail at
    '''     <c>trailMult × ATR</c> behind peak.
    '''   • When price reaches <c>entry ± atrTP1mult × ATR</c>: close
    '''     <c>tp1Pct%</c> of the position via <see cref="IOrderService.PartialCloseContractAsync"/>.
    '''   • At time-stop (<c>maxBarsInTrade</c> elapsed) or force-flat window: full flatten
    '''     via <see cref="IExitExecutionService"/>.
    '''
    ''' Strict single-position guard: while <see cref="IsInPosition"/>, additional
    ''' <c>SignalDetected</c> events are silently dropped (no DCA).
    ''' </summary>
    Public Class SlipStreamOrchestrator
        Implements IHostedService, IDisposable

        ''' <summary>Watchlist symbols. Equity-futures micros + MGC, matching UltimateScalper.</summary>
        Public Shared ReadOnly WatchlistSymbols As IReadOnlyList(Of String) =
            New String() {"MES", "MNQ", "MGC"}

        ''' <summary>Scan cadence — 30s, identical to UltimateScalper. Indicators are 5m-based
        ''' so anything tighter just recomputes the same numbers.</summary>
        Private Const ScanIntervalSeconds As Integer = 30

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _entryExecution As IEntryExecutionService
        Private ReadOnly _exitExecution As IExitExecutionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _marketHub As IMarketQuoteFeed
        Private ReadOnly _logger As ILogger(Of SlipStreamOrchestrator)
        Private ReadOnly _lastFiredAsOf As New ConcurrentDictionary(Of String, DateTimeOffset)()
        Private ReadOnly _livePositionLock As New Object()
        Private _livePosition As SlipStreamLivePosition
        Private _quoteHandler As EventHandler(Of MarketQuoteEventArgs)
        Private _timer As Timer
        Private _scanInFlight As Integer
        Private _isEnabled As Boolean
        Private _lastExitUtc As DateTime = DateTime.MinValue

        Public Event WatchlistTick As EventHandler(Of SlipStreamEvaluation)
        Public Event SignalDetected As EventHandler(Of SlipStreamEvaluation)
        Public Event EnabledChanged As EventHandler(Of Boolean)
        Public Event LivePositionChanged As EventHandler(Of Boolean)
        Public Event TrailUpdated As EventHandler(Of SlipStreamTrailSnapshot)
        Public Event ScanCompleted As EventHandler(Of ScannerScanCompletedEventArgs)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       session As ITradingSessionContext,
                       entryExecution As IEntryExecutionService,
                       exitExecution As IExitExecutionService,
                       orderService As IOrderService,
                       marketHub As IMarketQuoteFeed,
                       logger As ILogger(Of SlipStreamOrchestrator))
            _scopeFactory = scopeFactory
            _session = session
            _entryExecution = entryExecution
            _exitExecution = exitExecution
            _orderService = orderService
            _marketHub = marketHub
            _logger = logger
            _quoteHandler = AddressOf OnQuoteReceived
        End Sub

        Public ReadOnly Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
        End Property

        Public ReadOnly Property IsInPosition As Boolean
            Get
                Return _livePosition IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property CurrentTrailState As SlipStreamTrailState
            Get
                Return _livePosition?.TrailState
            End Get
        End Property

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            AddHandler _marketHub.QuoteReceived, _quoteHandler
            _timer = New Timer(AddressOf ScanCallback, Nothing,
                               TimeSpan.FromSeconds(ScanIntervalSeconds),
                               TimeSpan.FromSeconds(ScanIntervalSeconds))
            _logger?.LogInformation("SlipStreamOrchestrator started (disabled by default).")
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _timer?.Change(Timeout.Infinite, 0)
            RemoveHandler _marketHub.QuoteReceived, _quoteHandler
            Return Task.CompletedTask
        End Function

        Public Sub Enable()
            If _isEnabled Then Return
            _isEnabled = True
            _logger?.LogInformation("SlipStreamOrchestrator: enabled.")
            RaiseEvent EnabledChanged(Me, True)
        End Sub

        Public Sub Disable()
            If Not _isEnabled Then Return
            _isEnabled = False
            _logger?.LogInformation("SlipStreamOrchestrator: disabled.")
            ' If a position is open, flatten it — Disable() is a "stop now" intent.
            Dim slot As PositionSlot = Nothing
            SyncLock _livePositionLock
                slot = _livePosition?.Slot
            End SyncLock
            If slot IsNot Nothing Then
                Dim flatTask = CloseLivePositionAsync(slot, "Disabled")
            End If
            RaiseEvent EnabledChanged(Me, False)
        End Sub

        ' ─── Scan loop ──────────────────────────────────────────────────────────

        Private Async Sub ScanCallback(state As Object)
            If Not _isEnabled Then Return
            If Interlocked.Exchange(_scanInFlight, 1) = 1 Then Return
            Try
                Await ScanAllSymbolsAsync(CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "SlipStreamOrchestrator scan loop error")
            Finally
                Interlocked.Exchange(_scanInFlight, 0)
            End Try
        End Sub

        Private Async Function ScanAllSymbolsAsync(ct As CancellationToken) As Task
            Dim barsAvailable As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Using scope = _scopeFactory.CreateScope()
                Dim detector = scope.ServiceProvider.GetRequiredService(Of ISlipStreamSignalDetector)()
                Dim config = scope.ServiceProvider.GetRequiredService(Of SlipStreamConfig)()
                For Each symbol In WatchlistSymbols
                    If ct.IsCancellationRequested Then Exit For
                    Try
                        Dim eval = Await detector.EvaluateAsync(symbol, ct)
                        If eval IsNot Nothing Then barsAvailable(symbol) = eval.BarsAvailable
                        DispatchEvaluation(eval, config)
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "SlipStream evaluation failed for {Symbol}", symbol)
                    End Try
                Next
            End Using

            Try
                RaiseEvent ScanCompleted(Me, New ScannerScanCompletedEventArgs With {
                    .AsOfUtc = DateTime.UtcNow,
                    .BarsAvailable = barsAvailable
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "ScanCompleted handler threw")
            End Try
        End Function

        Private Sub DispatchEvaluation(eval As SlipStreamEvaluation, config As SlipStreamConfig)
            If eval Is Nothing Then Return

            Try
                RaiseEvent WatchlistTick(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistTick handler threw")
            End Try

            ' Forward-compat seam — config carries MaxConcurrentPositions but v1 clamps to 1.
            If config.MaxConcurrentPositions > 1 Then
                _logger?.LogWarning("SlipStream config MaxConcurrentPositions={N} clamped to 1 for v1",
                                    config.MaxConcurrentPositions)
            End If

            If eval.Signal = SlipStreamSignalSide.None Then Return

            ' Cooldown: suppress entries within (cooldownBars × signalTfMinutes) seconds of last exit.
            If _lastExitUtc <> DateTime.MinValue Then
                Dim cooldownSec = config.CooldownBars * 5 * 60  ' 5min × cooldown bars
                If (DateTime.UtcNow - _lastExitUtc).TotalSeconds < cooldownSec Then
                    Return
                End If
            End If

            ' De-dup: one fire per closed bar per symbol.
            Dim previousAsOf As DateTimeOffset
            _lastFiredAsOf.TryGetValue(eval.Symbol, previousAsOf)
            If eval.AsOf <= previousAsOf Then Return
            _lastFiredAsOf(eval.Symbol) = eval.AsOf

            _logger?.LogInformation(
                "SlipStream signal {Side} on {Symbol} @ {AsOf:o}: close={Close} ema21={Fast} ema200={Slow} rsi={Rsi:F1} adx={Adx:F1} atr={Atr}",
                eval.Signal, eval.Symbol, eval.AsOf, eval.LastClose, eval.EmaFast, eval.EmaSlow, eval.Rsi, eval.Adx, eval.Atr)

            Try
                RaiseEvent SignalDetected(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "SignalDetected handler threw")
            End Try

            ' Single-position cap — drop signals while a SlipStream position is open.
            If _livePosition IsNot Nothing Then
                _logger?.LogDebug("SlipStream signal dropped: live position already open ({Symbol})", _livePosition.Symbol)
                Return
            End If

            Dim fireAndForget = TryOpenPositionAsync(eval, config)
        End Sub

        ' ─── Live position lifecycle ────────────────────────────────────────────

        Private Async Function TryOpenPositionAsync(eval As SlipStreamEvaluation,
                                                     config As SlipStreamConfig) As Task
            ' Reserve the single-position slot atomically — second-tick races are dropped here.
            SyncLock _livePositionLock
                If _livePosition IsNot Nothing Then Return
                _livePosition = New SlipStreamLivePosition With {.Symbol = eval.Symbol, .Config = config}
            End SyncLock
            RaiseLivePositionChanged()

            Try
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(eval.Symbol)
                If contract Is Nothing Then
                    _logger?.LogWarning("SlipStream entry aborted: unknown contract {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If
                Dim account = _session?.SelectedAccount
                If account Is Nothing OrElse account.Id = 0 Then
                    _logger?.LogWarning("SlipStream entry aborted: no account selected")
                    ClearLivePosition() : Return
                End If

                Dim side = If(eval.Signal = SlipStreamSignalSide.Bullish, OrderSide.Buy, OrderSide.Sell)
                Dim sideStr = If(side = OrderSide.Buy, "Buy", "Sell")

                Dim atrLast As Decimal = eval.Atr
                Dim slMult As Decimal = CDec(config.EffectiveAtrSLmult(eval.Symbol))
                Dim stopDist As Decimal = atrLast * slMult
                If stopDist <= 0D Then
                    _logger?.LogWarning("SlipStream entry aborted: non-positive stop distance for {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If

                Dim initialStop As Decimal = ComputeRoundedStopPrice(eval.LastClose, side, stopDist, contract)

                ' Fractional-risk sizing: equity × riskPct / (stopDist × pointValue), floor, clamp ≥ 1.
                Dim equity As Decimal = account.Balance
                Dim riskCash As Decimal = equity * CDec(config.RiskPct / 100.0)
                Dim pointValue As Decimal = contract.PxTickValue / contract.PxTickSize  ' $ per 1 price point
                Dim contracts As Integer = 1
                If riskCash > 0D AndAlso pointValue > 0D Then
                    Dim raw = Math.Floor(CDbl(riskCash / (stopDist * pointValue)))
                    contracts = Math.Max(1, CInt(Math.Min(raw, 100)))  ' hard cap 100 as paranoia ceiling
                End If

                Dim slot As New PositionSlot With {
                    .SlotIndex = 0,
                    .Instrument = eval.Symbol,
                    .Side = sideStr,
                    .AccountId = account.Id,
                    .Contracts = contracts,
                    .IsOpen = False,
                    .EntryBarTime = eval.AsOf,
                    .EntryAtr = atrLast
                }
                SyncLock _livePositionLock
                    If _livePosition IsNot Nothing Then
                        _livePosition.PxContractId = contract.PxContractId
                        _livePosition.AccountId = account.Id
                        _livePosition.Slot = slot
                    End If
                End SyncLock

                Dim candidate As New EntryCandidate With {
                    .Side = side,
                    .StrategyName = "SlipStream",
                    .EntryReason = $"SlipStream {sideStr} RSI={eval.Rsi:F1} ADX={eval.Adx:F1} ATR={atrLast}",
                    .ReferencePrice = eval.LastClose,
                    .SuggestedInitialStopPrice = initialStop
                }
                Dim request As New EntryExecutionRequest With {
                    .Candidate = candidate,
                    .Slot = slot,
                    .AccountId = account.Id,
                    .ContractSymbol = eval.Symbol,
                    .LastClose = eval.LastClose,
                    .BarTime = eval.AsOf,
                    .StopReferencePrice = initialStop,
                    .StrategyName = "SlipStream",
                    .StrategyDisplayName = "SlipStream",
                    .ModelVersion = "SlipStream.v1",
                    .Persona = String.Empty,
                    .PersonaMinAdx = 0F,
                    .PersonaRrRatio = 0D,
                    .TimeframeLabel = "5min",
                    .TimeframeMinutes = 5,
                    .TimeframeForBars = BarTimeframe.FiveMinute,
                    .IsAiEnabled = False,
                    .DebugCaptureEnabled = False,
                    .StrategyConfigJson = String.Empty,
                    .EntryModeLabel = "BarClose",
                    .OnAiLogEntry = Sub(indicator As String, checkResult As String)
                                        ' AI veto disabled for v1 — no-op.
                                    End Sub,
                    .OnWatchlistAiStatus = Sub(contractSymbolArg As String, statusText As String)
                                               ' Watchlist updated by VM via WatchlistTick — no-op.
                                           End Sub,
                    .OnReleaseSlot = Sub(idx) OnSlotReleased(),
                    .OnSlotEntered = AddressOf OnSlotEntered,
                    .BandForAdx = Function(adx As Single) 0
                }

                Dim result = Await _entryExecution.PlaceAsync(request, CancellationToken.None)
                If Not result.Success Then
                    _logger?.LogInformation("SlipStream entry aborted by execution pipeline: {Reason}", result.AbortReason)
                    Return
                End If
            Catch ex As Exception
                _logger?.LogError(ex, "SlipStream entry pipeline threw")
                ClearLivePosition()
            End Try
        End Function

        ''' <summary>
        ''' Invoked by EntryExecutionService after a successful bracket placement. Initialise
        ''' the SlipStream trail state, compute TP1/SL targets, place a per-position SL via
        ''' EditPositionSlTpAsync (the entry path already placed a wide safety TP), then
        ''' subscribe to live quotes.
        ''' </summary>
        Private Sub OnSlotEntered(slot As PositionSlot)
            Dim pxContractId As String = Nothing
            Dim config As SlipStreamConfig = Nothing

            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument)
                If contract Is Nothing Then Return
                config = _livePosition.Config

                Dim entry As Decimal = slot.EntryPrice
                Dim atrLast As Decimal = slot.EntryAtr
                Dim side = If(slot.Side = "Buy", OrderSide.Buy, OrderSide.Sell)
                Dim slMult = CDec(config.EffectiveAtrSLmult(slot.Instrument))
                Dim tp1Mult = CDec(config.EffectiveAtrTP1mult(slot.Instrument))
                Dim trailMult = CDec(config.EffectiveTrailMult(slot.Instrument))
                Dim trailOffMult = CDec(config.EffectiveTrailOffsetMult(slot.Instrument))

                Dim sl = ComputeRoundedStopPrice(entry, side, atrLast * slMult, contract)
                Dim tp1 = ComputeRoundedTakeProfit(entry, side, atrLast * tp1Mult, contract)

                Dim trail As New SlipStreamTrailState With {
                    .Symbol = slot.Instrument,
                    .Side = side,
                    .EntryPrice = entry,
                    .EntryAtr = atrLast,
                    .TickSize = contract.PxTickSize,
                    .DollarsPerTick = contract.PxTickValue,
                    .InitialStopPrice = sl,
                    .Tp1Price = tp1,
                    .CurrentStopPrice = sl,
                    .PeakFavorablePrice = entry,
                    .Tp1Mult = tp1Mult,
                    .SlMult = slMult,
                    .TrailMult = trailMult,
                    .TrailOffsetMult = trailOffMult,
                    .EntryBarTime = slot.EntryBarTime
                }
                _livePosition.TrailState = trail
                _livePosition.Slot = slot
                _livePosition.PxContractId = contract.PxContractId
                pxContractId = contract.PxContractId
            End SyncLock
            RaiseLivePositionChanged()

            ' Place the strategy SL via EditPositionSlTpAsync (overrides the entry-path wide bracket).
            If slot.PositionId.HasValue AndAlso slot.PositionId.Value > 0 Then
                Dim pid = slot.PositionId.Value
                Dim newSl = _livePosition?.TrailState?.CurrentStopPrice
                If newSl.HasValue Then
                    Dim editTask = Task.Run(Async Function() As Task
                                                 Try
                                                     Await _orderService.EditPositionSlTpAsync(pid, newSl.Value, Nothing, False, CancellationToken.None)
                                                 Catch ex As Exception
                                                     _logger?.LogWarning(ex, "SlipStream initial SL placement failed (pid {Pid})", pid)
                                                 End Try
                                             End Function)
                End If
            End If

            ' Subscribe quotes off the entry callback thread.
            If Not String.IsNullOrEmpty(pxContractId) Then
                Dim subTask = Task.Run(Async Function()
                                            Try
                                                Await _marketHub.SubscribeContractAsync(pxContractId, CancellationToken.None)
                                            Catch ex As Exception
                                                _logger?.LogWarning(ex, "SlipStream quote subscribe failed for {Contract}", pxContractId)
                                            End Try
                                        End Function)
            End If
        End Sub

        Private Sub OnSlotReleased()
            ClearLivePosition()
        End Sub

        Private Sub ClearLivePosition()
            Dim contractIdToUnsub As String = Nothing
            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                contractIdToUnsub = _livePosition.PxContractId
                _livePosition = Nothing
            End SyncLock
            _lastExitUtc = DateTime.UtcNow
            RaiseLivePositionChanged()

            If Not String.IsNullOrEmpty(contractIdToUnsub) Then
                Dim unsubTask = Task.Run(Async Function()
                                              Try
                                                  Await _marketHub.UnsubscribeContractAsync(contractIdToUnsub, CancellationToken.None)
                                              Catch ex As Exception
                                                  _logger?.LogDebug(ex, "SlipStream quote unsubscribe failed for {Contract}", contractIdToUnsub)
                                              End Try
                                          End Function)
            End If
        End Sub

        Private Sub RaiseLivePositionChanged()
            Try
                RaiseEvent LivePositionChanged(Me, IsInPosition)
            Catch ex As Exception
                _logger?.LogDebug(ex, "LivePositionChanged handler threw")
            End Try
        End Sub

        ' ─── Quote handler (TP1 partial + runner trail + time stop + flat window) ──

        Private Sub OnQuoteReceived(sender As Object, args As MarketQuoteEventArgs)
            If args Is Nothing OrElse args.Quote Is Nothing Then Return
            Dim trail As SlipStreamTrailState = Nothing
            Dim positionId As Long? = Nothing
            Dim slot As PositionSlot = Nothing
            Dim config As SlipStreamConfig = Nothing
            Dim accountId As Long = 0
            Dim pxContractId As String = Nothing

            SyncLock _livePositionLock
                If _livePosition Is Nothing OrElse _livePosition.TrailState Is Nothing Then Return
                If Not String.Equals(_livePosition.PxContractId, args.Quote.ContractId, StringComparison.OrdinalIgnoreCase) Then Return
                trail = _livePosition.TrailState
                slot = _livePosition.Slot
                positionId = slot?.PositionId
                config = _livePosition.Config
                accountId = _livePosition.AccountId
                pxContractId = _livePosition.PxContractId
            End SyncLock

            Dim lastPrice = args.Quote.LastPrice
            If lastPrice <= 0D Then lastPrice = args.Quote.MidPrice
            If lastPrice <= 0D Then Return

            ' ── Update peak favorable price ────────────────────────────────────
            SyncLock _livePositionLock
                If trail.Side = OrderSide.Buy Then
                    If lastPrice > trail.PeakFavorablePrice Then trail.PeakFavorablePrice = lastPrice
                Else
                    If trail.PeakFavorablePrice = trail.EntryPrice OrElse lastPrice < trail.PeakFavorablePrice Then
                        trail.PeakFavorablePrice = lastPrice
                    End If
                End If
            End SyncLock

            ' ── Force-flat window override ────────────────────────────────────
            If config.UseSession AndAlso SlipStreamSignalDetector.IsTimestampInWindow(DateTimeOffset.UtcNow, config.FlatWindow) Then
                Dim flatTask = CloseLivePositionAsync(slot, "FlatWindow")
                Return
            End If

            ' ── Time stop ──────────────────────────────────────────────────────
            If config.MaxBarsInTrade > 0 Then
                Dim ageMinutes = (DateTimeOffset.UtcNow - trail.EntryBarTime).TotalMinutes
                Dim maxAgeMinutes = config.MaxBarsInTrade * 5  ' 5-min bars
                If ageMinutes >= maxAgeMinutes Then
                    Dim timeTask = CloseLivePositionAsync(slot, "TimeStop")
                    Return
                End If
            End If

            ' ── TP1 partial close (fires once) ────────────────────────────────
            If Not trail.Tp1Filled Then
                Dim tp1Hit As Boolean = False
                If trail.Side = OrderSide.Buy Then
                    tp1Hit = (lastPrice >= trail.Tp1Price)
                Else
                    tp1Hit = (lastPrice <= trail.Tp1Price)
                End If
                If tp1Hit AndAlso slot IsNot Nothing AndAlso slot.Contracts > 0 Then
                    Dim closeQty = CInt(Math.Floor(slot.Contracts * (config.Tp1Pct / 100.0)))
                    If closeQty < 1 Then closeQty = If(slot.Contracts > 1, 1, 0)
                    If closeQty > 0 AndAlso closeQty < slot.Contracts Then
                        SyncLock _livePositionLock
                            trail.Tp1Filled = True  ' set before await so duplicate quotes don't re-fire
                        End SyncLock
                        Dim partialTask = Task.Run(Async Function() As Task
                                                        Try
                                                            Dim ok = Await _orderService.PartialCloseContractAsync(accountId, pxContractId, closeQty, CancellationToken.None)
                                                            If ok Then
                                                                SyncLock _livePositionLock
                                                                    If _livePosition?.Slot IsNot Nothing Then
                                                                        _livePosition.Slot.Contracts -= closeQty
                                                                    End If
                                                                End SyncLock
                                                                _logger?.LogInformation("SlipStream TP1 partial closed {Qty} {Symbol} @ {Px}",
                                                                                        closeQty, trail.Symbol, lastPrice)
                                                            Else
                                                                _logger?.LogWarning("SlipStream TP1 PartialCloseContractAsync returned false ({Symbol})", trail.Symbol)
                                                            End If
                                                        Catch ex As Exception
                                                            _logger?.LogError(ex, "SlipStream TP1 partial close threw for {Symbol}", trail.Symbol)
                                                        End Try
                                                    End Function)
                    End If
                End If
            End If

            ' ── Runner trail (activates after trailOffsetMult × ATR favorable) ─
            Dim trailDistance = trail.EntryAtr * CDec(trail.TrailMult)
            Dim activationOffset = trail.EntryAtr * CDec(trail.TrailOffsetMult)
            Dim shouldArmTrail As Boolean = False
            If trail.Side = OrderSide.Buy Then
                shouldArmTrail = (trail.PeakFavorablePrice - trail.EntryPrice) >= activationOffset
            Else
                shouldArmTrail = (trail.EntryPrice - trail.PeakFavorablePrice) >= activationOffset
            End If

            If shouldArmTrail Then
                Dim proposedSl As Decimal
                If trail.Side = OrderSide.Buy Then
                    proposedSl = trail.PeakFavorablePrice - trailDistance
                Else
                    proposedSl = trail.PeakFavorablePrice + trailDistance
                End If
                ' Round to instrument tick away from entry (conservative).
                Dim ticks As Long = If(trail.Side = OrderSide.Buy,
                    CLng(Math.Floor(proposedSl / trail.TickSize)),
                    CLng(Math.Ceiling(proposedSl / trail.TickSize)))
                Dim rounded = CDec(ticks) * trail.TickSize

                ' Monotonic ratchet — never moves SL against the position.
                Dim shouldEdit As Boolean = False
                SyncLock _livePositionLock
                    If trail.Side = OrderSide.Buy AndAlso rounded > trail.CurrentStopPrice Then
                        Dim stepTicks = (rounded - trail.CurrentStopPrice) / trail.TickSize
                        If stepTicks >= config.MinSlEditStepTicks Then
                            trail.CurrentStopPrice = rounded
                            shouldEdit = True
                        End If
                    ElseIf trail.Side = OrderSide.Sell AndAlso rounded < trail.CurrentStopPrice Then
                        Dim stepTicks = (trail.CurrentStopPrice - rounded) / trail.TickSize
                        If stepTicks >= config.MinSlEditStepTicks Then
                            trail.CurrentStopPrice = rounded
                            shouldEdit = True
                        End If
                    End If
                End SyncLock

                If shouldEdit AndAlso positionId.HasValue Then
                    Dim pid = positionId.Value
                    Dim newSl = trail.CurrentStopPrice
                    Dim editTask = Task.Run(Async Function()
                                                 Try
                                                     Await _orderService.EditPositionSlTpAsync(pid, newSl, Nothing, False, CancellationToken.None)
                                                 Catch ex As Exception
                                                     _logger?.LogDebug(ex, "SlipStream trail SL edit failed (pid {Pid})", pid)
                                                 End Try
                                             End Function)
                End If
            End If

            ' ── UI snapshot ───────────────────────────────────────────────────
            Try
                RaiseEvent TrailUpdated(Me, New SlipStreamTrailSnapshot With {
                    .Symbol = trail.Symbol,
                    .Side = trail.Side,
                    .EntryPrice = trail.EntryPrice,
                    .CurrentStopPrice = trail.CurrentStopPrice,
                    .Tp1Price = trail.Tp1Price,
                    .Tp1Filled = trail.Tp1Filled,
                    .LastPrice = lastPrice,
                    .PeakFavorablePrice = trail.PeakFavorablePrice,
                    .TrailArmed = shouldArmTrail,
                    .TickSize = trail.TickSize,
                    .DollarsPerTick = trail.DollarsPerTick,
                    .CapturedUtc = DateTime.UtcNow
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "TrailUpdated handler threw")
            End Try
        End Sub

        Private Async Function CloseLivePositionAsync(slot As PositionSlot, reason As String) As Task
            If slot Is Nothing Then Return
            SyncLock _livePositionLock
                If _livePosition Is Nothing OrElse _livePosition.IsExiting Then Return
                _livePosition.IsExiting = True
            End SyncLock

            Try
                Await _exitExecution.CloseAsync(slot,
                                                exitReason:=If(String.IsNullOrEmpty(reason), "SlipStreamExit", reason),
                                                trigger:="QuoteEngine",
                                                timeframeMinutes:=5,
                                                ct:=CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "SlipStream exit failed for {Symbol}", slot.Instrument)
            Finally
                ClearLivePosition()
            End Try
        End Function

        ' ─── Helpers ────────────────────────────────────────────────────────────

        ''' <summary>Rounds the proposed stop price away from entry to the nearest tick.</summary>
        Private Shared Function ComputeRoundedStopPrice(referencePrice As Decimal,
                                                         side As OrderSide,
                                                         distance As Decimal,
                                                         contract As FavouriteContract) As Decimal
            Dim raw = If(side = OrderSide.Buy, referencePrice - distance, referencePrice + distance)
            Dim ticks = If(side = OrderSide.Buy, Math.Floor(raw / contract.PxTickSize), Math.Ceiling(raw / contract.PxTickSize))
            Return ticks * contract.PxTickSize
        End Function

        ''' <summary>Rounds the proposed TP price toward entry to the nearest tick (conservative).</summary>
        Private Shared Function ComputeRoundedTakeProfit(referencePrice As Decimal,
                                                          side As OrderSide,
                                                          distance As Decimal,
                                                          contract As FavouriteContract) As Decimal
            Dim raw = If(side = OrderSide.Buy, referencePrice + distance, referencePrice - distance)
            Dim ticks = If(side = OrderSide.Buy, Math.Floor(raw / contract.PxTickSize), Math.Ceiling(raw / contract.PxTickSize))
            Return ticks * contract.PxTickSize
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _timer?.Dispose()
            RemoveHandler _marketHub.QuoteReceived, _quoteHandler
        End Sub

        ' ─── Inner state ────────────────────────────────────────────────────────

        Private Class SlipStreamLivePosition
            Public Property Symbol As String = String.Empty
            Public Property PxContractId As String = String.Empty
            Public Property AccountId As Long
            Public Property Slot As PositionSlot
            Public Property TrailState As SlipStreamTrailState
            Public Property Config As SlipStreamConfig
            Public Property IsExiting As Boolean
        End Class

    End Class

    ''' <summary>FEAT-70: Mutable trail bookkeeping for a single live SlipStream position.</summary>
    Public Class SlipStreamTrailState
        Public Property Symbol As String = String.Empty
        Public Property Side As OrderSide
        Public Property EntryPrice As Decimal
        Public Property EntryAtr As Decimal
        Public Property TickSize As Decimal
        Public Property DollarsPerTick As Decimal
        Public Property InitialStopPrice As Decimal
        Public Property Tp1Price As Decimal
        Public Property CurrentStopPrice As Decimal
        Public Property PeakFavorablePrice As Decimal
        Public Property Tp1Filled As Boolean
        Public Property Tp1Mult As Decimal
        Public Property SlMult As Decimal
        Public Property TrailMult As Decimal
        Public Property TrailOffsetMult As Decimal
        Public Property EntryBarTime As DateTimeOffset
    End Class

    ''' <summary>FEAT-70: Immutable per-quote snapshot pushed to the UI via <c>TrailUpdated</c>.</summary>
    Public Class SlipStreamTrailSnapshot
        Public Property Symbol As String = String.Empty
        Public Property Side As OrderSide
        Public Property EntryPrice As Decimal
        Public Property CurrentStopPrice As Decimal
        Public Property Tp1Price As Decimal
        Public Property Tp1Filled As Boolean
        Public Property LastPrice As Decimal
        Public Property PeakFavorablePrice As Decimal
        Public Property TrailArmed As Boolean
        Public Property TickSize As Decimal
        Public Property DollarsPerTick As Decimal
        Public Property CapturedUtc As DateTime
    End Class

End Namespace
