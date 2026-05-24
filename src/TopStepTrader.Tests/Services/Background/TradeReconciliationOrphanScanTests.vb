Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Background
Imports Xunit

Namespace TopStepTrader.Tests.Services.Background

    ''' <summary>
    ''' BUG-94 F5: orphan-scan coverage for <see cref="TradeReconciliationWorker"/>.
    '''
    ''' Each test plugs stub ITradeRecordService / IOrderService doubles into the worker,
    ''' calls the <c>ScanOrphansAsync</c> test seam, and asserts both the raised
    ''' <c>OrphanPositionDetected</c> events and the auto-SL side-effects on the order
    ''' stub.
    ''' </summary>
    Public Class TradeReconciliationOrphanScanTests

        Private Const AccountId As Long = 100L
        ' MES — used by the auto-SL math tests because its tick spec is the canonical
        ' worked example in BUG-94 ($1.25/tick, 0.25 size).
        Private Const MesContractId As String = "CON.F.US.MES.U26"

        ' ── Test doubles ────────────────────────────────────────────────────

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

        Private Class StubTradeRecordService
            Implements ITradeRecordService

            ''' <summary>Map of (accountId, contractId) → LiveTradeRecord. When unset the lookup returns Nothing → orphan.</summary>
            Public Map As New Dictionary(Of String, LiveTradeRecord)(StringComparer.OrdinalIgnoreCase)

            Public Function FindOpenByContractIdAsync(accId As Long, contractId As String) As Task(Of LiveTradeRecord) _
                Implements ITradeRecordService.FindOpenByContractIdAsync
                Dim hit As LiveTradeRecord = Nothing
                Map.TryGetValue(contractId, hit)
                Return Task.FromResult(hit)
            End Function

            ' ── all other interface members no-op for the orphan-scan path ──
            Public Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long) Implements ITradeRecordService.OpenTradeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal, pnL As Decimal, exitReason As String) As Task Implements ITradeRecordService.CloseTradeAsync
                Return Task.CompletedTask
            End Function
            Public Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task Implements ITradeRecordService.UpdateEntryPriceAsync
                Return Task.CompletedTask
            End Function
            Public Function ResolveTopStepXTradeIdAsync(recordId As Long, topStepXTradeId As Long) As Task Implements ITradeRecordService.ResolveTopStepXTradeIdAsync
                Return Task.CompletedTask
            End Function
            Public Function GetRecentTradesAsync(count As Integer, Optional filter As TradeFilter = Nothing) As Task(Of IList(Of LiveTradeRecord)) Implements ITradeRecordService.GetRecentTradesAsync
                Return Task.FromResult(Of IList(Of LiveTradeRecord))(New List(Of LiveTradeRecord)())
            End Function
            Public Function GetOpenTradesAsync() As Task(Of IList(Of LiveTradeRecord)) Implements ITradeRecordService.GetOpenTradesAsync
                Return Task.FromResult(Of IList(Of LiveTradeRecord))(New List(Of LiveTradeRecord)())
            End Function
            Public Function GetTradeByIdAsync(id As Long) As Task(Of LiveTradeRecord) Implements ITradeRecordService.GetTradeByIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecord) Implements ITradeRecordService.FindByEntryOrderIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function RecoverOpenTradesAsync(accId As Long) As Task Implements ITradeRecordService.RecoverOpenTradesAsync
                Return Task.CompletedTask
            End Function
            Public Function LogStopAdjustmentAsync(liveTradeRecordId As Long, timestamp As DateTimeOffset, oldStop As Decimal, newStop As Decimal, triggerReason As String, Optional notes As String = Nothing) As Task Implements ITradeRecordService.LogStopAdjustmentAsync
                Return Task.CompletedTask
            End Function
            Public Function LogTickSnapshotAsync(liveTradeRecordId As Long, snapshot As TradeTickSnapshot) As Task Implements ITradeRecordService.LogTickSnapshotAsync
                Return Task.CompletedTask
            End Function
            Public Function GetStopAdjustmentsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeStopAdjustment)) Implements ITradeRecordService.GetStopAdjustmentsAsync
                Return Task.FromResult(Of IList(Of TradeStopAdjustment))(New List(Of TradeStopAdjustment)())
            End Function
            Public Function CaptureClosingSnapshotsAsync(recordId As Long, accId As Long) As Task Implements ITradeRecordService.CaptureClosingSnapshotsAsync
                Return Task.CompletedTask
            End Function
            Public Function BackfillSnapshotsAsync(accId As Long) As Task Implements ITradeRecordService.BackfillSnapshotsAsync
                Return Task.CompletedTask
            End Function
            Public Function BackfillExitPricesAsync(accId As Long) As Task(Of Integer) Implements ITradeRecordService.BackfillExitPricesAsync
                Return Task.FromResult(0)
            End Function
            Public Function SaveSignalAsync(signal As TradeSignal) As Task(Of Long) Implements ITradeRecordService.SaveSignalAsync
                Return Task.FromResult(0L)
            End Function
            Public Function OpenOutcomeAsync(signalId As Long, recordId As Long, model As TradeOutcome) As Task(Of Long) Implements ITradeRecordService.OpenOutcomeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function ResolveOutcomeAsync(outcomeId As Long, exitTime As DateTimeOffset, exitPrice As Decimal, pnl As Decimal, isWinner As Boolean, exitReason As String) As Task Implements ITradeRecordService.ResolveOutcomeAsync
                Return Task.CompletedTask
            End Function
            Public Function SaveSetupSnapshotAsync(tradeOutcomeId As Long, snapshot As TradeSetupSnapshot) As Task(Of Long) Implements ITradeRecordService.SaveSetupSnapshotAsync
                Return Task.FromResult(0L)
            End Function
            Public Function SaveLifespanRecordAsync(tradeOutcomeId As Long, record As TradeLifespan) As Task Implements ITradeRecordService.SaveLifespanRecordAsync
                Return Task.CompletedTask
            End Function
        End Class

        Private Class StubOrderService
            Implements IOrderService

            Public Positions As New List(Of LivePositionSnapshot)()
            Public ExistingBracketPrice As Decimal? = Nothing
            Public EditSucceeds As Boolean = True
            Public Property EditCallCount As Integer = 0
            Public Property LastEditPositionId As Long
            Public Property LastEditSlPrice As Decimal?

            Public Function GetOpenPositionsAsync(accId As Long, Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot)) _
                Implements IOrderService.GetOpenPositionsAsync
                Return Task.FromResult(Of IEnumerable(Of LivePositionSnapshot))(Positions.ToList())
            End Function

            Public Function TryGetBracketStopPriceAsync(accId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetBracketStopPriceAsync
                Return Task.FromResult(ExistingBracketPrice)
            End Function

            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?,
                                                   Optional enableTsl As Boolean = False,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.EditPositionSlTpAsync
                EditCallCount += 1
                LastEditPositionId = positionId
                LastEditSlPrice = slRate
                Return Task.FromResult(EditSucceeds)
            End Function

            ' ── unused interface members ─────────────────────────────────────
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
            Public Function GetOpenOrdersAsync(accId As Long) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOpenOrdersAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetOrderHistoryAsync(accId As Long, fromUtc As DateTime, toUtc As DateTime) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
                Throw New NotImplementedException()
            End Function
            Public Function TryGetOrderFillPriceAsync(externalOrderId As Long, accId As Long, Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) Implements IOrderService.TryGetOrderFillPriceAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetLiveWorkingOrdersAsync(accId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetLiveWorkingOrdersAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetLivePositionSnapshotAsync(accId As Long, contractId As String,
                                                          Optional positionId As Long? = Nothing,
                                                          Optional bypassCache As Boolean = False,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot) Implements IOrderService.GetLivePositionSnapshotAsync
                Throw New NotImplementedException()
            End Function
            Public Function FlattenContractAsync(accId As Long, contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.FlattenContractAsync
                Throw New NotImplementedException()
            End Function
            Public Function PartialCloseContractAsync(accId As Long, contractId As String, size As Integer, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.PartialCloseContractAsync
                Throw New NotImplementedException()
            End Function
        End Class

        ' ── Helpers ─────────────────────────────────────────────────────────

        Private Shared Function NewWorker(tr As StubTradeRecordService,
                                          ord As StubOrderService,
                                          settings As SafetyNetSettings) As TradeReconciliationWorker
            Dim sess As New StubSession With {.Account = New Account With {.Id = AccountId}}
            Return New TradeReconciliationWorker(tr, ord, Options.Create(settings), sess,
                                                  NullLogger(Of TradeReconciliationWorker).Instance)
        End Function

        Private Shared Function MesPosition(positionId As Long, netPos As Integer, entry As Decimal, openedAt As DateTimeOffset) As LivePositionSnapshot
            Return New LivePositionSnapshot With {
                .PositionId = positionId,
                .ContractId = MesContractId,
                .UnrealizedPnlUsd = -12.5D,
                .OpenedAtUtc = openedAt,
                .IsBuy = netPos > 0,
                .OpenRate = entry,
                .Amount = CDec(Math.Abs(netPos)),
                .Units = CDec(Math.Abs(netPos)),
                .PositionCount = 1,
                .NetPos = netPos
            }
        End Function

        Private Shared Function Subscribe(worker As TradeReconciliationWorker) As List(Of OrphanPositionDetectedEventArgs)
            Dim list As New List(Of OrphanPositionDetectedEventArgs)()
            AddHandler worker.OrphanPositionDetected, Sub(s, a) list.Add(a)
            Return list
        End Function

        ' ── Tests ───────────────────────────────────────────────────────────

        <Fact>
        Public Async Function OrphanDetected_When_BrokerPosition_HasNo_OpenLiveTradeRecord() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService
            ord.Positions.Add(MesPosition(positionId:=42, netPos:=1, entry:=4000D, openedAt:=now.AddSeconds(-60)))

            Dim worker = NewWorker(tr, ord, New SafetyNetSettings With {.OrphanGraceSeconds = 15})
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            Dim evt = Assert.Single(events)
            Assert.Equal(42L, evt.PositionId)
            Assert.Equal(MesContractId, evt.ContractId)
            Assert.Equal("MES", evt.Symbol)
            Assert.Equal("Long", evt.Side)
            Assert.Equal(1, evt.Size)
            Assert.Equal(4000D, evt.NetPrice)
        End Function

        <Fact>
        Public Async Function NoOrphan_When_LiveTradeRecord_Exists() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            tr.Map(MesContractId) = New LiveTradeRecord With {.Id = 7, .ContractId = MesContractId, .IsOpen = True}
            Dim ord As New StubOrderService
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-60)))

            Dim worker = NewWorker(tr, ord, New SafetyNetSettings With {.OrphanGraceSeconds = 15})
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            Assert.Empty(events)
            Assert.Equal(0, ord.EditCallCount)
        End Function

        <Fact>
        Public Async Function Grace_Period_Suppresses_FreshFill() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-5)))

            Dim worker = NewWorker(tr, ord, New SafetyNetSettings With {.OrphanGraceSeconds = 15})
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            Assert.Empty(events)
        End Function

        <Fact>
        Public Async Function AutoSL_PlacedWhenEnabled_AndNoExistingBracket() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService With {.ExistingBracketPrice = Nothing, .EditSucceeds = True}
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-60)))

            Dim settings As New SafetyNetSettings With {
                .AutoStopLossEnabled = True,
                .OrphanGraceSeconds = 15,
                .PerSymbol = New Dictionary(Of String, Decimal)(StringComparer.OrdinalIgnoreCase) From {{"MES", 50D}}
            }
            Dim worker = NewWorker(tr, ord, settings)
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            ' $50 / $1.25 = 40 ticks; 40 × 0.25 = 10 points; 4000 - 10 = 3990 (floor-snapped → exact)
            Assert.Equal(1, ord.EditCallCount)
            Assert.Equal(42L, ord.LastEditPositionId)
            Assert.True(ord.LastEditSlPrice.HasValue)
            Assert.Equal(3990D, ord.LastEditSlPrice.Value)
            Dim evt = Assert.Single(events)
            Assert.True(evt.AutoSlApplied)
            Assert.Equal(3990D, evt.AutoSlPriceApplied)
        End Function

        <Fact>
        Public Async Function AutoSL_Skipped_When_BracketAlreadyExists() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService With {.ExistingBracketPrice = 3995D}
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-60)))

            Dim settings As New SafetyNetSettings With {
                .AutoStopLossEnabled = True,
                .OrphanGraceSeconds = 15,
                .PerSymbol = New Dictionary(Of String, Decimal)(StringComparer.OrdinalIgnoreCase) From {{"MES", 50D}}
            }
            Dim worker = NewWorker(tr, ord, settings)
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            Assert.Equal(0, ord.EditCallCount)
            Dim evt = Assert.Single(events)
            Assert.False(evt.AutoSlApplied)
            Assert.Equal("ExistingBracketDetected", evt.AutoSlSkippedReason)
        End Function

        <Fact>
        Public Async Function AutoSL_Skipped_When_Setting_Disabled() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-60)))

            Dim settings As New SafetyNetSettings With {
                .AutoStopLossEnabled = False,
                .OrphanGraceSeconds = 15
            }
            Dim worker = NewWorker(tr, ord, settings)
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            Assert.Equal(0, ord.EditCallCount)
            Dim evt = Assert.Single(events)
            Assert.False(evt.AutoSlApplied)
            Assert.Equal("Disabled", evt.AutoSlSkippedReason)
        End Function

        <Fact>
        Public Async Function AutoSL_UsesDefault_When_PerSymbol_Missing() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-60)))

            Dim settings As New SafetyNetSettings With {
                .AutoStopLossEnabled = True,
                .OrphanGraceSeconds = 15,
                .DefaultFallbackStopDollars = 100D
            }
            Dim worker = NewWorker(tr, ord, settings)
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            ' $100 / $1.25 = 80 ticks; 80 × 0.25 = 20 points; 4000 - 20 = 3980
            Assert.Equal(1, ord.EditCallCount)
            Assert.True(ord.LastEditSlPrice.HasValue)
            Assert.Equal(3980D, ord.LastEditSlPrice.Value)
        End Function

        <Fact>
        Public Async Function Alarm_Deduplicated_Within_AlarmedTtl() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim tr As New StubTradeRecordService
            Dim ord As New StubOrderService
            ord.Positions.Add(MesPosition(42, 1, 4000D, now.AddSeconds(-60)))

            Dim worker = NewWorker(tr, ord, New SafetyNetSettings With {.OrphanGraceSeconds = 15})
            Dim events = Subscribe(worker)

            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)
            Await worker.ScanOrphansAsync(AccountId, CancellationToken.None)

            Assert.Single(events)
        End Function

    End Class

End Namespace
