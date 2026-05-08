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
        ''' On startup, finds any IsOpen records (indicating a crash before close was written)
        ''' and queries TopStepX trade history to resolve their exit fills.
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

            ' Search last 48 hours of fills from the broker
            Dim sinceMs = DateTimeOffset.UtcNow.AddHours(-48).ToUnixTimeMilliseconds()
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
            Next
        End Function

        Private Shared Function ParseTs(raw As String) As Long
            Dim v As Long
            Long.TryParse(raw, v)
            Return v
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
