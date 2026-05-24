Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Scalper

    ''' <summary>FEAT-69: state machine phase for a single watchlist symbol's pre-staged entry.</summary>
    Public Enum ScalperArmedPhase
        ''' <summary>No active resting order at the broker.</summary>
        Idle = 0
        ''' <summary>Confluence aligned; a stop-entry order is live at the broker.</summary>
        Primed = 1
    End Enum

    ''' <summary>FEAT-69: per-symbol arming state held by the stop-entry manager.</summary>
    Public Class ScalperArmedState
        Public Property Symbol As String
        Public Property Phase As ScalperArmedPhase
        Public Property Side As OrderSide
        Public Property TriggerPrice As Decimal
        ''' <summary>Broker-assigned order ID. Retained after Disarm so a late fill (cancel/fill race) is still recognised.</summary>
        Public Property BrokerOrderId As Long
        Public Property AccountId As Long
        Public Property Contracts As Integer
        Public Property ContractId As String
        Public Property ArmedAtUtc As DateTime
        Public Property LastReArmAttemptUtc As DateTime
        ''' <summary>Broker-reported fill price. Populated only when raising FilledIntoPosition.</summary>
        Public Property FillPrice As Decimal
        ''' <summary>Broker positionId from the fill. Populated only when raising FilledIntoPosition.</summary>
        Public Property PositionId As Long?
    End Class

    ''' <summary>FEAT-69: stop-entry arming + rate-budget owner. See <c>tickets/FEAT-69.md</c>.</summary>
    Public Interface IScalperStopEntryManager
        ReadOnly Property ArmedSymbols As IReadOnlyList(Of ScalperArmedState)

        ''' <summary>Raised when a symbol transitions Idle → Primed (order accepted by broker).</summary>
        Event Armed As EventHandler(Of ScalperArmedState)
        ''' <summary>Raised when a symbol transitions Primed → Idle (cancel acked, or un-primed, or stale).</summary>
        Event Disarmed As EventHandler(Of ScalperArmedState)
        ''' <summary>Raised when a primed order fills at the broker.</summary>
        Event FilledIntoPosition As EventHandler(Of ScalperArmedState)

        ''' <summary>Called by the orchestrator on each per-symbol evaluation tick.</summary>
        Function OnEvaluationAsync(eval As UltimateScalperEvaluation,
                                    config As UltimateScalperConfig,
                                    accountId As Long,
                                    ct As CancellationToken) As Task

        ''' <summary>Cancels every Primed symbol except the named one. Invoked when a fill claims the single-position slot.</summary>
        Function DisarmAllExceptAsync(filledSymbol As String, ct As CancellationToken) As Task

        ''' <summary>Cancels every Primed symbol. Invoked on Disable() and on shutdown.</summary>
        Function DisarmAllAsync(ct As CancellationToken) As Task
    End Interface

    ''' <summary>
    ''' FEAT-69 (Plan C): owns the per-symbol arming state machine and the rate-limit budget
    ''' for pre-staged stop-entry orders.
    '''
    ''' Behaviour summary:
    '''   • Idle → Primed   when <c>eval.PrimedSide ≠ None</c> AND debounce window elapsed.
    '''   • Primed re-price on bar close when trigger drift ≥ <c>RepriceThresholdTicks</c>.
    '''   • Primed → Idle   when un-primed, stale (&gt; <c>ArmStaleMinutes</c>), or a sibling fills.
    '''   • Primed → Filled when <c>IOrderService.OrderFilled</c> matches the tracked BrokerOrderId.
    '''
    ''' Rate-limit policy (strict): a rolling 60 s call counter (place + cancel + edit) bounds
    ''' broker traffic. Above <c>MaxBrokerCallsPerMinute</c>, arm/re-price actions defer; cancels
    ''' never defer — stale broker orders are a worse failure mode than missed opportunities.
    ''' </summary>
    Public Class ScalperStopEntryManager
        Implements IScalperStopEntryManager, IDisposable

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _logger As ILogger(Of ScalperStopEntryManager)

        Private ReadOnly _states As New ConcurrentDictionary(Of String, ScalperArmedState)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _stateLock As New Object()
        Private ReadOnly _callTimestamps As New Queue(Of DateTime)()
        Private ReadOnly _callLock As New Object()

        Private ReadOnly _orderFilledHandler As EventHandler(Of OrderFilledEventArgs)
        Private _disposed As Boolean

        Public Event Armed As EventHandler(Of ScalperArmedState) Implements IScalperStopEntryManager.Armed
        Public Event Disarmed As EventHandler(Of ScalperArmedState) Implements IScalperStopEntryManager.Disarmed
        Public Event FilledIntoPosition As EventHandler(Of ScalperArmedState) Implements IScalperStopEntryManager.FilledIntoPosition

        Public Sub New(orderService As IOrderService,
                       logger As ILogger(Of ScalperStopEntryManager))
            _orderService = orderService
            _logger = logger
            _orderFilledHandler = AddressOf OnOrderFilled
            AddHandler _orderService.OrderFilled, _orderFilledHandler
        End Sub

        Public ReadOnly Property ArmedSymbols As IReadOnlyList(Of ScalperArmedState) _
            Implements IScalperStopEntryManager.ArmedSymbols
            Get
                Return _states.Values _
                    .Where(Function(s) s.Phase = ScalperArmedPhase.Primed) _
                    .ToList()
            End Get
        End Property

        ' ─── Public entry points ────────────────────────────────────────────

        Public Async Function OnEvaluationAsync(eval As UltimateScalperEvaluation,
                                                config As UltimateScalperConfig,
                                                accountId As Long,
                                                ct As CancellationToken) As Task _
            Implements IScalperStopEntryManager.OnEvaluationAsync

            If eval Is Nothing OrElse Not eval.IsWarm Then Return
            If accountId = 0 Then Return

            Dim contract = FavouriteContracts.TryGetBySymbolResolved(eval.Symbol)
            If contract Is Nothing Then Return

            Dim state = _states.GetOrAdd(eval.Symbol,
                Function(s) New ScalperArmedState With {.Symbol = s, .Phase = ScalperArmedPhase.Idle})

            Dim desiredSide As OrderSide
            Dim desiredTrigger As Decimal = 0D
            Dim hasDesired As Boolean = False
            If eval.PrimedSide = UltimateScalperSignalSide.Bullish Then
                desiredSide = OrderSide.Buy
                desiredTrigger = eval.LastBarHigh + CDec(config.EntryTriggerOffsetTicks) * contract.PxTickSize
                hasDesired = True
            ElseIf eval.PrimedSide = UltimateScalperSignalSide.Bearish Then
                desiredSide = OrderSide.Sell
                desiredTrigger = eval.LastBarLow - CDec(config.EntryTriggerOffsetTicks) * contract.PxTickSize
                hasDesired = True
            End If

            ' Decide the action under lock, perform the broker I/O outside the lock.
            Dim action As String = "noop"
            Dim cancelOrderId As Long = 0
            Dim placeRequest As Order = Nothing

            SyncLock _stateLock
                If state.Phase = ScalperArmedPhase.Primed Then
                    ' Stale TTL check first — strict policy lets cancels run regardless of rate budget.
                    If DateTime.UtcNow.Subtract(state.ArmedAtUtc).TotalMinutes > config.ArmStaleMinutes Then
                        action = "cancel-stale"
                        cancelOrderId = state.BrokerOrderId
                    ElseIf Not hasDesired OrElse desiredSide <> state.Side Then
                        ' Un-primed (or flipped direction) → cancel.
                        action = "cancel-unprimed"
                        cancelOrderId = state.BrokerOrderId
                    Else
                        ' Same side, still primed — consider re-price.
                        Dim driftTicks = CDec(Math.Abs(desiredTrigger - state.TriggerPrice)) / contract.PxTickSize
                        If driftTicks >= config.RepriceThresholdTicks Then
                            Dim profile = config.GetProfile(eval.Symbol)
                            If profile Is Nothing Then
                                _logger?.LogWarning("scalper re-price aborted: no risk profile for {Symbol}", eval.Symbol)
                            ElseIf Not TryConsumeCallBudget(config.MaxBrokerCallsPerMinute, allowOverBudget:=False) Then
                                _logger?.LogWarning("scalper arming deferred — broker call budget exhausted (re-price {Symbol})", eval.Symbol)
                            Else
                                ' Re-price = cancel + place. Both calls counted; place is conditioned on cancel ack.
                                action = "reprice"
                                cancelOrderId = state.BrokerOrderId
                                placeRequest = BuildStopEntryOrder(eval.Symbol, contract, accountId,
                                                                    desiredSide, desiredTrigger, config.Leverage, profile)
                            End If
                        End If
                    End If
                Else
                    ' Idle — consider arming.
                    If hasDesired Then
                        Dim debounceOk = DateTime.UtcNow.Subtract(state.LastReArmAttemptUtc).TotalSeconds >= config.ReArmDebounceSeconds
                        If debounceOk Then
                            Dim profile = config.GetProfile(eval.Symbol)
                            If profile Is Nothing Then
                                _logger?.LogWarning("scalper arm aborted: no risk profile for {Symbol}", eval.Symbol)
                            ElseIf Not TryConsumeCallBudget(config.MaxBrokerCallsPerMinute, allowOverBudget:=False) Then
                                _logger?.LogWarning("scalper arming deferred — broker call budget exhausted (arm {Symbol})", eval.Symbol)
                            Else
                                action = "arm"
                                placeRequest = BuildStopEntryOrder(eval.Symbol, contract, accountId,
                                                                    desiredSide, desiredTrigger, config.Leverage, profile)
                                state.LastReArmAttemptUtc = DateTime.UtcNow
                            End If
                        End If
                    End If
                End If
            End SyncLock

            ' ── Broker I/O (no lock held) ──
            Select Case action
                Case "noop"
                    Return
                Case "cancel-stale", "cancel-unprimed"
                    Await DoCancelAsync(state, cancelOrderId, action, ct)
                Case "reprice"
                    Await DoCancelAsync(state, cancelOrderId, "reprice-cancel", ct)
                    Await DoPlaceAsync(state, placeRequest, desiredSide, desiredTrigger, ct)
                Case "arm"
                    Await DoPlaceAsync(state, placeRequest, desiredSide, desiredTrigger, ct)
            End Select
        End Function

        Public Async Function DisarmAllExceptAsync(filledSymbol As String, ct As CancellationToken) As Task _
            Implements IScalperStopEntryManager.DisarmAllExceptAsync
            Dim victims = _states.Values _
                .Where(Function(s) s.Phase = ScalperArmedPhase.Primed AndAlso
                                   Not String.Equals(s.Symbol, filledSymbol, StringComparison.OrdinalIgnoreCase)) _
                .ToList()
            For Each st In victims
                Await DoCancelAsync(st, st.BrokerOrderId, "disarm-sibling", ct)
            Next
        End Function

        Public Async Function DisarmAllAsync(ct As CancellationToken) As Task _
            Implements IScalperStopEntryManager.DisarmAllAsync
            Dim victims = _states.Values _
                .Where(Function(s) s.Phase = ScalperArmedPhase.Primed) _
                .ToList()
            For Each st In victims
                Await DoCancelAsync(st, st.BrokerOrderId, "disarm-all", ct)
            Next
        End Function

        ' ─── Broker I/O helpers ─────────────────────────────────────────────

        Private Async Function DoPlaceAsync(state As ScalperArmedState,
                                            request As Order,
                                            desiredSide As OrderSide,
                                            desiredTrigger As Decimal,
                                            ct As CancellationToken) As Task
            Try
                Dim placed = Await _orderService.PlaceOrderAsync(request)
                If placed Is Nothing OrElse Not placed.ExternalOrderId.HasValue Then
                    _logger?.LogWarning("scalper arm rejected: no externalOrderId returned for {Symbol}", state.Symbol)
                    Return
                End If
                SyncLock _stateLock
                    state.Side = desiredSide
                    state.TriggerPrice = desiredTrigger
                    state.BrokerOrderId = placed.ExternalOrderId.Value
                    state.AccountId = request.AccountId
                    state.Contracts = request.Quantity
                    state.ContractId = request.ContractId
                    state.ArmedAtUtc = DateTime.UtcNow
                    state.Phase = ScalperArmedPhase.Primed
                End SyncLock
                RaiseEvent Armed(Me, state)
                _logger?.LogInformation("scalper armed {Side} {Symbol} @ {Trigger} (orderId={OrderId})",
                                        state.Side, state.Symbol, state.TriggerPrice, state.BrokerOrderId)
            Catch ex As Exception
                _logger?.LogWarning(ex, "scalper PlaceOrderAsync failed for {Symbol}", state.Symbol)
            End Try
        End Function

        Private Async Function DoCancelAsync(state As ScalperArmedState,
                                             orderId As Long,
                                             reason As String,
                                             ct As CancellationToken) As Task
            If orderId = 0 Then Return
            ' Strict policy: cancels are never deferred. Always count the call but proceed.
            TryConsumeCallBudget(Int32.MaxValue, allowOverBudget:=True)
            Try
                Dim ok = Await _orderService.CancelOrderAsync(orderId)
                If Not ok Then
                    _logger?.LogWarning("scalper CancelOrderAsync returned false for {Symbol} orderId={OrderId} reason={Reason}",
                                        state.Symbol, orderId, reason)
                End If
            Catch ex As Exception
                _logger?.LogWarning(ex, "scalper CancelOrderAsync threw for {Symbol} orderId={OrderId} reason={Reason}",
                                    state.Symbol, orderId, reason)
            End Try
            SyncLock _stateLock
                ' Retain BrokerOrderId so a late fill (cancel/fill race) still matches in OnOrderFilled.
                state.Phase = ScalperArmedPhase.Idle
                state.TriggerPrice = 0D
            End SyncLock
            RaiseEvent Disarmed(Me, state)
            _logger?.LogInformation("scalper disarmed {Symbol} (reason={Reason})", state.Symbol, reason)
        End Function

        ''' <summary>
        ''' BUG-95: attaches a protective SL bracket atomically with the parent stop-entry.
        ''' <c>InitialStopTicks</c> is forwarded by <c>ProjectXOrderService.PlaceOrderAsync</c>
        ''' as a <c>stopLossBracket</c> on the place request, so the broker enforces protection
        ''' from the instant the parent fills (no fill→edit naked window).
        ''' </summary>
        Private Shared Function BuildStopEntryOrder(symbol As String,
                                                     contract As FavouriteContract,
                                                     accountId As Long,
                                                     side As OrderSide,
                                                     triggerPrice As Decimal,
                                                     contracts As Integer,
                                                     profile As UltimateScalperInstrumentRiskProfile) As Order
            Dim stopTicks = CInt(Math.Ceiling(CDbl(profile.InitialStopDollars / contract.PxTickValue)))
            Return New Order With {
                .AccountId = accountId,
                .ContractId = symbol,
                .Side = side,
                .Quantity = Math.Max(1, contracts),
                .OrderType = OrderType.StopOrder,
                .StopPrice = triggerPrice,
                .InitialStopTicks = stopTicks
            }
        End Function

        ' ─── OrderFilled subscription ───────────────────────────────────────

        Private Sub OnOrderFilled(sender As Object, e As OrderFilledEventArgs)
            If e?.Order Is Nothing OrElse Not e.Order.ExternalOrderId.HasValue Then Return
            Dim filledId = e.Order.ExternalOrderId.Value
            Dim matched As ScalperArmedState = Nothing
            SyncLock _stateLock
                For Each st In _states.Values
                    If st.BrokerOrderId = filledId Then
                        matched = st
                        Exit For
                    End If
                Next
                If matched IsNot Nothing Then
                    matched.Phase = ScalperArmedPhase.Idle
                    matched.TriggerPrice = 0D
                    matched.FillPrice = If(e.Order.FillPrice.HasValue, e.Order.FillPrice.Value, 0D)
                    matched.PositionId = e.Order.ExternalPositionId
                    ' Clear BrokerOrderId so a duplicate fill event for the same orderId is
                    ' not matched again. The order is gone at the broker after fill — late-fill
                    ' detection only needs to cover the cancel-sent-but-not-yet-acked window.
                    matched.BrokerOrderId = 0L
                End If
            End SyncLock
            If matched Is Nothing Then Return
            _logger?.LogInformation("scalper stop-entry filled {Symbol} fill={Fill} pid={Pid}",
                                    matched.Symbol, matched.FillPrice, matched.PositionId)
            RaiseEvent FilledIntoPosition(Me, matched)
        End Sub

        ' ─── Rate budget ────────────────────────────────────────────────────

        ''' <summary>
        ''' Charges one call against the rolling 60 s budget. Returns True when the call fits
        ''' within <paramref name="capPerMinute"/>; returns False (without charging) when full
        ''' AND <paramref name="allowOverBudget"/> is False. With <paramref name="allowOverBudget"/>
        ''' True, always charges and returns True — used for cancels (strict policy: never defer).
        ''' </summary>
        Friend Function TryConsumeCallBudget(capPerMinute As Integer, allowOverBudget As Boolean) As Boolean
            Dim now = DateTime.UtcNow
            SyncLock _callLock
                Dim cutoff = now.AddSeconds(-60)
                While _callTimestamps.Count > 0 AndAlso _callTimestamps.Peek() < cutoff
                    _callTimestamps.Dequeue()
                End While
                If Not allowOverBudget AndAlso _callTimestamps.Count >= capPerMinute Then
                    Return False
                End If
                _callTimestamps.Enqueue(now)
                Return True
            End SyncLock
        End Function

        ''' <summary>Diagnostic — count of broker calls made in the rolling 60 s window.</summary>
        Friend ReadOnly Property CallsInLastMinute As Integer
            Get
                SyncLock _callLock
                    Dim cutoff = DateTime.UtcNow.AddSeconds(-60)
                    While _callTimestamps.Count > 0 AndAlso _callTimestamps.Peek() < cutoff
                        _callTimestamps.Dequeue()
                    End While
                    Return _callTimestamps.Count
                End SyncLock
            End Get
        End Property

        ' ─── IDisposable ────────────────────────────────────────────────────

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            RemoveHandler _orderService.OrderFilled, _orderFilledHandler
        End Sub

    End Class

End Namespace
