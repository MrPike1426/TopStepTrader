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
        ''' Queries the eToro API directly for open positions on a specific instrument.
        ''' Does NOT use the local database — only the API has ground truth on live positions.
        ''' </summary>
        Function GetLiveWorkingOrdersAsync(accountId As Long,
                                           contractId As String,
                                           Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order))

        ''' <summary>
        ''' Returns an API-authoritative snapshot of the live position for the given contract,
        ''' including broker-reported unrealised P&amp;L and open timestamp.
        ''' Matches by positionId when supplied; falls back to the first open position for the contract.
        ''' Returns Nothing when no matching live position exists.
        ''' </summary>
        Function GetLivePositionSnapshotAsync(accountId As Long,
                                              contractId As String,
                                              Optional positionId As Long? = Nothing,
                                              Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot)

        ''' <summary>
        ''' Closes all live positions for a specific instrument (used by reversal flush).
        ''' Returns True if all closures succeeded or there were no positions to close.
        ''' </summary>
        Function FlattenContractAsync(accountId As Long,
                                      contractId As String,
                                      Optional cancel As CancellationToken = Nothing) As Task(Of Boolean)

        ''' <summary>
        ''' Updates the SL and/or TP of an open position on the broker.
        ''' Used by the stepped trailing bracket to push free-ride levels to eToro
        ''' so positions are protected even if the engine is stopped.
        ''' Set enableTsl=True to re-enable eToro's native Trailing Stop Loss from the
        ''' new SL level — this is the documented path and avoids minimum-distance
        ''' rejections from the undocumented PUT /positions endpoint.
        ''' Returns True on success.
        ''' </summary>
        Function EditPositionSlTpAsync(positionId As Long,
                                       slRate As Decimal?,
                                       tpRate As Decimal?,
                                       Optional enableTsl As Boolean = False,
                                       Optional cancel As CancellationToken = Nothing) As Task(Of Boolean)
    End Interface

End Namespace
