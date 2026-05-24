Imports System.Threading
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Trades

    ''' <summary>
    ''' BUG-93 F2: broker-fill persistence floor. Subscribes to
    ''' <see cref="IOrderService.OrderFilled"/> and writes a minimal <c>LiveTradeRecord</c>
    ''' for every broker fill the strategy layer has not already attributed. This guarantees
    ''' every executed trade is visible to the Post-Mortem tab even when the consuming
    ''' strategy never sees the fill (the 2026-05-21 UAT incident: pre-staged MGC sell-stop
    ''' filled with no SL attach, no card, no record — completely invisible).
    '''
    ''' Idempotency: <see cref="ITradeRecordService.FindByEntryOrderIdAsync"/> is consulted
    ''' on every fill; if a strategy-attributed record already exists for the broker order id
    ''' the logger no-ops. F1's per-orderId dedup on the hub bridge means this lookup
    ''' primarily defends against the strategy-then-logger race rather than duplicate hub
    ''' pushes for the same order.
    '''
    ''' Lifecycle: <see cref="IHostedService"/>. The handler is attached in
    ''' <see cref="StartAsync"/> and detached in <see cref="StopAsync"/>.
    ''' </summary>
    Public Class BrokerFillTradeLogger
        Implements IHostedService

        Private Const UnattributedStrategyName As String = "BrokerFill-Unattributed"

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _tradeRecord As ITradeRecordService
        Private ReadOnly _logger As ILogger(Of BrokerFillTradeLogger)

        Private ReadOnly _handler As EventHandler(Of OrderFilledEventArgs)
        Private _started As Boolean

        Public Sub New(orderService As IOrderService,
                       tradeRecord As ITradeRecordService,
                       logger As ILogger(Of BrokerFillTradeLogger))
            _orderService = orderService
            _tradeRecord = tradeRecord
            _logger = logger
            _handler = AddressOf OnOrderFilled
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            If Not _started Then
                AddHandler _orderService.OrderFilled, _handler
                _started = True
                _logger.LogInformation("BrokerFillTradeLogger started — listening on IOrderService.OrderFilled")
            End If
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            If _started Then
                RemoveHandler _orderService.OrderFilled, _handler
                _started = False
                _logger.LogInformation("BrokerFillTradeLogger stopped")
            End If
            Return Task.CompletedTask
        End Function

        ''' <summary>
        ''' BUG-93 F2: per-fill handler. Looks up any existing strategy-attributed record for the
        ''' broker order id; if none, writes a minimal "BrokerFill-Unattributed" record so the
        ''' post-mortem flow can surface the fill. <see cref="TryPersistFillAsync"/> is the
        ''' Friend-visible work method so the test can drive it deterministically without
        ''' waiting on the event-handler fire-and-forget background task.
        ''' </summary>
        Private Sub OnOrderFilled(sender As Object, e As OrderFilledEventArgs)
            If e?.Order Is Nothing Then Return
#Disable Warning BC42358
            Task.Run(Async Function()
                         Try
                             Await TryPersistFillAsync(e.Order)
                         Catch ex As Exception
                             _logger.LogWarning(ex, "BrokerFillTradeLogger: persistence failed for orderId={Id}",
                                 If(e.Order.ExternalOrderId.HasValue, e.Order.ExternalOrderId.Value, 0L))
                         End Try
                     End Function)
#Enable Warning BC42358
        End Sub

        ''' <summary>
        ''' BUG-93 F2 — Friend test seam. Writes the unattributed record when no strategy-side
        ''' record matches by EntryOrderId. Returns the new record id (or 0 for no-op / failure).
        ''' Defensive default on lookup failure: still persists, so we over-log rather than
        ''' silently drop a fill (matches the "F3b — Survives_FindByEntryOrderIdAsync_Throwing"
        ''' acceptance test).
        ''' </summary>
        Friend Async Function TryPersistFillAsync(order As Order) As Task(Of Long)
            If order Is Nothing OrElse Not order.ExternalOrderId.HasValue Then Return 0L
            Dim entryOrderId = order.ExternalOrderId.Value

            Dim existing As LiveTradeRecord = Nothing
            Try
                existing = Await _tradeRecord.FindByEntryOrderIdAsync(entryOrderId)
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "BrokerFillTradeLogger: FindByEntryOrderIdAsync threw for orderId={Id} — defaulting to persistence",
                    entryOrderId)
            End Try

            If existing IsNot Nothing Then
                _logger.LogDebug(
                    "BrokerFillTradeLogger: orderId={Id} already attributed to record {RecId} ({Strategy}) — skipping",
                    entryOrderId, existing.Id, existing.StrategyName)
                Return 0L
            End If

            Dim record = BuildUnattributedRecord(order)
            Try
                Dim newId = Await _tradeRecord.OpenTradeAsync(record)
                _logger.LogInformation(
                    "BrokerFillTradeLogger: persisted unattributed fill orderId={Id} {Dir} {Contract} @ {Price} → record {RecId}",
                    entryOrderId, record.Direction, record.ContractId, record.EntryPrice, newId)
                Return newId
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "BrokerFillTradeLogger: OpenTradeAsync failed for orderId={Id}", entryOrderId)
                Return 0L
            End Try
        End Function

        ''' <summary>
        ''' BUG-93 F2: builds the minimal <see cref="LiveTradeRecord"/> persisted when no strategy
        ''' claims the fill. <c>StrategyName = "BrokerFill-Unattributed"</c> is a clear post-mortem
        ''' signal. Empty Persona/Timeframe are tolerated by the schema (both columns are non-NULL
        ''' string with empty-string defaults — see LiveTradeRecordEntity).
        ''' </summary>
        Friend Shared Function BuildUnattributedRecord(order As Order) As LiveTradeRecord
            Dim entryPrice As Decimal = If(order.FillPrice.HasValue, order.FillPrice.Value, 0D)
            Return New LiveTradeRecord With {
                .EntryOrderId = If(order.ExternalOrderId.HasValue, order.ExternalOrderId.Value, 0L),
                .ContractId = If(order.ContractId, String.Empty),
                .Symbol = If(order.ContractId, String.Empty),
                .Direction = If(order.Side = OrderSide.Buy, "Long", "Short"),
                .Sizes = order.Quantity,
                .StrategyName = UnattributedStrategyName,
                .Persona = String.Empty,
                .Timeframe = String.Empty,
                .EntryTime = DateTimeOffset.UtcNow,
                .EntryPrice = entryPrice,
                .InitialStopPrice = 0D,
                .IsOpen = True
            }
        End Function

    End Class

End Namespace
