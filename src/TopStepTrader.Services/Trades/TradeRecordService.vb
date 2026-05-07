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
        Private ReadOnly _logger As ILogger(Of TradeRecordService)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       orderClient As PXOrderClient,
                       logger As ILogger(Of TradeRecordService))
            _scopeFactory = scopeFactory
            _orderClient = orderClient
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
                    Return Await repo.AddAsync(entity)
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
