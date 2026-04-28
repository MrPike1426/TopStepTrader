Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' Periodically fetches new 5-minute bars for all configured contracts
    ''' and stores them in the database for ML training and backtesting.
    ''' Default interval: every 6 minutes (aligned with 5-min bar cadence).
    ''' Uses IServiceScopeFactory to resolve the Scoped BarIngestionService
    ''' correctly from this Singleton worker.
    ''' </summary>
    Public Class BarIngestionWorker
        Implements IHostedService, IDisposable

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _tradingSettings As TradingSettings
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of BarIngestionWorker)
        Private _timer As System.Threading.Timer
        Private _disposed As Boolean = False

        Public Property Timeframe As BarTimeframe = BarTimeframe.FiveMinute

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       tradingOptions As IOptions(Of TradingSettings),
                       session As ITradingSessionContext,
                       logger As ILogger(Of BarIngestionWorker))
            _scopeFactory = scopeFactory
            _tradingSettings = tradingOptions.Value
            _session = session
            _logger = logger
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            If Not _tradingSettings.EnableBackgroundIngestion Then
                _logger.LogInformation("BarIngestionWorker: disabled via EnableBackgroundIngestion setting, not starting")
                Return Task.CompletedTask
            End If
            _logger.LogInformation("BarIngestionWorker started [{Broker}] (contracts: {Ids})",
                                   ActiveBroker, String.Join(", ", GetActiveContractIds()))
            _timer = New System.Threading.Timer(
                AddressOf DoWork, Nothing,
                TimeSpan.FromSeconds(30),   ' Initial delay (let auth settle)
                TimeSpan.FromMinutes(6))    ' Poll interval
            Return Task.CompletedTask
        End Function

        Private ReadOnly Property ActiveBroker As BrokerType
            Get
                Return _session.ActiveBroker
            End Get
        End Property

        Private Function GetActiveContractIds() As IReadOnlyList(Of String)
            Return FavouriteContracts.GetDefaults().
                Select(Function(f) f.PxContractId).ToList()
        End Function

        Private Async Sub DoWork(state As Object)
            Dim contractIds = GetActiveContractIds()
            If contractIds Is Nothing OrElse contractIds.Count = 0 Then
                _logger.LogDebug("BarIngestionWorker: no contracts configured, skipping")
                Return
            End If

            Using scope = _scopeFactory.CreateScope()
                Dim ingestionService = scope.ServiceProvider.GetRequiredService(Of IBarIngestionService)()
                For Each contractId In contractIds
                    Try
                        Dim inserted = Await ingestionService.IngestAsync(
                            contractId, Timeframe, barsToFetch:=100)
                        If inserted > 0 Then
                            _logger.LogInformation(
                                "BarIngestionWorker: stored {N} new bars for {Id}", inserted, contractId)
                        End If
                    Catch ex As Exception
                        _logger.LogError(ex, "BarIngestionWorker: error ingesting {Id}", contractId)
                    End Try
                Next
            End Using
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
