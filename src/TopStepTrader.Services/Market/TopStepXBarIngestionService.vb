Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Trading

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Fetches historical OHLCV bars from TopStepX (ProjectX) and persists them to SQLite.
    ''' Used exclusively by live trading views: Hydra, Asset Bassett, Sniper, CryptoJoe,
    ''' Test Trade, and PumpNDump.
    ''' Accepts TopStepX PX contract IDs directly (e.g. "CON.F.US.MCLE.K26").
    ''' POST /api/History/retrieveBars — rate-limited by the shared RateLimiter (50 req/30 s).
    ''' </summary>
    Public Class TopStepXBarIngestionService
        Implements IBarIngestionService

        Private ReadOnly _pxHistoryClient As PXHistoryClient
        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _catalog As TopStepXInstrumentCatalog
        Private ReadOnly _logger As ILogger(Of TopStepXBarIngestionService)

        Public Sub New(pxHistoryClient As PXHistoryClient,
                       barRepository As BarRepository,
                       catalog As TopStepXInstrumentCatalog,
                       logger As ILogger(Of TopStepXBarIngestionService))
            _pxHistoryClient = pxHistoryClient
            _barRepository = barRepository
            _catalog = catalog
            _logger = logger
        End Sub

        ''' <summary>
        ''' Fetch and store up to <paramref name="barsToFetch"/> bars from TopStepX.
        ''' Incremental: resumes from the latest stored timestamp when bars already exist.
        ''' </summary>
        Public Async Function IngestAsync(contractId As String,
                                          timeframe As BarTimeframe,
                                          Optional barsToFetch As Integer = 500,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of Integer) Implements IBarIngestionService.IngestAsync

            ' Translate eToro/Yahoo symbol (e.g. "OIL", "GOLD.24-7") to PX contract ID
            ' (e.g. "CON.F.US.MCLE.K26") so StrategyExecutionEngine can pass its ContractId
            ' directly without knowing about the ProjectX naming convention.
            ' Then resolve to the active front-month via the instrument catalog so that
            ' rolled contracts (e.g. MGC.J26 → MGC.M26) continue to receive bar data.
            Dim favContract = FavouriteContracts.TryGetBySymbol(contractId)
            If favContract IsNot Nothing AndAlso Not String.IsNullOrEmpty(favContract.PxContractId) Then
                Dim resolved = Await _catalog.GetResolvedContractIdAsync(favContract, cancel)
                If Not String.IsNullOrEmpty(resolved) Then
                    If Not String.Equals(resolved, favContract.PxContractId, StringComparison.OrdinalIgnoreCase) Then
                        _logger.LogInformation("Bar ingestion: resolved {Old} → {New} (front-month roll)",
                                               favContract.PxContractId, resolved)
                    End If
                    contractId = resolved
                Else
                    contractId = favContract.PxContractId
                End If
            End If

            Dim unit = TimeframeToUnit(timeframe)
            Dim unitNumber = TimeframeToUnitNumber(timeframe)
            Dim endTime = DateTimeOffset.UtcNow
            Dim latestStored = Await _barRepository.GetLatestTimestampAsync(contractId, timeframe)
            Dim startTime As DateTimeOffset

            If latestStored.HasValue Then
                ' Incremental: pick up where we left off
                startTime = latestStored.Value.AddMinutes(1)
            Else
                ' No data yet — use TopStepX max lookback for this timeframe
                startTime = endTime.AddDays(MaxLookbackDays(timeframe))
            End If

            _logger.LogInformation("PX ingesting {N} bars for {Id}, tf={Tf}, {From}→{To}",
                                   barsToFetch, contractId, timeframe,
                                   startTime.ToString("g"), endTime.ToString("g"))

            Dim response As BarResponse = Nothing
            Try
                response = Await _pxHistoryClient.RetrieveBarsAsync(
                    contractId, unit, unitNumber, barsToFetch,
                    live:=False,
                    startTime:=startTime,
                    endTime:=endTime,
                    cancel:=cancel)
            Catch ex As Exception
                _logger.LogWarning(ex, "TopStepX history request failed for {Id} ({Tf})", contractId, timeframe)
            End Try

            If response Is Nothing OrElse Not response.Success OrElse
               response.Bars Is Nothing OrElse response.Bars.Count = 0 Then
                _logger.LogWarning("TopStepX returned 0 bars for {Id} ({Tf})", contractId, timeframe)
                Return 0
            End If

            ' Parse bars individually so a single malformed entry does not kill the entire batch.
            Dim bars As New List(Of MarketBar)(response.Bars.Count)
            Dim skipped As Integer = 0
            For Each b In response.Bars
                Try
                    Dim ts As DateTimeOffset
                    If Not DateTimeOffset.TryParse(b.Timestamp, Nothing,
                                                   System.Globalization.DateTimeStyles.RoundtripKind, ts) Then
                        skipped += 1
                        _logger.LogWarning("PX: skipping bar with unparseable timestamp '{Ts}' for {Id}",
                                           b.Timestamp, contractId)
                        Continue For
                    End If
                    bars.Add(New MarketBar With {
                        .ContractId = contractId,
                        .Timeframe = CInt(timeframe),
                        .Timestamp = ts,
                        .Open = CDec(b.Open),
                        .High = CDec(b.High),
                        .Low = CDec(b.Low),
                        .Close = CDec(b.Close),
                        .Volume = b.Volume,
                        .VWAP = (CDec(b.Open) + CDec(b.Close)) / 2D
                    })
                Catch ex As Exception
                    skipped += 1
                    _logger.LogWarning(ex, "PX: skipping malformed bar for {Id} ({Tf})", contractId, timeframe)
                End Try
            Next

            If skipped > 0 Then
                _logger.LogWarning("PX: skipped {Skip}/{Total} bars for {Id} ({Tf})",
                                   skipped, response.Bars.Count, contractId, timeframe)
            End If

            If bars.Count = 0 Then
                _logger.LogWarning("PX: all {Total} bars failed parsing for {Id} ({Tf})",
                                   response.Bars.Count, contractId, timeframe)
                Return 0
            End If

            Dim inserted = Await _barRepository.BulkInsertAsync(bars, timeframe, cancel)
            _logger.LogInformation("PX: stored {N} new bars for {Id} (parsed {P}/{T})",
                                   inserted, contractId, bars.Count, response.Bars.Count)
            Return inserted
        End Function

        ''' <summary>Returns the N most recent bars from the DB for the strategy engine.</summary>
        Public Async Function GetBarsForMLAsync(contractId As String,
                                                 timeframe As BarTimeframe,
                                                 Optional maxBars As Integer = 200,
                                                 Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) Implements IBarIngestionService.GetBarsForMLAsync
            ' Mirror the same symbol translation as IngestAsync so the DB key matches.
            Dim favContract = FavouriteContracts.TryGetBySymbol(contractId)
            If favContract IsNot Nothing AndAlso Not String.IsNullOrEmpty(favContract.PxContractId) Then
                Dim resolved = Await _catalog.GetResolvedContractIdAsync(favContract, cancel)
                contractId = If(Not String.IsNullOrEmpty(resolved), resolved, favContract.PxContractId)
            End If
            Return Await _barRepository.GetRecentBarsAsync(contractId, timeframe, maxBars, cancel)
        End Function

        ''' <summary>
        ''' Returns the most recent bar close price via a 2-second bar query (no DB write).
        ''' Used exclusively for live P&amp;L calculation during active positions.
        ''' The 2-second cadence matches the position management timer period (BUG-22).
        ''' Returns 0 on any failure so callers fall back gracefully.
        ''' </summary>
        Public Async Function GetLatestPriceAsync(contractId As String,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of Decimal) Implements IBarIngestionService.GetLatestPriceAsync
            Try
                Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
                If fav Is Nothing OrElse String.IsNullOrEmpty(fav.PxContractId) Then Return 0D
                Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav, cancel)
                Dim pxId = If(Not String.IsNullOrEmpty(resolved), resolved, fav.PxContractId)
                Dim response = Await _pxHistoryClient.RetrieveBarsAsync(
                    pxId, unit:=1, unitNumber:=2, limit:=5,
                    live:=False, startTime:=DateTimeOffset.UtcNow.AddMinutes(-2),
                    endTime:=DateTimeOffset.UtcNow, cancel:=cancel)
                If response Is Nothing OrElse response.Bars Is Nothing OrElse response.Bars.Count = 0 Then
                    Return 0D
                End If
                Return CDec(response.Bars.Last().Close)
            Catch ex As Exception
                _logger.LogDebug(ex, "GetLatestPriceAsync: 15-second bar fetch failed for {Id}", contractId)
                Return 0D
            End Try
        End Function

        ''' <summary>
        ''' FEAT-11: Fetches the N most recent bars for the given timeframe directly from the
        ''' TopStepX API without persisting them to the database.  Used exclusively by
        ''' MultiConfluence flat sessions that poll on 15-second bar closes.
        ''' Returns an empty list on any failure; callers fall back gracefully.
        ''' </summary>
        Public Async Function GetLiveBarsAsync(contractId As String,
                                               timeframe As BarTimeframe,
                                               barCount As Integer,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) Implements IBarIngestionService.GetLiveBarsAsync
            Try
                Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
                If fav Is Nothing OrElse String.IsNullOrEmpty(fav.PxContractId) Then
                    Return New List(Of MarketBar)()
                End If
                Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav, cancel)
                Dim pxId = If(Not String.IsNullOrEmpty(resolved), resolved, fav.PxContractId)
                Dim unit = TimeframeToUnit(timeframe)
                Dim unitNumber = TimeframeToUnitNumber(timeframe)
                ' Request a window long enough to get barCount completed bars.
                ' 15-second bars: barCount × 15 s + 60 min safety margin.
                ' The +60 min (increased from +5 min) accounts for practice-account data gaps,
                ' CME maintenance windows, and session-break periods where no new 15s bars are
                ' produced for up to ~45 min.  Without the wider window the startTime would sit
                ' right at the edge of the last active period and the API would return bars
                ' that are immediately stale, causing every MC tile to declare market closed.
                Dim lookbackMinutes = If(timeframe = BarTimeframe.FifteenSecond,
                                        Math.Ceiling(barCount * 15.0 / 60.0) + 60,
                                        barCount * CDbl(_strategy_TimeframeMinutesForLiveBar(timeframe)) + 5)
                Dim response = Await _pxHistoryClient.RetrieveBarsAsync(
                    pxId, unit:=unit, unitNumber:=unitNumber, limit:=barCount,
                    live:=False,
                    startTime:=DateTimeOffset.UtcNow.AddMinutes(-lookbackMinutes),
                    endTime:=DateTimeOffset.UtcNow, cancel:=cancel)
                If response Is Nothing OrElse Not response.Success OrElse
                   response.Bars Is Nothing OrElse response.Bars.Count = 0 Then
                    _logger.LogDebug("GetLiveBarsAsync: 0 bars returned for {Id} ({Tf})", contractId, timeframe)
                    Return New List(Of MarketBar)()
                End If
                Dim result As New List(Of MarketBar)(response.Bars.Count)
                For Each b In response.Bars
                    Dim ts As DateTimeOffset
                    If Not DateTimeOffset.TryParse(b.Timestamp, Nothing,
                                                   System.Globalization.DateTimeStyles.RoundtripKind, ts) Then
                        Continue For
                    End If
                    result.Add(New MarketBar With {
                        .ContractId = pxId,
                        .Timeframe = CInt(timeframe),
                        .Timestamp = ts,
                        .Open = CDec(b.Open),
                        .High = CDec(b.High),
                        .Low = CDec(b.Low),
                        .Close = CDec(b.Close),
                        .Volume = b.Volume,
                        .VWAP = (CDec(b.Open) + CDec(b.Close)) / 2D
                    })
                Next
                Return result
            Catch ex As Exception
                _logger.LogDebug(ex, "GetLiveBarsAsync: bar fetch failed for {Id} ({Tf})", contractId, timeframe)
                Return New List(Of MarketBar)()
            End Try
        End Function

        ''' <summary>Helper: returns the bar period in minutes for lookback calculation.</summary>
        Private Shared Function _strategy_TimeframeMinutesForLiveBar(tf As BarTimeframe) As Integer
            Select Case tf
                Case BarTimeframe.OneMinute : Return 1
                Case BarTimeframe.ThreeMinute : Return 3
                Case BarTimeframe.FiveMinute : Return 5
                Case BarTimeframe.FifteenMinute : Return 15
                Case BarTimeframe.ThirtyMinute : Return 30
                Case BarTimeframe.OneHour : Return 60
                Case Else : Return 5
            End Select
        End Function

        ''' <summary>
        ''' Maps BarTimeframe to the TopStepX AggregateBarUnit TYPE enum.
        ''' 1=Second, 2=Minute, 3=Hour, 4=Day  (per /api/History/retrieveBars swagger schema).
        ''' </summary>
        Private Shared Function TimeframeToUnit(tf As BarTimeframe) As Integer
            Select Case tf
                Case BarTimeframe.TwoSecond : Return 1                  ' Second
                Case BarTimeframe.FifteenSecond : Return 1              ' Second
                Case BarTimeframe.OneMinute, BarTimeframe.ThreeMinute,
                     BarTimeframe.FiveMinute, BarTimeframe.FifteenMinute,
                     BarTimeframe.ThirtyMinute : Return 2               ' Minute
                Case BarTimeframe.OneHour : Return 3                    ' Hour
                Case BarTimeframe.Daily : Return 4                      ' Day
                Case Else : Return 2
            End Select
        End Function

        ''' <summary>
        ''' Maps BarTimeframe to the TopStepX unitNumber (period multiplier).
        ''' e.g. FiveMinute → unit=2 (Minute), unitNumber=5 → 5-minute bars.
        ''' FifteenSecond → unit=1 (Second), unitNumber=15 → 15-second bars.
        ''' </summary>
        Private Shared Function TimeframeToUnitNumber(tf As BarTimeframe) As Integer
            Select Case tf
                Case BarTimeframe.TwoSecond : Return 2
                Case BarTimeframe.FifteenSecond : Return 15
                Case BarTimeframe.OneMinute : Return 1
                Case BarTimeframe.ThreeMinute : Return 3
                Case BarTimeframe.FiveMinute : Return 5
                Case BarTimeframe.FifteenMinute : Return 15
                Case BarTimeframe.ThirtyMinute : Return 30
                Case BarTimeframe.OneHour : Return 1
                Case BarTimeframe.Daily : Return 1
                Case Else : Return 5
            End Select
        End Function

        ''' <summary>
        ''' Maximum lookback supported by TopStepX for each timeframe (as a negative day offset
        ''' for use with DateTimeOffset.AddDays).
        ''' TopStepX intraday history mirrors Yahoo Finance limits: 1m=7d, 5m/15m/30m=60d, 1h=90d.
        ''' </summary>
        Private Shared Function MaxLookbackDays(tf As BarTimeframe) As Integer
            Select Case tf
                Case BarTimeframe.FifteenSecond : Return -1  ' 15-second bars: 1 day lookback is ample for 80 bars
                Case BarTimeframe.OneMinute, BarTimeframe.ThreeMinute : Return -7
                Case BarTimeframe.FiveMinute, BarTimeframe.FifteenMinute, BarTimeframe.ThirtyMinute : Return -60
                Case BarTimeframe.OneHour : Return -90
                Case Else : Return -365
            End Select
        End Function

    End Class

End Namespace
