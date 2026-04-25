Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Fetches historical bars from Yahoo Finance and persists them to the database.
    ''' Lookback limits: 1m=7d, 5m/15m/30m=60d, 60m=730d, 1d=unlimited.
    ''' Unit codes passed to Yahoo: 1=1min, 2=5min, 3=15min, 4=30min, 5=1hr, 6=1day.
    ''' </summary>
    Public Class BarIngestionService
        Implements IBarIngestionService

        Private ReadOnly _yahooClient As YahooFinanceHistoryClient
        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _logger As ILogger(Of BarIngestionService)

        Public Sub New(yahooClient As YahooFinanceHistoryClient,
                       barRepository As BarRepository,
                       logger As ILogger(Of BarIngestionService))
            _yahooClient = yahooClient
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
                                          Optional cancel As CancellationToken = Nothing) As Task(Of Integer) Implements IBarIngestionService.IngestAsync

                                              Dim apiUnit As Integer
            Dim apiUnitNumber As Integer
            TimeframeToApiParams(timeframe, apiUnit, apiUnitNumber)

            ' Determine date range
            Dim endTime = DateTimeOffset.UtcNow
            Dim latestStored = Await _barRepository.GetLatestTimestampAsync(contractId, timeframe)
            Dim startTime As DateTimeOffset

            If latestStored.HasValue Then
                ' Incremental: pick up where we left off
                startTime = latestStored.Value.AddMinutes(1)
            Else
                ' No data yet — use Yahoo's max lookback for this timeframe
                startTime = endTime.Add(MaxLookback(timeframe))
            End If

            _logger.LogInformation("Ingesting {N} bars for contract {Id}, timeframe {Tf}, {From} → {To}",
                                   barsToFetch, contractId, timeframe,
                                   startTime.ToString("g"), endTime.ToString("g"))

            Dim response As TopStepTrader.API.Models.Responses.BarResponse = Nothing
            Try
                response = Await _yahooClient.RetrieveBarsAsync(contractId, apiUnit, startTime, endTime, cancel)
            Catch ex As Exception
                _logger.LogWarning(ex, "Yahoo Finance request failed for {Id} ({Tf})", contractId, timeframe)
            End Try

            If response Is Nothing OrElse Not response.Success OrElse response.Bars Is Nothing OrElse response.Bars.Count = 0 Then
                _logger.LogWarning("Yahoo Finance returned 0 bars for {Id} ({Tf})", contractId, timeframe)
                Return 0
            End If

            ' Map API BarDto → MarketBar
            Dim bars = response.Bars.Select(Function(b) New MarketBar With {
                .ContractId = contractId,
                .Timeframe = CInt(timeframe),
                .Timestamp = DateTimeOffset.Parse(b.Timestamp, Nothing, System.Globalization.DateTimeStyles.RoundtripKind),
                .Open = CDec(b.Open),
                .High = CDec(b.High),
                .Low = CDec(b.Low),
                .Close = CDec(b.Close),
                .Volume = b.Volume,
                .VWAP = (CDec(b.Open) + CDec(b.Close)) / 2D
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
                                                 Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) Implements IBarIngestionService.GetBarsForMLAsync
            Dim result As IList(Of MarketBar) = Await _barRepository.GetRecentBarsAsync(contractId, timeframe, maxBars, cancel)
            Return result
        End Function

        ''' <summary>
        ''' Yahoo Finance does not support sub-minute bars.
        ''' Returns 0 so callers fall back to the strategy-timeframe bar close.
        ''' </summary>
        Public Function GetLatestPriceAsync(contractId As String,
                                            Optional cancel As CancellationToken = Nothing) As Task(Of Decimal) Implements IBarIngestionService.GetLatestPriceAsync
            Return Task.FromResult(0D)
        End Function

        ''' <summary>
        ''' FEAT-11: Yahoo Finance does not support live sub-minute bar fetches.
        ''' Returns an empty list; callers fall back gracefully.
        ''' </summary>
        Public Function GetLiveBarsAsync(contractId As String,
                                         timeframe As BarTimeframe,
                                         barCount As Integer,
                                         Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) Implements IBarIngestionService.GetLiveBarsAsync
            Return Task.FromResult(CType(New List(Of MarketBar)(), IList(Of MarketBar)))
        End Function

        ''' <summary>
        ''' Maximum historical lookback Yahoo Finance supports for each timeframe.
        ''' Used when no bars are stored yet.
        ''' </summary>
        Private Shared Function MaxLookback(tf As BarTimeframe) As TimeSpan
            Select Case tf
                Case BarTimeframe.OneMinute, BarTimeframe.ThreeMinute
                    Return TimeSpan.FromDays(-7)
                Case BarTimeframe.FiveMinute, BarTimeframe.FifteenMinute, BarTimeframe.ThirtyMinute
                    Return TimeSpan.FromDays(-60)
                Case BarTimeframe.OneHour
                    Return TimeSpan.FromDays(-730)
                Case Else   ' Daily
                    Return TimeSpan.FromDays(-3650)
            End Select
        End Function

        Private Shared Sub TimeframeToApiParams(tf As BarTimeframe, ByRef unit As Integer, ByRef unitNumber As Integer)
            Select Case tf
                Case BarTimeframe.OneMinute
                    unit = 1 : unitNumber = 1
                Case BarTimeframe.ThreeMinute
                    unit = 1 : unitNumber = 1
                Case BarTimeframe.FiveMinute
                    unit = 2 : unitNumber = 2
                Case BarTimeframe.FifteenMinute
                    unit = 3 : unitNumber = 3
                Case BarTimeframe.ThirtyMinute
                    unit = 4 : unitNumber = 4
                Case BarTimeframe.OneHour
                    unit = 5 : unitNumber = 5
                Case BarTimeframe.Daily
                    unit = 6 : unitNumber = 6
                Case Else
                    unit = 2 : unitNumber = 2
            End Select
        End Sub

    End Class

End Namespace
