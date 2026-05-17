Imports System.Threading
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Models.Responses

Namespace TopStepTrader.Services.Debug

    ''' <summary>
    ''' BUG-83 F8: thin wrapper over <see cref="PXOrderClient"/> so the
    ''' reconciliation service can be unit-tested without spinning a real
    ''' HTTP stack. Only the surface required by reconciliation is exposed.
    ''' </summary>
    Public Interface IDebugReconciliationOrderClient
        Function SearchOpenPositionsAsync(accountId As Long, cancel As CancellationToken) As Task(Of PXPositionSearchResponse)
        Function SearchTradesAsync(accountId As Long, startTimestamp As Long?, endTimestamp As Long?, cancel As CancellationToken) As Task(Of PXTradeSearchResponse)
        Function SearchOrdersAsync(accountId As Long, startTimestamp As Long?, endTimestamp As Long?, cancel As CancellationToken) As Task(Of PXOrderSearchResponse)
    End Interface

    Public NotInheritable Class PXOrderClientReconciliationAdapter
        Implements IDebugReconciliationOrderClient

        Private ReadOnly _inner As PXOrderClient

        Public Sub New(inner As PXOrderClient)
            _inner = inner
        End Sub

        Public Function SearchOpenPositionsAsync(accountId As Long, cancel As CancellationToken) As Task(Of PXPositionSearchResponse) _
            Implements IDebugReconciliationOrderClient.SearchOpenPositionsAsync
            Return _inner.SearchOpenPositionsAsync(accountId, cancel)
        End Function

        Public Function SearchTradesAsync(accountId As Long, startTimestamp As Long?, endTimestamp As Long?, cancel As CancellationToken) As Task(Of PXTradeSearchResponse) _
            Implements IDebugReconciliationOrderClient.SearchTradesAsync
            Return _inner.SearchTradesAsync(accountId, startTimestamp, endTimestamp, cancel)
        End Function

        Public Function SearchOrdersAsync(accountId As Long, startTimestamp As Long?, endTimestamp As Long?, cancel As CancellationToken) As Task(Of PXOrderSearchResponse) _
            Implements IDebugReconciliationOrderClient.SearchOrdersAsync
            Return _inner.SearchOrdersAsync(accountId, startTimestamp, endTimestamp, cancel)
        End Function

    End Class

End Namespace
