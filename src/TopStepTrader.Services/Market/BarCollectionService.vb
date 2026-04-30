Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Trading

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Implements <see cref="IBarCollectionService"/>.
    '''
    ''' Ensures bars of the requested timeframe exist in the local SQLite database for a given
    ''' contract and date range, downloading missing bars from the TopStepX ProjectX API.
    ''' Bars are fetched in 5,000-bar pages covering up to 60 calendar days.
    '''
    ''' Symbol resolution:
    '''   contractId may be a display symbol ("GOLD.24-7"), a PX root ("MGC"), or a full PX ID
    '''   ("CON.F.US.MGC.Q26").  The service resolves to PxContractId for the API call, but
    '''   stores bars under the original contractId so the storage key is stable across contract rolls.
    '''
    ''' Algorithm:
    '''   1. Count bars already in SQLite for (contractId, timeframe, startDate–endDate).
    '''   2. If ≥ 50, span ≥ 80%, and fresh (latest bar &lt; 24 h old) → return success (cache hit).
    '''   3. Resolve PX contract ID → paginate API (5 000 bars/page) → INSERT OR IGNORE into SQLite.
    '''   4. Count final total and return success/failure result.
    '''
    ''' Non-native timeframes: TwoHour / FourHour download 1-hr source bars and aggregate accordingly.
    ''' </summary>
    Public Class BarCollectionService
        Implements IBarCollectionService

        ' BacktestEngine requires at least 50 bars; reject below this threshold
        Private Const MinBarsForBacktest As Integer = 50
        Private Const PageSize As Integer = 5000

        Private ReadOnly _pxHistoryClient As PXHistoryClient
        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _catalog As TopStepXInstrumentCatalog
        Private ReadOnly _logger As ILogger(Of BarCollectionService)

        Public Sub New(pxHistoryClient As PXHistoryClient,
                       barRepository As BarRepository,
                       catalog As TopStepXInstrumentCatalog,
                       logger As ILogger(Of BarCollectionService))
            _pxHistoryClient = pxHistoryClient
            _barRepository = barRepository
            _catalog = catalog
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
                            .Message = msg,
                            .WasCacheHit = True
                        }
                    End If

                    _logger.LogInformation(
                        "EnsureBarsAsync: {Count} {Tf} bars in DB for {Contract} — stale (latest={Latest:u}), re-downloading",
                        existing.Count, tfLabel, contractId, latestBar)
                    progress?.Report($"⏳ {tfLabel} bars exist but are stale — refreshing {contractId}...")
                End If
            End If

            ' ── Step 2: Resolve PX contract ID ────────────────────────────────────────
            ' The contractId stored in SQLite is the stable symbol key (e.g. "GOLD.24-7").
            ' The API call requires the active front-month PX contract ID (e.g. "CON.F.US.MGC.Q25").
            ' Use TopStepXInstrumentCatalog.GetResolvedContractIdAsync — the same catalog-backed
            ' resolution used by TopStepXBarIngestionService — so that quarterly rolls are handled
            ' automatically and we never request a far-out contract that has no bar history yet.
            Dim pxContractId As String = contractId
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
            If fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId) Then
                If _catalog IsNot Nothing Then
                    Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav, cancel)
                    If Not String.IsNullOrEmpty(resolved) Then
                        If Not String.Equals(resolved, fav.PxContractId, StringComparison.OrdinalIgnoreCase) Then
                            _logger.LogInformation(
                                "EnsureBarsAsync: resolved {Old} → {New} (front-month roll) for {Contract}",
                                fav.PxContractId, resolved, contractId)
                        End If
                        pxContractId = resolved
                    Else
                        pxContractId = fav.PxContractId
                    End If
                Else
                    pxContractId = fav.PxContractId
                End If
            End If

            ' Determine source timeframe for non-native intervals
            Dim sourceTimeframe As BarTimeframe
            Dim sourceUnit As Integer
            Dim sourceUnitNumber As Integer
            Select Case timeframe
                Case BarTimeframe.TwoHour
                    sourceTimeframe = BarTimeframe.OneHour
                    sourceUnit = 3 : sourceUnitNumber = 1
                Case BarTimeframe.FourHour
                    sourceTimeframe = BarTimeframe.OneHour
                    sourceUnit = 3 : sourceUnitNumber = 1
                Case Else
                    sourceTimeframe = timeframe
                    TimeframeToApiParams(timeframe, sourceUnit, sourceUnitNumber)
            End Select

            ' ── Step 3: Download from TopStepX in paginated 5 000-bar batches ──────────
            _logger.LogInformation(
                "EnsureBarsAsync: downloading {Tf} bars for {Contract} (PX={PxId}) from TopStepX...",
                tfLabel, contractId, pxContractId)

            progress?.Report($"⏳ Downloading {tfLabel} bars for {contractId} from TopStepX...")

            Dim allRawBars As New List(Of MarketBar)()
            Dim pageStart = fromDt
            Dim pagesDone As Integer = 0

            Do
                Dim pxResponse As API.Models.Responses.BarResponse = Nothing
                Try
                    pxResponse = Await _pxHistoryClient.RetrieveBarsAsync(
                        pxContractId,
                        unit:=sourceUnit,
                        unitNumber:=sourceUnitNumber,
                        limit:=PageSize,
                        live:=False,
                        startTime:=pageStart,
                        endTime:=toDt,
                        cancel:=cancel)
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "EnsureBarsAsync: TopStepX error for {Contract} page {Page} — stopping",
                        contractId, pagesDone + 1)
                    Exit Do
                End Try

                If pxResponse Is Nothing OrElse Not pxResponse.Success OrElse
                   pxResponse.Bars Is Nothing OrElse pxResponse.Bars.Count = 0 Then
                    Dim errDetail = If(pxResponse?.ErrorMessage, "no data returned")
                    _logger.LogWarning(
                        "EnsureBarsAsync: TopStepX returned no bars for {Contract} page {Page}: {Error}",
                        contractId, pagesDone + 1, errDetail)
                    If pagesDone = 0 Then
                        progress?.Report($"⚠ TopStepX: {errDetail}")
                    End If
                    Exit Do
                End If

                Dim pageBars As New List(Of MarketBar)(pxResponse.Bars.Count)
                For Each b In pxResponse.Bars
                    Try
                        Dim ts As DateTimeOffset
                        If Not DateTimeOffset.TryParse(b.Timestamp, Nothing,
                                                       Globalization.DateTimeStyles.RoundtripKind, ts) Then
                            Continue For
                        End If
                        If Not (b.Open > 0 AndAlso b.High > 0 AndAlso b.Low > 0 AndAlso b.Close > 0) Then
                            Continue For
                        End If
                        pageBars.Add(New MarketBar With {
                            .ContractId = contractId,   ' store under stable symbol key
                            .Timeframe = sourceTimeframe,
                            .Timestamp = ts,
                            .Open = CDec(b.Open),
                            .High = CDec(b.High),
                            .Low = CDec(b.Low),
                            .Close = CDec(b.Close),
                            .Volume = b.Volume,
                            .VWAP = (CDec(b.Open) + CDec(b.Close)) / 2D
                        })
                    Catch
                        ' Skip malformed bar
                    End Try
                Next

                allRawBars.AddRange(pageBars)
                pagesDone += 1

                progress?.Report(
                    $"⏳ {contractId} ({tfLabel}): {allRawBars.Count:N0} bars downloaded so far...")

                ' Stop paging if this is the last page (fewer bars than limit)
                If pxResponse.Bars.Count < PageSize Then Exit Do

                ' Advance window: start from 1 tick past the newest bar in this page
                Dim newestInPage = pageBars.Max(Function(b) b.Timestamp)
                pageStart = newestInPage.AddMinutes(1)

                ' Safety: stop if we've passed the requested end
                If pageStart >= toDt Then Exit Do

                ' Safety: cap total pages at 30 to avoid runaway loops
                If pagesDone >= 30 Then Exit Do
            Loop

            ' Aggregate non-native timeframes into their correct candle width
            Dim finalBars As List(Of MarketBar)
            If sourceTimeframe <> timeframe Then
                finalBars = AggregateBars(allRawBars, timeframe)
            Else
                For Each b In allRawBars
                    b.Timeframe = timeframe
                Next
                finalBars = allRawBars
            End If

            Dim totalInserted As Integer = 0
            If finalBars.Count > 0 Then
                Try
                    totalInserted = Await _barRepository.BulkInsertAsync(finalBars, timeframe, cancel)
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    _logger.LogError(ex, "EnsureBarsAsync: BulkInsertAsync failed for {Contract}", contractId)
                End Try

                progress?.Report(
                    $"⏳ {contractId} ({tfLabel}): {finalBars.Count:N0} bars downloaded, {totalInserted:N0} stored...")
                _logger.LogInformation(
                    "EnsureBarsAsync: {Fetched} bars from TopStepX for {Contract}, {Inserted} inserted",
                    finalBars.Count, contractId, totalInserted)
            End If

            ' ── Step 4: Final count of bars in SQLite for the requested range ──────────
            Dim countBars As New List(Of MarketBar)()
            Try
                countBars = Await _barRepository.GetBarsAsync(contractId, timeframe, fromDt, toDt, cancel)
            Catch ex As OperationCanceledException
                Throw
            Catch
                ' Use count 0 — message below will reflect failure
            End Try

            Dim success = countBars.Count >= MinBarsForBacktest

            Dim finalMessage As String
            If success Then
                Dim earliest = countBars.Min(Function(b) b.Timestamp)
                Dim latest   = countBars.Max(Function(b) b.Timestamp)
                finalMessage = $"✓ {countBars.Count:N0} {tfLabel} bars available for {contractId} " &
                               $"({earliest:MM/dd/yyyy} – {latest:MM/dd/yyyy})  [TopStepX]"
            ElseIf countBars.Count > 0 Then
                finalMessage = $"⚠ Only {countBars.Count:N0} {tfLabel} bars available for {contractId} — " &
                               "TopStepX may have returned partial data."
            Else
                Dim pxIdHint = If(pxContractId <> contractId, $" (PX contract: {pxContractId})", "")
                finalMessage = $"✗ No {tfLabel} bars returned for {contractId}{pxIdHint} from TopStepX. " &
                               "Ensure the contract is active and the API key is valid."
            End If

            progress?.Report(finalMessage)
            _logger.LogInformation(
                "EnsureBarsAsync: complete — {Count} {Tf} bars for {Contract}, success={Ok}",
                countBars.Count, tfLabel, contractId, success)

            Return New BarEnsureResult With {
                .Success = success,
                .BarCount = countBars.Count,
                .ContractId = contractId,
                .Message = finalMessage
            }
        End Function

        ' ── Helpers ───────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' Maps a BarTimeframe to the ProjectX API unit (AggregateBarUnit type) and unitNumber.
        '''   unit=2 Minute: 1m=1, 5m=5, 15m=15, 30m=30
        '''   unit=3 Hour:   1h=1
        '''   unit=4 Day:    daily=1
        ''' </summary>
        Private Shared Sub TimeframeToApiParams(tf As BarTimeframe,
                                                ByRef unit As Integer,
                                                ByRef unitNumber As Integer)
            Select Case tf
                Case BarTimeframe.FifteenSecond: unit = 1 : unitNumber = 15  ' unit=1 (Second)
                Case BarTimeframe.OneMinute    : unit = 2 : unitNumber = 1
                Case BarTimeframe.ThreeMinute  : unit = 2 : unitNumber = 3
                Case BarTimeframe.FiveMinute   : unit = 2 : unitNumber = 5
                Case BarTimeframe.FifteenMinute: unit = 2 : unitNumber = 15
                Case BarTimeframe.ThirtyMinute : unit = 2 : unitNumber = 30
                Case BarTimeframe.OneHour      : unit = 3 : unitNumber = 1
                Case BarTimeframe.Daily        : unit = 4 : unitNumber = 1
                Case Else                      : unit = 2 : unitNumber = 5
            End Select
        End Sub

        ''' <summary>
        ''' Aggregates a sorted list of source bars into wider candles for non-native timeframes.
        ''' Groups source bars by aligned UTC time windows of the target width so candle
        ''' boundaries always fall on even multiples of the target interval.
        ''' OHLCV: Open=first, High=Max, Low=Min, Close=last, Volume=Sum, VWAP=mean.
        ''' Used for: TwoHour (2×1-hr), FourHour (4×1-hr).
        ''' </summary>
        Friend Shared Function AggregateBars(sourceBars As List(Of MarketBar),
                                             targetTimeframe As BarTimeframe) As List(Of MarketBar)
            Dim targetMinutes As Long = CLng(targetTimeframe)
            Dim windowSeconds As Long = targetMinutes * 60L

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
                Case BarTimeframe.FifteenSecond : Return "15-sec"
                Case BarTimeframe.OneMinute    : Return "1-min"
                Case BarTimeframe.ThreeMinute  : Return "3-min"
                Case BarTimeframe.FiveMinute   : Return "5-min"
                Case BarTimeframe.FifteenMinute: Return "15-min"
                Case BarTimeframe.ThirtyMinute : Return "30-min"
                Case BarTimeframe.OneHour      : Return "1-hour"
                Case BarTimeframe.TwoHour      : Return "2-hour"
                Case BarTimeframe.FourHour     : Return "4-hour"
                Case Else                      : Return tf.ToString()
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
