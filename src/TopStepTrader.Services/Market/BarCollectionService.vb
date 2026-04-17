Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Implements <see cref="IBarCollectionService"/>.
    '''
    ''' Ensures bars of the requested timeframe exist in the local SQLite database for a given
    ''' contract and date range, downloading missing bars from Yahoo Finance if required.
    ''' Yahoo Finance is used regardless of the active broker — it provides free, accurate
    ''' historical data (5-min: up to 60 days; 60-min: up to 730 days; daily: unlimited).
    '''
    ''' Algorithm:
    '''   1. Count bars already in SQLite for (contractId, timeframe, startDate–endDate).
    '''   2. If ≥ 50 and they span ≥ 80% of the requested range → return success (cache hit).
    '''   3. If insufficient → fetch from Yahoo Finance in one request and store to SQLite.
    '''   4. Count final total and return success/failure result.
    '''
    ''' Deduplication: handled by BarRepository.BulkInsertAsync (INSERT OR IGNORE).
    ''' </summary>
    Public Class BarCollectionService
        Implements IBarCollectionService

        ' BacktestEngine requires at least 50 bars; reject below this threshold
        Private Const MinBarsForBacktest As Integer = 50

        Private ReadOnly _yahooClient As YahooFinanceHistoryClient
        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _logger As ILogger(Of BarCollectionService)

        Public Sub New(yahooClient As YahooFinanceHistoryClient,
                       barRepository As BarRepository,
                       logger As ILogger(Of BarCollectionService))
            _yahooClient = yahooClient
            _barRepository = barRepository
            _logger = logger
        End Sub

        ''' <inheritdoc/>
        Public Async Function EnsureBarsAsync(
                contractId As String,
                startDate As Date,
                endDate As Date,
                timeframe As BarTimeframe,
                Optional progress As IProgress(Of String) = Nothing,
                Optional cancel As CancellationToken = Nothing) As Task(Of BarEnsureResult) _
                Implements IBarCollectionService.EnsureBarsAsync

            If String.IsNullOrWhiteSpace(contractId) Then
                Return Fail(contractId, "Contract ID is required", progress)
            End If

            ' Resolve API unit code for Yahoo Finance interval mapping
            Dim apiUnit As Integer
            Dim apiUnitNumber As Integer
            Dim barMinutes As Integer  ' unused by Yahoo path but kept for helper signature
            TimeframeToApiParams(timeframe, apiUnit, apiUnitNumber, barMinutes)

            Dim tfLabel = TimeframeLabel(timeframe)

            ' Date range as UTC DateTimeOffset  (endDate + 1 day makes end inclusive)
            Dim fromDt = New DateTimeOffset(DateTime.SpecifyKind(startDate, DateTimeKind.Unspecified), TimeSpan.Zero)
            Dim toDt = New DateTimeOffset(DateTime.SpecifyKind(endDate.AddDays(1), DateTimeKind.Unspecified), TimeSpan.Zero)

            ' ── Step 1: Check existing bars in SQLite ──────────────────────────────────
            progress?.Report($"⏳ Checking local {tfLabel} bars for {contractId}...")

            Dim existing As List(Of MarketBar)
            Try
                existing = Await _barRepository.GetBarsAsync(
                    contractId, timeframe, fromDt, toDt, cancel)
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                _logger.LogError(ex, "EnsureBarsAsync: DB query failed for {Contract}", contractId)
                Return Fail(contractId, $"Database error: {ex.Message}", progress)
            End Try

            If existing.Count >= MinBarsForBacktest Then
                ' UAT-BUG-007: A simple count ≥ 50 cache hit is too permissive.
                ' Live-trading bar downloads only fetch recent bars (e.g. 1 day).
                ' If those bars happen to fall inside the requested range they satisfy
                ' the ≥ 50 count but the backtest then only sees 1 day of data.
                '
                ' Fix: also validate that the cached bars span the majority of the
                ' requested date range.  For short ranges (≤ 7 calendar days) the simple
                ' count check is sufficient; for longer ranges the bars must cover at
                ' least 80 % of the requested span, measured from earliest-to-latest bar.
                Dim rangeSpan = (toDt - fromDt).TotalDays

                Dim spanOk As Boolean
                If rangeSpan <= 7D Then
                    spanOk = True
                Else
                    Dim earliestBar = existing.Min(Function(b) b.Timestamp)
                    Dim latestBar = existing.Max(Function(b) b.Timestamp)
                    Dim coveredDays = (latestBar - earliestBar).TotalDays
                    spanOk = coveredDays >= rangeSpan * 0.8D
                End If

                If spanOk Then
                    ' Staleness check: if the most recent bar is older than 24 hours, force a
                    ' re-download so that weekly runs keep accumulating fresh bars in the DB.
                    ' INSERT OR IGNORE deduplication means re-fetching overlapping bars is safe.
                    Dim latestBar = existing.Max(Function(b) b.Timestamp)
                    Dim isStale = latestBar < DateTimeOffset.UtcNow.AddHours(-24)

                    If Not isStale Then
                        Dim msg = $"✓ {existing.Count:N0} {tfLabel} bars already available for {contractId}"
                        progress?.Report(msg)
                        _logger.LogInformation(
                            "EnsureBarsAsync: {Count} {Tf} bars in DB for {Contract} — span OK and fresh, skipping download",
                            existing.Count, tfLabel, contractId)
                        Return New BarEnsureResult With {
                            .Success = True,
                            .BarCount = existing.Count,
                            .ContractId = contractId,
                            .Message = msg
                        }
                    End If

                    _logger.LogInformation(
                        "EnsureBarsAsync: {Count} {Tf} bars in DB for {Contract} — span OK but stale (latest={Latest:u}), re-downloading",
                        existing.Count, tfLabel, contractId, latestBar)
                    progress?.Report($"⏳ {tfLabel} bars exist but are stale — refreshing {contractId}...")
                End If

                _logger.LogInformation(
                    "EnsureBarsAsync: {Count} {Tf} bars in DB for {Contract} but they don't cover " &
                    "the requested range — downloading missing history",
                    existing.Count, tfLabel, contractId)
            End If

            ' ── Step 2: Download from Yahoo Finance (single request for full date range) ──
            _logger.LogInformation(
                "EnsureBarsAsync: {Count} {Tf} bars in DB for {Contract} (need ≥ {Min}). " &
                "Downloading from Yahoo Finance (unit={U})...",
                existing.Count, tfLabel, contractId, MinBarsForBacktest, apiUnit)

            progress?.Report($"⏳ Downloading {tfLabel} bars for {contractId} from Yahoo Finance...")

            Dim totalFetched As Integer = 0
            Dim totalInserted As Integer = 0

            ' Translate contractId to Yahoo Finance ticker (e.g. "GOLD.24-7" → "GC=F").
            ' FavouriteContracts.YahooSymbol holds the correct ticker for each instrument.
            ' Falls back to contractId unchanged so direct Yahoo symbols (e.g. "GC=F") work too.
            Dim yahooTicker = contractId
            Dim favForYahoo = FavouriteContracts.TryGetBySymbol(contractId)
            If favForYahoo IsNot Nothing AndAlso Not String.IsNullOrEmpty(favForYahoo.YahooSymbol) Then
                yahooTicker = favForYahoo.YahooSymbol
            End If

            Dim yahooResponse As API.Models.Responses.BarResponse = Nothing
            Try
                yahooResponse = Await _yahooClient.RetrieveBarsAsync(
                    yahooTicker,
                    unit:=apiUnit,
                    startTime:=fromDt,
                    endTime:=toDt,
                    cancel:=cancel)
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "EnsureBarsAsync: Yahoo Finance error for {Contract} — aborting download",
                    contractId)
            End Try

            If yahooResponse IsNot Nothing AndAlso yahooResponse.Success AndAlso
               yahooResponse.Bars IsNot Nothing AndAlso yahooResponse.Bars.Count > 0 Then

                ' ── Map BarDto → MarketBar ────────────────────────────────────────────
                ' Non-native timeframes (TenMinute, TwoHour, FourHour) download their nearest
                ' native Yahoo interval (5-min or 1-hr) then aggregate here into the correct
                ' candle width before inserting.  Using source timeframe during construction
                ' so AggregateBars can detect candle boundaries by source width.
                Dim sourceTimeframe As BarTimeframe
                Select Case timeframe
                    Case BarTimeframe.TenMinute  : sourceTimeframe = BarTimeframe.FiveMinute
                    Case BarTimeframe.TwoHour    : sourceTimeframe = BarTimeframe.OneHour
                    Case BarTimeframe.FourHour   : sourceTimeframe = BarTimeframe.OneHour
                    Case Else                    : sourceTimeframe = timeframe
                End Select

                Dim rawBars = yahooResponse.Bars _
                    .Select(Function(b) New MarketBar With {
                        .ContractId = contractId,
                        .Timeframe = sourceTimeframe,
                        .Timestamp = DateTimeOffset.Parse(
                                          b.Timestamp, Nothing,
                                          System.Globalization.DateTimeStyles.RoundtripKind),
                        .Open = CDec(b.Open),
                        .High = CDec(b.High),
                        .Low = CDec(b.Low),
                        .Close = CDec(b.Close),
                        .Volume = b.Volume,
                        .VWAP = (CDec(b.Open) + CDec(b.Close)) / 2D
                    }) _
                    .OrderBy(Function(b) b.Timestamp) _
                    .ToList()

                ' Aggregate non-native timeframes into their correct candle width.
                Dim bars As List(Of MarketBar)
                If sourceTimeframe <> timeframe Then
                    bars = AggregateBars(rawBars, timeframe)
                Else
                    ' Native timeframe — update the Timeframe field (was set to sourceTimeframe above)
                    For Each b In rawBars
                        b.Timeframe = timeframe
                    Next
                    bars = rawBars
                End If

                totalFetched = bars.Count

                ' ── Persist to SQLite (INSERT OR IGNORE for deduplication) ────────────
                Try
                    totalInserted = Await _barRepository.BulkInsertAsync(bars, timeframe, cancel)
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    _logger.LogError(ex,
                        "EnsureBarsAsync: BulkInsertAsync failed for {Contract}",
                        contractId)
                End Try

                progress?.Report(
                    $"⏳ {contractId} ({tfLabel}): {totalFetched:N0} bars fetched from Yahoo Finance, {totalInserted:N0} stored...")

                _logger.LogInformation(
                    "EnsureBarsAsync: {Fetched} bars from Yahoo Finance for {Contract}, {Inserted} inserted",
                    totalFetched, contractId, totalInserted)
            Else
                ' Surface the Yahoo error directly so the user can see what went wrong
                Dim errDetail = If(yahooResponse?.ErrorMessage, "no data returned")
                _logger.LogWarning(
                    "EnsureBarsAsync: Yahoo Finance returned no bars for {Contract}: {Error}",
                    contractId, errDetail)
                progress?.Report($"⚠ Yahoo Finance: {errDetail}")
            End If

            ' ── Step 3: Final count of bars in SQLite for the requested range ──────────
            ' For intraday Yahoo data the bars span the last 60d regardless of the user's
            ' StartDate. Widen the DB query to the full available range (all stored bars for
            ' this contract + timeframe) so a cache-hit is possible even when StartDate is
            ' older than 60 days.
            Dim queryFrom = DateTimeOffset.UtcNow.AddDays(-800)   ' covers 730d 60-min window
            Dim queryTo   = DateTimeOffset.UtcNow.AddDays(1)

            Dim finalBars As New List(Of MarketBar)()
            Try
                finalBars = Await _barRepository.GetBarsAsync(
                    contractId, timeframe, queryFrom, queryTo, cancel)
            Catch ex As OperationCanceledException
                Throw
            Catch
                ' Use count 0 — result below will set the right message
            End Try

            Dim success = finalBars.Count >= MinBarsForBacktest

            Dim yahooErrDetail = If(yahooResponse IsNot Nothing AndAlso Not yahooResponse.Success,
                                    yahooResponse.ErrorMessage, Nothing)

            Dim finalMessage As String
            If success Then
                Dim earliest = finalBars.Min(Function(b) b.Timestamp)
                Dim latest   = finalBars.Max(Function(b) b.Timestamp)
                finalMessage = $"✓ {finalBars.Count:N0} {tfLabel} bars available for {contractId} " &
                               $"({earliest:MM/dd/yyyy} – {latest:MM/dd/yyyy})  [Yahoo Finance]"
            ElseIf finalBars.Count > 0 Then
                finalMessage = $"⚠ Only {finalBars.Count:N0} {tfLabel} bars available for {contractId} — " &
                               "Yahoo Finance may have returned partial data."
            ElseIf Not String.IsNullOrEmpty(yahooErrDetail) Then
                finalMessage = $"✗ Yahoo Finance error for {contractId}: {yahooErrDetail}"
            Else
                finalMessage = $"✗ No {tfLabel} bars returned for {contractId} from Yahoo Finance. " &
                               "Check that the symbol is supported (SPX500, NSDQ100, GOLD.24-7, OIL, UK100, BTC, ETH)."
            End If

            progress?.Report(finalMessage)
            _logger.LogInformation(
                "EnsureBarsAsync: complete — {Count} {Tf} bars for {Contract} in range, success={Ok}",
                finalBars.Count, tfLabel, contractId, success)

            Return New BarEnsureResult With {
                .Success = success,
                .BarCount = finalBars.Count,
                .ContractId = contractId,
                .Message = finalMessage
            }
        End Function

        ' ── Helpers ───────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' Maps a BarTimeframe to the ProjectX API unit codes and the bar width in minutes.
        ''' Mirrors BarIngestionService.TimeframeToApiParams so both services use identical mappings.
        '''   API codes: unit=1 → 1-min, unit=2 → 5-min, unit=3 → 15-min,
        '''              unit=4 → 30-min, unit=5 → 1-hour, unit=6 → daily.
        ''' </summary>
        Private Shared Sub TimeframeToApiParams(tf As BarTimeframe,
                                                ByRef unit As Integer,
                                                ByRef unitNumber As Integer,
                                                ByRef barMinutes As Integer)
            Select Case tf
                Case BarTimeframe.OneMinute
                    unit = 1 : unitNumber = 1 : barMinutes = 1
                Case BarTimeframe.ThreeMinute
                    ' Yahoo has no 3-min interval; use 1-min as closest source
                    unit = 1 : unitNumber = 1 : barMinutes = 3
                Case BarTimeframe.FiveMinute
                    unit = 2 : unitNumber = 2 : barMinutes = 5
                Case BarTimeframe.TenMinute
                    ' Yahoo has no 10-min interval; use 5-min as closest source
                    unit = 2 : unitNumber = 2 : barMinutes = 10
                Case BarTimeframe.FifteenMinute
                    unit = 3 : unitNumber = 3 : barMinutes = 15
                Case BarTimeframe.ThirtyMinute
                    unit = 4 : unitNumber = 4 : barMinutes = 30
                Case BarTimeframe.OneHour
                    unit = 5 : unitNumber = 5 : barMinutes = 60
                Case BarTimeframe.TwoHour
                    ' Yahoo has no 2-hr interval; use 1-hr as closest source
                    unit = 5 : unitNumber = 5 : barMinutes = 120
                Case BarTimeframe.FourHour
                    ' Yahoo has no 4-hr interval; use 1-hr as closest source
                    unit = 5 : unitNumber = 5 : barMinutes = 240
                Case Else
                    unit = 2 : unitNumber = 2 : barMinutes = 5
            End Select
        End Sub

        ''' <summary>
        ''' Aggregates a sorted list of source bars into wider candles for non-native timeframes.
        '''
        ''' Groups source bars by aligned UTC time windows of the target width so candle
        ''' boundaries always fall on even multiples of the target interval (e.g. 2-hr candles
        ''' open at 00:00, 02:00, 04:00 … UTC).  Only complete groups are emitted — trailing
        ''' partial candles at the end of the series are discarded.
        '''
        ''' OHLCV aggregation:
        '''   Open  = first bar in window
        '''   High  = Max(High) across all bars in window
        '''   Low   = Min(Low)  across all bars in window
        '''   Close = last bar in window
        '''   Volume = Sum
        '''   VWAP   = arithmetic mean of constituent bar VWAPs (approximation)
        '''
        ''' Used for: TenMinute (2 × 5-min), TwoHour (2 × 1-hr), FourHour (4 × 1-hr).
        ''' </summary>
        Private Shared Function AggregateBars(sourceBars As List(Of MarketBar),
                                              targetTimeframe As BarTimeframe) As List(Of MarketBar)
            Dim targetMinutes As Long = CLng(targetTimeframe)   ' enum value IS the minute count
            Dim windowSeconds As Long = targetMinutes * 60L

            ' Group by aligned UTC window: floor(epochSeconds / windowSeconds)
            Dim grouped = sourceBars _
                .GroupBy(Function(b) b.Timestamp.ToUnixTimeSeconds() \ windowSeconds) _
                .OrderBy(Function(g) g.Key) _
                .ToList()

            Dim result As New List(Of MarketBar)()
            For Each grp In grouped
                Dim grpBars = grp.OrderBy(Function(b) b.Timestamp).ToList()
                Dim agg As New MarketBar With {
                    .ContractId = grpBars(0).ContractId,
                    .Timeframe  = targetTimeframe,
                    .Timestamp  = grpBars(0).Timestamp,
                    .Open       = grpBars(0).Open,
                    .High       = grpBars.Max(Function(b) b.High),
                    .Low        = grpBars.Min(Function(b) b.Low),
                    .Close      = grpBars.Last().Close,
                    .Volume     = CLng(grpBars.Sum(Function(b) CDec(b.Volume))),
                    .VWAP       = grpBars.Average(Function(b) b.VWAP.GetValueOrDefault())
                }
                result.Add(agg)
            Next
            Return result
        End Function

        ''' <summary>Short display label for a timeframe, e.g. "5-min", "1-hour".</summary>
        Private Shared Function TimeframeLabel(tf As BarTimeframe) As String
            Select Case tf
                Case BarTimeframe.OneMinute : Return "1-min"
                Case BarTimeframe.ThreeMinute : Return "3-min"
                Case BarTimeframe.FiveMinute : Return "5-min"
                Case BarTimeframe.TenMinute : Return "10-min"
                Case BarTimeframe.FifteenMinute : Return "15-min"
                Case BarTimeframe.ThirtyMinute : Return "30-min"
                Case BarTimeframe.OneHour : Return "1-hour"
                Case BarTimeframe.TwoHour : Return "2-hour"
                Case BarTimeframe.FourHour : Return "4-hour"
                Case Else : Return tf.ToString()
            End Select
        End Function

        Private Shared Function Fail(contractId As String,
                                     message As String,
                                     progress As IProgress(Of String)) As BarEnsureResult
            progress?.Report($"✗ {message}")
            Return New BarEnsureResult With {
                .Success = False,
                .BarCount = 0,
                .ContractId = contractId,
                .Message = message
            }
        End Function

    End Class

End Namespace
