Imports System.Threading
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    Public Interface IOrderService
        Event OrderFilled As EventHandler(Of OrderFilledEventArgs)
        Event OrderRejected As EventHandler(Of OrderRejectedEventArgs)
        ''' <summary>Fires when the net position changes (via UserHub SignalR stream).</summary>
        Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs)

        Function PlaceOrderAsync(order As Order) As Task(Of Order)
        Function CancelOrderAsync(orderId As Long) As Task(Of Boolean)
        Function CancelAllOpenOrdersAsync() As Task
        Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order))
        Function GetOrderHistoryAsync(accountId As Long, from As DateTime, [to] As DateTime) As Task(Of IEnumerable(Of Order))
        ''' <summary>Returns the avg fill price for an already-placed order, or Nothing if not yet filled.</summary>
        Function TryGetOrderFillPriceAsync(externalOrderId As Long,
                                            accountId As Long,
                                            Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?)

        ''' <summary>
        ''' Returns the stop price from the companion Stop Market bracket order placed at entry,
        ''' or Nothing when no open stop bracket is found for the contract.
        ''' </summary>
        Function TryGetBracketStopPriceAsync(accountId As Long,
                                              contractId As String,
                                              Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?)

        ''' <summary>
        ''' Queries the broker API directly for open positions on a specific instrument.
        ''' Does NOT use the local database — only the API has ground truth on live positions.
        ''' </summary>
        Function GetLiveWorkingOrdersAsync(accountId As Long,
                                           contractId As String,
                                           Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order))

        ''' <summary>
        ''' Returns a snapshot of the live position for the given contract.
        ''' The underlying broker query (SearchOpenPositions) is cached for up to 5 s by
        ''' IOpenPositionsCache (PERF-02) for rate-limit protection — multiple per-contract
        ''' callers within the window share a single REST round-trip.
        ''' Pass bypassCache:=True to force a fresh fetch; required for pre-entry duplicate-
        ''' position guards and manual force-reconcile paths.
        ''' Matches by positionId when supplied; falls back to the first open position for the contract.
        ''' Returns Nothing when no matching live position exists.
        ''' </summary>
        Function GetLivePositionSnapshotAsync(accountId As Long,
                                              contractId As String,
                                              Optional positionId As Long? = Nothing,
                                              Optional bypassCache As Boolean = False,
                                              Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot)

        ''' <summary>
        ''' BUG-94 F2: returns every open broker position for the account (one
        ''' <see cref="LivePositionSnapshot"/> per contract with <c>NetPos &lt;&gt; 0</c>).
        ''' Used by the orphan-scan path inside <c>TradeReconciliationWorker</c> to cross-check
        ''' broker positions against the app-side <c>LiveTradeRecord</c> audit trail without
        ''' a per-contract round-trip.
        ''' </summary>
        Function GetOpenPositionsAsync(accountId As Long,
                                       Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot))

        ''' <summary>
        ''' Closes all live positions for a specific instrument (used by reversal flush).
        ''' Returns True if all closures succeeded or there were no positions to close.
        ''' </summary>
        Function FlattenContractAsync(accountId As Long,
                                      contractId As String,
                                      Optional cancel As CancellationToken = Nothing) As Task(Of Boolean)

        ''' <summary>
        ''' BUG-100: closes the contract AND awaits the broker-confirmed closing fill so the
        ''' caller can persist a truthful ExitPrice/PnL. The hub-side <c>GatewayUserOrder</c>
        ''' push is the preferred source (sub-second). On hub timeout the implementation falls
        ''' back to the REST search-history endpoint. When neither yields a fill within the
        ''' overall timeout, <see cref="BrokerCloseFill"/> is returned as <c>Nothing</c> and
        ''' the caller is expected to fall back to its engine-derived value.
        ''' </summary>
        Function FlattenContractWithFillAsync(accountId As Long,
                                              contractId As String,
                                              Optional cancel As CancellationToken = Nothing) _
            As Task(Of (Success As Boolean, Fill As BrokerCloseFill))

        ''' <summary>
        ''' Updates the SL and/or TP of an open position on the broker.
        ''' Used by the stepped trailing bracket to push free-ride levels to the broker
        ''' so positions are protected even if the engine is stopped.
        ''' Returns True on success.
        ''' </summary>
        Function EditPositionSlTpAsync(positionId As Long,
                                       slRate As Decimal?,
                                       tpRate As Decimal?,
                                       Optional enableTsl As Boolean = False,
                                       Optional cancel As CancellationToken = Nothing) As Task(Of Boolean)

        ''' <summary>
        ''' Partially closes an open position by reducing it to zero contracts for the
        ''' specified <paramref name="size"/>.  Uses the ProjectX partialCloseContract
        ''' endpoint.  Returns True on success.
        ''' </summary>
        Function PartialCloseContractAsync(accountId As Long,
                                           contractId As String,
                                           size As Integer,
                                           Optional cancel As CancellationToken = Nothing) As Task(Of Boolean)
    End Interface

End Namespace
