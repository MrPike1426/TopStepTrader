Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.API.Hubs

    ''' <summary>
    ''' Minimal abstraction over the MarketHub quote feed so consumers (and tests)
    ''' can subscribe to per-contract live quotes without depending on the concrete
    ''' SignalR client.
    ''' Implemented by <see cref="MarketHubClient"/> in production; replaced by a
    ''' fake in unit tests.
    ''' </summary>
    Public Interface IMarketQuoteFeed
        Event QuoteReceived As EventHandler(Of MarketQuoteEventArgs)

        Function SubscribeContractAsync(contractId As String,
                                        Optional cancel As CancellationToken = Nothing) As Task

        Function UnsubscribeContractAsync(contractId As String,
                                          Optional cancel As CancellationToken = Nothing) As Task
    End Interface

End Namespace
