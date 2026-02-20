Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Core.Interfaces

    Public Interface IMarketDataService
        Event QuoteReceived As EventHandler(Of QuoteEventArgs)
        Event BarCompleted As EventHandler(Of BarEventArgs)
        Function SubscribeAsync(contractId As Integer) As Task
        Function UnsubscribeAsync(contractId As Integer) As Task
        Function GetCurrentQuoteAsync(contractId As Integer) As Task(Of Quote)
        Function IsSubscribed(contractId As Integer) As Boolean
    End Interface

End Namespace
