Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class OrderRepository

        Private ReadOnly _context As AppDbContext
        Private ReadOnly _logger As ILogger(Of OrderRepository)

        Public Sub New(context As AppDbContext, logger As ILogger(Of OrderRepository))
            _context = context
            _logger = logger
        End Sub

        Public Async Function SaveOrderAsync(order As Order,
                                             Optional cancel As CancellationToken = Nothing) As Task(Of Long)
            Dim entity = MapToEntity(order)
            _context.Orders.Add(entity)
            Await _context.SaveChangesAsync(cancel)
            Return entity.Id
        End Function

        Public Async Function UpdateOrderStatusAsync(internalId As Long,
                                                     status As OrderStatus,
                                                     Optional externalOrderId As Long? = Nothing,
                                                     Optional fillPrice As Decimal? = Nothing,
                                                     Optional filledAt As DateTimeOffset? = Nothing,
                                                     Optional cancel As CancellationToken = Nothing) As Task
            Dim entity = Await _context.Orders.FindAsync(New Object() {internalId}, cancel)
            If entity Is Nothing Then Return
            entity.Status = CByte(status)
            If externalOrderId.HasValue Then entity.ExternalOrderId = externalOrderId
            If fillPrice.HasValue Then entity.FillPrice = fillPrice
            If filledAt.HasValue Then entity.FilledAt = filledAt
            Await _context.SaveChangesAsync(cancel)
        End Function

        ' UAT-BUG-009: PlacedAt is DateTimeOffset — EF Core SQLite cannot translate
        ' DateTimeOffset in ORDER BY clauses.  Fetch unordered, sort in-memory.

        ''' <summary>Returns all open orders across all accounts (for cancel-all operations).</summary>
        Public Async Function GetOpenOrdersAsync(Optional cancel As CancellationToken = Nothing) As Task(Of List(Of Order))
            Dim openStatuses = {CByte(OrderStatus.Pending), CByte(OrderStatus.Working)}
            Dim entities = Await _context.Orders _
                .Where(Function(o) openStatuses.Contains(o.Status)) _
                .ToListAsync(cancel)
            Return entities.OrderByDescending(Function(o) o.PlacedAt) _
                           .Select(AddressOf MapToModel).ToList()
        End Function

        Public Async Function GetOpenOrdersAsync(accountId As Long,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of List(Of Order))
            Dim openStatuses = {CByte(OrderStatus.Pending), CByte(OrderStatus.Working)}
            Dim entities = Await _context.Orders _
                .Where(Function(o) o.AccountId = accountId AndAlso openStatuses.Contains(o.Status)) _
                .ToListAsync(cancel)
            Return entities.OrderByDescending(Function(o) o.PlacedAt) _
                           .Select(AddressOf MapToModel).ToList()
        End Function

        Public Async Function GetOrderHistoryAsync(accountId As Long,
                                                    from As DateTime,
                                                    [to] As DateTime,
                                                    Optional cancel As CancellationToken = Nothing) As Task(Of List(Of Order))
            ' UAT-BUG-009: PlacedAt is DateTimeOffset — filter and sort in-memory.
            Dim entities = Await _context.Orders _
                .Where(Function(o) o.AccountId = accountId) _
                .ToListAsync(cancel)
            Return entities _
                .Where(Function(o) o.PlacedAt >= from AndAlso o.PlacedAt <= [to]) _
                .OrderByDescending(Function(o) o.PlacedAt) _
                .Select(AddressOf MapToModel).ToList()
        End Function

        ''' <summary>Today's total P&amp;L across all accounts (for risk guard without account context).</summary>
        Public Async Function GetTodayPnLAsync(Optional cancel As CancellationToken = Nothing) As Task(Of Decimal)
            Try
                ' UAT-BUG-009: FilledAt is DateTimeOffset — filter in-memory after fetch.
                Dim todayStart = DateTimeOffset.UtcNow.Date
                Dim filled = Await _context.Orders _
                    .Where(Function(o) o.Status = CByte(OrderStatus.Filled)) _
                    .ToListAsync(cancel)
                Return filled _
                    .Where(Function(o) o.FilledAt.HasValue AndAlso o.FilledAt.Value >= todayStart) _
                    .Sum(Function(o) If(o.FillPrice.HasValue, o.FillPrice.Value, 0D))
            Catch ex As Exception
                Return 0D
            End Try
        End Function

        Public Async Function GetTodayPnLAsync(accountId As Long,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of Decimal)
            Try
                ' UAT-BUG-009: FilledAt is DateTimeOffset — filter in-memory after fetch.
                Dim todayStart = DateTimeOffset.UtcNow.Date
                Dim filled = Await _context.Orders _
                    .Where(Function(o) o.AccountId = accountId _
                                   AndAlso o.Status = CByte(OrderStatus.Filled)) _
                    .ToListAsync(cancel)
                Return filled _
                    .Where(Function(o) o.FilledAt.HasValue AndAlso o.FilledAt.Value >= todayStart) _
                    .Sum(Function(o) If(o.FillPrice.HasValue, o.FillPrice.Value, 0D))
            Catch ex As Exception
                Return 0D
            End Try
        End Function

        Private Function MapToModel(entity As OrderEntity) As Order
            Return New Order With {
                .Id = entity.Id,
                .ExternalOrderId = entity.ExternalOrderId,
                .AccountId = entity.AccountId,
                .ContractId = entity.ContractId,
                .Side = CType(entity.Side, OrderSide),
                .OrderType = CType(entity.OrderType, OrderType),
                .Quantity = entity.Quantity,
                .Amount = entity.Amount,
                .Leverage = entity.Leverage,
                .LimitPrice = entity.LimitPrice,
                .StopPrice = entity.StopPrice,
                .StopLossRate = entity.StopLossRate,
                .TakeProfitRate = entity.TakeProfitRate,
                .Status = CType(entity.Status, OrderStatus),
                .PlacedAt = entity.PlacedAt,
                .FilledAt = entity.FilledAt,
                .FillPrice = entity.FillPrice,
                .SourceSignalId = entity.SourceSignalId,
                .Notes = If(entity.Notes, String.Empty)
            }
        End Function

        Private Function MapToEntity(order As Order) As OrderEntity
            Return New OrderEntity With {
                .ExternalOrderId = order.ExternalOrderId,
                .AccountId = order.AccountId,
                .ContractId = order.ContractId,
                .Side = CByte(order.Side),
                .OrderType = CByte(order.OrderType),
                .Quantity = order.Quantity,
                .Amount = order.Amount,
                .Leverage = order.Leverage,
                .LimitPrice = order.LimitPrice,
                .StopPrice = order.StopPrice,
                .StopLossRate = order.StopLossRate,
                .TakeProfitRate = order.TakeProfitRate,
                .Status = CByte(order.Status),
                .PlacedAt = order.PlacedAt,
                .FilledAt = order.FilledAt,
                .FillPrice = order.FillPrice,
                .SourceSignalId = order.SourceSignalId,
                .Notes = order.Notes
            }
        End Function

    End Class

End Namespace
