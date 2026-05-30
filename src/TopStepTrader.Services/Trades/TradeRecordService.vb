Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Trades

    ''' <summary>
    ''' Singleton service that persists live trade records to TradeHistory.db.
    ''' Uses IServiceScopeFactory to create short-lived scopes for the Scoped repository.
    ''' </summary>
    Public Class TradeRecordService
        Implements ITradeRecordService

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _orderClient As PXOrderClient
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of TradeRecordService)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       orderClient As PXOrderClient,
                       session As ITradingSessionContext,
                       logger As ILogger(Of TradeRecordService))
            _scopeFactory = scopeFactory
            _orderClient = orderClient
            _session = session
            _logger = logger
        End Sub

        Public Async Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long) _
            Implements ITradeRecordService.OpenTradeAsync
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim entity As New LiveTradeRecordEntity With {
                        .EntryOrderId = record.EntryOrderId,
                        .TopStepXTradeId = record.TopStepXTradeId,
                        .ContractId = record.ContractId,
                        .Symbol = record.Symbol,
                        .Direction = record.Direction,
                        .Sizes = record.Sizes,
                        .MaxScaleIns = record.MaxScaleIns,
                        .StrategyName = record.StrategyName,
                        .Persona = record.Persona,
                        .Timeframe = record.Timeframe,
                        .EntryTime = record.EntryTime,
                        .EntryPrice = record.EntryPrice,
                        .CommissionUsd = record.CommissionUsd,
                        .FeesUsd = record.FeesUsd,
                        .IsOpen = True,
                        .CreatedAt = DateTimeOffset.UtcNow,
                        .UpdatedAt = DateTimeOffset.UtcNow
                    }
                    Dim newId = Await repo.AddAsync(entity)
                    ' Log the Initial stop adjustment row
                    If newId > 0 AndAlso record.InitialStopPrice <> 0D Then
                        Dim stopRepo = scope.ServiceProvider.GetRequiredService(Of ITradeStopAdjustmentRepository)()
                        Await stopRepo.AddAsync(New Data.Entities.TradeStopAdjustmentEntity With {
                            .LiveTradeRecordId = newId,
                            .Timestamp = DateTimeOffset.UtcNow.UtcDateTime.ToString("o"),
                            .OldStop = record.InitialStopPrice.ToString("G"),
                            .NewStop = record.InitialStopPrice.ToString("G"),
                            .TriggerReason = "Initial"
                        })
                    End If
                    Return newId
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.OpenTradeAsync failed for {Symbol}", record.Symbol)
                Return 0
            End Try
        End Function

        Public Async Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset,
                                              exitPrice As Decimal, pnL As Decimal,
                                              exitReason As String,
                                              Optional closeFillSource As String = Nothing) As Task _
            Implements ITradeRecordService.CloseTradeAsync
            If id = 0 Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    ' BUG-102 F2: close-time integrity gate. A record with EntryPrice=0 at close
                    ' would poison every aggregate computed downstream. Attempt one more broker-
                    ' driven resolution before the close persists; on failure, log the unresolved
                    ' path and still proceed — the historical row is not blocked from closing.
                    Dim existing = Await repo.GetByIdAsync(id)
                    If existing IsNot Nothing AndAlso existing.EntryPrice = 0D Then
                        _logger.LogWarning(
                            "CloseTradeAsync: record {Id} has EntryPrice=0 at close (entryOrderId={Oid}) — attempting resolution (BUG-102 F2)",
                            id, existing.EntryOrderId)
                        Dim resolved As Decimal = Await TryResolveEntryPriceAsync(existing)
                        If resolved > 0D Then
                            Await repo.UpdateEntryPriceAsync(id, resolved)
                            _logger.LogInformation(
                                "CloseTradeAsync: record {Id} EntryPrice resolved to {Price:F4} pre-close (BUG-102 F2)",
                                id, resolved)
                        Else
                            _logger.LogWarning(
                                "CloseTradeAsync: record {Id} EntryPrice unresolvable — closing with EntryPrice=0; row will appear in F3 audit",
                                id)
                        End If
                    End If
                    Await repo.CloseAsync(id, exitTime, exitPrice, pnL, exitReason, closeFillSource)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.CloseTradeAsync failed for record {Id}", id)
            End Try

            ' FEAT-50: capture broker snapshots after the close is persisted.
            ' BUG-63: do NOT block CloseTradeAsync on PX REST calls. Schedule
            ' on the threadpool with full try/catch — trading hot path stays clean.
            ' OBS-07 F1: emit one structured "scheduled" log line every time, and
            ' explicitly Warning-log the skip path so silent zero-row outcomes
            ' (hypothesis B in OBS-07) are observable in DebugLog.
            Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
            If accountId = 0L Then
                _logger.LogWarning("{Message}", FormatAccountIdSkipWarning(id))
                Return
            End If

            _logger.LogInformation(
                "CaptureClosingSnapshots scheduled for record {Id} accountId={AccountId}",
                id, accountId)

            Dim svc = DirectCast(Me, ITradeRecordService)
            Dim log = _logger
            Dim recordId = id
            Dim acc = accountId
            #Disable Warning BC42358
                            Task.Run(Async Function()
                                         Try
                                             Await svc.CaptureClosingSnapshotsAsync(recordId, acc)
                                             log.LogInformation(
                                                 "CaptureClosingSnapshots task completed for record {Id}",
                                                 recordId)
                                         Catch ex As Exception
                                             log.LogWarning(ex, "TradeRecordService.CaptureClosingSnapshotsAsync (background) failed for record {Id}", recordId)
                                         End Try
                                     End Function)
            #Enable Warning BC42358
        End Function

        Public Async Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task _
            Implements ITradeRecordService.UpdateEntryPriceAsync
            If id = 0 OrElse entryPrice = 0D Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Await repo.UpdateEntryPriceAsync(id, entryPrice)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.UpdateEntryPriceAsync failed for record {Id}", id)
            End Try
        End Function

        ''' <summary>
        ''' BUG-102 F2/F3: live broker-driven entry-price resolution for records that carry
        ''' EntryPrice=0. Searches the order + trade history around the recorded EntryTime,
        ''' feeds the responses to <see cref="ResolveEntryPriceFromBrokerFills"/>, and returns
        ''' the resolved price (or 0 when neither source yields a non-zero value).
        ''' </summary>
        Private Async Function TryResolveEntryPriceAsync(entity As LiveTradeRecordEntity) As Task(Of Decimal)
            If entity Is Nothing Then Return 0D
            Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
            If accountId = 0L Then Return 0D

            Dim sinceMs = entity.EntryTime.AddMinutes(-5).ToUnixTimeMilliseconds()
            Dim untilMs = entity.EntryTime.AddMinutes(60).ToUnixTimeMilliseconds()

            Dim orders As IList(Of API.Models.Responses.PXOrderDto) = Nothing
            Dim trades As IList(Of API.Models.Responses.PXTradeDto) = Nothing
            Try
                Dim resp = Await _orderClient.SearchOrdersAsync(accountId, sinceMs, untilMs)
                orders = resp?.Orders
            Catch ex As Exception
                _logger.LogWarning(ex, "TryResolveEntryPriceAsync: SearchOrdersAsync failed for record {Id}", entity.Id)
            End Try
            Try
                Dim resp = Await _orderClient.SearchTradesAsync(accountId, sinceMs, untilMs)
                trades = resp?.Trades
            Catch ex As Exception
                _logger.LogWarning(ex, "TryResolveEntryPriceAsync: SearchTradesAsync failed for record {Id}", entity.Id)
            End Try

            Return ResolveEntryPriceFromBrokerFills(entity, orders, trades)
        End Function

        ''' <summary>
        ''' BUG-102 F2: pure helper that derives a non-zero entry price from broker order +
        ''' trade history. Prefers the entry order's <c>AvgFillPrice</c>; falls back to the
        ''' first trade fill whose <c>OrderId</c> matches <c>EntryOrderId</c>; finally falls
        ''' back to the earliest entry-side fill on the contract at/after <c>EntryTime</c>.
        ''' Returns 0 when none of those sources yields a non-zero value.
        ''' </summary>
        Friend Shared Function ResolveEntryPriceFromBrokerFills(entity As LiveTradeRecordEntity,
                                                                  orders As IList(Of API.Models.Responses.PXOrderDto),
                                                                  trades As IList(Of API.Models.Responses.PXTradeDto)) As Decimal
            If entity Is Nothing Then Return 0D

            ' 1. Entry order's AvgFillPrice.
            If entity.EntryOrderId <> 0L AndAlso orders IsNot Nothing Then
                Dim matchOrder = orders.FirstOrDefault(Function(o) o.Id = entity.EntryOrderId)
                If matchOrder IsNot Nothing AndAlso matchOrder.AvgFillPrice.HasValue AndAlso matchOrder.AvgFillPrice.Value > 0R Then
                    Return CDec(matchOrder.AvgFillPrice.Value)
                End If
            End If

            ' 2. Trade fill whose OrderId matches the entry order id.
            If entity.EntryOrderId <> 0L AndAlso trades IsNot Nothing Then
                Dim matchByOrder = trades _
                    .Where(Function(t) t.OrderId = entity.EntryOrderId AndAlso t.Price > 0R) _
                    .OrderBy(Function(t) ParseTs(t.CreationTimestamp)) _
                    .FirstOrDefault()
                If matchByOrder IsNot Nothing Then Return CDec(matchByOrder.Price)
            End If

            ' 3. Earliest entry-side fill on the contract at/after EntryTime.
            If trades IsNot Nothing AndAlso Not String.IsNullOrEmpty(entity.ContractId) Then
                Dim entrySide As Integer = If(entity.Direction = "Long", 0, 1)
                Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
                Dim rootPrefix = TryResolveContractRootPrefix(entity.ContractId)
                Dim matcher = BuildContractMatcher(entity.ContractId, rootPrefix)
                Dim matchAny = trades _
                    .Where(Function(t) matcher(t.ContractId) AndAlso
                                       t.Side = entrySide AndAlso
                                       t.Price > 0R AndAlso
                                       ParseTs(t.CreationTimestamp) >= entryMs) _
                    .OrderBy(Function(t) ParseTs(t.CreationTimestamp)) _
                    .FirstOrDefault()
                If matchAny IsNot Nothing Then Return CDec(matchAny.Price)
            End If

            Return 0D
        End Function

        Public Async Function ResolveTopStepXTradeIdAsync(recordId As Long, topStepXTradeId As Long) As Task _
            Implements ITradeRecordService.ResolveTopStepXTradeIdAsync
            If recordId = 0 Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Await repo.ResolveTopStepXTradeIdAsync(recordId, topStepXTradeId)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.ResolveTopStepXTradeIdAsync failed for record {Id}", recordId)
            End Try
        End Function

        Public Async Function GetRecentTradesAsync(count As Integer,
                                                   Optional filter As TradeFilter = Nothing) As Task(Of IList(Of LiveTradeRecord)) _
            Implements ITradeRecordService.GetRecentTradesAsync
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim pnlF = If(filter IsNot Nothing, filter.PnLFilter, PnLFilterType.All)
                    Dim closedOnlyF = If(filter IsNot Nothing, filter.ClosedOnly, False)
                    Dim entities = Await repo.GetRecentAsync(
                        count,
                        symbolFilter:=If(filter?.Symbol, String.Empty),
                        strategyFilter:=If(filter?.Strategy, String.Empty),
                        personaFilter:=If(filter?.Persona, String.Empty),
                        pnlFilter:=pnlF,
                        closedOnly:=closedOnlyF)
                    Return entities.Select(AddressOf ToModel).ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.GetRecentTradesAsync failed")
                Return New List(Of LiveTradeRecord)()
            End Try
        End Function

        Public Async Function GetOpenTradesAsync() As Task(Of IList(Of LiveTradeRecord)) _
            Implements ITradeRecordService.GetOpenTradesAsync
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim entities = Await repo.GetOpenRecordsAsync()
                    Return entities.Select(AddressOf ToModel).ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.GetOpenTradesAsync failed")
                Return New List(Of LiveTradeRecord)()
            End Try
        End Function

        Public Async Function GetTradeByIdAsync(id As Long) As Task(Of LiveTradeRecord) _
            Implements ITradeRecordService.GetTradeByIdAsync
            If id = 0 Then Return Nothing
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim entity = Await repo.GetByIdAsync(id)
                    Return If(entity Is Nothing, Nothing, ToModel(entity))
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.GetTradeByIdAsync failed for record {Id}", id)
                Return Nothing
            End Try
        End Function

        Public Async Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecord) _
            Implements ITradeRecordService.FindByEntryOrderIdAsync
            If externalOrderId = 0L Then Return Nothing
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim entity = Await repo.FindByEntryOrderIdAsync(externalOrderId)
                    Return If(entity Is Nothing, Nothing, ToModel(entity))
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.FindByEntryOrderIdAsync failed for orderId={Id}", externalOrderId)
                Return Nothing
            End Try
        End Function

        Public Async Function FindOpenByContractIdAsync(accountId As Long, contractId As String) As Task(Of LiveTradeRecord) _
            Implements ITradeRecordService.FindOpenByContractIdAsync
            ' BUG-94 F1: accountId is currently unused at the persistence layer because
            ' LiveTradeRecordEntity does not carry an AccountId column. The caller already
            ' filters to a single selected account upstream (TradeReconciliationWorker only
            ' runs against _session.SelectedAccount.Id). Kept on the interface to future-proof
            ' once accountId is added to the entity.
            If String.IsNullOrEmpty(contractId) Then Return Nothing
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim entity = Await repo.FindOpenByContractIdAsync(contractId)
                    Return If(entity Is Nothing, Nothing, ToModel(entity))
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.FindOpenByContractIdAsync failed for contractId={Id}", contractId)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' BUG-86 F1: cap on how far back the broker fill search will reach. Records whose
        ''' EntryTime is older than this are skipped with a warning instead of widening the
        ''' search window unbounded (which would risk REST timeouts and noise from years-old
        ''' records that got stuck open).
        ''' </summary>
        Friend Const RecoveryLookbackMaxDays As Integer = 30

        ''' <summary>
        ''' BUG-86 F1: pure helper computing the /api/Trade/search lookback window.
        ''' Returns the timestamp (ms) to use as the "since" cursor and the subset of
        ''' records whose EntryTime falls inside that window. Records older than
        ''' <see cref="RecoveryLookbackMaxDays"/> are returned in <paramref name="skipped"/>
        ''' so the caller can warn and leave them alone.
        ''' Buffer of 5 minutes is subtracted to tolerate clock skew + the gap between
        ''' the EntryTime we recorded and the broker's CreationTimestamp on the entry fill.
        ''' </summary>
        Friend Shared Function ComputeLookbackSinceMs(records As IEnumerable(Of LiveTradeRecordEntity),
                                                      nowUtc As DateTimeOffset,
                                                      ByRef skipped As List(Of LiveTradeRecordEntity)) As (sinceMs As Long, eligible As List(Of LiveTradeRecordEntity))
            Dim cap = nowUtc.AddDays(-RecoveryLookbackMaxDays)
            Dim eligible As New List(Of LiveTradeRecordEntity)()
            skipped = New List(Of LiveTradeRecordEntity)()
            For Each rec In records
                If rec.EntryTime < cap Then
                    skipped.Add(rec)
                Else
                    eligible.Add(rec)
                End If
            Next
            If eligible.Count = 0 Then Return (0L, eligible)
            Dim oldest = eligible.Min(Function(r) r.EntryTime)
            Dim sinceMs = oldest.AddMinutes(-5).ToUnixTimeMilliseconds()
            Return (sinceMs, eligible)
        End Function

        ''' <summary>
        ''' Finds any IsOpen records (force-flat or crash before close was written)
        ''' and queries TopStepX trade history to resolve their exit fills.
        ''' BUG-86: also invoked periodically by <c>TradeReconciliationWorker</c> while
        ''' the app is running — the method is idempotent (records already closed are
        ''' filtered out by GetOpenRecordsAsync on each pass).
        ''' </summary>
        Public Async Function RecoverOpenTradesAsync(accountId As Long) As Task _
            Implements ITradeRecordService.RecoverOpenTradesAsync
            If accountId = 0 Then Return

            Dim openRecords As IList(Of LiveTradeRecordEntity)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    openRecords = Await repo.GetOpenRecordsAsync()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService crash recovery: failed to read open records")
                Return
            End Try

            If openRecords.Count = 0 Then Return
            _logger.LogInformation("TradeRecordService: {Count} open record(s) found — attempting crash recovery", openRecords.Count)

            ' BUG-86 F1: derive lookback from the oldest open record's EntryTime (minus a
            ' 5-minute buffer), capped at RecoveryLookbackMaxDays. The previous fixed
            ' 48-hour window dropped force-closes that happened while the app was off
            ' for a long weekend / multi-day outage.
            Dim skipped As List(Of LiveTradeRecordEntity) = Nothing
            Dim window = ComputeLookbackSinceMs(openRecords, DateTimeOffset.UtcNow, skipped)
            For Each old In skipped
                _logger.LogWarning(
                    "TradeRecordService crash recovery: record {Id} ({Symbol}) EntryTime={Entry:o} is older than {Days} days — skipped; resolve manually",
                    old.Id, old.Symbol, old.EntryTime, RecoveryLookbackMaxDays)
            Next
            openRecords = window.eligible
            If openRecords.Count = 0 Then Return

            Dim sinceMs = window.sinceMs
            Dim nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            Dim allFills As List(Of API.Models.Responses.PXTradeDto)
            Try
                Dim resp = Await _orderClient.SearchTradesAsync(accountId, sinceMs, nowMs)
                allFills = If(resp?.Trades, New List(Of API.Models.Responses.PXTradeDto)())
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService crash recovery: SearchTradesAsync failed — open records left as-is")
                Return
            End Try

            For Each rec In openRecords
                ' Entry side: Long entered with a Sell-side = 1 exit, Short entered with a Buy-side = 0 exit
                Dim exitSide As Integer = If(rec.Direction = "Long", 1, 0)
                Dim entryMs = rec.EntryTime.ToUnixTimeMilliseconds()

                ' Find the first exit fill after entry time for the same contract on the opposite side
                Dim exitFill = allFills _
                    .Where(Function(t) t.ContractId = rec.ContractId AndAlso
                                       t.Side = exitSide) _
                    .Select(Function(t) New With {
                        .Fill = t,
                        .Ts = ParseTs(t.CreationTimestamp)
                    }) _
                    .Where(Function(x) x.Ts > entryMs) _
                    .OrderBy(Function(x) x.Ts) _
                    .Select(Function(x) x.Fill) _
                    .FirstOrDefault()

                If exitFill Is Nothing Then
                    _logger.LogInformation("TradeRecordService: no exit fill found for open record {Id} ({Symbol}) — leaving as open", rec.Id, rec.Symbol)
                    Continue For
                End If

                Dim exitTs = ParseTs(exitFill.CreationTimestamp)
                Dim exitTime = DateTimeOffset.FromUnixTimeMilliseconds(exitTs)
                Dim exitPx = CDec(exitFill.Price)

                ' Best-effort P&L from FavouriteContracts point value
                Dim root = rec.Symbol.TrimStart("/"c)
                Dim fc = FavouriteContracts.TryGetBySymbol(root)
                Dim pointValue As Decimal = If(fc IsNot Nothing AndAlso fc.PxPointValue > 0D, fc.PxPointValue, 1D)
                Dim priceDiff = If(rec.Direction = "Long",
                                   exitPx - rec.EntryPrice,
                                   rec.EntryPrice - exitPx)
                Dim pnl = Math.Round(priceDiff * pointValue * rec.Sizes, 2)

                Try
                    Using scope = _scopeFactory.CreateScope()
                        Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                        Await repo.CloseAsync(rec.Id, exitTime, exitPx, pnl, "Recovered")
                    End Using
                    _logger.LogInformation("TradeRecordService: recovered record {Id} — exit {Price} P&L {PnL}", rec.Id, exitPx, pnl)
                Catch ex As Exception
                    _logger.LogWarning(ex, "TradeRecordService: failed to close recovered record {Id}", rec.Id)
                End Try

                ' FEAT-57: also resolve any dangling open TradeOutcomes row whose
                ' OrderId == this LiveTradeRecord.Id (the link we stash at entry).
                Try
                    Using scope = _scopeFactory.CreateScope()
                        Dim outcomeRepo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                        Dim openOutcomes = Await outcomeRepo.GetOpenOutcomesAsync()
                        Dim match = openOutcomes.FirstOrDefault(Function(o) o.OrderId.HasValue AndAlso o.OrderId.Value = rec.Id)
                        If match IsNot Nothing Then
                            Await outcomeRepo.ResolveOutcomeAsync(match.Id, exitTime, exitPx, pnl, pnl > 0D, "Recovered")
                        End If
                    End Using
                Catch ex As Exception
                    _logger.LogWarning(ex, "TradeRecordService: failed to resolve open outcome for recovered record {Id}", rec.Id)
                End Try
            Next
        End Function

        Private Shared Function ParseTs(raw As String) As Long
            Dim v As Long
            Long.TryParse(raw, v)
            Return v
        End Function

        ' ── FEAT-57: TradeOutcomes lifecycle ────────────────────────────────
        ' These methods wire the previously-orphan TradeOutcomeRepository into the live
        ' trade lifecycle so ML retraining can consume real-world P&L outcomes instead
        ' of falling back to look-ahead synthetic labels in SignalModelTrainer.
        '
        ' Cross-DB note: TradeOutcomes lives in AppDbContext (app.db); LiveTradeRecords
        ' lives in TradeHistoryDbContext (TradeHistory.db). The recordId passed to
        ' OpenOutcomeAsync is stashed in TradeOutcomes.OrderId so the crash-recovery
        ' path can find the matching open outcome row by LiveTradeRecord.Id alone.

        Public Async Function SaveSignalAsync(signal As TradeSignal) As Task(Of Long) _
            Implements ITradeRecordService.SaveSignalAsync
            If signal Is Nothing Then Return 0
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of SignalRepository)()
                    Return Await repo.SaveSignalAsync(signal)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.SaveSignalAsync failed for {Contract}", signal.ContractId)
                Return 0
            End Try
        End Function

        Public Async Function OpenOutcomeAsync(signalId As Long, recordId As Long,
                                                model As TradeOutcome) As Task(Of Long) _
            Implements ITradeRecordService.OpenOutcomeAsync
            If model Is Nothing Then Return 0
            Try
                model.SignalId = signalId
                ' OrderId stores the LiveTradeRecord.Id so RecoverOpenTradesAsync
                ' can resolve dangling open outcomes after a crash.
                model.OrderId = If(recordId > 0, CType(recordId, Long?), Nothing)
                model.IsOpen = True
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                    Return Await repo.SaveOutcomeAsync(model)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.OpenOutcomeAsync failed for {Contract}", model.ContractId)
                Return 0
            End Try
        End Function

        Public Async Function ResolveOutcomeAsync(outcomeId As Long, exitTime As DateTimeOffset,
                                                   exitPrice As Decimal, pnl As Decimal,
                                                   isWinner As Boolean, exitReason As String) As Task _
            Implements ITradeRecordService.ResolveOutcomeAsync
            If outcomeId = 0 Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                    Await repo.ResolveOutcomeAsync(outcomeId, exitTime, exitPrice, pnl, isWinner, exitReason)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.ResolveOutcomeAsync failed for outcome {Id}", outcomeId)
            End Try
        End Function

        ' ── FEAT-58: TradeSetupSnapshot + TradeLifespan persistence ──────────
        ' Linked to TradeOutcomes via TradeOutcomeId. Both live in AppDbContext (app.db).
        ' Together with FEAT-57 these complete the ML training triple: (outcome label) +
        ' (entry-time feature snapshot) + (lifespan metrics like MAE/MFE/R-multiple).

        Public Async Function SaveSetupSnapshotAsync(tradeOutcomeId As Long, snapshot As TradeSetupSnapshot) As Task(Of Long) _
            Implements ITradeRecordService.SaveSetupSnapshotAsync
            If tradeOutcomeId = 0 OrElse snapshot Is Nothing Then Return 0
            Try
                Dim entity = MapSnapshotToEntity(snapshot)
                entity.TradeOutcomeId = tradeOutcomeId
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeSetupSnapshotRepository)()
                    Return Await repo.SaveAsync(entity)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.SaveSetupSnapshotAsync failed for outcome {Id}", tradeOutcomeId)
                Return 0
            End Try
        End Function

        Public Async Function SaveLifespanRecordAsync(tradeOutcomeId As Long, record As TradeLifespan) As Task _
            Implements ITradeRecordService.SaveLifespanRecordAsync
            If tradeOutcomeId = 0 OrElse record Is Nothing Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeLifespanRepository)()
                    ' Upsert on TradeOutcomeId — same trade can transit ReleaseSlot multiple
                    ' times via different paths (engine exit, P&L guard, crash recovery).
                    Dim existing = Await repo.GetByTradeOutcomeIdAsync(tradeOutcomeId)
                    Dim entity = MapLifespanToEntity(record)
                    entity.TradeOutcomeId = tradeOutcomeId
                    If existing IsNot Nothing Then
                        entity.Id = existing.Id
                        entity.CreatedAt = existing.CreatedAt
                        Await repo.UpdateAsync(entity)
                    Else
                        Await repo.SaveAsync(entity)
                    End If
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.SaveLifespanRecordAsync failed for outcome {Id}", tradeOutcomeId)
            End Try
        End Function

        Private Shared Function MapSnapshotToEntity(s As TradeSetupSnapshot) As TradeSetupSnapshotEntity
            Return New TradeSetupSnapshotEntity With {
                .TradeOutcomeId = s.TradeOutcomeId,
                .CapturedAt = s.CapturedAt,
                .Tenkan = s.Tenkan,
                .Kijun = s.Kijun,
                .Cloud1 = s.Cloud1,
                .Cloud2 = s.Cloud2,
                .Ema21 = s.Ema21,
                .Ema50 = s.Ema50,
                .MacdHist = s.MacdHist,
                .MacdHistPrev = s.MacdHistPrev,
                .StochRsiK = s.StochRsiK,
                .PlusDI = s.PlusDI,
                .MinusDI = s.MinusDI,
                .AdxValue = s.AdxValue,
                .Rsi14 = s.Rsi14,
                .VidyaValue = s.VidyaValue,
                .CmoValue = s.CmoValue,
                .DeltaVol = s.DeltaVol,
                .LongCount = s.LongCount,
                .ShortCount = s.ShortCount,
                .TotalConditions = s.TotalConditions,
                .UpPct = s.UpPct,
                .DownPct = s.DownPct,
                .SignalBarOpen = s.SignalBarOpen,
                .SignalBarHigh = s.SignalBarHigh,
                .SignalBarLow = s.SignalBarLow,
                .SignalBarClose = s.SignalBarClose,
                .SignalBarVolume = s.SignalBarVolume,
                .AtrValue = s.AtrValue,
                .SessionWindow = If(s.SessionWindow, String.Empty),
                .DayOfWeek = s.DayOfWeek,
                .HourOfDay = s.HourOfDay,
                .StrategyName = If(s.StrategyName, String.Empty),
                .PersonaName = If(s.PersonaName, String.Empty),
                .SlMultiple = s.SlMultiple,
                .TpMultiple = s.TpMultiple,
                .TimeframeMinutes = s.TimeframeMinutes
            }
        End Function

        Private Shared Function MapLifespanToEntity(r As TradeLifespan) As TradeLifespanRecordEntity
            Return New TradeLifespanRecordEntity With {
                .TradeOutcomeId = r.TradeOutcomeId,
                .MaxAdverseExcursionDollars = r.MaxAdverseExcursionDollars,
                .MaxFavorableExcursionDollars = r.MaxFavorableExcursionDollars,
                .MaxAdverseExcursionTicks = r.MaxAdverseExcursionTicks,
                .MaxFavorableExcursionTicks = r.MaxFavorableExcursionTicks,
                .SlRatchetCount = r.SlRatchetCount,
                .TpAdvanceCount = r.TpAdvanceCount,
                .FreeRideActivated = r.FreeRideActivated,
                .FreeRideActivatedAtMinutes = r.FreeRideActivatedAtMinutes,
                .DurationMinutes = r.DurationMinutes,
                .BarsInTrade = r.BarsInTrade,
                .EntrySessionWindow = If(r.EntrySessionWindow, String.Empty),
                .ExitSessionWindow = If(r.ExitSessionWindow, String.Empty),
                .CrossedSessionBoundary = r.CrossedSessionBoundary,
                .RMultiple = r.RMultiple,
                .CreatedAt = r.CreatedAt,
                .UpdatedAt = DateTimeOffset.UtcNow
            }
        End Function

        Public Async Function LogStopAdjustmentAsync(liveTradeRecordId As Long,
                                                     timestamp As DateTimeOffset,
                                                     oldStop As Decimal,
                                                     newStop As Decimal,
                                                     triggerReason As String,
                                                     Optional notes As String = Nothing) As Task _
            Implements ITradeRecordService.LogStopAdjustmentAsync
            If liveTradeRecordId = 0 Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeStopAdjustmentRepository)()
                    Dim entity As New Data.Entities.TradeStopAdjustmentEntity With {
                        .LiveTradeRecordId = liveTradeRecordId,
                        .Timestamp = timestamp.UtcDateTime.ToString("o"),
                        .OldStop = oldStop.ToString("G"),
                        .NewStop = newStop.ToString("G"),
                        .TriggerReason = If(triggerReason, String.Empty),
                        .Notes = notes
                    }
                    Await repo.AddAsync(entity)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.LogStopAdjustmentAsync failed for record {Id}", liveTradeRecordId)
            End Try
        End Function

        Public Async Function LogTickSnapshotAsync(liveTradeRecordId As Long,
                                                   snapshot As Core.Models.TradeTickSnapshot) As Task _
            Implements ITradeRecordService.LogTickSnapshotAsync
            ' FEAT-59: best-effort per-bar snapshot write. Any failure here is logged and
            ' swallowed so the management tick never blocks on diagnostic persistence.
            If liveTradeRecordId = 0 OrElse snapshot Is Nothing Then Return
            Try
                Dim entity = MapTickSnapshotToEntity(snapshot)
                entity.LiveTradeRecordId = liveTradeRecordId
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                    Await repo.AddAsync(entity)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.LogTickSnapshotAsync failed for record {Id}", liveTradeRecordId)
            End Try
        End Function

        Private Shared Function MapTickSnapshotToEntity(s As Core.Models.TradeTickSnapshot) As TradeTickSnapshotEntity
            Return New TradeTickSnapshotEntity With {
                .LiveTradeRecordId = s.LiveTradeRecordId,
                .BarTimestamp = s.BarTimestamp,
                .BarOpen = s.BarOpen,
                .BarHigh = s.BarHigh,
                .BarLow = s.BarLow,
                .BarClose = s.BarClose,
                .BarVolume = s.BarVolume,
                .SuperTrendLine = s.SuperTrendLine,
                .SuperTrendDirection = s.SuperTrendDirection,
                .Atr = s.Atr,
                .Adx = s.Adx,
                .PlusDi = s.PlusDi,
                .MinusDi = s.MinusDi,
                .CurrentStopPrice = s.CurrentStopPrice,
                .CurrentTakeProfitPrice = s.CurrentTakeProfitPrice,
                .UnrealisedPnlDollars = s.UnrealisedPnlDollars,
                .MaxAdverseExcursionDollars = s.MaxAdverseExcursionDollars,
                .MaxFavorableExcursionDollars = s.MaxFavorableExcursionDollars,
                .StopPhase = If(s.StopPhase, String.Empty),
                .ExitScore = s.ExitScore
            }
        End Function

        Public Async Function GetStopAdjustmentsAsync(liveTradeRecordId As Long) As Task(Of IList(Of Core.Models.TradeStopAdjustment)) _
            Implements ITradeRecordService.GetStopAdjustmentsAsync
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeStopAdjustmentRepository)()
                    Dim rows = Await repo.GetByTradeRecordAsync(liveTradeRecordId)
                    Return rows.Select(Function(r) New Core.Models.TradeStopAdjustment With {
                        .Id = r.Id,
                        .LiveTradeRecordId = r.LiveTradeRecordId,
                        .Timestamp = DateTimeOffset.Parse(r.Timestamp, Nothing, Globalization.DateTimeStyles.RoundtripKind),
                        .OldStop = Decimal.Parse(r.OldStop, Globalization.CultureInfo.InvariantCulture),
                        .NewStop = Decimal.Parse(r.NewStop, Globalization.CultureInfo.InvariantCulture),
                        .TriggerReason = r.TriggerReason,
                        .Notes = r.Notes
                    }).ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.GetStopAdjustmentsAsync failed for record {Id}", liveTradeRecordId)
                Return New List(Of Core.Models.TradeStopAdjustment)()
            End Try
        End Function

        Public Async Function CaptureClosingSnapshotsAsync(recordId As Long, accountId As Long) As Task _
            Implements ITradeRecordService.CaptureClosingSnapshotsAsync
            If recordId = 0 OrElse accountId = 0 Then Return

            ' BUG-64: load by primary key instead of materialising 2000 rows.
            Dim entity As LiveTradeRecordEntity = Nothing
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    entity = Await repo.GetByIdAsync(recordId)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "CaptureClosingSnapshots: failed to load record {Id}", recordId)
                Return
            End Try
            If entity Is Nothing Then
                _logger.LogWarning(
                    "CaptureClosingSnapshots: record {Id} not found in LiveTradeRecords — aborting",
                    recordId)
                Return
            End If

            ' OBS-07 F1: entity-loaded waypoint — covers hypothesis E (DB-path drift),
            ' makes the in-context record shape visible alongside any matcher decisions.
            _logger.LogInformation(
                "CaptureClosingSnapshots: record {Id} entity loaded contract={Contract} symbol={Symbol} entryTime={EntryTime:o} exitTime={ExitTime}",
                recordId, entity.ContractId, entity.Symbol, entity.EntryTime,
                If(entity.ExitTime.HasValue, entity.ExitTime.Value.ToString("o"), "open"))

            Dim startTs = entity.EntryTime.AddMinutes(-1).ToUnixTimeMilliseconds()
            Dim endTimeRef = If(entity.ExitTime.HasValue, entity.ExitTime.Value, DateTimeOffset.UtcNow)
            Dim endTs = endTimeRef.AddMinutes(1).ToUnixTimeMilliseconds()

            ' BUG-92: LiveTradeRecords.ContractId is stored as the short symbol (e.g. "MNQ"),
            ' but the broker returns the resolved PX contract ID (e.g. "CON.F.US.MNQ.U26").
            ' The old `x.ContractId = entity.ContractId` filter never matched, leaving every
            ' snapshot table empty system-wide. We now match against the CME root-symbol prefix
            ' (e.g. "CON.F.US.MNQ.") via FavouriteContracts, falling back to the literal
            ' ContractId when no favourite is found (legacy rows / unknown symbols).
            Dim rootPrefix As String = TryResolveContractRootPrefix(entity.ContractId)
            Dim contractIdMatcher As Func(Of String, Boolean) = BuildContractMatcher(entity.ContractId, rootPrefix)
            ' OBS-07 F1: rootPrefix waypoint — covers hypothesis C (matcher mis-resolution).
            _logger.LogInformation(
                "CaptureClosingSnapshots: record {Id} rootPrefix={Prefix} (raw ContractId={ContractId})",
                recordId,
                If(String.IsNullOrEmpty(rootPrefix), "(literal-fallback)", rootPrefix),
                entity.ContractId)

            ' BUG-63: run the 3 PX search calls in parallel with a hard timeout.
            Dim cts As New Threading.CancellationTokenSource(TimeSpan.FromSeconds(10))
            Dim ordersTask = SafePxCallAsync(Function() _orderClient.SearchOrdersAsync(accountId, startTs, endTs), cts.Token, recordId, "SearchOrdersAsync")
            Dim positionsTask = SafePxCallAsync(Function() _orderClient.SearchPositionsAsync(accountId, startTs, endTs), cts.Token, recordId, "SearchPositionsAsync")
            Dim tradesTask = SafePxCallAsync(Function() _orderClient.SearchTradesAsync(accountId, startTs, endTs), cts.Token, recordId, "SearchTradesAsync")
            Try
                Await Task.WhenAll(ordersTask, positionsTask, tradesTask)
            Catch
                ' individual failures already logged inside SafePxCallAsync
            End Try

            Dim ordersResp = ordersTask.Result
            Dim positionsResp = positionsTask.Result
            Dim tradesResp = tradesTask.Result

            ' OBS-07 F1: broker-response counts waypoint — covers hypothesis D
            ' (SafePxCallAsync silently returning Nothing on timeout/error).
            _logger.LogInformation(
                "CaptureClosingSnapshots: record {Id} broker responses — orders={OrdersCount} positions={PositionsCount} trades={TradesCount}",
                recordId,
                If(ordersResp Is Nothing, "(null)", If(ordersResp.Orders Is Nothing, "0", ordersResp.Orders.Count.ToString())),
                If(positionsResp Is Nothing, "(null)", If(positionsResp.Positions Is Nothing, "0", positionsResp.Positions.Count.ToString())),
                If(tradesResp Is Nothing, "(null)", If(tradesResp.Trades Is Nothing, "0", tradesResp.Trades.Count.ToString())))

            Dim orderRows = MapMatchingOrders(If(ordersResp?.Orders, New List(Of API.Models.Responses.PXOrderDto)()),
                                               contractIdMatcher, recordId)
            Dim positionRows = MapMatchingPositions(If(positionsResp?.Positions, New List(Of API.Models.Responses.PXPositionDto)()),
                                                     contractIdMatcher, recordId,
                                                     entity.EntryTime, entity.ExitTime)
            Dim fillRows = MapMatchingFills(If(tradesResp?.Trades, New List(Of API.Models.Responses.PXTradeDto)()),
                                             contractIdMatcher, recordId)

            ' OBS-07 F1: matcher-rejection waypoints — emit one Warning per response shape
            ' that arrived non-empty yet had every row rejected by the matcher (hypothesis C).
            EmitMatcherRejectionWarning(ordersResp?.Orders, contractIdMatcher, recordId,
                                         "orders", rootPrefix, entity.ContractId,
                                         Function(o) o.ContractId)
            EmitMatcherRejectionWarning(positionsResp?.Positions, contractIdMatcher, recordId,
                                         "positions", rootPrefix, entity.ContractId,
                                         Function(p) p.ContractId)
            EmitMatcherRejectionWarning(tradesResp?.Trades, contractIdMatcher, recordId,
                                         "trades", rootPrefix, entity.ContractId,
                                         Function(t) t.ContractId)

            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim snapRepo = scope.ServiceProvider.GetRequiredService(Of ITradeSnapshotRepository)()
                    If orderRows.Count > 0 Then Await snapRepo.AddOrdersAsync(orderRows)
                    If positionRows.Count > 0 Then Await snapRepo.AddPositionsAsync(positionRows)
                    If fillRows.Count > 0 Then Await snapRepo.AddFillsAsync(fillRows)
                End Using
                _logger.LogInformation("CaptureClosingSnapshots: record {Id} — {Orders} orders, {Positions} positions, {Fills} fills persisted",
                                       recordId, orderRows.Count, positionRows.Count, fillRows.Count)
            Catch ex As Exception
                _logger.LogWarning(ex, "CaptureClosingSnapshots: persistence failed for record {Id}", recordId)
            End Try

            ' BUG-92: reconcile ExitPrice + PnL from the broker-confirmed closing fill. The
            ' close path previously stamped an engine-derived ExitPrice computed from local
            ' UnrealizedPnl, which diverged from the broker's ExecutePrice by several ticks
            ' in fast markets. Replace with broker-truth now that we have the fill rows in hand.
            Try
                Await ReconcileExitFromFillsAsync(entity, tradesResp?.Trades, ordersResp?.Orders, contractIdMatcher)
            Catch ex As Exception
                _logger.LogWarning(ex, "CaptureClosingSnapshots: reconciliation failed for record {Id}", recordId)
            End Try
        End Function

        ''' <summary>
        ''' BUG-92: corrects LiveTradeRecord.ExitPrice/PnL + TradeOutcome.ExitPrice/PnL from the
        ''' broker-confirmed closing fill price. Identifies the closing fill (Side opposite the
        ''' entry direction, occurring at or after EntryTime, on the matching contract). When
        ''' multiple closing fills exist (partial fills), uses the volume-weighted average price.
        ''' Idempotent — re-running with the same broker data is a no-op.
        ''' </summary>
        Private Async Function ReconcileExitFromFillsAsync(entity As LiveTradeRecordEntity,
                                                            trades As IList(Of API.Models.Responses.PXTradeDto),
                                                            orders As IList(Of API.Models.Responses.PXOrderDto),
                                                            matcher As Func(Of String, Boolean)) As Task
            If entity Is Nothing OrElse trades Is Nothing OrElse trades.Count = 0 Then Return

            Dim plan = ComputeExitReconciliation(entity, trades, orders, matcher)
            If plan Is Nothing Then Return

            If entity.ExitOrderId = plan.ExitOrderId AndAlso
               entity.ExitPrice.HasValue AndAlso entity.ExitPrice.Value = plan.ExitPrice AndAlso
               entity.PnL.HasValue AndAlso entity.PnL.Value = plan.PnL Then
                ' Already reconciled with these exact values.
                Return
            End If

            Dim originalExitPrice = entity.ExitPrice
            Dim originalPnL = entity.PnL

            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Await repo.ReconcileExitAsync(entity.Id, plan.ExitPrice, plan.PnL, plan.ExitOrderId, plan.ExitTime)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "ReconcileExit: failed to update LiveTradeRecord {Id}", entity.Id)
                Return
            End Try

            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim outcomeRepo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                    Await outcomeRepo.ReconcileExitByLiveTradeRecordIdAsync(entity.Id, plan.ExitPrice, plan.PnL)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "ReconcileExit: failed to update TradeOutcome linked to record {Id}", entity.Id)
            End Try

            _logger.LogInformation(
                "ReconcileExit: record {Id} {Symbol} {Dir} — ExitPrice {OldPx}→{NewPx}, PnL {OldPnL}→{NewPnL} (closing orderId={OrderId})",
                entity.Id, entity.Symbol, entity.Direction,
                If(originalExitPrice.HasValue, originalExitPrice.Value.ToString("G"), "?"),
                plan.ExitPrice.ToString("G"),
                If(originalPnL.HasValue, originalPnL.Value.ToString("G"), "?"),
                plan.PnL.ToString("G"),
                plan.ExitOrderId)
        End Function

        ''' <summary>
        ''' BUG-92: pure helper computing the broker-reconciled exit price/PnL/closing order ID
        ''' for a closed LiveTradeRecord. Extracted from the live path so unit tests can drive
        ''' every fill-shape scenario (single fill, partial fills, missing side, etc.) without a
        ''' DB or PX REST mock. Returns Nothing when no closing fill can be identified.
        ''' </summary>
        Friend Shared Function ComputeExitReconciliation(entity As LiveTradeRecordEntity,
                                                          trades As IList(Of API.Models.Responses.PXTradeDto),
                                                          orders As IList(Of API.Models.Responses.PXOrderDto),
                                                          matcher As Func(Of String, Boolean)) As ExitReconciliationPlan
            If entity Is Nothing OrElse trades Is Nothing OrElse trades.Count = 0 Then Return Nothing
            If matcher Is Nothing Then matcher = Function(s) True

            ' Long entered → exit Side = 1 (Sell). Short entered → exit Side = 0 (Buy).
            Dim exitSide As Integer = If(entity.Direction = "Long", 1, 0)
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()

            ' Optional cross-check: closing orders carry Disposition=Closing on the broker. The
            ' PXOrderDto doesn't expose that field today, but we can still narrow down using
            ' OrderType=Market (2) + Side=opposite-entry + CreationTimestamp >= EntryTime.
            ' We use that order set only as a filter on trades — trades carry the actual fill price.
            Dim closingOrderIds As HashSet(Of Long) = Nothing
            If orders IsNot Nothing Then
                closingOrderIds = New HashSet(Of Long)(
                    orders _
                        .Where(Function(o) matcher(o.ContractId) AndAlso
                                           o.Side = exitSide AndAlso
                                           o.OrderType = 2 AndAlso
                                           ParseTs(o.CreationTimestamp) >= entryMs) _
                        .Select(Function(o) o.Id))
            End If

            Dim closingFills = trades _
                .Where(Function(t) matcher(t.ContractId) AndAlso
                                   t.Side = exitSide AndAlso
                                   ParseTs(t.CreationTimestamp) >= entryMs) _
                .ToList()

            If closingOrderIds IsNot Nothing AndAlso closingOrderIds.Count > 0 Then
                Dim narrowed = closingFills.Where(Function(t) closingOrderIds.Contains(t.OrderId)).ToList()
                If narrowed.Count > 0 Then closingFills = narrowed
            End If

            If closingFills.Count = 0 Then Return Nothing

            ' Volume-weighted average over partial fills (in practice, ClosePosition fills as one).
            Dim totalSize As Decimal = closingFills.Sum(Function(t) CDec(t.Size))
            If totalSize <= 0D Then Return Nothing
            Dim vwap As Decimal = closingFills.Sum(Function(t) CDec(t.Price) * CDec(t.Size)) / totalSize

            ' PnL from FavouriteContracts point value (matches RecoverOpenTradesAsync).
            Dim root = If(entity.Symbol, String.Empty).TrimStart("/"c)
            Dim fc = FavouriteContracts.TryGetBySymbol(root)
            Dim pointValue As Decimal = If(fc IsNot Nothing AndAlso fc.PxPointValue > 0D, fc.PxPointValue, 1D)
            Dim priceDiff = If(entity.Direction = "Long", vwap - entity.EntryPrice, entity.EntryPrice - vwap)
            Dim pnl = Math.Round(priceDiff * pointValue * entity.Sizes, 2)

            ' Closing order ID: prefer the OrderId reported on the fills (broker-authoritative).
            ' When fills share a single OrderId use that; otherwise fall back to the latest fill's OrderId.
            Dim distinctOrderIds = closingFills.Select(Function(t) t.OrderId).Distinct().ToList()
            Dim exitOrderId As Long
            If distinctOrderIds.Count = 1 Then
                exitOrderId = distinctOrderIds(0)
            Else
                exitOrderId = closingFills _
                    .OrderByDescending(Function(t) ParseTs(t.CreationTimestamp)) _
                    .First().OrderId
            End If

            ' Authoritative exit time: latest closing fill timestamp.
            Dim latestExitMs = closingFills.Max(Function(t) ParseTs(t.CreationTimestamp))
            Dim exitTimeFromBroker As DateTimeOffset? = Nothing
            If latestExitMs > 0 Then exitTimeFromBroker = DateTimeOffset.FromUnixTimeMilliseconds(latestExitMs)

            Return New ExitReconciliationPlan With {
                .ExitPrice = Math.Round(vwap, 6),
                .PnL = pnl,
                .ExitOrderId = exitOrderId,
                .ExitTime = exitTimeFromBroker
            }
        End Function

        ''' <summary>
        ''' OBS-07 F1: when the broker returns rows but the contract matcher rejects every
        ''' single one, emit a structured Warning naming the matcher prefix and a sample of
        ''' the rejected ContractIds so the next paper-session close immediately surfaces
        ''' hypothesis C from the OBS-07 ticket (matcher mis-resolution).
        ''' </summary>
        Private Sub EmitMatcherRejectionWarning(Of T)(rows As IList(Of T),
                                                       matcher As Func(Of String, Boolean),
                                                       recordId As Long,
                                                       kind As String,
                                                       rootPrefix As String,
                                                       literalContractId As String,
                                                       contractIdSelector As Func(Of T, String))
            Dim diag = DetectMatcherRejection(rows, matcher, contractIdSelector)
            If Not diag.Rejected Then Return
            Dim matcherLabel = If(String.IsNullOrEmpty(rootPrefix),
                                   $"literal='{literalContractId}'",
                                   $"prefix='{rootPrefix}'")
            _logger.LogWarning(
                "CaptureClosingSnapshots: record {Id} matcher rejected all {Count} {Kind} (sample={Sample}) — matcher={Matcher}",
                recordId, diag.Count, kind, diag.Sample, matcherLabel)
        End Sub

        ''' <summary>
        ''' OBS-07 F4: pure detector. Returns (Rejected=True, Count, Sample) when the
        ''' broker response has rows but the matcher accepts none of them; otherwise
        ''' Rejected=False. Tests use this to verify matcher-rejection paths without a
        ''' logger capture.
        ''' </summary>
        Friend Shared Function DetectMatcherRejection(Of T)(rows As IList(Of T),
                                                             matcher As Func(Of String, Boolean),
                                                             contractIdSelector As Func(Of T, String)) _
            As (Rejected As Boolean, Count As Integer, Sample As String)
            If rows Is Nothing OrElse rows.Count = 0 Then Return (False, 0, String.Empty)
            Dim accepted = Enumerable.Count(rows, Function(x) matcher(contractIdSelector(x)))
            If accepted > 0 Then Return (False, rows.Count, String.Empty)
            Dim sample = String.Join(", ",
                rows.Take(3) _
                    .Select(Function(x) If(contractIdSelector(x), "(null)")))
            Return (True, rows.Count, sample)
        End Function

        ''' <summary>
        ''' OBS-07 F4: pure mapper. Filters PXOrderDtos by the contract matcher and
        ''' converts the survivors to TradeOrderSnapshotEntity rows ready for persistence.
        ''' </summary>
        Friend Shared Function MapMatchingOrders(orders As IList(Of API.Models.Responses.PXOrderDto),
                                                   matcher As Func(Of String, Boolean),
                                                   recordId As Long) As List(Of TradeOrderSnapshotEntity)
            Dim result As New List(Of TradeOrderSnapshotEntity)()
            If orders Is Nothing OrElse matcher Is Nothing Then Return result
            For Each o In orders.Where(Function(x) matcher(x.ContractId))
                result.Add(New TradeOrderSnapshotEntity With {
                    .LiveTradeRecordId = recordId,
                    .TopStepXOrderId = o.Id,
                    .ContractId = o.ContractId,
                    .OrderType = MapOrderType(o.OrderType),
                    .Side = MapSide(o.Side),
                    .Status = MapOrderStatus(o.Status),
                    .Size = o.Size,
                    .LimitPrice = If(o.LimitPrice.HasValue, o.LimitPrice.Value.ToString("G", Globalization.CultureInfo.InvariantCulture), Nothing),
                    .StopPrice = If(o.StopPrice.HasValue, o.StopPrice.Value.ToString("G", Globalization.CultureInfo.InvariantCulture), Nothing),
                    .FilledPrice = If(o.AvgFillPrice.HasValue, o.AvgFillPrice.Value.ToString("G", Globalization.CultureInfo.InvariantCulture), Nothing),
                    .CreatedAt = If(String.IsNullOrEmpty(o.CreationTimestamp), DateTimeOffset.UtcNow.ToString("o"), o.CreationTimestamp),
                    .UpdatedAt = Nothing,
                    .RawJson = SafeSerialize(o)
                })
            Next
            Return result
        End Function

        ''' <summary>
        ''' OBS-07 F4: pure mapper for broker-side position rows.
        ''' </summary>
        Friend Shared Function MapMatchingPositions(positions As IList(Of API.Models.Responses.PXPositionDto),
                                                     matcher As Func(Of String, Boolean),
                                                     recordId As Long,
                                                     entryTime As DateTimeOffset,
                                                     exitTime As DateTimeOffset?) As List(Of TradePositionSnapshotEntity)
            Dim result As New List(Of TradePositionSnapshotEntity)()
            If positions Is Nothing OrElse matcher Is Nothing Then Return result
            For Each p In positions.Where(Function(x) matcher(x.ContractId))
                result.Add(New TradePositionSnapshotEntity With {
                    .LiveTradeRecordId = recordId,
                    .TopStepXPositionId = p.Id,
                    .ContractId = p.ContractId,
                    .Side = If(p.PositionType = 1, "Buy", "Sell"),
                    .Size = p.Size,
                    .AvgEntryPrice = p.AveragePrice.ToString("G", Globalization.CultureInfo.InvariantCulture),
                    .RealisedPnL = p.OpenPnL.ToString("G", Globalization.CultureInfo.InvariantCulture),
                    .OpenedAt = If(String.IsNullOrEmpty(p.CreationTimestamp), entryTime.UtcDateTime.ToString("o"), p.CreationTimestamp),
                    .ClosedAt = If(exitTime.HasValue, exitTime.Value.UtcDateTime.ToString("o"), Nothing),
                    .RawJson = SafeSerialize(p)
                })
            Next
            Return result
        End Function

        ''' <summary>
        ''' OBS-07 F4: pure mapper for broker-side fill rows.
        ''' </summary>
        Friend Shared Function MapMatchingFills(trades As IList(Of API.Models.Responses.PXTradeDto),
                                                  matcher As Func(Of String, Boolean),
                                                  recordId As Long) As List(Of TradeFillSnapshotEntity)
            Dim result As New List(Of TradeFillSnapshotEntity)()
            If trades Is Nothing OrElse matcher Is Nothing Then Return result
            For Each t In trades.Where(Function(x) matcher(x.ContractId))
                result.Add(New TradeFillSnapshotEntity With {
                    .LiveTradeRecordId = recordId,
                    .TopStepXTradeId = t.Id,
                    .TopStepXOrderId = t.OrderId,
                    .ContractId = t.ContractId,
                    .Side = MapSide(t.Side),
                    .Size = t.Size,
                    .Price = t.Price.ToString("G", Globalization.CultureInfo.InvariantCulture),
                    .Timestamp = If(String.IsNullOrEmpty(t.CreationTimestamp), DateTimeOffset.UtcNow.ToString("o"), t.CreationTimestamp),
                    .RawJson = SafeSerialize(t)
                })
            Next
            Return result
        End Function

        ''' <summary>
        ''' OBS-07 F4: pure helper for the accountId-zero skip path. Returns the
        ''' canonical warning message format so tests can assert the wording without
        ''' a logger-capture harness, and the production scheduler in
        ''' <c>CloseTradeAsync</c> reuses the same message.
        ''' </summary>
        Friend Shared Function FormatAccountIdSkipWarning(recordId As Long) As String
            Return $"CaptureClosingSnapshots skipped for record {recordId} — no SelectedAccount (accountId=0); broker snapshots will not be captured"
        End Function

        ''' <summary>
        ''' BUG-92: helper exposed for unit tests so we can drive ContractId resolution without
        ''' loading the full FavouriteContracts table. Returns "CON.F.US.&lt;root&gt;." when the
        ''' record's symbol resolves to a favourite with a PxRootSymbol, otherwise Nothing.
        ''' </summary>
        Friend Shared Function TryResolveContractRootPrefix(contractIdOrSymbol As String) As String
            If String.IsNullOrEmpty(contractIdOrSymbol) Then Return Nothing
            Dim fav = FavouriteContracts.TryGetBySymbol(contractIdOrSymbol.TrimStart("/"c))
            If fav Is Nothing Then fav = FavouriteContracts.TryGetBySymbolResolved(contractIdOrSymbol)
            If fav Is Nothing OrElse String.IsNullOrEmpty(fav.PxRootSymbol) Then Return Nothing
            Return $"CON.F.US.{fav.PxRootSymbol}."
        End Function

        ''' <summary>
        ''' BUG-92: builds the contract-ID matcher used to filter broker rows. Prefers root-prefix
        ''' matching when the favourite resolves; falls back to literal equality so legacy rows
        ''' with fully-qualified ContractIds still match.
        ''' </summary>
        Friend Shared Function BuildContractMatcher(literalContractId As String,
                                                     rootPrefix As String) As Func(Of String, Boolean)
            If Not String.IsNullOrEmpty(rootPrefix) Then
                Return Function(s) Not String.IsNullOrEmpty(s) AndAlso
                                   s.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            End If
            Dim literal = If(literalContractId, String.Empty)
            Return Function(s) String.Equals(s, literal, StringComparison.OrdinalIgnoreCase)
        End Function

        Public Async Function BackfillSnapshotsAsync(accountId As Long) As Task _
            Implements ITradeRecordService.BackfillSnapshotsAsync
            If accountId = 0 Then Return
            Dim closedRecords As IList(Of LiveTradeRecordEntity)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    closedRecords = Await repo.GetRecentAsync(5000, closedOnly:=True)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "BackfillSnapshots: failed to load closed records")
                Return
            End Try

            ' BUG-67: throttle PX traffic, cancellable, periodic progress.
            Dim processed As Integer = 0
            Dim total = closedRecords.Count
            For Each rec In closedRecords
                Dim hasAny As Boolean
                Try
                    Using scope = _scopeFactory.CreateScope()
                        Dim snapRepo = scope.ServiceProvider.GetRequiredService(Of ITradeSnapshotRepository)()
                        hasAny = Await snapRepo.HasAnySnapshotsAsync(rec.Id)
                    End Using
                Catch ex As Exception
                    _logger.LogWarning(ex, "BackfillSnapshots: HasAny check failed for record {Id}", rec.Id)
                    Continue For
                End Try
                If hasAny Then Continue For

                Try
                    Await CaptureClosingSnapshotsAsync(rec.Id, accountId)
                    processed += 1
                    If processed Mod 25 = 0 Then
                        _logger.LogInformation("BackfillSnapshots: {Done}/{Total} records processed", processed, total)
                    End If
                    ' Inter-request pacing so we don't hammer TopStepX REST.
                    Await Task.Delay(100)
                Catch ex As Exception
                    _logger.LogWarning(ex, "BackfillSnapshots: capture failed for record {Id}", rec.Id)
                End Try
            Next
            _logger.LogInformation("BackfillSnapshots complete: {Count} record(s) backfilled", processed)
        End Function

        Public Async Function BackfillExitPricesAsync(accountId As Long) As Task(Of Integer) _
            Implements ITradeRecordService.BackfillExitPricesAsync
            If accountId = 0 Then Return 0

            Dim closedRecords As IList(Of LiveTradeRecordEntity)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    closedRecords = Await repo.GetRecentAsync(5000, closedOnly:=True)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "BackfillExitPrices: failed to load closed records")
                Return 0
            End Try

            ' Skip records that already carry a broker-confirmed exit (ExitOrderId > 0).
            Dim eligible = closedRecords.Where(Function(r) r.ExitOrderId = 0L).ToList()
            If eligible.Count = 0 Then
                _logger.LogInformation("BackfillExitPrices: no records need reconciliation")
                Return 0
            End If

            _logger.LogInformation("BackfillExitPrices: {Count} record(s) need reconciliation", eligible.Count)

            Dim reconciledCount As Integer = 0
            Dim attempts As Integer = 0
            For Each rec In eligible
                attempts += 1
                Try
                    Dim before = rec.ExitOrderId
                    Await CaptureClosingSnapshotsAsync(rec.Id, accountId)

                    ' Re-load to detect whether the reconciliation step actually wrote a non-zero ExitOrderId.
                    Using scope = _scopeFactory.CreateScope()
                        Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                        Dim refreshed = Await repo.GetByIdAsync(rec.Id)
                        If refreshed IsNot Nothing AndAlso refreshed.ExitOrderId <> 0L AndAlso refreshed.ExitOrderId <> before Then
                            reconciledCount += 1
                        End If
                    End Using

                    If attempts Mod 25 = 0 Then
                        _logger.LogInformation("BackfillExitPrices: {Done}/{Total} attempted, {Reconciled} reconciled so far",
                                               attempts, eligible.Count, reconciledCount)
                    End If
                    ' Inter-request pacing so we don't hammer TopStepX REST.
                    Await Task.Delay(100)
                Catch ex As Exception
                    _logger.LogWarning(ex, "BackfillExitPrices: reconciliation failed for record {Id}", rec.Id)
                End Try
            Next

            _logger.LogInformation("BackfillExitPrices complete: {Reconciled}/{Attempted} record(s) reconciled",
                                   reconciledCount, attempts)
            Return reconciledCount
        End Function

        Public Async Function AuditZeroEntryPriceRowsAsync(accountId As Long) As Task(Of EntryPriceAuditResult) _
            Implements ITradeRecordService.AuditZeroEntryPriceRowsAsync
            Dim result As New EntryPriceAuditResult()
            If accountId = 0L Then Return result

            Dim candidates As List(Of LiveTradeRecordEntity)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Dim closed = Await repo.GetRecentAsync(5000, closedOnly:=True)
                    candidates = closed.Where(Function(r) r.EntryPrice = 0D).ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "AuditZeroEntryPriceRowsAsync: failed to read closed records")
                Return result
            End Try

            result.Audited = candidates.Count
            If candidates.Count = 0 Then
                _logger.LogInformation("AuditZeroEntryPriceRowsAsync: no candidate rows found")
                Return result
            End If

            _logger.LogInformation("AuditZeroEntryPriceRowsAsync: {Count} candidate row(s) found", candidates.Count)
            For Each rec In candidates
                Try
                    Dim resolved As Decimal = Await TryResolveEntryPriceAsync(rec)
                    If resolved > 0D Then
                        Using scope = _scopeFactory.CreateScope()
                            Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                            Await repo.UpdateEntryPriceAsync(rec.Id, resolved)
                        End Using
                        result.Repaired += 1
                        _logger.LogInformation("AuditZeroEntryPriceRowsAsync: repaired record {Id} EntryPrice→{Price:F4}", rec.Id, resolved)
                    Else
                        result.Unresolvable += 1
                        _logger.LogWarning(
                            "AuditZeroEntryPriceRowsAsync: record {Id} ({Symbol}, EntryTime={Entry:o}) unresolvable — manual handling required",
                            rec.Id, rec.Symbol, rec.EntryTime)
                    End If
                    Await Task.Delay(100)
                Catch ex As Exception
                    result.Unresolvable += 1
                    _logger.LogWarning(ex, "AuditZeroEntryPriceRowsAsync: resolution threw for record {Id}", rec.Id)
                End Try
            Next

            _logger.LogInformation(
                "AuditZeroEntryPriceRowsAsync complete: audited={Audited} repaired={Repaired} unresolvable={Unresolvable}",
                result.Audited, result.Repaired, result.Unresolvable)
            Return result
        End Function

        Private Shared Function MapSide(side As Integer) As String
            Return If(side = 0, "Buy", "Sell")
        End Function

        Private Shared Function MapOrderType(t As Integer) As String
            Select Case t
                Case 1 : Return "Limit"
                Case 2 : Return "Market"
                Case 4 : Return "Stop"
                Case 5 : Return "TrailingStop"
                Case Else : Return $"Type{t}"
            End Select
        End Function

        Private Shared Function MapOrderStatus(s As Integer) As String
            Select Case s
                Case 1 : Return "Open"
                Case 2 : Return "Filled"
                Case 3 : Return "Cancelled"
                Case 4 : Return "Expired"
                Case 5 : Return "Rejected"
                Case 6 : Return "Pending"
                Case Else : Return $"Status{s}"
            End Select
        End Function

        Private Shared Function SafeSerialize(value As Object) As String
            Try
                Return System.Text.Json.JsonSerializer.Serialize(value)
            Catch
                Return "{}"
            End Try
        End Function

        ''' <summary>
        ''' BUG-63: wraps a PX REST call so the parallel WhenAll for snapshot capture
        ''' never throws \u2014 each failure is logged independently and returns Nothing.
        ''' OBS-07 F1: when the wrapped response inherits PXBaseResponse and reports
        ''' Success=False, log the broker errorCode/errorMessage at Warning so the
        ''' postmortem CLI can grep-mine the failure mode (covers hypothesis D).
        ''' </summary>
        Private Async Function SafePxCallAsync(Of TResp As Class)(invoker As Func(Of Task(Of TResp)),
                                                                  cancel As Threading.CancellationToken,
                                                                  recordId As Long,
                                                                  callName As String) As Task(Of TResp)
            Try
                Dim t = invoker()
                If t Is Nothing Then Return Nothing
                Dim resp = Await t.WaitAsync(cancel)
                Dim baseResp = TryCast(CObj(resp), API.Models.Responses.PXBaseResponse)
                If baseResp IsNot Nothing AndAlso Not baseResp.Success Then
                    _logger.LogWarning(
                        "CaptureClosingSnapshots: {Call} returned Success=false for record {Id} errorCode={Code} errorMessage={Msg}",
                        callName, recordId, baseResp.ErrorCode, If(baseResp.ErrorMessage, "(none)"))
                End If
                Return resp
            Catch ex As Exception
                _logger.LogWarning(ex, "CaptureClosingSnapshots: {Call} failed for record {Id}", callName, recordId)
                Return Nothing
            End Try
        End Function

        Private Shared Function ToModel(e As LiveTradeRecordEntity) As LiveTradeRecord
            Return New LiveTradeRecord With {
                .Id = e.Id,
                .EntryOrderId = e.EntryOrderId,
                .TopStepXTradeId = e.TopStepXTradeId,
                .ExitOrderId = e.ExitOrderId,
                .ContractId = e.ContractId,
                .Symbol = e.Symbol,
                .Direction = e.Direction,
                .Sizes = e.Sizes,
                .MaxScaleIns = e.MaxScaleIns,
                .StrategyName = e.StrategyName,
                .Persona = e.Persona,
                .Timeframe = e.Timeframe,
                .EntryTime = e.EntryTime,
                .ExitTime = e.ExitTime,
                .EntryPrice = e.EntryPrice,
                .ExitPrice = e.ExitPrice,
                .PnL = e.PnL,
                .CommissionUsd = e.CommissionUsd,
                .FeesUsd = e.FeesUsd,
                .ExitReason = e.ExitReason,
                .IsOpen = e.IsOpen,
                .IsRecoveredFromCrash = e.IsRecoveredFromCrash
            }
        End Function

    End Class

    ''' <summary>
    ''' BUG-92: result of <see cref="TradeRecordService.ComputeExitReconciliation"/>. Mutable
    ''' so tests can construct expected values inline without a record constructor.
    ''' </summary>
    Public Class ExitReconciliationPlan
        Public Property ExitPrice As Decimal
        Public Property PnL As Decimal
        Public Property ExitOrderId As Long
        Public Property ExitTime As DateTimeOffset?
    End Class

End Namespace
