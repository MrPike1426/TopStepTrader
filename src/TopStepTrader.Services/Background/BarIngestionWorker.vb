Imports System.Threading
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' Periodically fetches new 5-minute bars for the configured contract
    ''' and stores them in the database for ML training and backtesting.
    ''' Default interval: every 6 minutes (aligned with 5-min bar cadence).
    ''' </summary>
    Public Class BarIngestionWorker
        Implements IHostedService, IDisposable

        Private ReadOnly _ingestionService As BarIngestionService
        Private ReadOnly _logger As ILogger(Of BarIngestionWorker)
        Private _timer As System.Threading.Timer
        Private _disposed As Boolean = False

        ' Default contract (ES front month) — updated by UI/settings
        Public Property ContractId As String = String.Empty
        Public Property Timeframe As BarTimeframe = BarTimeframe.FiveMinute

        Public Sub New(ingestionService As BarIngestionService,
                       logger As ILogger(Of BarIngestionWorker))
            _ingestionService = ingestionService
            _logger = logger
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            _logger.LogInformation("BarIngestionWorker started")
            _timer = New System.Threading.Timer(
                AddressOf DoWork, Nothing,
                TimeSpan.FromSeconds(30),   ' Initial delay (let auth settle)
                TimeSpan.FromMinutes(6))    ' Poll interval
            Return Task.CompletedTask
        End Function

        Private Async Sub DoWork(state As Object)
            If String.IsNullOrEmpty(ContractId) Then
                _logger.LogDebug("BarIngestionWorker: no contract configured, skipping")
                Return
            End If
            Try
                Dim inserted = Await _ingestionService.IngestAsync(ContractId, Timeframe, barsToFetch:=100)
                If inserted > 0 Then
                    _logger.LogInformation("BarIngestionWorker: stored {N} new bars for contract {Id}",
                                           inserted, ContractId)
                End If
            Catch ex As Exception
                _logger.LogError(ex, "BarIngestionWorker: error ingesting bars for contract {Id}", ContractId)
            End Try
        End Sub

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _logger.LogInformation("BarIngestionWorker stopping")
            _timer?.Change(Timeout.Infinite, 0)
            Return Task.CompletedTask
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _timer?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
