Imports System.Collections.Generic
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trading

    ''' <summary>
    ''' BUG-100 F4: regression tests for the broker-fill ⇒ ExitPrice/PnL reconciliation
    ''' in <see cref="ExitExecutionService.CloseAsync"/>. Drives every fill-source path
    ''' (hub / rest-poll / engine-fallback) plus a Short-side trade to catch sign-flip
    ''' bugs in the PnL recompute.
    ''' </summary>
    Public Class ExitExecutionServiceBrokerFillReconciliationTests

        Private Const InstrumentNq As String = "NQ"
        Private Const InstrumentMnqEntry As Decimal = 29606.0D
        Private Const InstrumentMnqShortEntry As Decimal = 29600.0D
        Private Const Contracts As Integer = 4

        Private Shared Function MakeSlot(side As String,
                                          entryPrice As Decimal,
                                          contracts As Integer,
                                          Optional unrealizedPnl As Decimal = 0D) As PositionSlot
            Return New PositionSlot With {
                .Instrument = InstrumentNq,
                .Side = side,
                .EntryPrice = entryPrice,
                .Contracts = contracts,
                .UnrealizedPnl = unrealizedPnl,
                .AccountId = 12345L,
                .TradeRecordId = 110L,
                .TradeOutcomeId = 220L,
                .SlotIndex = 0
            }
        End Function

        Private Shared Function MakeService(orderSvc As IOrderService,
                                             recordSvc As ITradeRecordService) As ExitExecutionService
            Return New ExitExecutionService(orderSvc, recordSvc, Nothing,
                                            NullLogger(Of ExitExecutionService).Instance)
        End Function

        ' ── F4-a: hub-fill happy path ──

        <Fact>
        Public Async Function HubFill_LongLoss_PersistsBrokerExitPriceAndRecomputesPnL() As Task
            Dim fillTime = New DateTimeOffset(2026, 5, 22, 14, 0, 48, TimeSpan.Zero)
            Dim orderSvc As New StubOrderService() With {
                .FillToReturn = New BrokerCloseFill With {
                    .FillPrice = 29591.5D,
                    .FillTimeUtc = fillTime,
                    .FillSize = Contracts,
                    .OrderId = 3019266675L,
                    .Source = "hub"
                }
            }
            Dim recordSvc As New CapturingTradeRecordService()
            Dim svc = MakeService(orderSvc, recordSvc)

            Dim slot = MakeSlot("Buy", InstrumentMnqEntry, Contracts)
            Dim result = Await svc.CloseAsync(slot, "STOP_HIT", "test-trigger", 5, CancellationToken.None)

            Assert.True(result.Released)
            Assert.Equal("hub", result.CloseFillSource)
            Assert.True(result.ExitPrice.HasValue)
            Assert.Equal(29591.5D, result.ExitPrice.Value)
            ' Long down 14.5 pts × $0.5/tick × 0.25 tick/pt × 4 contracts
            ' = closeTicks(-58) × $0.5 × 4 = -$116
            Assert.Equal(-116D, result.RealizedPnlUsd)
            Assert.Single(recordSvc.CloseTradeCalls)
            Dim call0 = recordSvc.CloseTradeCalls(0)
            Assert.Equal(29591.5D, call0.ExitPrice)
            Assert.Equal(-116D, call0.Pnl)
            Assert.Equal("hub", call0.CloseFillSource)
            Assert.Equal(fillTime, call0.ExitTime)
        End Function

        ' ── F4-b: hub miss, rest-poll succeeds ──

        <Fact>
        Public Async Function RestPollFill_PreservesSource_OnExitResult() As Task
            Dim orderSvc As New StubOrderService() With {
                .FillToReturn = New BrokerCloseFill With {
                    .FillPrice = 29605.0D,
                    .FillTimeUtc = New DateTimeOffset(2026, 5, 22, 14, 0, 49, TimeSpan.Zero),
                    .FillSize = Contracts,
                    .OrderId = 3019266676L,
                    .Source = "rest-poll"
                }
            }
            Dim recordSvc As New CapturingTradeRecordService()
            Dim svc = MakeService(orderSvc, recordSvc)

            Dim slot = MakeSlot("Buy", InstrumentMnqEntry, Contracts)
            Dim result = Await svc.CloseAsync(slot, "FLIP", "test-trigger", 5, CancellationToken.None)

            Assert.Equal("rest-poll", result.CloseFillSource)
            Assert.Equal("rest-poll", recordSvc.CloseTradeCalls(0).CloseFillSource)
        End Function

        ' ── F4-c: hub + REST both fail → engine-fallback + Warning ──

        <Fact>
        Public Async Function NoBrokerFill_FallsBackToEngineDerived_AndLogsWarning() As Task
            Dim orderSvc As New StubOrderService() With {.FillToReturn = Nothing}
            Dim recordSvc As New CapturingTradeRecordService()
            Dim warningLogger As New CapturingLogger(Of ExitExecutionService)()
            Dim svc As New ExitExecutionService(orderSvc, recordSvc, Nothing, warningLogger)

            ' Engine fallback path: ExitPrice computed from slot.UnrealizedPnl × tick metadata.
            ' For Long entry=29606, contracts=4, unrealizedPnl=-100:
            '   ticks = -100 / (0.5 × 4) = -50
            '   exitPx = 29606 + 1 × -50 × 0.25 = 29593.5
            Dim slot = MakeSlot("Buy", InstrumentMnqEntry, Contracts, unrealizedPnl:=-100D)
            Dim result = Await svc.CloseAsync(slot, "PnLClose", "test-trigger", 5, CancellationToken.None)

            Assert.Equal("engine-fallback", result.CloseFillSource)
            Assert.True(result.ExitPrice.HasValue)
            Assert.Equal(29593.5D, result.ExitPrice.Value)
            Assert.Equal(-100D, result.RealizedPnlUsd)   ' engine UnrealizedPnl preserved
            Assert.Equal("engine-fallback", recordSvc.CloseTradeCalls(0).CloseFillSource)
            Assert.Contains(warningLogger.Warnings,
                Function(m) m.IndexOf("no broker close-fill", StringComparison.OrdinalIgnoreCase) >= 0)
        End Function

        ' ── F4-d: Short-side reconciliation — sign flip on the PnL recompute ──

        <Fact>
        Public Async Function HubFill_ShortWin_RecomputesPositivePnLWithCorrectSignFlip() As Task
            Dim orderSvc As New StubOrderService() With {
                .FillToReturn = New BrokerCloseFill With {
                    .FillPrice = 29595.0D,
                    .FillTimeUtc = New DateTimeOffset(2026, 5, 22, 14, 0, 50, TimeSpan.Zero),
                    .FillSize = Contracts,
                    .OrderId = 3019266677L,
                    .Source = "hub"
                }
            }
            Dim recordSvc As New CapturingTradeRecordService()
            Dim svc = MakeService(orderSvc, recordSvc)

            Dim slot = MakeSlot("Sell", InstrumentMnqShortEntry, Contracts)
            Dim result = Await svc.CloseAsync(slot, "EXIT_ENGINE", "test-trigger", 5, CancellationToken.None)

            ' Short profits when price falls: diff = entry(29600) - fill(29595) = +5
            ' closeTicks = 5 / 0.25 = 20; pnl = 20 × 0.5 × 4 = +$40
            Assert.Equal("hub", result.CloseFillSource)
            Assert.True(result.ExitPrice.HasValue)
            Assert.Equal(29595.0D, result.ExitPrice.Value)
            Assert.Equal(40D, result.RealizedPnlUsd)
        End Function

        ' ── Stubs ────────────────────────────────────────────────────────────

        Private Class StubOrderService
            Implements IOrderService
            Public Property FillToReturn As BrokerCloseFill

            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

            Public Function FlattenContractWithFillAsync(accountId As Long, contractId As String,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of (Success As Boolean, Fill As BrokerCloseFill)) _
                Implements IOrderService.FlattenContractWithFillAsync
                Return Task.FromResult((True, FillToReturn))
            End Function

            Public Function FlattenContractAsync(accountId As Long, contractId As String,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.FlattenContractAsync
                Return Task.FromResult(True)
            End Function

            ' ── Unused IOrderService members ──
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
            Public Function GetOrderHistoryAsync(accountId As Long, fromUtc As DateTime, toUtc As DateTime) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
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
            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?, Optional enableTsl As Boolean = False, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.EditPositionSlTpAsync
                Throw New NotImplementedException()
            End Function
            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.PartialCloseContractAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetOpenPositionsAsync(accountId As Long, Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot)) Implements IOrderService.GetOpenPositionsAsync
                Throw New NotImplementedException()
            End Function

#Disable Warning BC42024
            Private Sub SuppressEventWarnings()
                RaiseEvent OrderFilled(Me, Nothing)
                RaiseEvent OrderRejected(Me, Nothing)
                RaiseEvent PositionUpdated(Me, Nothing)
            End Sub
#Enable Warning BC42024
        End Class

        Private Class CapturingTradeRecordService
            Implements ITradeRecordService

            Public ReadOnly CloseTradeCalls As New List(Of (Id As Long, ExitTime As DateTimeOffset, ExitPrice As Decimal, Pnl As Decimal, ExitReason As String, CloseFillSource As String))()
            Public ReadOnly ResolveOutcomeCalls As New List(Of (OutcomeId As Long, ExitPrice As Decimal, Pnl As Decimal))()

            Public Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                                              pnL As Decimal, exitReason As String,
                                              Optional closeFillSource As String = Nothing) As Task _
                Implements ITradeRecordService.CloseTradeAsync
                CloseTradeCalls.Add((id, exitTime, exitPrice, pnL, exitReason, closeFillSource))
                Return Task.CompletedTask
            End Function

            Public Function ResolveOutcomeAsync(outcomeId As Long, exitTime As DateTimeOffset, exitPrice As Decimal, pnl As Decimal, isWinner As Boolean, exitReason As String) As Task _
                Implements ITradeRecordService.ResolveOutcomeAsync
                ResolveOutcomeCalls.Add((outcomeId, exitPrice, pnl))
                Return Task.CompletedTask
            End Function

            Public Function SaveLifespanRecordAsync(tradeOutcomeId As Long, record As TradeLifespan) As Task _
                Implements ITradeRecordService.SaveLifespanRecordAsync
                Return Task.CompletedTask
            End Function

            Public Function AuditZeroEntryPriceRowsAsync(accountId As Long) As Task(Of EntryPriceAuditResult) _
                Implements ITradeRecordService.AuditZeroEntryPriceRowsAsync
                Return Task.FromResult(New EntryPriceAuditResult())
            End Function

            ' ── Unused members ──
            Public Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long) Implements ITradeRecordService.OpenTradeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task Implements ITradeRecordService.UpdateEntryPriceAsync
                Return Task.CompletedTask
            End Function
            Public Function ResolveTopStepXTradeIdAsync(recordId As Long, topStepXTradeId As Long) As Task Implements ITradeRecordService.ResolveTopStepXTradeIdAsync
                Return Task.CompletedTask
            End Function
            Public Function GetRecentTradesAsync(count As Integer, Optional filter As TradeFilter = Nothing) As Task(Of IList(Of LiveTradeRecord)) Implements ITradeRecordService.GetRecentTradesAsync
                Return Task.FromResult(Of IList(Of LiveTradeRecord))(New List(Of LiveTradeRecord))
            End Function
            Public Function GetOpenTradesAsync() As Task(Of IList(Of LiveTradeRecord)) Implements ITradeRecordService.GetOpenTradesAsync
                Return Task.FromResult(Of IList(Of LiveTradeRecord))(New List(Of LiveTradeRecord))
            End Function
            Public Function GetTradeByIdAsync(id As Long) As Task(Of LiveTradeRecord) Implements ITradeRecordService.GetTradeByIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecord) Implements ITradeRecordService.FindByEntryOrderIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function FindOpenByContractIdAsync(accountId As Long, contractId As String) As Task(Of LiveTradeRecord) Implements ITradeRecordService.FindOpenByContractIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function RecoverOpenTradesAsync(accountId As Long) As Task Implements ITradeRecordService.RecoverOpenTradesAsync
                Return Task.CompletedTask
            End Function
            Public Function LogStopAdjustmentAsync(liveTradeRecordId As Long, timestamp As DateTimeOffset, oldStop As Decimal, newStop As Decimal, triggerReason As String, Optional notes As String = Nothing) As Task Implements ITradeRecordService.LogStopAdjustmentAsync
                Return Task.CompletedTask
            End Function
            Public Function LogTickSnapshotAsync(liveTradeRecordId As Long, snapshot As TradeTickSnapshot) As Task Implements ITradeRecordService.LogTickSnapshotAsync
                Return Task.CompletedTask
            End Function
            Public Function GetStopAdjustmentsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeStopAdjustment)) Implements ITradeRecordService.GetStopAdjustmentsAsync
                Return Task.FromResult(Of IList(Of TradeStopAdjustment))(New List(Of TradeStopAdjustment))
            End Function
            Public Function CaptureClosingSnapshotsAsync(recordId As Long, accountId As Long) As Task Implements ITradeRecordService.CaptureClosingSnapshotsAsync
                Return Task.CompletedTask
            End Function
            Public Function BackfillSnapshotsAsync(accountId As Long) As Task Implements ITradeRecordService.BackfillSnapshotsAsync
                Return Task.CompletedTask
            End Function
            Public Function BackfillExitPricesAsync(accountId As Long) As Task(Of Integer) Implements ITradeRecordService.BackfillExitPricesAsync
                Return Task.FromResult(0)
            End Function
            Public Function SaveSignalAsync(signal As TradeSignal) As Task(Of Long) Implements ITradeRecordService.SaveSignalAsync
                Return Task.FromResult(0L)
            End Function
            Public Function OpenOutcomeAsync(signalId As Long, recordId As Long, model As TradeOutcome) As Task(Of Long) Implements ITradeRecordService.OpenOutcomeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function SaveSetupSnapshotAsync(tradeOutcomeId As Long, snapshot As TradeSetupSnapshot) As Task(Of Long) Implements ITradeRecordService.SaveSetupSnapshotAsync
                Return Task.FromResult(0L)
            End Function
        End Class

        Private Class CapturingLogger(Of T)
            Implements ILogger(Of T)

            Public ReadOnly Warnings As New List(Of String)()

            Public Function BeginScope(Of TState)(state As TState) As IDisposable Implements ILogger.BeginScope
                Return Nothing
            End Function

            Public Function IsEnabled(logLevel As LogLevel) As Boolean Implements ILogger.IsEnabled
                Return True
            End Function

            Public Sub Log(Of TState)(logLevel As LogLevel, eventId As EventId, state As TState, exception As Exception,
                                       formatter As Func(Of TState, Exception, String)) Implements ILogger.Log
                If logLevel = LogLevel.Warning AndAlso formatter IsNot Nothing Then
                    Warnings.Add(formatter(state, exception))
                End If
            End Sub
        End Class

    End Class

End Namespace
