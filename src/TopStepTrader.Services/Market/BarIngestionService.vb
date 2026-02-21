Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.API.Http
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Fetches historical bars from the ProjectX API and persists them to the database.
    ''' Maps API bar unit codes: 1=1min, 2=5min, 3=15min, 4=30min, 5=1hr, 6=1day.
    ''' </summary>
    Public Class BarIngestionService

        Private ReadOnly _historyClient As HistoryClient
        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _logger As ILogger(Of BarIngestionService)

        Public Sub New(historyClient As HistoryClient,
                       barRepository As BarRepository,
                       logger As ILogger(Of BarIngestionService))
            _historyClient = historyClient
            _barRepository = barRepository
            _logger = logger
        End Sub

        ''' <summary>
        ''' Fetch and store up to <paramref name="barsToFetch"/> bars for a contract.
        ''' Skips bars already in the database (based on latest stored timestamp).
        ''' </summary>
        Public Async Function IngestAsync(contractId As String,
                                          timeframe As BarTimeframe,
                                          Optional barsToFetch As Integer = 500,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of Integer)
            Dim apiUnit = TimeframeToApiUnit(timeframe)

            ' Find the latest bar we already have to avoid re-fetching
            Dim latestStored = Await _barRepository.GetLatestTimestampAsync(contractId, timeframe)
            Dim startTime As DateTimeOffset? = Nothing
            If latestStored.HasValue Then
                startTime = latestStored.Value.AddMinutes(1)
            End If

            _logger.LogInformation(
                "Ingesting {N} bars for contract {Id}, timeframe {Tf}, since {Since}",
                barsToFetch, contractId, timeframe, If(startTime.HasValue, startTime.Value.ToString("g"), "beginning"))

            Dim response = Await _historyClient.RetrieveBarsAsync(
                contractId, apiUnit, barsToFetch, startTime, Nothing, cancel)

            If response Is Nothing OrElse Not response.Success Then
                _logger.LogWarning("History API returned failure for contract {Id}: {Msg}",
                                   contractId, response?.ErrorMessage)
                Return 0
            End If

            If response.Bars Is Nothing OrElse response.Bars.Count = 0 Then
                _logger.LogInformation("No new bars returned for contract {Id}", contractId)
                Return 0
            End If

            ' Map API BarDto → MarketBar (BulkInsertAsync expects domain objects)
            Dim bars = response.Bars.Select(Function(b) New MarketBar With {
                .ContractId = contractId,
                .Timeframe = CInt(timeframe),
                .Timestamp = DateTimeOffset.Parse(b.Timestamp, Nothing, System.Globalization.DateTimeStyles.RoundtripKind),
                .Open = CDec(b.Open),
                .High = CDec(b.High),
                .Low = CDec(b.Low),
                .Close = CDec(b.Close),
                .Volume = b.Volume,
                .VWAP = (CDec(b.Open) + CDec(b.Close)) / 2D   ' Approx until real VWAP available
            }).ToList()

            Dim inserted = Await _barRepository.BulkInsertAsync(bars, timeframe, cancel)
            _logger.LogInformation("Stored {N} new bars for contract {Id}", inserted, contractId)
            Return inserted
        End Function

        ''' <summary>
        ''' Returns the N most recent bars from the DB as domain objects for the ML engine.
        ''' </summary>
        Public Async Function GetBarsForMLAsync(contractId As String,
                                                 timeframe As BarTimeframe,
                                                 Optional maxBars As Integer = 200,
                                                 Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar))
            ' GetRecentBarsAsync already returns domain MarketBar objects sorted ascending
            Return Await _barRepository.GetRecentBarsAsync(contractId, timeframe, maxBars, cancel)
        End Function

        Private Shared Function TimeframeToApiUnit(tf As BarTimeframe) As Integer
            Select Case tf
                Case BarTimeframe.OneMinute : Return 1
                Case BarTimeframe.FiveMinute : Return 2
                Case BarTimeframe.FifteenMinute : Return 3
                Case BarTimeframe.ThirtyMinute : Return 4
                Case BarTimeframe.OneHour : Return 5
                Case BarTimeframe.Daily : Return 6
                Case Else : Return 2  ' Default to 5min
            End Select
        End Function

    End Class

End Namespace
