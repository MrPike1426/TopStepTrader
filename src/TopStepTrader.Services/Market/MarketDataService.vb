Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.API.Hubs

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Bridges the MarketHubClient SignalR events to the IMarketDataService interface.
    ''' Keeps the last known quote per contract for polling callers.
    ''' </summary>
    Public Class MarketDataService
        Implements IMarketDataService

        Private ReadOnly _hub As MarketHubClient
        Private ReadOnly _logger As ILogger(Of MarketDataService)
        Private ReadOnly _quotes As New ConcurrentDictionary(Of String, Quote)()
        Private ReadOnly _subscriptions As New HashSet(Of String)()
        Private ReadOnly _subLock As New Object()

        Public Event QuoteReceived As EventHandler(Of QuoteEventArgs) Implements IMarketDataService.QuoteReceived
        Public Event BarCompleted As EventHandler(Of BarEventArgs) Implements IMarketDataService.BarCompleted

        Public Sub New(hub As MarketHubClient, logger As ILogger(Of MarketDataService))
            _hub = hub
            _logger = logger
            AddHandler _hub.QuoteReceived, AddressOf OnHubQuote
            AddHandler _hub.BarReceived, AddressOf OnHubBar
        End Sub

        ' Hub uses MarketQuoteEventArgs/MarketBarEventArgs; translate to Core event args
        Private Sub OnHubQuote(sender As Object, e As MarketQuoteEventArgs)
            _quotes(e.Quote.ContractId) = e.Quote
            RaiseEvent QuoteReceived(Me, New QuoteEventArgs(e.Quote))
        End Sub

        Private Sub OnHubBar(sender As Object, e As MarketBarEventArgs)
            RaiseEvent BarCompleted(Me, New BarEventArgs(e.Bar))
        End Sub

        Public Async Function SubscribeAsync(contractId As String) As Task _
            Implements IMarketDataService.SubscribeAsync
            SyncLock _subLock
                If _subscriptions.Contains(contractId) Then Return
                _subscriptions.Add(contractId)
            End SyncLock
            Await _hub.SubscribeContractAsync(contractId)
            _logger.LogInformation("Subscribed to contract {Id}", contractId)
        End Function

        Public Async Function UnsubscribeAsync(contractId As String) As Task _
            Implements IMarketDataService.UnsubscribeAsync
            SyncLock _subLock
                _subscriptions.Remove(contractId)
            End SyncLock
            Await _hub.UnsubscribeContractAsync(contractId)
            _logger.LogInformation("Unsubscribed from contract {Id}", contractId)
        End Function

        Public Function GetCurrentQuoteAsync(contractId As String) As Task(Of Quote) _
            Implements IMarketDataService.GetCurrentQuoteAsync
            Dim q As Quote = Nothing
            _quotes.TryGetValue(contractId, q)
            Return Task.FromResult(q)
        End Function

        Public Function IsSubscribed(contractId As String) As Boolean _
            Implements IMarketDataService.IsSubscribed
            SyncLock _subLock
                Return _subscriptions.Contains(contractId)
            End SyncLock
        End Function

    End Class

End Namespace
