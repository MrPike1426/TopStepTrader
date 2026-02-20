Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Enums
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
                                                     Optional fillPrice As Decimal? = Nothing,
                                                     Optional filledAt As DateTimeOffset? = Nothing,
                                                     Optional cancel As CancellationToken = Nothing) As Task
            Dim entity = Await _context.Orders.FindAsync(New Object() {internalId}, cancel)
            If entity Is Nothing Then Return
            entity.Status = CByte(status)
            If fillPrice.HasValue Then entity.FillPrice = fillPrice
            If filledAt.HasValue Then entity.FilledAt = filledAt
            Await _context.SaveChangesAsync(cancel)
        End Function

        Public Async Function GetOpenOrdersAsync(accountId As Long,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of List(Of Order))
            Dim openStatuses = {CByte(OrderStatus.Pending), CByte(OrderStatus.Working)}
            Dim entities = Await _context.Orders _
                .Where(Function(o) o.AccountId = accountId AndAlso openStatuses.Contains(o.Status)) _
                .OrderByDescending(Function(o) o.PlacedAt) _
                .ToListAsync(cancel)
            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        Public Async Function GetOrderHistoryAsync(accountId As Long,
                                                    from As DateTime,
                                                    [to] As DateTime,
                                                    Optional cancel As CancellationToken = Nothing) As Task(Of List(Of Order))
            Dim entities = Await _context.Orders _
                .Where(Function(o) o.AccountId = accountId _
                               AndAlso o.PlacedAt >= from _
                               AndAlso o.PlacedAt <= [to]) _
                .OrderByDescending(Function(o) o.PlacedAt) _
                .ToListAsync(cancel)
            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        Public Async Function GetTodayPnLAsync(accountId As Long,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of Decimal)
            Dim todayStart = DateTimeOffset.UtcNow.Date
            Dim pnl = Await _context.Orders _
                .Where(Function(o) o.AccountId = accountId _
                               AndAlso o.Status = CByte(OrderStatus.Filled) _
                               AndAlso o.FilledAt >= todayStart) _
                .SumAsync(Function(o) If(o.FillPrice.HasValue, o.FillPrice.Value, 0D), cancel)
            Return pnl   ' Simplified — real P&L needs entry vs exit price tracking
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
                .LimitPrice = entity.LimitPrice,
                .StopPrice = entity.StopPrice,
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
                .LimitPrice = order.LimitPrice,
                .StopPrice = order.StopPrice,
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
