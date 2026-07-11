Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Risk

Namespace TopStepTrader.Services.VwapMeanReversion

    ''' <summary>
    ''' FEAT-75: Singleton scanner-orchestrator for the VWAP Mean-Reversion strategy.
    ''' Mirrors <c>SlipStreamOrchestrator</c>:
    '''
    '''   • Scans the (adaptive) watchlist every <see cref="ScanIntervalSeconds"/> seconds
    '''     while <see cref="IsEnabled"/>; emits <c>WatchlistTick</c> + <c>SignalDetected</c>.
    '''   • Respects <c>ContractSessionHours.IsContractTradingNow</c> per symbol and gates
    '''     entries on <c>IDailyLossGuard.CanEnterNewTrade()</c> (FEAT-73).
    '''   • On a fade signal while flat, opens a single bracket position via
    '''     <see cref="IEntryExecutionService"/> (StrategyName = "VwapMeanReversion" so
    '''     OBS-08 reporting can attribute trades), then drives a quote-based exit policy:
    '''     half-close at T1 = VWAP, full close at T2, ATR trail on the runner after T1,
    '''     force-flat at <c>VwapMeanReversionConfig.ForceFlatUtc</c>.
    '''
    ''' Strict single-position guard: while <see cref="IsInPosition"/>, additional
    ''' signals are dropped (no DCA).
    ''' </summary>
    Public Class VwapMeanReversionOrchestrator
        Implements IHostedService, IDisposable, IOpenSlotInstrumentSource

        ''' <summary>Default watchlist when the FEAT-72 adaptive toggle is OFF.</summary>
        Public Shared ReadOnly WatchlistSymbols As IReadOnlyList(Of String) =
            New String() {"MES", "MNQ", "MGC"}

        ''' <summary>Scan cadence — 30s, identical to SlipStream (indicators are 5m-based).</summary>
        Private Const ScanIntervalSeconds As Integer = 30

        ''' <summary>Fraction of the position closed at T1 (the "half close" of STRAT-43 §3).</summary>
        Private Const T1ClosePct As Double = 50.0

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _entryExecution As IEntryExecutionService
        Private ReadOnly _exitExecution As IExitExecutionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _marketHub As IMarketQuoteFeed
        Private ReadOnly _logger As ILogger(Of VwapMeanReversionOrchestrator)
        Private ReadOnly _dailyLossGuard As IDailyLossGuard
        Private ReadOnly _adaptiveWatchlist As AdaptiveWatchlistService
        Private ReadOnly _combineSettings As CombineSettings
        Private ReadOnly _lastFiredAsOf As New ConcurrentDictionary(Of String, DateTimeOffset)()
        Private ReadOnly _livePositionLock As New Object()
        Private _livePosition As VwapMrLivePosition
        Private _quoteHandler As EventHandler(Of MarketQuoteEventArgs)
        Private _timer As Timer
        Private _scanInFlight As Integer
        Private _isEnabled As Boolean
        Private _lastExitUtc As DateTime = DateTime.MinValue

        ''' <summary>Injectable clock so tests can pin the session-hours + force-flat gates.</summary>
        Friend Property UtcNowProvider As Func(Of DateTime) = Function() DateTime.UtcNow

        Public Event WatchlistTick As EventHandler(Of VwapMeanReversionEvaluation)
        Public Event SignalDetected As EventHandler(Of VwapMeanReversionEvaluation)
        Public Event EnabledChanged As EventHandler(Of Boolean)
        Public Event LivePositionChanged As EventHandler(Of Boolean)
        Public Event TrailUpdated As EventHandler(Of VwapMrTrailSnapshot)
        Public Event ScanCompleted As EventHandler(Of ScannerScanCompletedEventArgs)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       session As ITradingSessionContext,
                       entryExecution As IEntryExecutionService,
                       exitExecution As IExitExecutionService,
                       orderService As IOrderService,
                       marketHub As IMarketQuoteFeed,
                       logger As ILogger(Of VwapMeanReversionOrchestrator),
                       Optional dailyLossGuard As IDailyLossGuard = Nothing,
                       Optional adaptiveWatchlist As AdaptiveWatchlistService = Nothing,
                       Optional combineOptions As IOptions(Of CombineSettings) = Nothing)
            _scopeFactory = scopeFactory
            _session = session
            _entryExecution = entryExecution
            _exitExecution = exitExecution
            _orderService = orderService
            _marketHub = marketHub
            _logger = logger
            _dailyLossGuard = dailyLossGuard
            _adaptiveWatchlist = adaptiveWatchlist
            _combineSettings = If(combineOptions?.Value, New CombineSettings())
            _dailyLossGuard?.RegisterOpenSlotPnlSource(New OrchestratorPnlSource(Function() GetLiveUnrealisedPnl(),
                                                                                  Function() IsInPosition))
            _adaptiveWatchlist?.RegisterOpenSlotSource(Me)
            _quoteHandler = AddressOf OnQuoteReceived
        End Sub

        ''' <summary>FEAT-72: pin the live position's symbol into the adaptive watchlist.</summary>
        Public Function GetOpenInstrumentRootSymbols() As IEnumerable(Of String) _
            Implements IOpenSlotInstrumentSource.GetOpenInstrumentRootSymbols
            Dim sym As String = Nothing
            SyncLock _livePositionLock
                If _livePosition IsNot Nothing Then sym = _livePosition.Symbol
            End SyncLock
            If String.IsNullOrEmpty(sym) Then Return Array.Empty(Of String)()
            Return New String() {sym}
        End Function

        Private Function GetActiveWatchlistSymbols() As IList(Of String)
            If _adaptiveWatchlist IsNot Nothing AndAlso _adaptiveWatchlist.IsEnabled Then
                Dim live = _adaptiveWatchlist.GetCurrentWatchlist()
                If live IsNot Nothing AndAlso live.Count > 0 Then
                    Return live.
                        Select(Function(c) c.PxRootSymbol).
                        Where(Function(s) Not String.IsNullOrEmpty(s)).
                        ToList()
                End If
            End If
            Return WatchlistSymbols.ToList()
        End Function

        ''' <summary>FEAT-71/73: unrealised PnL snapshot for the daily-loss guard. 0 when flat.</summary>
        Private Function GetLiveUnrealisedPnl() As Decimal
            SyncLock _livePositionLock
                Dim slot = _livePosition?.Slot
                If slot Is Nothing OrElse Not slot.IsOpen Then Return 0D
                Return slot.UnrealizedPnl
            End SyncLock
        End Function

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

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            AddHandler _marketHub.QuoteReceived, _quoteHandler
            _timer = New Timer(AddressOf ScanCallback, Nothing,
                               TimeSpan.FromSeconds(ScanIntervalSeconds),
                               TimeSpan.FromSeconds(ScanIntervalSeconds))
            _logger?.LogInformation("VwapMeanReversionOrchestrator started (disabled by default).")
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
            _logger?.LogInformation("VwapMeanReversionOrchestrator: enabled.")
            RaiseEvent EnabledChanged(Me, True)
        End Sub

        Public Sub Disable()
            If Not _isEnabled Then Return
            _isEnabled = False
            _logger?.LogInformation("VwapMeanReversionOrchestrator: disabled.")
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
                _logger?.LogError(ex, "VwapMeanReversionOrchestrator scan loop error")
            Finally
                Interlocked.Exchange(_scanInFlight, 0)
            End Try
        End Sub

        Friend Async Function ScanAllSymbolsAsync(ct As CancellationToken) As Task
            Dim barsAvailable As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Dim symbols = GetActiveWatchlistSymbols()
            Using scope = _scopeFactory.CreateScope()
                Dim detector = scope.ServiceProvider.GetRequiredService(Of IVwapMeanReversionSignalDetector)()
                Dim config = scope.ServiceProvider.GetRequiredService(Of VwapMeanReversionConfig)()
                For Each symbol In symbols
                    If ct.IsCancellationRequested Then Exit For

                    ' STRAT-42 F4: per-contract session-hours gate.
                    If Not ContractSessionHours.IsContractTradingNow(symbol, UtcNowProvider.Invoke()) Then
                        Dim opensAt = ContractSessionHours.NextOpenUtc(symbol, UtcNowProvider.Invoke())
                        _logger?.LogInformation(
                            "VwapMeanReversion [{Symbol}] contract closed — next session opens at {OpensAt:u}",
                            symbol, opensAt)
                        Continue For
                    End If

                    Try
                        Dim eval = Await detector.EvaluateAsync(symbol, ct)
                        If eval IsNot Nothing Then barsAvailable(symbol) = eval.BarsAvailable
                        DispatchEvaluation(eval, config)
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "VwapMeanReversion evaluation failed for {Symbol}", symbol)
                    End Try
                Next
            End Using

            Try
                RaiseEvent ScanCompleted(Me, New ScannerScanCompletedEventArgs With {
                    .AsOfUtc = UtcNowProvider.Invoke(),
                    .BarsAvailable = barsAvailable
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "ScanCompleted handler threw")
            End Try
        End Function

        Friend Sub DispatchEvaluation(eval As VwapMeanReversionEvaluation, config As VwapMeanReversionConfig)
            If eval Is Nothing Then Return

            Try
                RaiseEvent WatchlistTick(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistTick handler threw")
            End Try

            ' Forward-compat seam — config carries MaxConcurrentPositions but v1 clamps to 1.
            If config.MaxConcurrentPositions > 1 Then
                _logger?.LogWarning("VwapMeanReversion config MaxConcurrentPositions={N} clamped to 1 for v1",
                                    config.MaxConcurrentPositions)
            End If

            If eval.Signal = VwapMeanReversionSignalSide.None Then Return

            ' Cooldown: suppress entries within (cooldownBars × 5min) of the last exit.
            If _lastExitUtc <> DateTime.MinValue Then
                Dim cooldownSec = config.CooldownBars * 5 * 60
                If (UtcNowProvider.Invoke() - _lastExitUtc).TotalSeconds < cooldownSec Then
                    Return
                End If
            End If

            ' De-dup: one fire per closed bar per symbol.
            Dim previousAsOf As DateTimeOffset
            _lastFiredAsOf.TryGetValue(eval.Symbol, previousAsOf)
            If eval.AsOf <= previousAsOf Then Return
            _lastFiredAsOf(eval.Symbol) = eval.AsOf

            _logger?.LogInformation(
                "VwapMeanReversion signal {Side} on {Symbol} @ {AsOf:o}: close={Close} vwap={Vwap:F2} dev={Dev:F2}σ adx15={Adx:F1} atr={Atr}",
                eval.Signal, eval.Symbol, eval.AsOf, eval.LastClose, eval.Vwap, eval.DeviationSd, eval.Adx, eval.Atr)

            Try
                RaiseEvent SignalDetected(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "SignalDetected handler threw")
            End Try

            ' Single-position cap — drop signals while a position is open.
            If _livePosition IsNot Nothing Then
                _logger?.LogDebug("VwapMeanReversion signal dropped: live position already open ({Symbol})", _livePosition.Symbol)
                Return
            End If

            ' FEAT-71/73: daily-loss guard — suppress the entry instead of placing it.
            If _dailyLossGuard IsNot Nothing AndAlso Not _dailyLossGuard.CanEnterNewTrade() Then
                Dim guardState = _dailyLossGuard.GetState()
                _logger?.LogInformation(
                    "VwapMeanReversion Entry suppressed — DailyLossGuard halted: {Reason} (combined={Combined:F2}, limit={Limit:F2})",
                    guardState.Reason, guardState.CombinedDailyPnl, guardState.LimitDollars)
                Return
            End If

            Dim fireAndForget = TryOpenPositionAsync(eval, config)
        End Sub

        ' ─── Live position lifecycle ────────────────────────────────────────────

        Private Async Function TryOpenPositionAsync(eval As VwapMeanReversionEvaluation,
                                                     config As VwapMeanReversionConfig) As Task
            ' Reserve the single-position slot atomically — second-tick races are dropped here.
            SyncLock _livePositionLock
                If _livePosition IsNot Nothing Then Return
                _livePosition = New VwapMrLivePosition With {.Symbol = eval.Symbol, .Config = config}
            End SyncLock
            RaiseLivePositionChanged()

            Try
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(eval.Symbol)
                If contract Is Nothing Then
                    _logger?.LogWarning("VwapMeanReversion entry aborted: unknown contract {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If
                Dim account = _session?.SelectedAccount
                If account Is Nothing OrElse account.Id = 0 Then
                    _logger?.LogWarning("VwapMeanReversion entry aborted: no account selected")
                    ClearLivePosition() : Return
                End If

                Dim side = If(eval.Signal = VwapMeanReversionSignalSide.Bullish, OrderSide.Buy, OrderSide.Sell)
                Dim sideStr = If(side = OrderSide.Buy, "Buy", "Sell")

                Dim stopDist As Decimal = Math.Abs(eval.LastClose - eval.SuggestedInitialStopPrice)
                If stopDist <= 0D Then
                    _logger?.LogWarning("VwapMeanReversion entry aborted: non-positive stop distance for {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If
                Dim initialStop As Decimal = RoundStopAwayFromEntry(eval.SuggestedInitialStopPrice, side, contract)

                ' Fractional-risk sizing (combine profile values are STRAT-45 scope; only the
                ' MaxContracts hard cap is honoured here).
                Dim pointValue As Decimal = contract.PxTickValue / contract.PxTickSize
                Dim contracts As Integer = ComputeContracts(account.Balance, config.RiskPct,
                                                            stopDist, pointValue, _combineSettings)

                Dim slot As New PositionSlot With {
                    .SlotIndex = 0,
                    .Instrument = eval.Symbol,
                    .Side = sideStr,
                    .AccountId = account.Id,
                    .Contracts = contracts,
                    .IsOpen = False,
                    .EntryBarTime = eval.AsOf,
                    .EntryAtr = eval.Atr
                }
                SyncLock _livePositionLock
                    If _livePosition IsNot Nothing Then
                        _livePosition.PxContractId = contract.PxContractId
                        _livePosition.AccountId = account.Id
                        _livePosition.Slot = slot
                        _livePosition.PlannedT1 = eval.T1Price
                        _livePosition.PlannedT2 = eval.T2Price
                        _livePosition.PlannedStop = initialStop
                    End If
                End SyncLock

                Dim candidate As New EntryCandidate With {
                    .Side = side,
                    .StrategyName = "VwapMeanReversion",
                    .EntryReason = $"VwapMR {sideStr} dev={eval.DeviationSd:F2}σ adx15={eval.Adx:F1} {eval.ConfirmationPattern}",
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
                    .StrategyName = "VwapMeanReversion",
                    .StrategyDisplayName = "VWAP Mean-Reversion",
                    .ModelVersion = "VwapMeanReversion.v1",
                    .Persona = config.ActivePersona,
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
                    _logger?.LogInformation("VwapMeanReversion entry aborted by execution pipeline: {Reason}", result.AbortReason)
                    Return
                End If
            Catch ex As Exception
                _logger?.LogError(ex, "VwapMeanReversion entry pipeline threw")
                ClearLivePosition()
            End Try
        End Function

        ''' <summary>
        ''' Invoked by EntryExecutionService after a successful bracket placement. Initialise
        ''' the trail state from the planned T1/T2/SL, place the strategy SL via
        ''' EditPositionSlTpAsync, then subscribe to live quotes.
        ''' </summary>
        Private Sub OnSlotEntered(slot As PositionSlot)
            Dim pxContractId As String = Nothing

            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument)
                If contract Is Nothing Then Return
                Dim config = _livePosition.Config

                Dim side = If(slot.Side = "Buy", OrderSide.Buy, OrderSide.Sell)
                Dim trail As New VwapMrTrailState With {
                    .Symbol = slot.Instrument,
                    .Side = side,
                    .EntryPrice = slot.EntryPrice,
                    .EntryAtr = slot.EntryAtr,
                    .TickSize = contract.PxTickSize,
                    .DollarsPerTick = contract.PxTickValue,
                    .InitialStopPrice = _livePosition.PlannedStop,
                    .CurrentStopPrice = _livePosition.PlannedStop,
                    .T1Price = _livePosition.PlannedT1,
                    .T2Price = _livePosition.PlannedT2,
                    .PeakFavorablePrice = slot.EntryPrice,
                    .TrailAtrMult = CDec(config.TrailAtrMult),
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
                                                     _logger?.LogWarning(ex, "VwapMeanReversion initial SL placement failed (pid {Pid})", pid)
                                                 End Try
                                             End Function)
                End If
            End If

            If Not String.IsNullOrEmpty(pxContractId) Then
                Dim subTask = Task.Run(Async Function()
                                            Try
                                                Await _marketHub.SubscribeContractAsync(pxContractId, CancellationToken.None)
                                            Catch ex As Exception
                                                _logger?.LogWarning(ex, "VwapMeanReversion quote subscribe failed for {Contract}", pxContractId)
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
            _lastExitUtc = UtcNowProvider.Invoke()
            RaiseLivePositionChanged()

            If Not String.IsNullOrEmpty(contractIdToUnsub) Then
                Dim unsubTask = Task.Run(Async Function()
                                              Try
                                                  Await _marketHub.UnsubscribeContractAsync(contractIdToUnsub, CancellationToken.None)
                                              Catch ex As Exception
                                                  _logger?.LogDebug(ex, "VwapMeanReversion quote unsubscribe failed for {Contract}", contractIdToUnsub)
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

        ' ─── Quote handler (T1 half-close + T2 target + ATR trail + force-flat) ──

        Private Sub OnQuoteReceived(sender As Object, args As MarketQuoteEventArgs)
            If args Is Nothing OrElse args.Quote Is Nothing Then Return
            Dim trail As VwapMrTrailState = Nothing
            Dim positionId As Long? = Nothing
            Dim slot As PositionSlot = Nothing
            Dim config As VwapMeanReversionConfig = Nothing
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

            ' ── Force-flat before the 21:10 UTC close ──────────────────────────
            If IsAtOrPastForceFlat(UtcNowProvider.Invoke(), config.ForceFlatUtc) Then
                Dim flatTask = CloseLivePositionAsync(slot, "ForceFlat")
                Return
            End If

            ' ── T2 target: full close of the runner ────────────────────────────
            Dim t2Hit As Boolean = If(trail.Side = OrderSide.Buy,
                                      lastPrice >= trail.T2Price,
                                      lastPrice <= trail.T2Price)
            If t2Hit Then
                Dim t2Task = CloseLivePositionAsync(slot, "T2Target")
                Return
            End If

            ' ── T1 half-close at VWAP (fires once) ─────────────────────────────
            If Not trail.T1Filled Then
                Dim t1Hit As Boolean = If(trail.Side = OrderSide.Buy,
                                          lastPrice >= trail.T1Price,
                                          lastPrice <= trail.T1Price)
                If t1Hit AndAlso slot IsNot Nothing AndAlso slot.Contracts > 0 Then
                    Dim closeQty = CInt(Math.Floor(slot.Contracts * (T1ClosePct / 100.0)))
                    If closeQty < 1 Then closeQty = If(slot.Contracts > 1, 1, 0)
                    If closeQty > 0 AndAlso closeQty < slot.Contracts Then
                        SyncLock _livePositionLock
                            trail.T1Filled = True  ' set before await so duplicate quotes don't re-fire
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
                                                                _logger?.LogInformation("VwapMeanReversion T1 half-closed {Qty} {Symbol} @ {Px} (VWAP target)",
                                                                                        closeQty, trail.Symbol, lastPrice)
                                                            Else
                                                                _logger?.LogWarning("VwapMeanReversion T1 PartialCloseContractAsync returned false ({Symbol})", trail.Symbol)
                                                            End If
                                                        Catch ex As Exception
                                                            _logger?.LogError(ex, "VwapMeanReversion T1 partial close threw for {Symbol}", trail.Symbol)
                                                        End Try
                                                    End Function)
                    ElseIf slot.Contracts = 1 Then
                        ' Single-contract position: T1 degenerates to a full close at VWAP.
                        SyncLock _livePositionLock
                            trail.T1Filled = True
                        End SyncLock
                        Dim fullTask = CloseLivePositionAsync(slot, "T1Target")
                        Return
                    End If
                End If
            End If

            ' ── ATR trail on the runner — armed only after T1 fills ────────────
            If trail.T1Filled Then
                Dim trailDistance = trail.EntryAtr * trail.TrailAtrMult
                If trailDistance > 0D Then
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
                                                         _logger?.LogDebug(ex, "VwapMeanReversion trail SL edit failed (pid {Pid})", pid)
                                                     End Try
                                                 End Function)
                    End If
                End If
            End If

            ' ── UI snapshot ───────────────────────────────────────────────────
            Try
                RaiseEvent TrailUpdated(Me, New VwapMrTrailSnapshot With {
                    .Symbol = trail.Symbol,
                    .Side = trail.Side,
                    .EntryPrice = trail.EntryPrice,
                    .CurrentStopPrice = trail.CurrentStopPrice,
                    .T1Price = trail.T1Price,
                    .T2Price = trail.T2Price,
                    .T1Filled = trail.T1Filled,
                    .LastPrice = lastPrice,
                    .PeakFavorablePrice = trail.PeakFavorablePrice,
                    .TrailArmed = trail.T1Filled,
                    .TickSize = trail.TickSize,
                    .DollarsPerTick = trail.DollarsPerTick,
                    .CapturedUtc = UtcNowProvider.Invoke()
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
                                                exitReason:=If(String.IsNullOrEmpty(reason), "VwapMeanReversionExit", reason),
                                                trigger:="QuoteEngine",
                                                timeframeMinutes:=5,
                                                ct:=CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "VwapMeanReversion exit failed for {Symbol}", slot.Instrument)
            Finally
                ClearLivePosition()
            End Try
        End Function

        ' ─── Helpers ────────────────────────────────────────────────────────────

        ''' <summary>
        ''' True when <paramref name="utcNow"/> is at/after the "HHmm" UTC force-flat time
        ''' and before the 22:00 UTC session reopen. Malformed config disables the check.
        ''' </summary>
        Friend Shared Function IsAtOrPastForceFlat(utcNow As DateTime, forceFlatUtc As String) As Boolean
            If String.IsNullOrWhiteSpace(forceFlatUtc) Then Return False
            Dim hm As Integer
            If Not Integer.TryParse(forceFlatUtc.Trim(), hm) Then Return False
            Dim flat As New TimeSpan(hm \ 100, hm Mod 100, 0)
            Return utcNow.TimeOfDay >= flat AndAlso utcNow.TimeOfDay < VwapMeanReversionConfig.SessionReopenUtc
        End Function

        ''' <summary>
        ''' Fractional-risk quantity — floor(equity × riskPct% / (stopDist × pointValue)),
        ''' clamped ≥ 1 and to the hard cap (100, or <c>CombineSettings.MaxContracts</c>
        ''' when combine mode is on). Mirrors <c>SlipStreamOrchestrator.ComputeContracts</c>.
        ''' </summary>
        Friend Shared Function ComputeContracts(equity As Decimal,
                                                 riskPct As Double,
                                                 stopDist As Decimal,
                                                 pointValue As Decimal,
                                                 combine As CombineSettings) As Integer
            Dim hardCap As Integer = 100
            If combine IsNot Nothing AndAlso combine.Enabled AndAlso combine.MaxContracts > 0 Then
                hardCap = Math.Min(hardCap, combine.MaxContracts)
            End If
            Dim contracts As Integer = 1
            Dim riskCash As Decimal = equity * CDec(riskPct / 100.0)
            If riskCash > 0D AndAlso pointValue > 0D AndAlso stopDist > 0D Then
                Dim raw = Math.Floor(CDbl(riskCash / (stopDist * pointValue)))
                contracts = Math.Max(1, CInt(Math.Min(raw, hardCap)))
            End If
            Return contracts
        End Function

        ''' <summary>Rounds the strategy stop away from entry to the nearest tick.</summary>
        Private Shared Function RoundStopAwayFromEntry(stopPrice As Decimal,
                                                        side As OrderSide,
                                                        contract As FavouriteContract) As Decimal
            Dim ticks = If(side = OrderSide.Buy,
                           Math.Floor(stopPrice / contract.PxTickSize),
                           Math.Ceiling(stopPrice / contract.PxTickSize))
            Return ticks * contract.PxTickSize
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _timer?.Dispose()
            RemoveHandler _marketHub.QuoteReceived, _quoteHandler
        End Sub

        ' ─── Inner state ────────────────────────────────────────────────────────

        Private Class VwapMrLivePosition
            Public Property Symbol As String = String.Empty
            Public Property PxContractId As String = String.Empty
            Public Property AccountId As Long
            Public Property Slot As PositionSlot
            Public Property TrailState As VwapMrTrailState
            Public Property Config As VwapMeanReversionConfig
            Public Property PlannedT1 As Decimal
            Public Property PlannedT2 As Decimal
            Public Property PlannedStop As Decimal
            Public Property IsExiting As Boolean
        End Class

    End Class

    ''' <summary>FEAT-75: Mutable trail bookkeeping for a single live VWAP-MR position.</summary>
    Public Class VwapMrTrailState
        Public Property Symbol As String = String.Empty
        Public Property Side As OrderSide
        Public Property EntryPrice As Decimal
        Public Property EntryAtr As Decimal
        Public Property TickSize As Decimal
        Public Property DollarsPerTick As Decimal
        Public Property InitialStopPrice As Decimal
        Public Property CurrentStopPrice As Decimal
        Public Property T1Price As Decimal
        Public Property T2Price As Decimal
        Public Property T1Filled As Boolean
        Public Property PeakFavorablePrice As Decimal
        Public Property TrailAtrMult As Decimal
        Public Property EntryBarTime As DateTimeOffset
    End Class

    ''' <summary>FEAT-75: Immutable per-quote snapshot pushed to the UI via <c>TrailUpdated</c>.</summary>
    Public Class VwapMrTrailSnapshot
        Public Property Symbol As String = String.Empty
        Public Property Side As OrderSide
        Public Property EntryPrice As Decimal
        Public Property CurrentStopPrice As Decimal
        Public Property T1Price As Decimal
        Public Property T2Price As Decimal
        Public Property T1Filled As Boolean
        Public Property LastPrice As Decimal
        Public Property PeakFavorablePrice As Decimal
        Public Property TrailArmed As Boolean
        Public Property TickSize As Decimal
        Public Property DollarsPerTick As Decimal
        Public Property CapturedUtc As DateTime
    End Class

End Namespace
