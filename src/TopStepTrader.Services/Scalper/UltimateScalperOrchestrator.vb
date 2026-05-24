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

Namespace TopStepTrader.Services.Scalper

    ''' <summary>
    ''' FEAT-64: Singleton scanner-orchestrator for the Ultimate Scalper strategy.
    '''
    ''' F3: scans MES / MNQ / MGC every <see cref="ScanIntervalSeconds"/> seconds when
    ''' <see cref="IsEnabled"/> is True; emits <c>WatchlistTick</c> + <c>SignalDetected</c>.
    ''' F4: on <c>SignalDetected</c>, opens a single live position via
    ''' <c>IEntryExecutionService</c>, then drives a quote-driven trail
    ''' (<see cref="IScalperTrailEngine"/>) that pushes monotonic SL ratchets to the broker
    ''' and flattens via <c>IExitExecutionService</c> when the trail trips.
    '''
    ''' Strict single-position guard: while <see cref="IsInPosition"/>, additional
    ''' <c>SignalDetected</c> events are silently dropped (no DCA).
    ''' </summary>
    Public Class UltimateScalperOrchestrator
        Implements IHostedService, IDisposable

        ''' <summary>Watchlist symbols. Equity-futures micros only per the FEAT-64 decision.</summary>
        Public Shared ReadOnly WatchlistSymbols As IReadOnlyList(Of String) =
            New String() {"MES", "MNQ", "MGC"}

        ''' <summary>How often the orchestrator wakes to check for new closed 5m bars.
        ''' 30 s is a "liveness heartbeat" cadence: the inputs (MA200/VWAP/RSI) are computed
        ''' from 5-minute bars, so anything tighter mostly recomputes identical numbers. 30 s
        ''' keeps the "TopStepX checked @ HH:mm:ss" header fresh and bounds the worst-case
        ''' lag from a new bar close to ~30 s, with no scan-cost concern.</summary>
        Private Const ScanIntervalSeconds As Integer = 30

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _entryExecution As IEntryExecutionService
        Private ReadOnly _exitExecution As IExitExecutionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _marketHub As IMarketQuoteFeed
        Private ReadOnly _trailEngine As IScalperTrailEngine
        Private ReadOnly _stopEntryManager As IScalperStopEntryManager
        Private ReadOnly _logger As ILogger(Of UltimateScalperOrchestrator)
        Private ReadOnly _lastFiredAsOf As New ConcurrentDictionary(Of String, DateTimeOffset)()
        Private ReadOnly _livePositionLock As New Object()
        Private _livePosition As ScalperLivePosition
        Private _quoteHandler As EventHandler(Of MarketQuoteEventArgs)
        Private _stopEntryFilledHandler As EventHandler(Of ScalperArmedState)
        Private _stopEntryArmedHandler As EventHandler(Of ScalperArmedState)
        Private _stopEntryDisarmedHandler As EventHandler(Of ScalperArmedState)
        Private _timer As Timer
        Private _scanInFlight As Integer  ' 0/1 latch via Interlocked.Exchange
        Private _isEnabled As Boolean

        Public Event WatchlistTick As EventHandler(Of UltimateScalperEvaluation)
        Public Event SignalDetected As EventHandler(Of UltimateScalperEvaluation)
        Public Event EnabledChanged As EventHandler(Of Boolean)
        ''' <summary>Fires when <see cref="IsInPosition"/> flips. Drives the status badge.</summary>
        Public Event LivePositionChanged As EventHandler(Of Boolean)
        ''' <summary>FEAT-64 F5: per-quote trail snapshot for the live-position card.</summary>
        Public Event TrailUpdated As EventHandler(Of ScalperTrailSnapshot)
        ''' <summary>FEAT-65: Fires after the per-symbol scan loop completes (regardless of signal). Drives the warmup/heartbeat header status line.</summary>
        Public Event ScanCompleted As EventHandler(Of ScannerScanCompletedEventArgs)
        ''' <summary>FEAT-69: Re-broadcast of the stop-entry manager's Armed/Disarmed events. Drives the watchlist Armed column + header armed-symbols summary.</summary>
        Public Event WatchlistArmedChanged As EventHandler(Of ScalperWatchlistArmedChangedArgs)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       session As ITradingSessionContext,
                       entryExecution As IEntryExecutionService,
                       exitExecution As IExitExecutionService,
                       orderService As IOrderService,
                       marketHub As IMarketQuoteFeed,
                       trailEngine As IScalperTrailEngine,
                       stopEntryManager As IScalperStopEntryManager,
                       logger As ILogger(Of UltimateScalperOrchestrator))
            _scopeFactory = scopeFactory
            _session = session
            _entryExecution = entryExecution
            _exitExecution = exitExecution
            _orderService = orderService
            _marketHub = marketHub
            _trailEngine = trailEngine
            _stopEntryManager = stopEntryManager
            _logger = logger
            _quoteHandler = AddressOf OnQuoteReceived
            _stopEntryFilledHandler = AddressOf OnStopEntryFilled
            _stopEntryArmedHandler = AddressOf OnStopEntryArmed
            _stopEntryDisarmedHandler = AddressOf OnStopEntryDisarmed
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

        ''' <summary>For diagnostics + UI: shallow read of the current trail state. Nothing when flat.</summary>
        Public ReadOnly Property CurrentTrailState As ScalperTrailState
            Get
                Return _livePosition?.TrailState
            End Get
        End Property

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            AddHandler _marketHub.QuoteReceived, _quoteHandler
            AddHandler _stopEntryManager.FilledIntoPosition, _stopEntryFilledHandler
            AddHandler _stopEntryManager.Armed, _stopEntryArmedHandler
            AddHandler _stopEntryManager.Disarmed, _stopEntryDisarmedHandler
            _timer = New Timer(AddressOf ScanCallback, Nothing,
                               TimeSpan.FromSeconds(ScanIntervalSeconds),
                               TimeSpan.FromSeconds(ScanIntervalSeconds))
            _logger?.LogInformation("UltimateScalperOrchestrator started (disabled by default).")
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _timer?.Change(Timeout.Infinite, 0)
            RemoveHandler _marketHub.QuoteReceived, _quoteHandler
            RemoveHandler _stopEntryManager.FilledIntoPosition, _stopEntryFilledHandler
            RemoveHandler _stopEntryManager.Armed, _stopEntryArmedHandler
            RemoveHandler _stopEntryManager.Disarmed, _stopEntryDisarmedHandler
            Return Task.CompletedTask
        End Function

        Public Sub Enable()
            If _isEnabled Then Return
            _isEnabled = True
            _logger?.LogInformation("UltimateScalperOrchestrator: enabled.")
            RaiseEvent EnabledChanged(Me, True)
        End Sub

        Public Sub Disable()
            If Not _isEnabled Then Return
            _isEnabled = False
            _logger?.LogInformation("UltimateScalperOrchestrator: disabled.")
            ' FEAT-69: cancel any armed pre-staged stop-entry orders so they don't fire after Disable.
            Dim disarmTask = Task.Run(Async Function() As Task
                                           Try
                                               Await _stopEntryManager.DisarmAllAsync(CancellationToken.None)
                                           Catch ex As Exception
                                               _logger?.LogWarning(ex, "Scalper Disable: DisarmAllAsync threw")
                                           End Try
                                       End Function)
            RaiseEvent EnabledChanged(Me, False)
        End Sub

        ' ─── Scan loop ──────────────────────────────────────────────────────────

        Private Async Sub ScanCallback(state As Object)
            If Not _isEnabled Then Return
            If Interlocked.Exchange(_scanInFlight, 1) = 1 Then Return
            Try
                Await ScanAllSymbolsAsync(CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "UltimateScalperOrchestrator scan loop error")
            Finally
                Interlocked.Exchange(_scanInFlight, 0)
            End Try
        End Sub

        Private Async Function ScanAllSymbolsAsync(ct As CancellationToken) As Task
            Dim barsAvailable As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Using scope = _scopeFactory.CreateScope()
                Dim detector = scope.ServiceProvider.GetRequiredService(Of IUltimateScalperSignalDetector)()
                Dim config = scope.ServiceProvider.GetRequiredService(Of UltimateScalperConfig)()
                For Each symbol In WatchlistSymbols
                    If ct.IsCancellationRequested Then Exit For
                    Try
                        Dim eval = Await detector.EvaluateAsync(symbol, ct)
                        If eval IsNot Nothing Then barsAvailable(symbol) = eval.BarsAvailable
                        DispatchEvaluation(eval, config)
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "Scalper evaluation failed for {Symbol}", symbol)
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

        Private Sub DispatchEvaluation(eval As UltimateScalperEvaluation, config As UltimateScalperConfig)
            If eval Is Nothing Then Return

            Try
                RaiseEvent WatchlistTick(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistTick handler threw")
            End Try

            ' FEAT-69: enforce single-position cap. Forward-compat seam — config carries
            ' MaxConcurrentPositions but v1 clamps to 1 here.
            If config.MaxConcurrentPositions > 1 Then
                _logger?.LogWarning("Scalper config MaxConcurrentPositions={N} clamped to 1 for v1 (FEAT-69 forward-compat seam)",
                                    config.MaxConcurrentPositions)
            End If

            ' FEAT-69: pre-staged stop-entry path. The manager handles arm/re-price/disarm
            ' decisions on every evaluation when enabled AND flat. The legacy signal-fires
            ' market-order path below is bypassed when pre-staging is enabled — entries flow
            ' exclusively through the manager + OnStopEntryFilled handler.
            If config.PreStagedEntriesEnabled AndAlso _livePosition Is Nothing Then
                Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
                If accountId > 0 Then
                    Dim mgrTask = Task.Run(Async Function() As Task
                                                Try
                                                    Await _stopEntryManager.OnEvaluationAsync(eval, config, accountId, CancellationToken.None)
                                                Catch ex As Exception
                                                    _logger?.LogWarning(ex, "Scalper stop-entry manager OnEvaluationAsync threw")
                                                End Try
                                            End Function)
                End If
            End If

            If eval.Signal = UltimateScalperSignalSide.None Then Return
            Dim previousAsOf As DateTimeOffset
            _lastFiredAsOf.TryGetValue(eval.Symbol, previousAsOf)
            If eval.AsOf <= previousAsOf Then Return
            _lastFiredAsOf(eval.Symbol) = eval.AsOf

            _logger?.LogInformation(
                "Scalper signal {Side} on {Symbol} @ {AsOf:o}: close={Close} ma={Ma} vwap={Vwap} rsi={Rsi:F1}",
                eval.Signal, eval.Symbol, eval.AsOf, eval.LastClose, eval.Ma200, eval.Vwap, eval.Rsi)

            Try
                RaiseEvent SignalDetected(Me, eval)
            Catch ex As Exception
                _logger?.LogDebug(ex, "SignalDetected handler threw")
            End Try

            ' FEAT-69: legacy signal-fires market-order path. Runs only when pre-staging is
            ' disabled — otherwise the manager owns all entries (avoids double-trade).
            If config.PreStagedEntriesEnabled Then Return

            ' F4: open a live position when flat. Strict single-position cap.
            If _livePosition IsNot Nothing Then
                _logger?.LogDebug("Scalper signal dropped: live position already open ({Symbol})", _livePosition.Symbol)
                Return
            End If
            Dim fireAndForget = TryOpenPositionAsync(eval, config)
        End Sub

        ' ─── Live position lifecycle (F4) ───────────────────────────────────────

        Private Async Function TryOpenPositionAsync(eval As UltimateScalperEvaluation,
                                                     config As UltimateScalperConfig) As Task
            ' Reserve the single-position slot atomically — second-tick races are dropped here.
            SyncLock _livePositionLock
                If _livePosition IsNot Nothing Then Return
                _livePosition = New ScalperLivePosition With {.Symbol = eval.Symbol, .Config = config}
            End SyncLock
            RaiseLivePositionChanged()

            Try
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(eval.Symbol)
                If contract Is Nothing Then
                    _logger?.LogWarning("Scalper entry aborted: unknown contract {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If
                Dim account = _session?.SelectedAccount
                If account Is Nothing OrElse account.Id = 0 Then
                    _logger?.LogWarning("Scalper entry aborted: no account selected")
                    ClearLivePosition() : Return
                End If
                Dim profile = config.GetProfile(eval.Symbol)
                If profile Is Nothing Then
                    _logger?.LogWarning("Scalper entry aborted: no risk profile for {Symbol}", eval.Symbol)
                    ClearLivePosition() : Return
                End If

                Dim side = If(eval.Signal = UltimateScalperSignalSide.Bullish, OrderSide.Buy, OrderSide.Sell)
                Dim sideStr = If(side = OrderSide.Buy, "Buy", "Sell")
                Dim initialStop = ComputeInitialStopPrice(eval.LastClose, side, profile, contract)

                Dim contracts = Math.Max(1, config.Leverage)
                Dim slot As New PositionSlot With {
                    .SlotIndex = 0,
                    .Instrument = eval.Symbol,
                    .Side = sideStr,
                    .AccountId = account.Id,
                    .Contracts = contracts,
                    .IsOpen = False,
                    .EntryBarTime = eval.AsOf
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
                    .StrategyName = "UltimateScalper",
                    .EntryReason = $"Confluence {sideStr} RSI={eval.Rsi:F1} recency={If(side = OrderSide.Buy, eval.BarsSinceCrossAbove, eval.BarsSinceCrossBelow)}",
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
                    .StrategyName = "UltimateScalper",
                    .StrategyDisplayName = "Ultimate Scalper",
                    .ModelVersion = "UltimateScalper.v1",
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
                                               ' Watchlist already handled by the VM via WatchlistTick.
                                           End Sub,
                    .OnReleaseSlot = Sub(idx) OnSlotReleased(),
                    .OnSlotEntered = AddressOf OnSlotEntered,
                    .BandForAdx = Function(adx As Single) 0
                }

                Dim result = Await _entryExecution.PlaceAsync(request, CancellationToken.None)
                If Not result.Success Then
                    _logger?.LogInformation("Scalper entry aborted by execution pipeline: {Reason}", result.AbortReason)
                    ' OnReleaseSlot has already fired which clears _livePosition.
                    Return
                End If
            Catch ex As Exception
                _logger?.LogError(ex, "Scalper entry pipeline threw")
                ClearLivePosition()
            End Try
        End Function

        ' ─── FEAT-69: arm/disarm re-broadcast ───────────────────────────────────

        Private Sub OnStopEntryArmed(sender As Object, armed As ScalperArmedState)
            If armed Is Nothing Then Return
            Try
                Dim side As UltimateScalperSignalSide =
                    If(armed.Side = OrderSide.Buy, UltimateScalperSignalSide.Bullish, UltimateScalperSignalSide.Bearish)
                RaiseEvent WatchlistArmedChanged(Me, New ScalperWatchlistArmedChangedArgs With {
                    .Symbol = armed.Symbol,
                    .IsArmed = True,
                    .Side = side,
                    .TriggerPrice = armed.TriggerPrice
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistArmedChanged (armed) handler threw")
            End Try
        End Sub

        Private Sub OnStopEntryDisarmed(sender As Object, armed As ScalperArmedState)
            If armed Is Nothing Then Return
            Try
                RaiseEvent WatchlistArmedChanged(Me, New ScalperWatchlistArmedChangedArgs With {
                    .Symbol = armed.Symbol,
                    .IsArmed = False,
                    .Side = UltimateScalperSignalSide.None,
                    .TriggerPrice = 0D
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistArmedChanged (disarmed) handler threw")
            End Try
        End Sub

        ''' <summary>FEAT-69 UI: snapshot of currently-armed symbols for header status text.</summary>
        Public ReadOnly Property ArmedSymbols As IReadOnlyList(Of String)
            Get
                Return _stopEntryManager.ArmedSymbols.Select(Function(s) s.Symbol).ToList()
            End Get
        End Property

        ' ─── FEAT-69: stop-entry fill → live position lifecycle ─────────────────

        ''' <summary>
        ''' Invoked by <see cref="IScalperStopEntryManager.FilledIntoPosition"/> when a primed
        ''' stop-entry order fills at the broker. Runs the post-fill subset of the lifecycle
        ''' (slot setup, protective SL placement, trail engine init, quote subscription,
        ''' persistence) — skipping the entry-placement and AI veto steps that <c>EntryExecutionService</c>
        ''' handles for the legacy market-on-close path. The broker-reported fill price becomes
        ''' the slot's entry price.
        ''' </summary>
        Private Sub OnStopEntryFilled(sender As Object, armed As ScalperArmedState)
            If armed Is Nothing Then Return

            ' Reserve the single-position slot atomically. On race (a sibling fill claimed
            ' first, or the legacy path opened a position), flatten the surprise fill to
            ' restore the cap invariant.
            Dim claimed As Boolean = False
            SyncLock _livePositionLock
                If _livePosition Is Nothing Then
                    _livePosition = New ScalperLivePosition With {.Symbol = armed.Symbol}
                    claimed = True
                End If
            End SyncLock

            If Not claimed Then
                _logger?.LogWarning("Scalper stop-entry race: {Symbol} filled but live position already held — flattening surprise fill",
                                    armed.Symbol)
                Dim flatTask = Task.Run(Async Function() As Task
                                             Try
                                                 Await _orderService.FlattenContractAsync(armed.AccountId, armed.Symbol, CancellationToken.None)
                                             Catch ex As Exception
                                                 _logger?.LogError(ex, "Scalper race-flatten failed for {Symbol}", armed.Symbol)
                                             End Try
                                         End Function)
                Return
            End If

            ' All further work is async — do it off the OrderFilled event thread.
            Dim openTask = Task.Run(Async Function() As Task
                                         Await CompleteStopEntryOpenAsync(armed)
                                     End Function)
        End Sub

        Private Async Function CompleteStopEntryOpenAsync(armed As ScalperArmedState) As Task
            Dim config As UltimateScalperConfig = Nothing
            Try
                Using scope = _scopeFactory.CreateScope()
                    config = scope.ServiceProvider.GetRequiredService(Of UltimateScalperConfig)()
                End Using
            Catch ex As Exception
                _logger?.LogError(ex, "Scalper stop-entry: failed to resolve config")
                ClearLivePosition()
                Return
            End Try

            ' Cancel sibling primed orders so the broker can't fill a second one while we set up.
            Try
                Await _stopEntryManager.DisarmAllExceptAsync(armed.Symbol, CancellationToken.None)
            Catch ex As Exception
                _logger?.LogWarning(ex, "Scalper stop-entry: DisarmAllExceptAsync threw")
            End Try

            Try
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(armed.Symbol)
                If contract Is Nothing Then
                    _logger?.LogWarning("Scalper stop-entry: unknown contract {Symbol}", armed.Symbol)
                    ClearLivePosition() : Return
                End If
                Dim profile = config.GetProfile(armed.Symbol)
                If profile Is Nothing Then
                    _logger?.LogWarning("Scalper stop-entry: no risk profile for {Symbol}", armed.Symbol)
                    ClearLivePosition() : Return
                End If
                If armed.FillPrice <= 0D Then
                    _logger?.LogWarning("Scalper stop-entry: missing fill price for {Symbol}", armed.Symbol)
                    ClearLivePosition() : Return
                End If

                Dim sideStr = If(armed.Side = OrderSide.Buy, "Buy", "Sell")
                Dim slot As New PositionSlot With {
                    .SlotIndex = 0,
                    .Instrument = armed.Symbol,
                    .Side = sideStr,
                    .AccountId = armed.AccountId,
                    .Contracts = Math.Max(1, armed.Contracts),
                    .IsOpen = True,
                    .EntryOrderId = armed.BrokerOrderId,
                    .PositionId = armed.PositionId,
                    .EntryPrice = armed.FillPrice,
                    .EntryTime = DateTime.UtcNow,
                    .EntryBarTime = DateTimeOffset.UtcNow,
                    .StopPhase = StopPhase.Initial
                }

                Dim initialStop = ComputeInitialStopPrice(armed.FillPrice, armed.Side, profile, contract)
                slot.StopPrice = initialStop

                SyncLock _livePositionLock
                    If _livePosition IsNot Nothing Then
                        _livePosition.Config = config
                        _livePosition.AccountId = armed.AccountId
                        _livePosition.PxContractId = contract.PxContractId
                        _livePosition.Slot = slot
                    End If
                End SyncLock

                ' Place protective SL via the EditPositionSlTp endpoint (bracket-on-fill pattern).
                If armed.PositionId.HasValue AndAlso armed.PositionId.Value > 0 Then
                    Try
                        Await _orderService.EditPositionSlTpAsync(armed.PositionId.Value, initialStop, Nothing, False, CancellationToken.None)
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "Scalper stop-entry: protective SL placement failed for {Symbol} pid={Pid}",
                                            armed.Symbol, armed.PositionId.Value)
                    End Try
                Else
                    _logger?.LogWarning("Scalper stop-entry: no broker positionId — protective SL not placed for {Symbol}", armed.Symbol)
                End If

                ' Seed the trail engine + subscribe quotes (mirrors OnSlotEntered).
                Dim trail As New ScalperTrailState With {
                    .Symbol = slot.Instrument,
                    .Side = armed.Side,
                    .EntryPrice = armed.FillPrice,
                    .TickSize = contract.PxTickSize,
                    .DollarsPerTick = contract.PxTickValue,
                    .InitialStopDollars = profile.InitialStopDollars,
                    .BreakevenSnapDollars = profile.BreakevenSnapDollars,
                    .TrailDistanceDollars = profile.TrailDistanceDollars
                }
                _trailEngine.Initialise(trail)
                SyncLock _livePositionLock
                    If _livePosition IsNot Nothing Then _livePosition.TrailState = trail
                End SyncLock

                If Not String.IsNullOrEmpty(contract.PxContractId) Then
                    Try
                        Await _marketHub.SubscribeContractAsync(contract.PxContractId, CancellationToken.None)
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "Scalper stop-entry: quote subscribe failed for {Contract}", contract.PxContractId)
                    End Try
                End If

                RaiseLivePositionChanged()

                _logger?.LogInformation("Scalper stop-entry opened {Side} {Symbol} @ {Fill} SL={Sl} (orderId={OrderId} pid={Pid})",
                                        sideStr, armed.Symbol, armed.FillPrice, initialStop,
                                        armed.BrokerOrderId, armed.PositionId)

                ' Persistence — a minimal LiveTradeRecord so the trade appears in postmortem.
                ' Full setup-snapshot/lifespan parity with the EntryExecutionService path is out
                ' of scope for FEAT-69 (would require extracting the persistence subset of
                ' EntryExecutionService into a shared helper — tracked as a follow-up).
                Try
                    Using persistScope = _scopeFactory.CreateScope()
                        Dim tradeRecord = persistScope.ServiceProvider.GetService(Of ITradeRecordService)()
                        If tradeRecord IsNot Nothing Then
                            Dim rec As New LiveTradeRecord With {
                                .EntryOrderId = armed.BrokerOrderId,
                                .ContractId = contract.PxContractId,
                                .Symbol = "/" & If(contract.Name, armed.Symbol),
                                .Direction = If(armed.Side = OrderSide.Buy, "Long", "Short"),
                                .Sizes = slot.Contracts,
                                .StrategyName = "UltimateScalper-Primed",
                                .Persona = String.Empty,
                                .Timeframe = "5min",
                                .EntryTime = DateTimeOffset.UtcNow,
                                .EntryPrice = armed.FillPrice,
                                .InitialStopPrice = initialStop,
                                .IsOpen = True
                            }
                            Await tradeRecord.OpenTradeAsync(rec)
                        End If
                    End Using
                Catch ex As Exception
                    _logger?.LogWarning(ex, "Scalper stop-entry: OpenTradeAsync persistence threw for {Symbol}", armed.Symbol)
                End Try
            Catch ex As Exception
                _logger?.LogError(ex, "Scalper stop-entry post-fill lifecycle threw for {Symbol}", armed.Symbol)
                ClearLivePosition()
            End Try
        End Function

        ''' <summary>Computes the broker-rounded initial stop price from per-contract risk dollars.</summary>
        Private Shared Function ComputeInitialStopPrice(referencePrice As Decimal,
                                                         side As OrderSide,
                                                         profile As UltimateScalperInstrumentRiskProfile,
                                                         contract As FavouriteContract) As Decimal
            Dim distanceTicks = CInt(Math.Ceiling(CDbl(profile.InitialStopDollars / contract.PxTickValue)))
            Dim distance = distanceTicks * contract.PxTickSize
            Dim raw = If(side = OrderSide.Buy, referencePrice - distance, referencePrice + distance)
            ' Round away from entry to next tick for safety.
            Dim ticks = If(side = OrderSide.Buy, Math.Floor(raw / contract.PxTickSize), Math.Ceiling(raw / contract.PxTickSize))
            Return ticks * contract.PxTickSize
        End Function

        ''' <summary>
        ''' Invoked by <c>EntryExecutionService.PlaceAsync</c> immediately after a successful
        ''' bracket order placement. The slot now carries the broker positionId, entry fill
        ''' price, and SL price — perfect time to seed the trail engine + subscribe quotes.
        ''' </summary>
        Private Sub OnSlotEntered(slot As PositionSlot)
            Dim pxContractId As String = Nothing
            Dim profile As UltimateScalperInstrumentRiskProfile = Nothing
            Dim contract As FavouriteContract = Nothing

            SyncLock _livePositionLock
                If _livePosition Is Nothing Then Return
                contract = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument)
                profile = _livePosition.Config?.GetProfile(slot.Instrument)
                If contract Is Nothing OrElse profile Is Nothing Then Return

                Dim trail = New ScalperTrailState With {
                    .Symbol = slot.Instrument,
                    .Side = If(slot.Side = "Buy", OrderSide.Buy, OrderSide.Sell),
                    .EntryPrice = slot.EntryPrice,
                    .TickSize = contract.PxTickSize,
                    .DollarsPerTick = contract.PxTickValue,
                    .InitialStopDollars = profile.InitialStopDollars,
                    .BreakevenSnapDollars = profile.BreakevenSnapDollars,
                    .TrailDistanceDollars = profile.TrailDistanceDollars
                }
                _trailEngine.Initialise(trail)
                _livePosition.TrailState = trail
                _livePosition.Slot = slot
                _livePosition.PxContractId = contract.PxContractId
                pxContractId = contract.PxContractId
            End SyncLock
            RaiseLivePositionChanged()

            ' Fire-and-forget quote subscription so we don't block the entry callback.
            If Not String.IsNullOrEmpty(pxContractId) Then
                Dim subTask = Task.Run(Async Function()
                                            Try
                                                Await _marketHub.SubscribeContractAsync(pxContractId, CancellationToken.None)
                                            Catch ex As Exception
                                                _logger?.LogWarning(ex, "Scalper quote subscribe failed for {Contract}", pxContractId)
                                            End Try
                                        End Function)
            End If
        End Sub

        ''' <summary>
        ''' Fires on any abort path or after a successful exit. The exit-execution service
        ''' invokes this via the request's <c>OnReleaseSlot</c> callback once the slot has
        ''' been closed broker-side.
        ''' </summary>
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
                                                  _logger?.LogDebug(ex, "Scalper quote unsubscribe failed for {Contract}", contractIdToUnsub)
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

        ' ─── Quote handler (F4 trail) ───────────────────────────────────────────

        Private Sub OnQuoteReceived(sender As Object, args As MarketQuoteEventArgs)
            If args Is Nothing OrElse args.Quote Is Nothing Then Return
            Dim state As ScalperTrailState = Nothing
            Dim positionId As Long? = Nothing
            Dim slot As PositionSlot = Nothing
            Dim config As UltimateScalperConfig = Nothing
            SyncLock _livePositionLock
                If _livePosition Is Nothing OrElse _livePosition.TrailState Is Nothing Then Return
                If Not String.Equals(_livePosition.PxContractId, args.Quote.ContractId, StringComparison.OrdinalIgnoreCase) Then Return
                state = _livePosition.TrailState
                positionId = _livePosition.Slot?.PositionId
                slot = _livePosition.Slot
                config = _livePosition.Config
            End SyncLock

            Dim lastPrice = args.Quote.LastPrice
            If lastPrice <= 0D Then lastPrice = args.Quote.MidPrice
            If lastPrice <= 0D Then Return

            Dim update As ScalperTrailUpdate
            Try
                update = _trailEngine.OnQuote(state, lastPrice, DateTime.UtcNow,
                                              config.MinSlEditStepTicks, config.MaxSlEditsPerSecond)
            Catch ex As Exception
                _logger?.LogError(ex, "Scalper trail engine threw")
                Return
            End Try

            ' UI snapshot — fires on every quote so the live-position card animates live.
            Try
                RaiseEvent TrailUpdated(Me, New ScalperTrailSnapshot With {
                    .Symbol = state.Symbol,
                    .Side = state.Side,
                    .EntryPrice = state.EntryPrice,
                    .CurrentStopPrice = state.CurrentStopPrice,
                    .LastPrice = lastPrice,
                    .PeakFavorablePrice = state.PeakFavorablePrice,
                    .HasBreakevenSnapped = state.HasBreakevenSnapped,
                    .TickSize = state.TickSize,
                    .DollarsPerTick = state.DollarsPerTick,
                    .EditsThisSecond = state.EditsThisSecond,
                    .CapturedUtc = DateTime.UtcNow
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "TrailUpdated handler threw")
            End Try

            If update.BrokerEditShouldFire AndAlso positionId.HasValue Then
                Dim pid = positionId.Value
                Dim newSl = update.NewStopPrice
                Dim editTask = Task.Run(Async Function()
                                             Try
                                                 Await _orderService.EditPositionSlTpAsync(pid, newSl, Nothing, False, CancellationToken.None)
                                             Catch ex As Exception
                                                 _logger?.LogDebug(ex, "Scalper trail SL edit failed (position {Pid})", pid)
                                             End Try
                                         End Function)
            End If

            If update.ExitRequested AndAlso slot IsNot Nothing Then
                Dim closeTask = CloseLivePositionAsync(slot, update.Reason)
            End If
        End Sub

        Private Async Function CloseLivePositionAsync(slot As PositionSlot, reason As String) As Task
            ' Guard against re-entry: only the FIRST exit decision per position should fire.
            SyncLock _livePositionLock
                If _livePosition Is Nothing OrElse _livePosition.IsExiting Then Return
                _livePosition.IsExiting = True
            End SyncLock

            Try
                Await _exitExecution.CloseAsync(slot,
                                                exitReason:=If(String.IsNullOrEmpty(reason), "ScalperTrailHit", reason),
                                                trigger:="QuoteTrail",
                                                timeframeMinutes:=5,
                                                ct:=CancellationToken.None)
            Catch ex As Exception
                _logger?.LogError(ex, "Scalper exit failed for {Symbol}", slot.Instrument)
            Finally
                ClearLivePosition()
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _timer?.Dispose()
            RemoveHandler _marketHub.QuoteReceived, _quoteHandler
        End Sub

        ' ─── Inner state ────────────────────────────────────────────────────────

        Private Class ScalperLivePosition
            Public Property Symbol As String = String.Empty
            Public Property PxContractId As String = String.Empty
            Public Property AccountId As Long
            Public Property Slot As PositionSlot
            Public Property TrailState As ScalperTrailState
            Public Property Config As UltimateScalperConfig
            Public Property IsExiting As Boolean
        End Class

    End Class

End Namespace
