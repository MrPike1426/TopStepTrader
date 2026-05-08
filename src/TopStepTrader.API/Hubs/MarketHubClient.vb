Imports System.Threading
Imports Microsoft.AspNetCore.SignalR.Client
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.API.Hubs

    ''' <summary>
    ''' SignalR client for the TopStepX / ProjectX Market Hub.
    ''' Delivers real-time quotes, trades, and bars via events.
    ''' Hub URL: https://rtc.thefuturesdesk.projectx.com/hubs/market?access_token=JWT
    ''' </summary>
    Public Class MarketHubClient
        Implements IAsyncDisposable
        Implements IMarketQuoteFeed

        Private ReadOnly _settings As ProjectXSettings
        Private ReadOnly _tokenManager As ProjectXTokenManager
        Private ReadOnly _logger As ILogger(Of MarketHubClient)
        Private _connection As HubConnection
        Private ReadOnly _subscribedContracts As New HashSet(Of String)

        Public Event QuoteReceived As EventHandler(Of MarketQuoteEventArgs) Implements IMarketQuoteFeed.QuoteReceived
        Public Event BarReceived As EventHandler(Of MarketBarEventArgs)
        Public Event ConnectionStateChanged As EventHandler(Of HubConnectionState)

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       tokenManager As ProjectXTokenManager,
                       logger As ILogger(Of MarketHubClient))
            _settings = options.Value
            _tokenManager = tokenManager
            _logger = logger
        End Sub

        Public ReadOnly Property State As HubConnectionState
            Get
                Return If(_connection Is Nothing, HubConnectionState.Disconnected, _connection.State)
            End Get
        End Property

        Public Async Function StartAsync(Optional cancel As CancellationToken = Nothing) As Task
            _connection = New HubConnectionBuilder() _
                .WithUrl(_settings.MarketHubUrl,
                         Sub(opts)
                             opts.AccessTokenProvider =
                                 Function() _tokenManager.GetValidTokenAsync()
                         End Sub) _
                .WithAutomaticReconnect(New TimeSpan() {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30)}) _
                .Build()

            _connection.On(Of MarketQuoteData)("GatewayQuote",
                Sub(data)
                    RaiseEvent QuoteReceived(Me, New MarketQuoteEventArgs(MapToQuote(data)))
                End Sub)

            _connection.On(Of MarketBarData)("GatewayBar",
                Sub(data)
                    RaiseEvent BarReceived(Me, New MarketBarEventArgs(MapToBar(data)))
                End Sub)

            AddHandler _connection.Reconnecting,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "Market hub reconnecting...")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            AddHandler _connection.Reconnected,
                Async Function(connectionId As String) As Task
                    _logger.LogInformation("Market hub reconnected: {Id}", connectionId)
                    For Each contractId In _subscribedContracts.ToList()
                        Await ResubscribeAsync(contractId)
                    Next
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                End Function

            AddHandler _connection.Closed,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "Market hub closed")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            Await _connection.StartAsync(cancel)
            _logger.LogInformation("Market hub connected: {Url}", _settings.MarketHubUrl)
            RaiseEvent ConnectionStateChanged(Me, _connection.State)
        End Function

        Public Async Function SubscribeContractAsync(contractId As String,
                                                      Optional cancel As CancellationToken = Nothing) As Task Implements IMarketQuoteFeed.SubscribeContractAsync
            If _connection Is Nothing Then Await StartAsync(cancel)
            If _subscribedContracts.Contains(contractId) Then Return
            Await ResubscribeAsync(contractId, cancel)
            _subscribedContracts.Add(contractId)
        End Function

        Public Async Function UnsubscribeContractAsync(contractId As String,
                                                        Optional cancel As CancellationToken = Nothing) As Task Implements IMarketQuoteFeed.UnsubscribeContractAsync
            If Not _subscribedContracts.Contains(contractId) Then Return
            Await _connection.InvokeAsync("UnsubscribeContractQuotes", contractId, cancel)
            Await _connection.InvokeAsync("UnsubscribeContractTrades", contractId, cancel)
            _subscribedContracts.Remove(contractId)
        End Function

        Private Async Function ResubscribeAsync(contractId As String,
                                                 Optional cancel As CancellationToken = Nothing) As Task
            Await _connection.InvokeAsync("SubscribeContractQuotes", contractId, cancel)
            Await _connection.InvokeAsync("SubscribeContractTrades", contractId, cancel)
            _logger.LogInformation("Subscribed market data: {ContractId}", contractId)
        End Function

        Private Shared Function MapToQuote(data As MarketQuoteData) As Quote
            Return New Quote With {
                .ContractId = data.ContractId,
                .Timestamp = DateTimeOffset.UtcNow,
                .BidPrice = CDec(data.Bp),
                .AskPrice = CDec(data.Ap),
                .LastPrice = CDec(data.Lp),
                .BidSize = data.Bs,
                .AskSize = data.As_,
                .Volume = data.V
            }
        End Function

        Private Shared Function MapToBar(data As MarketBarData) As MarketBar
            Return New MarketBar With {
                .ContractId = data.ContractId,
                .Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(data.T),
                .Open = CDec(data.O),
                .High = CDec(data.H),
                .Low = CDec(data.L),
                .Close = CDec(data.C),
                .Volume = data.V
            }
        End Function

        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            If _connection IsNot Nothing Then Return _connection.DisposeAsync()
            Return ValueTask.CompletedTask
        End Function

    End Class

    ' ── SignalR message DTOs ─────────────────────────────────────────────────────

    Public Class MarketQuoteData
        Public Property ContractId As String = String.Empty
        Public Property Bp As Double        ' Bid price
        Public Property Ap As Double        ' Ask price
        Public Property Lp As Double        ' Last price
        Public Property Bs As Integer       ' Bid size
        Public Property As_ As Integer      ' Ask size (trailing underscore avoids VB keyword)
        Public Property V As Long           ' Volume
    End Class

    Public Class MarketBarData
        Public Property ContractId As String = String.Empty
        Public Property T As Long           ' Timestamp unix ms
        Public Property O As Double
        Public Property H As Double
        Public Property L As Double
        Public Property C As Double
        Public Property V As Long
    End Class

    Public Class MarketQuoteEventArgs
        Inherits EventArgs
        Public ReadOnly Property Quote As Quote
        Public Sub New(q As Quote)
            Quote = q
        End Sub
    End Class

    Public Class MarketBarEventArgs
        Inherits EventArgs
        Public ReadOnly Property Bar As MarketBar
        Public Sub New(b As MarketBar)
            Bar = b
        End Sub
    End Class

End Namespace
