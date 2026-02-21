Imports System.Threading
Imports Microsoft.AspNetCore.SignalR.Client
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.API.Hubs

    ''' <summary>
    ''' SignalR client for the ProjectX Market Hub.
    ''' Provides live quote and bar data via events.
    ''' </summary>
    Public Class MarketHubClient
        Implements IAsyncDisposable

        Private ReadOnly _settings As ApiSettings
        Private ReadOnly _tokenManager As TokenManager
        Private ReadOnly _logger As ILogger(Of MarketHubClient)
        Private _connection As HubConnection
        Private ReadOnly _subscribedContracts As New HashSet(Of String)

        Public Event QuoteReceived As EventHandler(Of MarketQuoteEventArgs)
        Public Event BarReceived As EventHandler(Of MarketBarEventArgs)
        Public Event ConnectionStateChanged As EventHandler(Of HubConnectionState)

        Public Sub New(options As IOptions(Of ApiSettings),
                       tokenManager As TokenManager,
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
                             opts.AccessTokenProvider = Function()
                                 Return _tokenManager.GetValidTokenAsync()
                             End Function
                         End Sub) _
                .WithAutomaticReconnect(New TimeSpan() {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30)}) _
                .Build()

            ' Wire up hub message handlers
            _connection.On(Of MarketQuoteData)("GatewayQuote",
                Sub(data)
                    RaiseEvent QuoteReceived(Me, New MarketQuoteEventArgs(MapToQuote(data)))
                End Sub)

            _connection.On(Of MarketBarData)("GatewayBar",
                Sub(data)
                    RaiseEvent BarReceived(Me, New MarketBarEventArgs(MapToBar(data)))
                End Sub)

            ' Reconnecting: Func(Of Exception, Task)
            AddHandler _connection.Reconnecting,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "Market hub reconnecting...")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            ' Reconnected: Func(Of String, Task)
            AddHandler _connection.Reconnected,
                Async Function(connectionId As String) As Task
                    _logger.LogInformation("Market hub reconnected: {Id}", connectionId)
                    For Each contractId In _subscribedContracts.ToList()
                        Await ResubscribeAsync(contractId)
                    Next
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                End Function

            ' Closed: Func(Of Exception, Task)
            AddHandler _connection.Closed,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "Market hub connection closed")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            Await _connection.StartAsync(cancel)
            _logger.LogInformation("Market hub connected to {Url}", _settings.MarketHubUrl)
            RaiseEvent ConnectionStateChanged(Me, _connection.State)
        End Function

        Public Async Function SubscribeContractAsync(contractId As String,
                                                      Optional cancel As CancellationToken = Nothing) As Task
            If _subscribedContracts.Contains(contractId) Then Return
            Await ResubscribeAsync(contractId, cancel)
            _subscribedContracts.Add(contractId)
        End Function

        Public Async Function UnsubscribeContractAsync(contractId As String,
                                                        Optional cancel As CancellationToken = Nothing) As Task
            If Not _subscribedContracts.Contains(contractId) Then Return
            Await _connection.InvokeAsync("UnsubscribeContractQuotes", contractId, cancel)
            Await _connection.InvokeAsync("UnsubscribeContractTrades", contractId, cancel)
            _subscribedContracts.Remove(contractId)
            _logger.LogInformation("Unsubscribed from contract {ContractId}", contractId)
        End Function

        Private Async Function ResubscribeAsync(contractId As String,
                                                 Optional cancel As CancellationToken = Nothing) As Task
            Await _connection.InvokeAsync("SubscribeContractQuotes", contractId, cancel)
            Await _connection.InvokeAsync("SubscribeContractTrades", contractId, cancel)
            _logger.LogInformation("Subscribed to market data for contract {ContractId}", contractId)
        End Function

        Private Function MapToQuote(data As MarketQuoteData) As Quote
            Return New Quote With {
                .ContractId = data.ContractId,
                .Timestamp = DateTimeOffset.UtcNow,
                .BidPrice = CDec(data.Bp),
                .AskPrice = CDec(data.Ap),
                .LastPrice = CDec(data.Lp),
                .BidSize = data.Bs,
                .AskSize = data.AskSize,
                .Volume = data.V
            }
        End Function

        Private Function MapToBar(data As MarketBarData) As MarketBar
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
            If _connection IsNot Nothing Then
                Return _connection.DisposeAsync()
            End If
            Return ValueTask.CompletedTask
        End Function

    End Class

    ' ---- SignalR message DTOs ----

    Public Class MarketQuoteData
        Public Property ContractId As String = String.Empty
        Public Property Bp As Double        ' Bid price
        Public Property Ap As Double        ' Ask price
        Public Property Lp As Double        ' Last price
        Public Property Bs As Integer       ' Bid size
        Public Property AskSize As Integer  ' Ask size (renamed to avoid keyword conflict)
        Public Property V As Long           ' Volume
    End Class

    Public Class MarketBarData
        Public Property ContractId As String = String.Empty
        Public Property T As Long           ' Timestamp (unix ms)
        Public Property O As Double         ' Open
        Public Property H As Double         ' High
        Public Property L As Double         ' Low
        Public Property C As Double         ' Close
        Public Property V As Long           ' Volume
    End Class

    Public Class MarketQuoteEventArgs
        Inherits EventArgs
        Public ReadOnly Property Quote As Quote
        Public Sub New(quote As Quote)
            Me.Quote = quote
        End Sub
    End Class

    Public Class MarketBarEventArgs
        Inherits EventArgs
        Public ReadOnly Property Bar As MarketBar
        Public Sub New(bar As MarketBar)
            Me.Bar = bar
        End Sub
    End Class

End Namespace
