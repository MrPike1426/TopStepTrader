Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trades
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trades

    ''' <summary>
    ''' BUG-93 F3b: regression tests for <see cref="BrokerFillTradeLogger"/>. Uses a
    ''' hand-rolled <see cref="FakeOrderService"/> + <see cref="FakeTradeRecordService"/> so
    ''' the persistence path can be driven deterministically without an EF Core context or a
    ''' broker REST mock. The logger's per-fill work is exercised via the Friend
    ''' <c>TryPersistFillAsync</c> seam so tests do not race the fire-and-forget background
    ''' task spun up inside the event handler.
    ''' </summary>
    Public Class BrokerFillTradeLoggerTests

        Private Const ContractId As String = "CON.F.US.MGC.M26"
        Private Const OrderId As Long = 3014320453L

        ' ─── Tests ─────────────────────────────────────────────────────────

        <Fact>
        Public Async Function Persists_Unattributed_Record_When_No_Existing_Match() As Task
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService()
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            Dim fill = MakeFilledOrder(OrderId, side:=OrderSide.Sell, fillPrice:=2050.4D, qty:=1)
            Dim newId = Await logger.TryPersistFillAsync(fill)

            Assert.Equal(1, recordSvc.OpenCalls.Count)
            Dim record = recordSvc.OpenCalls(0)
            Assert.Equal("BrokerFill-Unattributed", record.StrategyName)
            Assert.Equal(OrderId, record.EntryOrderId)
            Assert.Equal(2050.4D, record.EntryPrice)
            Assert.Equal("Short", record.Direction)
            Assert.Equal(1, record.Sizes)
            Assert.Equal(ContractId, record.ContractId)
            Assert.True(record.IsOpen)
            Assert.Equal(recordSvc.NextId - 1L, newId)
        End Function

        <Fact>
        Public Async Function Persists_LongDirection_When_Side_Is_Buy() As Task
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService()
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            Dim fill = MakeFilledOrder(OrderId, side:=OrderSide.Buy, fillPrice:=2051.1D, qty:=2)
            Await logger.TryPersistFillAsync(fill)

            Assert.Single(recordSvc.OpenCalls)
            Assert.Equal("Long", recordSvc.OpenCalls(0).Direction)
            Assert.Equal(2, recordSvc.OpenCalls(0).Sizes)
        End Function

        <Fact>
        Public Async Function Skips_Persistence_When_Record_Already_Exists() As Task
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService()
            ' Seed an existing record so the strategy-then-logger race short-circuits.
            recordSvc.Seed(OrderId, New LiveTradeRecord With {
                .Id = 7L,
                .EntryOrderId = OrderId,
                .StrategyName = "UltimateScalper-Primed",
                .IsOpen = True
            })
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            Dim fill = MakeFilledOrder(OrderId, side:=OrderSide.Sell)
            Dim newId = Await logger.TryPersistFillAsync(fill)

            Assert.Equal(0L, newId)
            Assert.Empty(recordSvc.OpenCalls)
        End Function

        <Fact>
        Public Async Function Survives_FindByEntryOrderIdAsync_Throwing() As Task
            ' Defensive default: if the lookup throws we still persist so we never silently
            ' drop a fill. Over-logging beats under-logging in the broker-fill audit floor.
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService() With {.FindThrows = True}
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            Dim fill = MakeFilledOrder(OrderId, side:=OrderSide.Buy)
            Dim newId = Await logger.TryPersistFillAsync(fill)

            Assert.Single(recordSvc.OpenCalls)
            Assert.Equal("BrokerFill-Unattributed", recordSvc.OpenCalls(0).StrategyName)
            Assert.NotEqual(0L, newId)
        End Function

        <Fact>
        Public Async Function NoOp_When_Order_Has_No_ExternalOrderId() As Task
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService()
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            ' Garbled fill with no broker order id — must not write an audit row.
            Dim fill As New Order With {.ExternalOrderId = Nothing, .Side = OrderSide.Buy, .Quantity = 1}
            Dim newId = Await logger.TryPersistFillAsync(fill)

            Assert.Equal(0L, newId)
            Assert.Empty(recordSvc.OpenCalls)
        End Function

        <Fact>
        Public Async Function StartAsync_AttachesHandler_AndPersists_WhenOrderFilledRaised() As Task
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService()
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            Await logger.StartAsync(CancellationToken.None)
            orderSvc.RaiseFill(MakeFilledOrder(OrderId, side:=OrderSide.Buy, fillPrice:=2050D))

            ' Persistence runs on a fire-and-forget Task.Run inside the handler. Spin briefly
            ' until the fake's OpenCalls is populated so the assertion is not racy.
            Await WaitForAsync(Function() recordSvc.OpenCalls.Count = 1, TimeSpan.FromSeconds(2))
            Assert.Single(recordSvc.OpenCalls)

            Await logger.StopAsync(CancellationToken.None)
        End Function

        <Fact>
        Public Async Function StopAsync_Detaches_Handler() As Task
            Dim orderSvc As New FakeOrderService()
            Dim recordSvc As New FakeTradeRecordService()
            Dim logger As New BrokerFillTradeLogger(orderSvc, recordSvc, NullLogger(Of BrokerFillTradeLogger).Instance)

            Await logger.StartAsync(CancellationToken.None)
            Await logger.StopAsync(CancellationToken.None)

            orderSvc.RaiseFill(MakeFilledOrder(OrderId, side:=OrderSide.Sell))
            ' Give the (theoretical) background task room to do nothing.
            Await Task.Delay(100)
            Assert.Empty(recordSvc.OpenCalls)
        End Function

        ' ─── Helpers / fakes ───────────────────────────────────────────────

        Private Shared Function MakeFilledOrder(orderId As Long,
                                                  Optional side As OrderSide = OrderSide.Buy,
                                                  Optional fillPrice As Decimal = 2050D,
                                                  Optional qty As Integer = 1) As Order
            Return New Order With {
                .ExternalOrderId = orderId,
                .ContractId = ContractId,
                .Side = side,
                .Quantity = qty,
                .OrderType = OrderType.StopOrder,
                .Status = OrderStatus.Filled,
                .FillPrice = fillPrice
            }
        End Function

        Private Shared Async Function WaitForAsync(predicate As Func(Of Boolean),
                                                    timeout As TimeSpan) As Task
            Dim deadline = DateTime.UtcNow.Add(timeout)
            While DateTime.UtcNow < deadline
                If predicate() Then Return
                Await Task.Delay(10)
            End While
        End Function

        Private Class FakeOrderService
            Implements IOrderService

            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

            Public Sub RaiseFill(order As Order)
                RaiseEvent OrderFilled(Me, New OrderFilledEventArgs(order))
            End Sub

            ' ── Unused IOrderService members (BrokerFillTradeLogger only consumes OrderFilled). ──
            Public Function PlaceOrderAsync(order As Order) As Task(Of Order) Implements IOrderService.PlaceOrderAsync
                Throw New NotImplementedException()
            End Function
            Public Function CancelOrderAsync(orderId As Long) As Task(Of Boolean) Implements IOrderService.CancelOrderAsync
                Throw New NotImplementedException()
            End Function
            Public Function CancelAllOpenOrdersAsync() As Task Implements IOrderService.CancelAllOpenOrdersAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOpenOrdersAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetOrderHistoryAsync(accountId As Long, [from] As DateTime, [to] As DateTime) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
                Throw New NotImplementedException()
            End Function
            Public Function TryGetOrderFillPriceAsync(externalOrderId As Long, accountId As Long, Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) Implements IOrderService.TryGetOrderFillPriceAsync
                Throw New NotImplementedException()
            End Function
            Public Function TryGetBracketStopPriceAsync(accountId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) Implements IOrderService.TryGetBracketStopPriceAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetLiveWorkingOrdersAsync(accountId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetLiveWorkingOrdersAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetLivePositionSnapshotAsync(accountId As Long, contractId As String, Optional positionId As Long? = Nothing, Optional bypassCache As Boolean = False, Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot) Implements IOrderService.GetLivePositionSnapshotAsync
                Throw New NotImplementedException()
            End Function
            Public Function FlattenContractAsync(accountId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.FlattenContractAsync
                Throw New NotImplementedException()
            End Function
            Public Function FlattenContractWithFillAsync(accountId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of (Success As Boolean, Fill As BrokerCloseFill)) Implements IOrderService.FlattenContractWithFillAsync
                Throw New NotImplementedException()
            End Function
            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?, Optional enableTsl As Boolean = False, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.EditPositionSlTpAsync
                Throw New NotImplementedException()
            End Function
            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.PartialCloseContractAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetOpenPositionsAsync(accountId As Long, Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot)) Implements IOrderService.GetOpenPositionsAsync
                Return Task.FromResult(Of IEnumerable(Of LivePositionSnapshot))(New List(Of LivePositionSnapshot)())
            End Function

#Disable Warning BC42024
            Private Sub SuppressEventWarnings()
                RaiseEvent OrderRejected(Me, Nothing)
                RaiseEvent PositionUpdated(Me, Nothing)
            End Sub
#Enable Warning BC42024
        End Class

        Private Class FakeTradeRecordService
            Implements ITradeRecordService

            Public ReadOnly OpenCalls As New List(Of LiveTradeRecord)()
            Public ReadOnly SeededByEntryOrderId As New ConcurrentDictionary(Of Long, LiveTradeRecord)()
            Public Property FindThrows As Boolean
            Public NextId As Long = 100L

            Public Sub Seed(entryOrderId As Long, record As LiveTradeRecord)
                SeededByEntryOrderId(entryOrderId) = record
            End Sub

            Public Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long) _
                Implements ITradeRecordService.OpenTradeAsync
                OpenCalls.Add(record)
                Dim assigned = NextId
                NextId += 1L
                record.Id = assigned
                Return Task.FromResult(assigned)
            End Function

            Public Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecord) _
                Implements ITradeRecordService.FindByEntryOrderIdAsync
                If FindThrows Then Throw New InvalidOperationException("DB unavailable")
                Dim existing As LiveTradeRecord = Nothing
                SeededByEntryOrderId.TryGetValue(externalOrderId, existing)
                Return Task.FromResult(existing)
            End Function

            Public Function FindOpenByContractIdAsync(accountId As Long, contractId As String) As Task(Of LiveTradeRecord) _
                Implements ITradeRecordService.FindOpenByContractIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function

            ' ── Unused ITradeRecordService members ──────────────────────────
            Public Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                                              pnL As Decimal, exitReason As String,
                                              Optional closeFillSource As String = Nothing) As Task _
                Implements ITradeRecordService.CloseTradeAsync
                Return Task.CompletedTask
            End Function
            Public Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task _
                Implements ITradeRecordService.UpdateEntryPriceAsync
                Return Task.CompletedTask
            End Function
            Public Function ResolveTopStepXTradeIdAsync(recordId As Long, topStepXTradeId As Long) As Task _
                Implements ITradeRecordService.ResolveTopStepXTradeIdAsync
                Return Task.CompletedTask
            End Function
            Public Function GetRecentTradesAsync(count As Integer, Optional filter As TradeFilter = Nothing) As Task(Of IList(Of LiveTradeRecord)) _
                Implements ITradeRecordService.GetRecentTradesAsync
                Return Task.FromResult(Of IList(Of LiveTradeRecord))(New List(Of LiveTradeRecord))
            End Function
            Public Function GetOpenTradesAsync() As Task(Of IList(Of LiveTradeRecord)) _
                Implements ITradeRecordService.GetOpenTradesAsync
                Return Task.FromResult(Of IList(Of LiveTradeRecord))(New List(Of LiveTradeRecord))
            End Function
            Public Function GetTradeByIdAsync(id As Long) As Task(Of LiveTradeRecord) _
                Implements ITradeRecordService.GetTradeByIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function RecoverOpenTradesAsync(accountId As Long) As Task _
                Implements ITradeRecordService.RecoverOpenTradesAsync
                Return Task.CompletedTask
            End Function
            Public Function LogStopAdjustmentAsync(liveTradeRecordId As Long, timestamp As DateTimeOffset,
                                                     oldStop As Decimal, newStop As Decimal,
                                                     triggerReason As String,
                                                     Optional notes As String = Nothing) As Task _
                Implements ITradeRecordService.LogStopAdjustmentAsync
                Return Task.CompletedTask
            End Function
            Public Function LogTickSnapshotAsync(liveTradeRecordId As Long, snapshot As TradeTickSnapshot) As Task _
                Implements ITradeRecordService.LogTickSnapshotAsync
                Return Task.CompletedTask
            End Function
            Public Function GetStopAdjustmentsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeStopAdjustment)) _
                Implements ITradeRecordService.GetStopAdjustmentsAsync
                Return Task.FromResult(Of IList(Of TradeStopAdjustment))(New List(Of TradeStopAdjustment))
            End Function
            Public Function CaptureClosingSnapshotsAsync(recordId As Long, accountId As Long) As Task _
                Implements ITradeRecordService.CaptureClosingSnapshotsAsync
                Return Task.CompletedTask
            End Function
            Public Function BackfillSnapshotsAsync(accountId As Long) As Task _
                Implements ITradeRecordService.BackfillSnapshotsAsync
                Return Task.CompletedTask
            End Function
            Public Function BackfillExitPricesAsync(accountId As Long) As Task(Of Integer) _
                Implements ITradeRecordService.BackfillExitPricesAsync
                Return Task.FromResult(0)
            End Function
            Public Function SaveSignalAsync(signal As TradeSignal) As Task(Of Long) _
                Implements ITradeRecordService.SaveSignalAsync
                Return Task.FromResult(0L)
            End Function
            Public Function OpenOutcomeAsync(signalId As Long, recordId As Long, model As TradeOutcome) As Task(Of Long) _
                Implements ITradeRecordService.OpenOutcomeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function ResolveOutcomeAsync(outcomeId As Long, exitTime As DateTimeOffset,
                                                  exitPrice As Decimal, pnl As Decimal,
                                                  isWinner As Boolean, exitReason As String) As Task _
                Implements ITradeRecordService.ResolveOutcomeAsync
                Return Task.CompletedTask
            End Function
            Public Function SaveSetupSnapshotAsync(tradeOutcomeId As Long, snapshot As TradeSetupSnapshot) As Task(Of Long) _
                Implements ITradeRecordService.SaveSetupSnapshotAsync
                Return Task.FromResult(0L)
            End Function
            Public Function SaveLifespanRecordAsync(tradeOutcomeId As Long, record As TradeLifespan) As Task _
                Implements ITradeRecordService.SaveLifespanRecordAsync
                Return Task.CompletedTask
            End Function
            Public Function AuditZeroEntryPriceRowsAsync(accountId As Long) As Task(Of EntryPriceAuditResult) _
                Implements ITradeRecordService.AuditZeroEntryPriceRowsAsync
                Return Task.FromResult(New EntryPriceAuditResult())
            End Function
        End Class

    End Class

End Namespace
