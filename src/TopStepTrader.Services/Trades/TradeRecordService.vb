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
                                              exitReason As String) As Task _
            Implements ITradeRecordService.CloseTradeAsync
            If id = 0 Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Await repo.CloseAsync(id, exitTime, exitPrice, pnL, exitReason)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeRecordService.CloseTradeAsync failed for record {Id}", id)
            End Try

            ' FEAT-50: capture broker snapshots after the close is persisted.
            ' BUG-63: do NOT block CloseTradeAsync on PX REST calls. Schedule
            ' on the threadpool with full try/catch — trading hot path stays clean.
            Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
            If accountId <> 0 Then
                Dim svc = DirectCast(Me, ITradeRecordService)
                Dim log = _logger
                Dim recordId = id
                Dim acc = accountId
                #Disable Warning BC42358
                                Task.Run(Async Function()
                                             Try
                                                 Await svc.CaptureClosingSnapshotsAsync(recordId, acc)
                                             Catch ex As Exception
                                                 log.LogWarning(ex, "TradeRecordService.CaptureClosingSnapshotsAsync (background) failed for record {Id}", recordId)
                                             End Try
                                         End Function)
                #Enable Warning BC42358
            End If
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
            If entity Is Nothing Then Return

            Dim startTs = entity.EntryTime.AddMinutes(-1).ToUnixTimeMilliseconds()
            Dim endTimeRef = If(entity.ExitTime.HasValue, entity.ExitTime.Value, DateTimeOffset.UtcNow)
            Dim endTs = endTimeRef.AddMinutes(1).ToUnixTimeMilliseconds()
            Dim contractId = entity.ContractId

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

            Dim orderRows As New List(Of TradeOrderSnapshotEntity)()
            Dim ordersResp = ordersTask.Result
            If ordersResp IsNot Nothing AndAlso ordersResp.Orders IsNot Nothing Then
                For Each o In ordersResp.Orders.Where(Function(x) x.ContractId = contractId)
                    orderRows.Add(New TradeOrderSnapshotEntity With {
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
            End If

            Dim positionRows As New List(Of TradePositionSnapshotEntity)()
            Dim positionsResp = positionsTask.Result
            If positionsResp IsNot Nothing AndAlso positionsResp.Positions IsNot Nothing Then
                For Each p In positionsResp.Positions.Where(Function(x) x.ContractId = contractId)
                    positionRows.Add(New TradePositionSnapshotEntity With {
                        .LiveTradeRecordId = recordId,
                        .TopStepXPositionId = p.Id,
                        .ContractId = p.ContractId,
                        .Side = If(p.PositionType = 1, "Buy", "Sell"),
                        .Size = p.Size,
                        .AvgEntryPrice = p.AveragePrice.ToString("G", Globalization.CultureInfo.InvariantCulture),
                        .RealisedPnL = p.OpenPnL.ToString("G", Globalization.CultureInfo.InvariantCulture),
                        .OpenedAt = If(String.IsNullOrEmpty(p.CreationTimestamp), entity.EntryTime.UtcDateTime.ToString("o"), p.CreationTimestamp),
                        .ClosedAt = If(entity.ExitTime.HasValue, entity.ExitTime.Value.UtcDateTime.ToString("o"), Nothing),
                        .RawJson = SafeSerialize(p)
                    })
                Next
            End If

            Dim fillRows As New List(Of TradeFillSnapshotEntity)()
            Dim tradesResp = tradesTask.Result
            If tradesResp IsNot Nothing AndAlso tradesResp.Trades IsNot Nothing Then
                For Each t In tradesResp.Trades.Where(Function(x) x.ContractId = contractId)
                    fillRows.Add(New TradeFillSnapshotEntity With {
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
            End If

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
        ''' </summary>
        Private Async Function SafePxCallAsync(Of TResp As Class)(invoker As Func(Of Task(Of TResp)),
                                                                  cancel As Threading.CancellationToken,
                                                                  recordId As Long,
                                                                  callName As String) As Task(Of TResp)
            Try
                Dim t = invoker()
                If t Is Nothing Then Return Nothing
                Return Await t.WaitAsync(cancel)
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

End Namespace
