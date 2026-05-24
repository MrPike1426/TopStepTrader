Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trading

    ''' <summary>
    ''' ARCH-20 acceptance: drives <see cref="PositionManagementService.UpdateAsync"/>
    ''' end-to-end with hand-rolled fakes for the broker / bar / trade-record services,
    ''' the real <see cref="ExitSignalEngine"/>, and synthetic bar fixtures.
    '''
    ''' Coverage matrix:
    '''   (a) snapshot-skip alternation — broker REST is called on alternating ticks
    '''       once the slot is in steady state.
    '''   (b) phased-stop ratchet — <c>StopAdjusted = True</c>, broker SL modify fires,
    '''       and the trade record service records a stop-adjustment row (fire-and-forget).
    '''   (c) ExitGate two-bar gate — two consecutive ticks with a qualifying score
    '''       return <c>Outcome = ExitRequested</c> with <c>trigger = "exit-engine"</c>.
    '''   (d) E1 SuperTrend flip — bars that flip direction return
    '''       <c>Outcome = ExitRequested</c> with reason starting "ExitEngine: SuperTrend flip".
    ''' </summary>
    Public Class PositionManagementServiceTests

        Private Const Instrument As String = "MES"

        ' ── Stubs ───────────────────────────────────────────────────────────

        Private Class StubOrderService
            Implements IOrderService
            Public Property SnapshotResult As LivePositionSnapshot
            Public Property ThrowOnSnapshot As Boolean = False
            Public Property BracketStop As Decimal? = 99.5D
            Public Property EditAcceptsAlways As Boolean = True
            Public SnapshotCallCount As Integer = 0
            Public EditCalls As New List(Of (PositionId As Long, Sl As Decimal?, Tp As Decimal?))

            Public Function GetLivePositionSnapshotAsync(accountId As Long, contractId As String,
                                                          Optional positionId As Long? = Nothing,
                                                          Optional bypassCache As Boolean = False,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot) _
                Implements IOrderService.GetLivePositionSnapshotAsync
                SnapshotCallCount += 1
                If ThrowOnSnapshot Then Throw New InvalidOperationException("simulated outage")
                Return Task.FromResult(SnapshotResult)
            End Function

            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

            Public Function PlaceOrderAsync(order As Order) As Task(Of Order) Implements IOrderService.PlaceOrderAsync
                Return Task.FromResult(Of Order)(Nothing)
            End Function
            Public Function CancelOrderAsync(orderId As Long) As Task(Of Boolean) Implements IOrderService.CancelOrderAsync
                Return Task.FromResult(False)
            End Function
            Public Function CancelAllOpenOrdersAsync() As Task Implements IOrderService.CancelAllOpenOrdersAsync
                Return Task.CompletedTask
            End Function
            Public Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOpenOrdersAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function
            Public Function GetOrderHistoryAsync(accountId As Long, fromUtc As DateTime, toUtc As DateTime) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function
            Public Function TryGetOrderFillPriceAsync(externalOrderId As Long, accountId As Long,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetOrderFillPriceAsync
                Return Task.FromResult(Of Decimal?)(Nothing)
            End Function
            Public Function TryGetBracketStopPriceAsync(accountId As Long, contractId As String,
                                                         Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetBracketStopPriceAsync
                Return Task.FromResult(BracketStop)
            End Function
            Public Function GetLiveWorkingOrdersAsync(accountId As Long, contractId As String,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order)) _
                Implements IOrderService.GetLiveWorkingOrdersAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function
            Public Function FlattenContractAsync(accountId As Long, contractId As String,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.FlattenContractAsync
                Return Task.FromResult(True)
            End Function
            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?,
                                                   Optional enableTsl As Boolean = False,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.EditPositionSlTpAsync
                EditCalls.Add((positionId, slRate, tpRate))
                Return Task.FromResult(EditAcceptsAlways)
            End Function
            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.PartialCloseContractAsync
                Return Task.FromResult(True)
            End Function
        End Class

        Private Class StubBarService
            Implements IBarIngestionService
            Public Function IngestAsync(contractId As String, timeframe As BarTimeframe,
                                          Optional barsToFetch As Integer = 500,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of Integer) _
                Implements IBarIngestionService.IngestAsync
                Return Task.FromResult(0)
            End Function
            Public Function GetBarsForMLAsync(contractId As String, timeframe As BarTimeframe,
                                                Optional maxBars As Integer = 200,
                                                Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetBarsForMLAsync
                Return Task.FromResult(Of IList(Of MarketBar))(New List(Of MarketBar))
            End Function
            Public Function GetLatestPriceAsync(contractId As String,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of Decimal) _
                Implements IBarIngestionService.GetLatestPriceAsync
                Return Task.FromResult(0D)
            End Function
            Public Function GetLiveBarsAsync(contractId As String, timeframe As BarTimeframe, barCount As Integer,
                                               Optional cancel As CancellationToken = Nothing,
                                               Optional live As Boolean = False) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetLiveBarsAsync
                Return Task.FromResult(Of IList(Of MarketBar))(New List(Of MarketBar))
            End Function
        End Class

        Private Class StubTradeRecordService
            Implements ITradeRecordService
            Public StopAdjustments As New ConcurrentBag(Of (RecordId As Long, OldStop As Decimal, NewStop As Decimal, Phase As String))
            Public Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long) Implements ITradeRecordService.OpenTradeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                                              pnL As Decimal, exitReason As String) As Task _
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
                StopAdjustments.Add((liveTradeRecordId, oldStop, newStop, triggerReason))
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
        End Class

        Private Class StubContractResolver
            Implements IContractResolutionService
            Public Function InitialiseAsync(Optional cancel As CancellationToken = Nothing) As Task _
                Implements IContractResolutionService.InitialiseAsync
                Return Task.CompletedTask
            End Function
            Public Function GetContractId(rootSymbol As String) As String _
                Implements IContractResolutionService.GetContractId
                Return rootSymbol
            End Function
            Public Function IsResolved(rootSymbol As String) As Boolean _
                Implements IContractResolutionService.IsResolved
                Return True
            End Function
            Public ReadOnly Property FailedSymbols As IReadOnlyList(Of String) _
                Implements IContractResolutionService.FailedSymbols
                Get
                    Return Array.Empty(Of String)()
                End Get
            End Property
        End Class

        ' ── Helpers ─────────────────────────────────────────────────────────

        Private Shared Function MakeService(orderSvc As IOrderService,
                                              barSvc As IBarIngestionService,
                                              tradeRec As ITradeRecordService,
                                              resolver As IContractResolutionService) As PositionManagementService
            Dim engine As New ExitSignalEngine(NullLogger(Of ExitSignalEngine).Instance)
            Return New PositionManagementService(orderSvc, barSvc, resolver, tradeRec, engine,
                                                  NullLogger(Of PositionManagementService).Instance)
        End Function

        Private Shared Function MakeOpenSlot() As PositionSlot
            Return New PositionSlot With {
                .SlotIndex = 0,
                .Instrument = Instrument,
                .Side = "Buy",
                .IsOpen = True,
                .Contracts = 1,
                .AccountId = 42,
                .EntryPrice = 100D,
                .StopPrice = 99.5D,
                .PositionId = 555L,
                .EntryTime = DateTime.UtcNow.AddMinutes(-10),
                .EntryBarTime = DateTimeOffset.UtcNow.AddMinutes(-10),
                .Health = SlotHealth.Healthy,
                .LastSnapshotOkUtc = DateTime.UtcNow.AddMinutes(-1),
                .InitialRisk = 0.5D,
                .InitialRiskDollars = 25D
            }
        End Function

        Private Shared Function MakeConfirmedOpenSnapshot() As LivePositionSnapshot
            Return New LivePositionSnapshot With {
                .PositionId = 555L,
                .OpenRate = 100D,
                .Units = 1D,
                .Amount = 1D,
                .IsBuy = True,
                .UnrealizedPnlUsd = 0D,
                .PositionCount = 1
            }
        End Function

        ''' <summary>Generates 30 bars at ~100 with tiny zigzag noise so ATR is non-zero
        ''' and SuperTrend.Line is a valid Single. Closes cluster well above 99 so no
        ''' SuperTrend flip occurs for a long slot.</summary>
        Private Shared Function FlatBars(startTs As DateTimeOffset,
                                          Optional count As Integer = 30,
                                          Optional basePrice As Decimal = 100D) As IList(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To count - 1
                Dim wiggle = If((i Mod 2) = 0, 0.05D, -0.05D)
                Dim close = basePrice + wiggle
                bars.Add(New MarketBar With {
                    .Timestamp = startTs.AddMinutes(15 * i),
                    .Open = basePrice,
                    .High = basePrice + 0.10D,
                    .Low = basePrice - 0.10D,
                    .Close = close,
                    .Volume = 1000
                })
            Next
            Return bars
        End Function

        ''' <summary>Bars that climb monotonically — a stable uptrend that gives
        ''' SuperTrend a clear up direction.</summary>
        Private Shared Function UptrendBars(startTs As DateTimeOffset,
                                              Optional count As Integer = 30,
                                              Optional basePrice As Decimal = 100D,
                                              Optional priceStep As Decimal = 0.5D) As IList(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To count - 1
                Dim close = basePrice + priceStep * i
                bars.Add(New MarketBar With {
                    .Timestamp = startTs.AddMinutes(15 * i),
                    .Open = close - 0.10D,
                    .High = close + 0.20D,
                    .Low = close - 0.20D,
                    .Close = close,
                    .Volume = 1000
                })
            Next
            Return bars
        End Function

        ''' <summary>Bars that climb monotonically, then drop sharply on the final bar —
        ''' designed to flip SuperTrend direction.</summary>
        Private Shared Function UptrendThenCrashBars(startTs As DateTimeOffset) As IList(Of MarketBar)
            Dim bars = New List(Of MarketBar)(UptrendBars(startTs, count:=30, basePrice:=100D, priceStep:=0.5D))
            Dim lastTs = bars(bars.Count - 1).Timestamp.AddMinutes(15)
            bars.Add(New MarketBar With {
                .Timestamp = lastTs,
                .Open = bars(bars.Count - 1).Close,
                .High = bars(bars.Count - 1).Close,
                .Low = 50D,
                .Close = 50D,
                .Volume = 5000
            })
            Return bars
        End Function

        Private Shared Function MakeTickContext(bars As IList(Of MarketBar)) As PositionManagementTickContext
            Return New PositionManagementTickContext With {
                .Bars = bars,
                .StrategyTimeframe = BarTimeframe.FifteenMinute,
                .AsOfUtc = DateTime.UtcNow,
                .StMultiplier = 3.0R,
                .ExitScoreThreshold = 7,
                .EarlyModeMaxAgeMinutes = 30,
                .IsPrimaryForBracketEdit = True,
                .IsDebugCaptureEnabled = False
            }
        End Function

        ' ── (a) Snapshot-skip alternation ───────────────────────────────────

        <Fact>
        Public Async Function A_SnapshotSkipAlternation_RestCalledOnAlternateTicks() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeOpenSlot()
            Dim ctx = MakeTickContext(FlatBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29)))

            For i = 0 To 3
                Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)
                Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)
            Next

            ' Tick sequence: 1=fetch (add), 2=skip (remove), 3=fetch (add), 4=skip (remove).
            Assert.Equal(2, orderSvc.SnapshotCallCount)
        End Function

        ' ── (b) Phased-stop ratchet → broker SL modify + stop-adjustment row ──

        <Fact>
        Public Async Function B_PhasedStopRatchet_StopAdjustedAndPersisted() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeOpenSlot()
            slot.TradeRecordId = 7L
            slot.LivePrice = 0D  ' force currentClose to last bar close
            ' Start the slot with an initial-phase stop deliberately below where the rising
            ' ST line will be on the latest bar. The ratchet should advance the stop up.
            slot.StopPhase = StopPhase.Initial

            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29))
            Dim ctx = MakeTickContext(bars)

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)
            Assert.True(r.StopAdjusted, "Expected the rising SuperTrend line to ratchet the stop on a steady uptrend.")
            ' Broker SL modify should fire on the primary slot.
            Assert.NotEmpty(orderSvc.EditCalls)
            ' The fire-and-forget stop-adjustment persist may still be in flight; wait briefly.
            Await WaitForCondition(Function() Not tradeRec.StopAdjustments.IsEmpty, TimeSpan.FromSeconds(2))
            Assert.NotEmpty(tradeRec.StopAdjustments)
        End Function

        ' ── (c) ExitGate two-bar gate fires ExitRequested ────────────────────

        <Fact>
        Public Async Function C_ExitGateTwoBarGate_ReturnsExitRequested() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeOpenSlot()
            ' Disable early-mode so the engine + gate fully fire.
            slot.IsEarlyModeEntry = False

            Dim baseTs = DateTimeOffset.UtcNow.AddMinutes(-15 * 29)
            Dim barsTick1 = FlatBars(baseTs, count:=30)

            ' Threshold = 0 so every score (incl. 0) qualifies — exercising the two-bar gate
            ' transition rather than the underlying signal computation (which is covered by
            ' ExitSignalEngineTests + ExitGateTests).
            Dim ctx1 = MakeTickContext(barsTick1)
            ctx1.ExitScoreThreshold = 0
            Dim r1 = Await svc.UpdateAsync(slot, ctx1, CancellationToken.None)
            Assert.Equal(PositionManagementOutcome.Continue, r1.Outcome)
            Assert.Equal(1, slot.ConsecutiveExitBars)

            ' Advance the latest bar timestamp by one strategy-TF so the gate's per-bar
            ' throttle releases.
            Dim barsTick2 = FlatBars(baseTs.AddMinutes(15), count:=30)
            Dim ctx2 = MakeTickContext(barsTick2)
            ctx2.ExitScoreThreshold = 0
            Dim r2 = Await svc.UpdateAsync(slot, ctx2, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.ExitRequested, r2.Outcome)
            Assert.Equal("exit-engine", r2.ExitTrigger)
            Assert.Contains("ExitEngine: score=", r2.ExitReason)
        End Function

        ' ── (d) E1 SuperTrend flip ──────────────────────────────────────────

        <Fact>
        Public Async Function D_SuperTrendFlip_ReturnsImmediateExit() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False

            Dim ctx = MakeTickContext(UptrendThenCrashBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29)))

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.ExitRequested, r.Outcome)
            Assert.Equal("exit-engine", r.ExitTrigger)
            Assert.Equal("ExitEngine: SuperTrend flip", r.ExitReason)
        End Function

        Private Shared Async Function WaitForCondition(predicate As Func(Of Boolean),
                                                         timeout As TimeSpan) As Task
            Dim deadline = DateTime.UtcNow + timeout
            While DateTime.UtcNow < deadline
                If predicate() Then Return
                Await Task.Delay(25)
            End While
        End Function

    End Class

End Namespace
