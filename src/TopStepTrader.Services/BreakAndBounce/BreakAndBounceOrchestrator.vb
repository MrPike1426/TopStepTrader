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
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Risk

Namespace TopStepTrader.Services.BreakAndBounce

    ''' <summary>
    ''' FEAT-62: Singleton scanner-orchestrator for the Break and Bounce strategy.
    '''
    ''' Scans MES/MNQ/MGC every <see cref="ScanIntervalSeconds"/> seconds while
    ''' <see cref="IsEnabled"/> is True; emits <c>WatchlistTick</c> + <c>SignalDetected</c>
    ''' so the VM can drive UI updates. On a confluence signal while flat opens
    ''' a single live position via <see cref="IEntryExecutionService"/> using the
    ''' detector's SuggestedInitialStopPrice. No fixed take-profit is placed.
    '''
    ''' Exit policy (matches SlipStream's structural shape, no fixed 3R target):
    '''   • Break-even ratchet once price moves <c>1.0×</c> initial-SL distance favorably.
    '''   • Force-flat once the contract's bar timestamp enters <see cref="BreakAndBounceConfig.FlatWindow"/>.
    '''   • Exit via <see cref="IExitExecutionService"/>.
    '''
    ''' Single-position guard: while <see cref="IsInPosition"/>, additional signals
    ''' are silently dropped (no DCA). Matches the Q5 PDF-faithful sizing decision.
    ''' </summary>
    Public Class BreakAndBounceOrchestrator
        Implements IHostedService, IDisposable, IOpenSlotInstrumentSource

        ''' <summary>
        ''' Default watchlist when the FEAT-72 adaptive toggle is OFF — same equity-futures
        ''' micros + MGC as SlipStream/UltimateScalper. When the toggle is ON, the orchestrator
        ''' iterates <see cref="AdaptiveWatchlistService.GetCurrentWatchlist"/> instead.
        ''' </summary>
        Public Shared ReadOnly WatchlistSymbols As IReadOnlyList(Of String) =
            New String() {"MES", "MNQ", "MGC"}

        Private Const ScanIntervalSeconds As Integer = 30

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _entryExecution As IEntryExecutionService
        Private ReadOnly _exitExecution As IExitExecutionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _marketHub As IMarketQuoteFeed
        Private ReadOnly _logger As ILogger(Of BreakAndBounceOrchestrator)
        Private ReadOnly _dailyLossGuard As IDailyLossGuard
        Private ReadOnly _adaptiveWatchlist As AdaptiveWatchlistService
        Private ReadOnly _lastFiredAsOf As New ConcurrentDictionary(Of String, DateTimeOffset)()
        Private ReadOnly _livePositionLock As New Object()
        Private _livePosition As LivePositionState
        Private _quoteHandler As EventHandler(Of MarketQuoteEventArgs)
        Private _timer As Timer
        Private _scanInFlight As Integer
        Private _isEnabled As Boolean

        Public Event WatchlistTick As EventHandler(Of BreakAndBounceEvaluation)
        Public Event SignalDetected As EventHandler(Of BreakAndBounceEvaluation)
        Public Event EnabledChanged As EventHandler(Of Boolean)
        Public Event LivePositionChanged As EventHandler(Of Boolean)
        Public Event StopRatcheted As EventHandler(Of BreakAndBounceStopSnapshot)
        Public Event ScanCompleted As EventHandler(Of DateTime)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       session As ITradingSessionContext,
                       entryExecution As IEntryExecutionService,
                       exitExecution As IExitExecutionService,
                       orderService As IOrderService,
                       marketHub As IMarketQuoteFeed,
                       logger As ILogger(Of BreakAndBounceOrchestrator),
                       Optional dailyLossGuard As IDailyLossGuard = Nothing,
                       Optional adaptiveWatchlist As AdaptiveWatchlistService = Nothing)
            _scopeFactory = scopeFactory
            _session = session
            _entryExecution = entryExecution
            _exitExecution = exitExecution
            _orderService = orderService
            _marketHub = marketHub
            _logger = logger
            _dailyLossGuard = dailyLossGuard
            _adaptiveWatchlist = adaptiveWatchlist
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

        ''' <summary>FEAT-72: returns the adaptive watchlist symbols when the toggle is ON,
        ''' otherwise the static <see cref="WatchlistSymbols"/> default.</summary>
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

        ''' <summary>FEAT-71: snapshot of the open position's unrealised PnL for the daily-loss
        ''' guard. Returns 0 when flat. Reads under the same lock used by the entry/exit
        ''' pipelines so a half-published state cannot leak.</summary>
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
            _logger?.LogInformation("BreakAndBounceOrchestrator started (disabled by default).")
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
            _logger?.LogInformation("BreakAndBounceOrchestrator: enabled.")
            RaiseEvent EnabledChanged(Me, True)
        End Sub

        Public Sub Disable()
            If Not _isEnabled Then Return
            _isEnabled = False
            _logger?.LogInformation("BreakAndBounceOrchestrator: disabled.")
            Dim slot As PositionSlot = Nothing
            SyncLock _livePositionLock
                slot = _livePosition?.Slot
            End SyncLock
            If slot IsNot Nothing Then
                Dim flatTask = CloseLivePositionAsync(slot, "Disabled")
            End If
            RaiseEvent EnabledChanged(Me, False)
        End Sub

        ' ─── Scan loop ──────────────────────────────────────────────────────

        Private Async Sub ScanCallback(state As Object)
            If Not _isEnabled Then Return
            If Interlocked.Exchange(_scanInFlight, 1) = 1 Then Return
            Try
                Await ScanAllSymbolsAsync(CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "BreakAndBounceOrchestrator scan loop error")
            Finally
                Interlocked.Exchange(_scanInFlight, 0)
            End Try
        End Sub

        Private Async Function ScanAllSymbolsAsync(ct As CancellationToken) As Task
            Dim symbols = GetActiveWatchlistSymbols()
            Using scope = _scopeFactory.CreateScope()
                Dim detector = scope.ServiceProvider.GetRequiredService(Of IBreakAndBounceSignalDetector)()
                Dim config = scope.ServiceProvider.GetRequiredService(Of BreakAndBounceConfig)()
                For Each symbol In symbols
                    If ct.IsCancellationRequested Then Exit For

                    ' STRAT-42 F4: per-contract session-hours gate.
                    If Not ContractSessionHours.IsContractTradingNow(symbol, DateTime.UtcNow) Then
                        Dim opensAt = ContractSessionHours.NextOpenUtc(symbol, DateTime.UtcNow)
                        _logger?.LogInformation(
                            "BreakAndBounce [{Symbol}] contract closed — next session opens at {OpensAt:u}",
                            symbol, opensAt)
                        Continue For
                    End If

                    Try
                        Dim eval = Await detector.EvaluateAsync(symbol, ct)
                        DispatchEvaluation(eval, config)
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "BreakAndBounce evaluation failed for {Symbol}", symbol)
                    End Try
                Next
            End Using

            Try
                RaiseEvent ScanCompleted(Me, DateTime.UtcNow)
            Catch ex As Exception
                _logger?.LogDebug(ex, "ScanCompleted handler threw")
            End Try
        End Function

        Private Sub DispatchEvaluation(eval As BreakAndBounceEvaluation, config As BreakAndBounceConfig)
            If eval Is Nothing Then Return

            Try
                RaiseEvent WatchlistTick(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistTick handler threw")
            End Try

            ' Force-flat window dominates everything else for the live position.
            If eval.InFlatWindow AndAlso _livePosition IsNot Nothing AndAlso
               String.Equals(_livePosition.Symbol, eval.Symbol, StringComparison.OrdinalIgnoreCase) Then
                Dim flatTask = CloseLivePositionAsync(_livePosition.Slot, "FlatWindow")
                Return
            End If

            If eval.Signal = BreakAndBounceSignalSide.None Then Return

            ' De-dup: at most one fire per closed retest bar per symbol.
            Dim previousAsOf As DateTimeOffset
            _lastFiredAsOf.TryGetValue(eval.Symbol, previousAsOf)
            If eval.AsOf <= previousAsOf Then Return
            _lastFiredAsOf(eval.Symbol) = eval.AsOf

            _logger?.LogInformation(
                "BreakAndBounce {Side} on {Symbol} @ {AsOf:o}: close={Close} dir={Dir} pattern={Pat} stop={Stop}",
                eval.Signal, eval.Symbol, eval.AsOf, eval.LastFiveClose, eval.Direction,
                eval.PatternHit, eval.SuggestedInitialStopPrice)

            Try
                RaiseEvent SignalDetected(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "SignalDetected handler threw")
            End Try

            If _livePosition IsNot Nothing Then
                _logger?.LogDebug("BreakAndBounce signal dropped: position already open ({Symbol})", _livePosition.Symbol)
                Return
            End If

            ' FEAT-71: hard daily-loss kill switch. Suppress the entry instead of placing it.
            If _dailyLossGuard IsNot Nothing AndAlso Not _dailyLossGuard.CanEnterNewTrade() Then
                Dim guardState = _dailyLossGuard.GetState()
                _logger?.LogInformation(
                    "BreakAndBounce Entry suppressed — DailyLossGuard halted: {Reason} (combined={Combined:F2}, limit={Limit:F2})",
                    guardState.Reason, guardState.CombinedDailyPnl, guardState.LimitDollars)
                Return
            End If

            Dim fireAndForget = TryOpenPositionAsync(eval, config)
        End Sub

        ' ─── Live position lifecycle ────────────────────────────────────────

        Private Async Function TryOpenPositionAsync(eval As BreakAndBounceEvaluation,
                                                     config As BreakAndBounceConfig) As Task
            SyncLock _livePositionLock
                If _livePosition IsNot Nothing Then Return
                _livePosition = New LivePositionState With {.Symbol = eval.Symbol, .Config = config}
            End SyncLock
            RaiseLivePositionChanged()

            Try
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(eval.Symbol)
                If contract Is Nothing Then
                    _logger?.LogWarning("BreakAndBounce entry aborted: unknown contract {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If
                Dim account = _session?.SelectedAccount
                If account Is Nothing OrElse account.Id = 0 Then
                    _logger?.LogWarning("BreakAndBounce entry aborted: no account selected")
                    ClearLivePosition() : Return
                End If

                Dim side = If(eval.Signal = BreakAndBounceSignalSide.Bullish, OrderSide.Buy, OrderSide.Sell)
                Dim sideStr = If(side = OrderSide.Buy, "Buy", "Sell")

                Dim slot As New PositionSlot With {
                    .SlotIndex = 0,
                    .Instrument = eval.Symbol,
                    .Side = sideStr,
                    .AccountId = account.Id,
                    .Contracts = Math.Max(1, config.ContractsPerEntry),
                    .IsOpen = False,
                    .EntryBarTime = eval.AsOf,
                    .EntryAtr = eval.Atr
                }
                SyncLock _livePositionLock
                    If _livePosition IsNot Nothing Then
                        _livePosition.PxContractId = contract.PxContractId
                        _livePosition.AccountId = account.Id
                        _livePosition.Slot = slot
                        _livePosition.InitialStopPrice = eval.SuggestedInitialStopPrice
                    End If
                End SyncLock

                Dim candidate As New EntryCandidate With {
                    .Side = side,
                    .StrategyName = "BreakAndBounce",
                    .EntryReason = $"BreakAndBounce {sideStr} {eval.PatternHit} dir={eval.Direction} stopFloor={eval.StopFloorSource}",
                    .ReferencePrice = eval.LastFiveClose,
                    .SuggestedInitialStopPrice = eval.SuggestedInitialStopPrice
                }
                Dim request As New EntryExecutionRequest With {
                    .Candidate = candidate,
                    .Slot = slot,
                    .AccountId = account.Id,
                    .ContractSymbol = eval.Symbol,
                    .LastClose = eval.LastFiveClose,
                    .BarTime = eval.AsOf,
                    .StopReferencePrice = eval.SuggestedInitialStopPrice,
                    .StrategyName = "BreakAndBounce",
                    .StrategyDisplayName = "Break and Bounce",
                    .ModelVersion = "BreakAndBounce.v1",
                    .Persona = String.Empty,
                    .PersonaMinAdx = 0F,
                    .PersonaRrRatio = 0D,
                    .TimeframeLabel = config.RetestTimeframe,
                    .TimeframeMinutes = 5,
                    .TimeframeForBars = BarTimeframe.FiveMinute,
                    .IsAiEnabled = config.AiVetoEnabled,
                    .DebugCaptureEnabled = False,
                    .StrategyConfigJson = String.Empty,
                    .EntryModeLabel = "BarClose",
                    .OnAiLogEntry = Sub(indicator As String, checkResult As String)
                                        ' AI log surfaced via signal events; no-op here.
                                    End Sub,
                    .OnWatchlistAiStatus = Sub(contractSymbolArg As String, statusText As String)
                                               ' Watchlist drives status from WatchlistTick; no-op.
                                           End Sub,
                    .OnReleaseSlot = Sub(idx) OnSlotReleased(),
                    .OnSlotEntered = AddressOf OnSlotEntered,
                    .BandForAdx = Function(adx As Single) 0
                }

                Dim result = Await _entryExecution.PlaceAsync(request, CancellationToken.None)
                If Not result.Success Then
                    _logger?.LogInformation("BreakAndBounce entry aborted by pipeline: {Reason}", result.AbortReason)
                    Return
                End If
            Catch ex As Exception
                _logger?.LogError(ex, "BreakAndBounce entry pipeline threw")
                ClearLivePosition()
            End Try
        End Function

        ''' <summary>
        ''' Invoked by EntryExecutionService after the bracket is accepted. Subscribes
        ''' to the contract's quote stream so the per-quote break-even ratchet engages.
        ''' </summary>
        Private Sub OnSlotEntered(slot As PositionSlot)
            Dim pxContractId As String = Nothing
            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                _livePosition.Slot = slot
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument)
                If contract IsNot Nothing Then
                    _livePosition.PxContractId = contract.PxContractId
                    _livePosition.TickSize = contract.PxTickSize
                End If
                _livePosition.EntryPrice = slot.EntryPrice
                _livePosition.PeakFavorablePrice = slot.EntryPrice
                _livePosition.CurrentStopPrice = If(_livePosition.InitialStopPrice <> 0D, _livePosition.InitialStopPrice, slot.StopPrice)
                pxContractId = _livePosition.PxContractId
            End SyncLock
            RaiseLivePositionChanged()

            If Not String.IsNullOrEmpty(pxContractId) Then
                Dim subTask = Task.Run(Async Function()
                                            Try
                                                Await _marketHub.SubscribeContractAsync(pxContractId, CancellationToken.None)
                                            Catch ex As Exception
                                                _logger?.LogWarning(ex, "BreakAndBounce quote subscribe failed for {Contract}", pxContractId)
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
            RaiseLivePositionChanged()

            If Not String.IsNullOrEmpty(contractIdToUnsub) Then
                Dim unsubTask = Task.Run(Async Function()
                                              Try
                                                  Await _marketHub.UnsubscribeContractAsync(contractIdToUnsub, CancellationToken.None)
                                              Catch ex As Exception
                                                  _logger?.LogDebug(ex, "BreakAndBounce quote unsubscribe failed for {Contract}", contractIdToUnsub)
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

        ' ─── Quote handler — break-even ratchet ────────────────────────────

        Private Sub OnQuoteReceived(sender As Object, args As MarketQuoteEventArgs)
            If args Is Nothing OrElse args.Quote Is Nothing Then Return
            Dim positionId As Long? = Nothing
            Dim slot As PositionSlot = Nothing
            Dim side As OrderSide
            Dim entry As Decimal, initialStop As Decimal
            Dim currentStop As Decimal, tickSize As Decimal
            Dim symbol As String = Nothing

            SyncLock _livePositionLock
                If _livePosition Is Nothing OrElse _livePosition.Slot Is Nothing Then Return
                If Not String.Equals(_livePosition.PxContractId, args.Quote.ContractId, StringComparison.OrdinalIgnoreCase) Then Return
                slot = _livePosition.Slot
                positionId = slot?.PositionId
                side = If(slot.Side = "Buy", OrderSide.Buy, OrderSide.Sell)
                entry = _livePosition.EntryPrice
                initialStop = _livePosition.InitialStopPrice
                currentStop = _livePosition.CurrentStopPrice
                tickSize = _livePosition.TickSize
                symbol = _livePosition.Symbol
            End SyncLock

            Dim lastPrice = args.Quote.LastPrice
            If lastPrice <= 0D Then lastPrice = args.Quote.MidPrice
            If lastPrice <= 0D Then Return

            ' Update peak favorable
            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                If side = OrderSide.Buy Then
                    If lastPrice > _livePosition.PeakFavorablePrice Then _livePosition.PeakFavorablePrice = lastPrice
                Else
                    If _livePosition.PeakFavorablePrice = _livePosition.EntryPrice OrElse lastPrice < _livePosition.PeakFavorablePrice Then
                        _livePosition.PeakFavorablePrice = lastPrice
                    End If
                End If
            End SyncLock

            ' Break-even ratchet: once price has moved one initial-SL distance in our
            ' favor, advance SL to entry. Strictly monotonic — never moves against us.
            If initialStop = 0D OrElse entry = 0D Then Return
            Dim slDistance As Decimal = Math.Abs(entry - initialStop)
            If slDistance <= 0D Then Return

            Dim shouldRatchet As Boolean = False
            Dim newStop As Decimal = currentStop
            If side = OrderSide.Buy Then
                If lastPrice - entry >= slDistance AndAlso currentStop < entry Then
                    newStop = entry
                    shouldRatchet = True
                End If
            Else
                If entry - lastPrice >= slDistance AndAlso (currentStop > entry OrElse currentStop = 0D) Then
                    newStop = entry
                    shouldRatchet = True
                End If
            End If

            If Not shouldRatchet OrElse Not positionId.HasValue Then Return

            ' Tick-round the SL away from entry (conservative)
            If tickSize > 0D Then
                Dim ticks = If(side = OrderSide.Buy,
                                CLng(Math.Floor(newStop / tickSize)),
                                CLng(Math.Ceiling(newStop / tickSize)))
                newStop = CDec(ticks) * tickSize
            End If

            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                _livePosition.CurrentStopPrice = newStop
            End SyncLock

            Dim pid = positionId.Value
            Dim editTask = Task.Run(Async Function()
                                         Try
                                             Await _orderService.EditPositionSlTpAsync(pid, newStop, Nothing, False, CancellationToken.None)
                                         Catch ex As Exception
                                             _logger?.LogDebug(ex, "BreakAndBounce break-even SL edit failed (pid {Pid})", pid)
                                         End Try
                                     End Function)

            Try
                RaiseEvent StopRatcheted(Me, New BreakAndBounceStopSnapshot With {
                    .Symbol = symbol,
                    .Side = side,
                    .EntryPrice = entry,
                    .NewStopPrice = newStop,
                    .LastPrice = lastPrice,
                    .CapturedUtc = DateTime.UtcNow
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "StopRatcheted handler threw")
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
                                                 exitReason:=If(String.IsNullOrEmpty(reason), "BreakAndBounceExit", reason),
                                                 trigger:="Orchestrator",
                                                 timeframeMinutes:=5,
                                                 ct:=CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "BreakAndBounce exit failed for {Symbol}", slot.Instrument)
            Finally
                ClearLivePosition()
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _timer?.Dispose()
            RemoveHandler _marketHub.QuoteReceived, _quoteHandler
        End Sub

        ' ─── Inner state ────────────────────────────────────────────────────

        Private Class LivePositionState
            Public Property Symbol As String = String.Empty
            Public Property PxContractId As String = String.Empty
            Public Property AccountId As Long
            Public Property Slot As PositionSlot
            Public Property Config As BreakAndBounceConfig
            Public Property EntryPrice As Decimal
            Public Property InitialStopPrice As Decimal
            Public Property CurrentStopPrice As Decimal
            Public Property PeakFavorablePrice As Decimal
            Public Property TickSize As Decimal
            Public Property IsExiting As Boolean
        End Class

    End Class

    ''' <summary>FEAT-62: Snapshot pushed to the UI on each SL ratchet event.</summary>
    Public Class BreakAndBounceStopSnapshot
        Public Property Symbol As String = String.Empty
        Public Property Side As OrderSide
        Public Property EntryPrice As Decimal
        Public Property NewStopPrice As Decimal
        Public Property LastPrice As Decimal
        Public Property CapturedUtc As DateTime
    End Class

End Namespace
