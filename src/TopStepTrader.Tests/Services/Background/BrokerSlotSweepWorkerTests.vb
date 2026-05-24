Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Background
Imports Xunit

Namespace TopStepTrader.Tests.Services.Background

    ''' <summary>
    ''' BUG-90 F6 regression coverage for the fourth release channel
    ''' (<see cref="BrokerSlotSweepWorker"/>).
    '''
    ''' Covers hypotheses:
    '''   • H1 — hub event was dropped (REST flat).
    '''   • H2 — REST returned a stale non-zero row to the per-tick path; the sweep
    '''           queries fresh and gets the real flat reading.
    '''   • H4 — broker call throws on every poll (the sweep must NOT mass-release
    '''           in that case; the per-tick MissCount path handles transient outages).
    '''
    ''' Each scenario plugs a different <see cref="StubOrderService"/> behaviour into the
    ''' worker, runs one sweep pass synchronously via <see cref="BrokerSlotSweepWorker.SweepOnceAsync"/>,
    ''' and asserts the recording sink saw (or did not see) a release with the correct
    ''' BUG-90 F5 trigger tag.
    ''' </summary>
    Public Class BrokerSlotSweepWorkerTests

        ' ── Test doubles ────────────────────────────────────────────────────

        Private Class RecordingSink
            Implements IOpenSlotReleaseSink
            Public Slots As New List(Of PositionSlot)
            Public Releases As New List(Of (Idx As Integer, Reason As String, Trigger As String))
            Public ReadOnly Property OccupiedSlots As IReadOnlyList(Of PositionSlot) _
                Implements IOpenSlotReleaseSink.OccupiedSlots
                Get
                    Return Slots
                End Get
            End Property
            Public Function ForceReleaseAsync(slotIndex As Integer, reason As String, trigger As String) As Task _
                Implements IOpenSlotReleaseSink.ForceReleaseAsync
                Releases.Add((slotIndex, reason, trigger))
                Return Task.CompletedTask
            End Function
        End Class

        Private Class StubSession
            Implements ITradingSessionContext
            Public Property Account As Account
            Public ReadOnly Property SelectedAccount As Account Implements ITradingSessionContext.SelectedAccount
                Get
                    Return Account
                End Get
            End Property
            Public ReadOnly Property ActiveBroker As BrokerType Implements ITradingSessionContext.ActiveBroker
                Get
                    Return BrokerType.TopStepX
                End Get
            End Property
            Public Sub SelectAccount(a As Account) Implements ITradingSessionContext.SelectAccount
                Account = a
            End Sub
            Public Event AccountChanged As EventHandler(Of Account) Implements ITradingSessionContext.AccountChanged
            Public ReadOnly Property AutoExecutionEnabled As Boolean Implements ITradingSessionContext.AutoExecutionEnabled
                Get
                    Return False
                End Get
            End Property
            Public Sub SetAutoExecution(enabled As Boolean) Implements ITradingSessionContext.SetAutoExecution
            End Sub
            Public Event AutoExecutionChanged As EventHandler Implements ITradingSessionContext.AutoExecutionChanged
        End Class

        Private Class StubOrderService
            Implements IOrderService
            Public Property SnapshotResult As LivePositionSnapshot
            Public Property ThrowOnSnapshot As Boolean = False
            Public CallCount As Integer = 0

            Public Function GetLivePositionSnapshotAsync(accountId As Long, contractId As String,
                                                          Optional positionId As Long? = Nothing,
                                                          Optional bypassCache As Boolean = False,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot) _
                Implements IOrderService.GetLivePositionSnapshotAsync
                CallCount += 1
                If ThrowOnSnapshot Then Throw New InvalidOperationException("simulated broker outage")
                Return Task.FromResult(SnapshotResult)
            End Function

            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

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
            Public Function TryGetOrderFillPriceAsync(externalOrderId As Long, accountId As Long,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetOrderFillPriceAsync
                Throw New NotImplementedException()
            End Function
            Public Function TryGetBracketStopPriceAsync(accountId As Long, contractId As String,
                                                         Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetBracketStopPriceAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetLiveWorkingOrdersAsync(accountId As Long, contractId As String,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order)) _
                Implements IOrderService.GetLiveWorkingOrdersAsync
                Throw New NotImplementedException()
            End Function
            Public Function FlattenContractAsync(accountId As Long, contractId As String,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.FlattenContractAsync
                Throw New NotImplementedException()
            End Function
            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?,
                                                   Optional enableTsl As Boolean = False,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.EditPositionSlTpAsync
                Throw New NotImplementedException()
            End Function
            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.PartialCloseContractAsync
                Throw New NotImplementedException()
            End Function
        End Class

        ' ── Helpers ─────────────────────────────────────────────────────────

        Private Shared Function NewWorker(orderSvc As IOrderService,
                                           session As ITradingSessionContext,
                                           registry As OpenSlotReleaseSinkRegistry) As BrokerSlotSweepWorker
            Return New BrokerSlotSweepWorker(registry, orderSvc, session,
                                              NullLogger(Of BrokerSlotSweepWorker).Instance)
        End Function

        Private Shared Function OccupiedSlot(idx As Integer, instrument As String) As PositionSlot
            Return New PositionSlot With {
                .SlotIndex = idx,
                .Instrument = instrument,
                .IsOpen = True,
                .Contracts = 2
            }
        End Function

        ' ── H1: hub-drop case ───────────────────────────────────────────────

        <Fact>
        Public Async Function H1_BrokerFlat_ReleasesViaSweep() As Task
            Dim sink As New RecordingSink
            sink.Slots.Add(OccupiedSlot(0, "M6E"))

            Dim registry As New OpenSlotReleaseSinkRegistry
            registry.Register(sink)
            Dim session As New StubSession With {.Account = New Account With {.Id = 42}}
            Dim orderSvc As New StubOrderService With {.SnapshotResult = Nothing}

            Dim worker = NewWorker(orderSvc, session, registry)
            Await worker.SweepOnceAsync()

            Dim release = Assert.Single(sink.Releases)
            Assert.Equal(0, release.Idx)
            Assert.Equal("Closed by Broker (sweep)", release.Reason)
            Assert.Equal("sweep", release.Trigger)
        End Function

        ' ── H2: degenerate snapshot (Units=0) ───────────────────────────────

        <Fact>
        Public Async Function H2_DegenerateSnapshot_ReleasesViaSweep() As Task
            Dim sink As New RecordingSink
            sink.Slots.Add(OccupiedSlot(1, "MES"))

            Dim registry As New OpenSlotReleaseSinkRegistry
            registry.Register(sink)
            Dim session As New StubSession With {.Account = New Account With {.Id = 42}}
            Dim orderSvc As New StubOrderService With {
                .SnapshotResult = New LivePositionSnapshot With {.Units = 0D, .Amount = 0D}
            }

            Dim worker = NewWorker(orderSvc, session, registry)
            Await worker.SweepOnceAsync()

            Dim release = Assert.Single(sink.Releases)
            Assert.Equal("Closed by Broker (sweep)", release.Reason)
            Assert.Equal("sweep", release.Trigger)
        End Function

        ' ── Confirmed-open snapshot must NOT release ────────────────────────

        <Fact>
        Public Async Function ConfirmedOpenSnapshot_DoesNotRelease() As Task
            Dim sink As New RecordingSink
            sink.Slots.Add(OccupiedSlot(0, "MNQ"))

            Dim registry As New OpenSlotReleaseSinkRegistry
            registry.Register(sink)
            Dim session As New StubSession With {.Account = New Account With {.Id = 42}}
            Dim orderSvc As New StubOrderService With {
                .SnapshotResult = New LivePositionSnapshot With {.Units = 3D, .Amount = 3D, .OpenRate = 18000D}
            }

            Dim worker = NewWorker(orderSvc, session, registry)
            Await worker.SweepOnceAsync()

            Assert.Empty(sink.Releases)
        End Function

        ' ── H4: broker throws → sweep skips, per-tick MissCount handles it ──

        <Fact>
        Public Async Function H4_BrokerThrows_DoesNotRelease() As Task
            ' If GetLivePositionSnapshotAsync throws every call (broker outage), the sweep
            ' must NOT mass-release every open slot. The per-tick MissCount path is the
            ' channel that handles transient outages — the sweep only acts on a definitive
            ' "flat" reading.
            Dim sink As New RecordingSink
            sink.Slots.Add(OccupiedSlot(0, "MES"))
            sink.Slots.Add(OccupiedSlot(1, "MNQ"))

            Dim registry As New OpenSlotReleaseSinkRegistry
            registry.Register(sink)
            Dim session As New StubSession With {.Account = New Account With {.Id = 42}}
            Dim orderSvc As New StubOrderService With {.ThrowOnSnapshot = True}

            Dim worker = NewWorker(orderSvc, session, registry)
            Await worker.SweepOnceAsync()

            Assert.Empty(sink.Releases)
            Assert.Equal(2, orderSvc.CallCount) ' each slot was attempted
        End Function

        ' ── No account selected → sweep is a no-op ──────────────────────────

        <Fact>
        Public Async Function NoAccount_SweepNoOp() As Task
            Dim sink As New RecordingSink
            sink.Slots.Add(OccupiedSlot(0, "MES"))
            Dim registry As New OpenSlotReleaseSinkRegistry
            registry.Register(sink)
            Dim session As New StubSession ' no account
            Dim orderSvc As New StubOrderService With {.SnapshotResult = Nothing}

            Dim worker = NewWorker(orderSvc, session, registry)
            Await worker.SweepOnceAsync()

            Assert.Empty(sink.Releases)
            Assert.Equal(0, orderSvc.CallCount)
        End Function

    End Class

End Namespace
