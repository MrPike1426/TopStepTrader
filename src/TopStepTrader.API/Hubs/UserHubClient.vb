Imports System.Threading
Imports Microsoft.AspNetCore.SignalR.Client
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.API.Hubs

    ''' <summary>
    ''' SignalR client for the ProjectX User Hub.
    ''' Delivers order fills, account updates, and position changes in real time.
    ''' </summary>
    Public Class UserHubClient
        Implements IAsyncDisposable

        Private ReadOnly _settings As ApiSettings
        Private ReadOnly _tokenManager As TokenManager
        Private ReadOnly _logger As ILogger(Of UserHubClient)
        Private _connection As HubConnection

        Public Event OrderFillReceived As EventHandler(Of OrderFillEventArgs)
        Public Event AccountUpdated As EventHandler(Of AccountUpdateEventArgs)
        Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs)
        Public Event ConnectionStateChanged As EventHandler(Of HubConnectionState)

        Public Sub New(options As IOptions(Of ApiSettings),
                       tokenManager As TokenManager,
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

            ' Order fill notification
            _connection.On(Of UserOrderFillData)("GatewayUserOrder",
                Sub(data)
                    _logger.LogInformation("Order fill: OrderId={Id}, Price={Price}", data.OrderId, data.FillPrice)
                    RaiseEvent OrderFillReceived(Me, New OrderFillEventArgs(data))
                End Sub)

            ' Account balance update
            _connection.On(Of UserAccountData)("GatewayUserAccount",
                Sub(data)
                    _logger.LogDebug("Account updated: Balance={Balance}", data.Balance)
                    RaiseEvent AccountUpdated(Me, New AccountUpdateEventArgs(data))
                End Sub)

            ' Position update
            _connection.On(Of UserPositionData)("GatewayUserPosition",
                Sub(data)
                    _logger.LogDebug("Position: Contract={Id}, Size={Size}", data.ContractId, data.NetPos)
                    RaiseEvent PositionUpdated(Me, New PositionUpdateEventArgs(data))
                End Sub)

            ' Reconnecting: Func(Of Exception, Task)
            AddHandler _connection.Reconnecting,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "User hub reconnecting...")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            ' Reconnected: Func(Of String, Task)
            AddHandler _connection.Reconnected,
                Async Function(connectionId As String) As Task
                    _logger.LogInformation("User hub reconnected: {Id}", connectionId)
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            ' Closed: Func(Of Exception, Task)
            AddHandler _connection.Closed,
                Async Function(ex As Exception) As Task
                    _logger.LogWarning(ex, "User hub connection closed")
                    RaiseEvent ConnectionStateChanged(Me, _connection.State)
                    Await Task.CompletedTask
                End Function

            Await _connection.StartAsync(cancel)
            _logger.LogInformation("User hub connected to {Url}", _settings.UserHubUrl)
        End Function

        Public Async Function StopAsync(Optional cancel As CancellationToken = Nothing) As Task
            If _connection IsNot Nothing Then
                Await _connection.StopAsync(cancel)
            End If
        End Function

        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            If _connection IsNot Nothing Then
                Return _connection.DisposeAsync()
            End If
            Return ValueTask.CompletedTask
        End Function

    End Class

    ' ---- User Hub DTOs ----

    Public Class UserOrderFillData
        Public Property OrderId As Long
        Public Property AccountId As Long
        Public Property ContractId As String = String.Empty
        Public Property Side As Integer
        Public Property FillPrice As Double
        Public Property FillSize As Integer
        Public Property Status As Integer
        Public Property Timestamp As Long
    End Class

    Public Class UserAccountData
        Public Property AccountId As Long
        Public Property Balance As Double
        Public Property OpenPnL As Double
        Public Property DailyPnL As Double
    End Class

    Public Class UserPositionData
        Public Property AccountId As Long
        Public Property ContractId As String = String.Empty
        Public Property NetPos As Integer       ' positive=long, negative=short, 0=flat
        Public Property AvgPrice As Double
        Public Property OpenPnL As Double
    End Class

    Public Class OrderFillEventArgs
        Inherits EventArgs
        Public ReadOnly Property FillData As UserOrderFillData
        Public Sub New(data As UserOrderFillData)
            FillData = data
        End Sub
    End Class

    Public Class AccountUpdateEventArgs
        Inherits EventArgs
        Public ReadOnly Property AccountData As UserAccountData
        Public Sub New(data As UserAccountData)
            AccountData = data
        End Sub
    End Class

    Public Class PositionUpdateEventArgs
        Inherits EventArgs
        Public ReadOnly Property PositionData As UserPositionData
        Public Sub New(data As UserPositionData)
            PositionData = data
        End Sub
    End Class

End Namespace
