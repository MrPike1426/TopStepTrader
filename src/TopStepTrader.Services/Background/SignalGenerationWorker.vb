Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' Periodically triggers ML signal generation for all active contracts.
    ''' Pulls recent bars from the DB and invokes ISignalService.GenerateSignalAsync.
    ''' Default interval: every 5 minutes (aligned with 5-min bar close).
    ''' Uses IServiceScopeFactory to resolve the Scoped ISignalService + BarIngestionService
    ''' correctly from this Singleton worker.
    ''' </summary>
    Public Class SignalGenerationWorker
        Implements IHostedService, IDisposable

        Private ReadOnly _scopeFactory    As IServiceScopeFactory
        Private ReadOnly _tradingSettings As TradingSettings
        Private ReadOnly _logger          As ILogger(Of SignalGenerationWorker)
        Private _timer As System.Threading.Timer
        Private _disposed As Boolean = False

        Public Property Timeframe As BarTimeframe = BarTimeframe.FiveMinute

        Public Sub New(scopeFactory    As IServiceScopeFactory,
                       tradingOptions  As IOptions(Of TradingSettings),
                       logger          As ILogger(Of SignalGenerationWorker))
            _scopeFactory    = scopeFactory
            _tradingSettings = tradingOptions.Value
            _logger          = logger
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            _logger.LogInformation("SignalGenerationWorker started")
            _timer = New System.Threading.Timer(
                AddressOf DoWork, Nothing,
                TimeSpan.FromSeconds(60),   ' Initial delay (let bar ingestion settle first)
                TimeSpan.FromMinutes(5))    ' Poll interval
            Return Task.CompletedTask
        End Function

        Private Async Sub DoWork(state As Object)
            Dim contractIds = _tradingSettings.ActiveContractIds
            If contractIds Is Nothing OrElse contractIds.Count = 0 Then
                _logger.LogDebug("SignalGenerationWorker: no contracts configured, skipping")
                Return
            End If

            Using scope = _scopeFactory.CreateScope()
                Dim signalService    = scope.ServiceProvider.GetRequiredService(Of ISignalService)()
                Dim ingestionService = scope.ServiceProvider.GetRequiredService(Of BarIngestionService)()

                For Each contractId In contractIds
                    Try
                        ' Get the most recent bars for the contract
                        Dim bars = Await ingestionService.GetBarsForMLAsync(
                            contractId, Timeframe, maxBars:=100)
                        If bars.Count < 30 Then
                            _logger.LogDebug(
                                "Insufficient bars ({N}) for signal generation on {Id}", bars.Count, contractId)
                            Continue For
                        End If
                        Await signalService.GenerateSignalAsync(contractId, bars)
                    Catch ex As Exception
                        _logger.LogError(ex, "SignalGenerationWorker: error on {Id}", contractId)
                    End Try
                Next
            End Using
        End Sub

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _logger.LogInformation("SignalGenerationWorker stopping")
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
