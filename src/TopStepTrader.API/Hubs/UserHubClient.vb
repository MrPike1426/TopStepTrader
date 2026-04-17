Imports System.Threading
Imports Microsoft.AspNetCore.SignalR.Client
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.API.Hubs

    ''' <summary>
    ''' SignalR client for the TopStepX / ProjectX User Hub.
    ''' Delivers real-time order updates, account balance changes, position changes, and trade fills.
    ''' Hub URL: https://rtc.thefuturesdesk.projectx.com/hubs/user?access_token=JWT
    '''
    ''' Note: eToro does not use SignalR. When eToro is the active broker, this hub remains
    ''' disconnected and the REST portfolio endpoint is polled instead.
    ''' </summary>
    Public Class UserHubClient
        Implements IAsyncDisposable

        Private ReadOnly _settings As ProjectXSettings
        Private ReadOnly _tokenManager As ProjectXTokenManager
        Private ReadOnly _logger As ILogger(Of UserHubClient)
        Private _connection As HubConnection

        Public Event OrderUpdated As EventHandler(Of PXOrderUpdateEventArgs)
        Public Event AccountUpdated As EventHandler(Of PXAccountUpdateEventArgs)
        Public Event PositionUpdated As EventHandler(Of PXPositionUpdateEventArgs)
        Public Event TradeReceived As EventHandler(Of PXTradeEventArgs)
        Public Event ConnectionStateChanged As EventHandler(Of HubConnectionState)

        ' ── Legacy eToro event stubs kept for compile compatibility ─────────────────
        Public Event OrderFillReceived As EventHandler(Of OrderFillEventArgs)

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       tokenManager As ProjectXTokenManager,
                       logger As ILogger(Of UserHubClient))
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
                .WithUrl(_settings.UserHubUrl,
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

            _connection.On(Of PXUserOrderData)("GatewayUserOrder",
                Sub(data)
                    _logger.LogDebug("User order: OrderId={Id} Status={Status}", data.Id, data.Status)
                    RaiseEvent OrderUpdated(Me, New PXOrderUpdateEventArgs(data))
                End Sub)

            _connection.On(Of PXUserAccountData)("GatewayUserAccount",
                Sub(data)
                    _logger.LogDebug("Account: Balance={Balance} OpenPnL={PnL}", data.Balance, data.OpenPnL)
                    RaiseEvent AccountUpdated(Me, New PXAccountUpdateEventArgs(data))
                End Sub)

            _connection.On(Of PXUserPositionData)("GatewayUserPosition",
                Sub(data)
                    _logger.LogDebug("Position: {Contract} NetPos={Pos}", data.ContractId, data.NetPos)
                    RaiseEvent PositionUpdated(Me, New PXPositionUpdateEventArgs(data))
                End Sub)

            _connection.On(Of PXUserTradeData)("GatewayUserTrade",
                Sub(data)
                    _logger.LogInformation("Trade: {Contract} Side={Side} @{Price} x{Size}",
                                           data.ContractId, data.Side, data.Price, data.Size)
                    RaiseEvent TradeReceived(Me, New PXTradeEventArgs(data))
                End Sub)

            AddHandler _connection.Reconnecting,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "User hub reconnecting...")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            AddHandler _connection.Reconnected,
                Async Function(connectionId As String) As Task
                    _logger.LogInformation("User hub reconnected: {Id}", connectionId)
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            AddHandler _connection.Closed,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "User hub closed")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            Await _connection.StartAsync(cancel)
            _logger.LogInformation("User hub connected: {Url}", _settings.UserHubUrl)
        End Function

        Public Async Function StopAsync(Optional cancel As CancellationToken = Nothing) As Task
            If _connection IsNot Nothing Then Await _connection.StopAsync(cancel)
        End Function

        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            If _connection IsNot Nothing Then Return _connection.DisposeAsync()
            Return ValueTask.CompletedTask
        End Function

    End Class

    ' ── User Hub DTOs ────────────────────────────────────────────────────────────

    Public Class PXUserOrderData
        Public Property Id As Long
        Public Property AccountId As Long
        Public Property ContractId As String = String.Empty
        Public Property Side As Integer
        Public Property OrderType As Integer
        Public Property Size As Integer
        Public Property LimitPrice As Double?
        Public Property StopPrice As Double?
        Public Property AvgFillPrice As Double?
        Public Property Status As Integer
        Public Property CreationTimestamp As Long
    End Class

    Public Class PXUserAccountData
        Public Property AccountId As Long
        Public Property Balance As Double
        Public Property OpenPnL As Double
        Public Property DailyPnL As Double
    End Class

    Public Class PXUserPositionData
        Public Property AccountId As Long
        Public Property ContractId As String = String.Empty
        Public Property NetPos As Integer
        Public Property NetPrice As Double
        Public Property OpenPnL As Double
    End Class

    Public Class PXUserTradeData
        Public Property Id As Long
        Public Property AccountId As Long
        Public Property ContractId As String = String.Empty
        Public Property Side As Integer
        Public Property Price As Double
        Public Property Size As Integer
        Public Property OrderId As Long
        Public Property CreationTimestamp As Long
    End Class

    ' ── Event args ──────────────────────────────────────────────────────────────

    Public Class PXOrderUpdateEventArgs
        Inherits EventArgs
        Public ReadOnly Property OrderData As PXUserOrderData
        Public Sub New(data As PXUserOrderData)
            OrderData = data
        End Sub
    End Class

    Public Class PXAccountUpdateEventArgs
        Inherits EventArgs
        Public ReadOnly Property AccountData As PXUserAccountData
        Public Sub New(data As PXUserAccountData)
            AccountData = data
        End Sub
    End Class

    Public Class PXPositionUpdateEventArgs
        Inherits EventArgs
        Public ReadOnly Property PositionData As PXUserPositionData
        Public Sub New(data As PXUserPositionData)
            PositionData = data
        End Sub
    End Class

    Public Class PXTradeEventArgs
        Inherits EventArgs
        Public ReadOnly Property TradeData As PXUserTradeData
        Public Sub New(data As PXUserTradeData)
            TradeData = data
        End Sub
    End Class

    ' ── Legacy eToro stubs (kept for compile compatibility) ──────────────────────

    Public Class UserOrderFillData
        Public Property OrderId As Long
        Public Property ContractId As String = String.Empty
        Public Property FillPrice As Double
        Public Property FillSize As Integer
        Public Property Status As Integer
    End Class

    Public Class OrderFillEventArgs
        Inherits EventArgs
        Public ReadOnly Property FillData As UserOrderFillData
        Public Sub New(data As UserOrderFillData)
            FillData = data
        End Sub
    End Class

End Namespace
