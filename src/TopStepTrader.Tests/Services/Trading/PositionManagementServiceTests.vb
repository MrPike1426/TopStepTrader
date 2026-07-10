Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
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
            Public Property FillPriceResult As Decimal? = Nothing
            Public Property OpenPositionsResult As IEnumerable(Of LivePositionSnapshot) =
                CType(New List(Of LivePositionSnapshot)(), IEnumerable(Of LivePositionSnapshot))
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
                Return Task.FromResult(FillPriceResult)
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
            Public Function FlattenContractWithFillAsync(accountId As Long, contractId As String,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of (Success As Boolean, Fill As BrokerCloseFill)) _
                Implements IOrderService.FlattenContractWithFillAsync
                Return Task.FromResult((True, CType(Nothing, BrokerCloseFill)))
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
            Public Function GetOpenPositionsAsync(accountId As Long,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot)) _
                Implements IOrderService.GetOpenPositionsAsync
                Return Task.FromResult(OpenPositionsResult)
            End Function
        End Class

        Private Class StubBarService
            Implements IBarIngestionService
            Public Property LiveBarsResult As IList(Of MarketBar) = New List(Of MarketBar)
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
                Return Task.FromResult(LiveBarsResult)
            End Function
        End Class

        Private Class StubTradeRecordService
            Implements ITradeRecordService
            Public StopAdjustments As New ConcurrentBag(Of (RecordId As Long, OldStop As Decimal, NewStop As Decimal, Phase As String))
            Public TickSnapshots As New ConcurrentBag(Of TradeTickSnapshot)
            Public UpdateEntryPriceCalls As New ConcurrentBag(Of (RecordId As Long, EntryPrice As Decimal))
            Public Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long) Implements ITradeRecordService.OpenTradeAsync
                Return Task.FromResult(0L)
            End Function
            Public Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                                              pnL As Decimal, exitReason As String,
                                              Optional closeFillSource As String = Nothing) As Task _
                Implements ITradeRecordService.CloseTradeAsync
                Return Task.CompletedTask
            End Function
            Public Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task _
                Implements ITradeRecordService.UpdateEntryPriceAsync
                UpdateEntryPriceCalls.Add((id, entryPrice))
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
            Public Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecord) _
                Implements ITradeRecordService.FindByEntryOrderIdAsync
                Return Task.FromResult(Of LiveTradeRecord)(Nothing)
            End Function
            Public Function FindOpenByContractIdAsync(accountId As Long, contractId As String) As Task(Of LiveTradeRecord) _
                Implements ITradeRecordService.FindOpenByContractIdAsync
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
                TickSnapshots.Add(snapshot)
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

        ''' <summary>BUG-103: minimal daily-loss-guard stub — a CanEnter switch plus a
        ''' canned state snapshot for the suppression log line.</summary>
        Private Class StubDailyLossGuard
            Implements IDailyLossGuard

            Public Property CanEnter As Boolean = True
            Public Property State As New DailyLossGuardState()
            Public CanEnterCallCount As Integer = 0

            Public Function GetState() As DailyLossGuardState Implements IDailyLossGuard.GetState
                Return State
            End Function
            Public Function CanEnterNewTrade() As Boolean Implements IDailyLossGuard.CanEnterNewTrade
                CanEnterCallCount += 1
                Return CanEnter
            End Function
            Public Function EvaluateAsync() As Task(Of DailyLossGuardState) Implements IDailyLossGuard.EvaluateAsync
                Return Task.FromResult(State)
            End Function
            Public Function ResetAsync(reason As String) As Task Implements IDailyLossGuard.ResetAsync
                Return Task.CompletedTask
            End Function
            Public Sub RegisterOpenSlotPnlSource(source As IOpenSlotPnlSource) Implements IDailyLossGuard.RegisterOpenSlotPnlSource
            End Sub
            Public Sub UnregisterOpenSlotPnlSource(source As IOpenSlotPnlSource) Implements IDailyLossGuard.UnregisterOpenSlotPnlSource
            End Sub
            Public Event Halted As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.Halted
            Public Event Released As EventHandler Implements IDailyLossGuard.Released
            Public Event ForceFlattened As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.ForceFlattened
        End Class

        ' ── Helpers ─────────────────────────────────────────────────────────

        Private Shared Function MakeService(orderSvc As IOrderService,
                                              barSvc As IBarIngestionService,
                                              tradeRec As ITradeRecordService,
                                              resolver As IContractResolutionService,
                                              Optional logger As ILogger(Of PositionManagementService) = Nothing,
                                              Optional guard As IDailyLossGuard = Nothing) As PositionManagementService
            Dim engine As New ExitSignalEngine(NullLogger(Of ExitSignalEngine).Instance)
            Return New PositionManagementService(orderSvc, barSvc, resolver, tradeRec, engine,
                                                  If(logger, NullLogger(Of PositionManagementService).Instance),
                                                  dailyLossGuard:=guard)
        End Function

        ''' <summary>BUG-102 F4: captures Warning lines (and, for BUG-103, Information
        ''' lines) for assertion.</summary>
        Private Class CapturingLogger(Of T)
            Implements ILogger(Of T)

            Public ReadOnly Warnings As New List(Of String)()
            Public ReadOnly Infos As New List(Of String)()

            Public Function BeginScope(Of TState)(state As TState) As IDisposable Implements ILogger.BeginScope
                Return Nothing
            End Function
            Public Function IsEnabled(logLevel As LogLevel) As Boolean Implements ILogger.IsEnabled
                Return True
            End Function
            Public Sub Log(Of TState)(logLevel As LogLevel, eventId As EventId, state As TState, exception As Exception,
                                       formatter As Func(Of TState, Exception, String)) Implements ILogger.Log
                If formatter Is Nothing Then Return
                If logLevel = LogLevel.Warning Then
                    Warnings.Add(formatter(state, exception))
                ElseIf logLevel = LogLevel.Information Then
                    Infos.Add(formatter(state, exception))
                End If
            End Sub
        End Class

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

        ' ── (e) BUG-101: forming-bar strip on refetch path ───────────────────

        ''' <summary>Builds 19 closed 5-minute bars in a steady uptrend, then a 20th forming
        ''' bar (age 30s) whose High is an artificial spike. The forming bar's intra-period
        ''' high is exactly the input that BUG-101 says must not reach the SuperTrend math.</summary>
        Private Shared Function BarsWithFormingSpike(now As DateTime) As IList(Of MarketBar)
            Const Tf As Integer = 5
            Dim bars As New List(Of MarketBar)
            ' 19 closed bars at 5-min spacing, ending at now - 5min.
            For i = 0 To 18
                Dim ts = New DateTimeOffset(now.AddMinutes(-Tf * (19 - i)), TimeSpan.Zero)
                Dim close = 100D + 0.5D * i
                bars.Add(New MarketBar With {
                    .Timestamp = ts,
                    .Open = close - 0.10D,
                    .High = close + 0.20D,
                    .Low = close - 0.20D,
                    .Close = close,
                    .Volume = 1000
                })
            Next
            ' Forming bar: age 30s, with a spike-high 50x the normal bar range.
            Dim formingClose = 100D + 0.5D * 19
            bars.Add(New MarketBar With {
                .Timestamp = New DateTimeOffset(now.AddSeconds(-30), TimeSpan.Zero),
                .Open = formingClose - 0.10D,
                .High = formingClose + 50D,
                .Low = formingClose - 0.20D,
                .Close = formingClose,
                .Volume = 1000
            })
            Return bars
        End Function

        <Fact>
        Public Async Function E_FormingBarStrip_RefetchPath_ExcludesSpikeFromSuperTrend() As Task
            Dim now = DateTime.UtcNow
            Dim feedBars = BarsWithFormingSpike(now)
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService With {.LiveBarsResult = feedBars}
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.TradeRecordId = 9L  ' enables fire-and-forget tick-snapshot persist
            slot.LivePrice = 0D

            ' tickContext.Bars = Nothing forces the refetch path.
            Dim ctx As New PositionManagementTickContext With {
                .Bars = Nothing,
                .StrategyTimeframe = BarTimeframe.FiveMinute,
                .AsOfUtc = now,
                .StMultiplier = 3.0R,
                .ExitScoreThreshold = 7,
                .EarlyModeMaxAgeMinutes = 30,
                .IsPrimaryForBracketEdit = True,
                .IsDebugCaptureEnabled = False
            }

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)

            ' Compute the SuperTrend line both with and without the forming bar; the spike's
            ' inflated High makes these values diverge by orders of magnitude, so the test is
            ' robust to indicator internals.
            Dim strippedHighs = feedBars.Take(19).Select(Function(b) b.High).ToList()
            Dim strippedLows = feedBars.Take(19).Select(Function(b) b.Low).ToList()
            Dim strippedCloses = feedBars.Take(19).Select(Function(b) b.Close).ToList()
            Dim stStripped = TechnicalIndicators.SuperTrend(strippedHighs, strippedLows, strippedCloses,
                                                             period:=10, multiplier:=3.0R)
            Dim expectedStLine = CDec(stStripped.Line(strippedHighs.Count - 1))

            Dim unstrippedHighs = feedBars.Select(Function(b) b.High).ToList()
            Dim unstrippedLows = feedBars.Select(Function(b) b.Low).ToList()
            Dim unstrippedCloses = feedBars.Select(Function(b) b.Close).ToList()
            Dim stUnstripped = TechnicalIndicators.SuperTrend(unstrippedHighs, unstrippedLows, unstrippedCloses,
                                                               period:=10, multiplier:=3.0R)
            Dim unstrippedStLine = CDec(stUnstripped.Line(unstrippedHighs.Count - 1))
            Assert.True(Math.Abs(unstrippedStLine - expectedStLine) > 1D,
                        "Test fixture failed to produce a measurable spike — the stripped vs. unstripped ST lines should differ.")

            ' The fire-and-forget LogTickSnapshotAsync must record the ST line computed on the
            ' stripped 19-bar series, with BarTimestamp matching the last *closed* bar.
            Await WaitForCondition(Function() Not tradeRec.TickSnapshots.IsEmpty, TimeSpan.FromSeconds(2))
            Assert.NotEmpty(tradeRec.TickSnapshots)
            Dim snap = tradeRec.TickSnapshots.First()
            Assert.Equal(feedBars(18).Timestamp, snap.BarTimestamp)
            Assert.Equal(CSng(expectedStLine), snap.SuperTrendLine, 3)
        End Function

        ' ── (f) BUG-101: closed forming bar (age >= tf) preserved ────────────

        <Fact>
        Public Async Function F_FormingBarStrip_ClosedLastBar_BarsCountPreserved() As Task
            Dim now = DateTime.UtcNow
            Dim feedBars As New List(Of MarketBar)
            ' 20 closed 5-minute bars; the most recent bar is older than tf so no strip should occur.
            For i = 0 To 19
                Dim ts = New DateTimeOffset(now.AddMinutes(-5 * (20 - i)), TimeSpan.Zero)
                Dim close = 100D + 0.5D * i
                feedBars.Add(New MarketBar With {
                    .Timestamp = ts,
                    .Open = close - 0.10D,
                    .High = close + 0.20D,
                    .Low = close - 0.20D,
                    .Close = close,
                    .Volume = 1000
                })
            Next

            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService With {.LiveBarsResult = feedBars}
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.TradeRecordId = 11L
            slot.LivePrice = 0D

            Dim ctx As New PositionManagementTickContext With {
                .Bars = Nothing,
                .StrategyTimeframe = BarTimeframe.FiveMinute,
                .AsOfUtc = now,
                .StMultiplier = 3.0R,
                .ExitScoreThreshold = 7,
                .EarlyModeMaxAgeMinutes = 30,
                .IsPrimaryForBracketEdit = True,
                .IsDebugCaptureEnabled = False
            }

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)
            Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)

            Await WaitForCondition(Function() Not tradeRec.TickSnapshots.IsEmpty, TimeSpan.FromSeconds(2))
            Assert.NotEmpty(tradeRec.TickSnapshots)
            ' Last bar timestamp survives unstripped → strip was correctly age-gated.
            Dim snap = tradeRec.TickSnapshots.First()
            Assert.Equal(feedBars(19).Timestamp, snap.BarTimestamp)
        End Function

        Private Shared Async Function WaitForCondition(predicate As Func(Of Boolean),
                                                         timeout As TimeSpan) As Task
            Dim deadline = DateTime.UtcNow + timeout
            While DateTime.UtcNow < deadline
                If predicate() Then Return
                Await Task.Delay(25)
            End While
        End Function

        ' ── (g) BUG-102 F4-a: all broker resolution fails, LivePrice estimate kicks in ────

        ''' <summary>Builds a slot in the pre-backfill state (EntryPrice=0) with a known
        ''' LivePrice so the F1 fallback chain has a non-zero last-resort source.</summary>
        Private Shared Function MakeUnbackfilledSlot(Optional livePrice As Decimal = 99.25D) As PositionSlot
            Dim slot = MakeOpenSlot()
            slot.EntryPrice = 0D
            slot.StopPrice = 0D
            slot.LivePrice = livePrice
            slot.EntryOrderId = 7777L
            slot.TradeRecordId = 123L
            Return slot
        End Function

        Private Shared Function MakeSnapshotWithOpenRate(openRate As Decimal) As LivePositionSnapshot
            Return New LivePositionSnapshot With {
                .PositionId = 555L,
                .OpenRate = openRate,
                .Units = 1D,
                .Amount = 1D,
                .IsBuy = True,
                .UnrealizedPnlUsd = 0D,
                .PositionCount = 1
            }
        End Function

        <Fact>
        Public Async Function G_BackfillEntry_AllBrokerSourcesFail_UsesLivePriceEstimate() As Task
            ' All three broker steps fail: TryGetOrderFillPriceAsync = Nothing, snapshot.OpenRate = 0,
            ' GetOpenPositionsAsync = empty. slot.LivePrice = 99.25 provides the last-resort estimate.
            Dim orderSvc As New StubOrderService With {
                .SnapshotResult = MakeSnapshotWithOpenRate(0D),
                .FillPriceResult = Nothing,
                .OpenPositionsResult = CType(New List(Of LivePositionSnapshot)(), IEnumerable(Of LivePositionSnapshot))
            }
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim warnLogger As New CapturingLogger(Of PositionManagementService)()
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver, warnLogger)

            Dim slot = MakeUnbackfilledSlot(livePrice:=99.25D)
            Dim ctx = MakeTickContext(FlatBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29)))

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(99.25D, slot.EntryPrice)
            Assert.True(slot.IsEntryPriceEstimated)
            Assert.Contains(warnLogger.Warnings,
                Function(m) m.IndexOf("live-price estimate", StringComparison.OrdinalIgnoreCase) >= 0)
            ' No UpdateEntryPriceAsync call with 0 — and the estimated value also must not be persisted
            ' (the F1 suppression only allows non-zero confirmed prices through).
            Assert.DoesNotContain(tradeRec.UpdateEntryPriceCalls, Function(c) c.EntryPrice = 0D)
        End Function

        ' ── (h) BUG-102 F4-b: fill-price succeeds, OpenRate=0 — not estimated ────

        <Fact>
        Public Async Function H_BackfillEntry_FillPriceWins_NotEstimated() As Task
            Dim orderSvc As New StubOrderService With {
                .SnapshotResult = MakeSnapshotWithOpenRate(0D),
                .FillPriceResult = CType(101.5D, Decimal?)
            }
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim slot = MakeUnbackfilledSlot(livePrice:=99.25D)
            Dim ctx = MakeTickContext(FlatBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29)))

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(101.5D, slot.EntryPrice)
            Assert.False(slot.IsEntryPriceEstimated)
            ' UpdateEntryPriceAsync is fire-and-forget; wait briefly.
            Await WaitForCondition(Function() Not tradeRec.UpdateEntryPriceCalls.IsEmpty, TimeSpan.FromSeconds(2))
            Assert.Contains(tradeRec.UpdateEntryPriceCalls, Function(c) c.EntryPrice = 101.5D)
        End Function

        ' ── (i) BUG-102 F4-c: every source fails and LivePrice is 0 — slot stays at 0 ────

        <Fact>
        Public Async Function I_BackfillEntry_AllSourcesFail_NoUpdateEntryPriceWithZero() As Task
            Dim orderSvc As New StubOrderService With {
                .SnapshotResult = MakeSnapshotWithOpenRate(0D),
                .FillPriceResult = Nothing
            }
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim warnLogger As New CapturingLogger(Of PositionManagementService)()
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver, warnLogger)

            Dim slot = MakeUnbackfilledSlot(livePrice:=0D)
            Dim ctx = MakeTickContext(FlatBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29)))

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(0D, slot.EntryPrice)
            Assert.True(slot.IsEntryPriceEstimated)
            Assert.Contains(warnLogger.Warnings,
                Function(m) m.IndexOf("could not resolve a non-zero entry price", StringComparison.OrdinalIgnoreCase) >= 0)
            ' UpdateEntryPriceAsync must NEVER be invoked with 0 — give the fire-and-forget a moment
            ' to run, then assert no such call landed.
            Await Task.Delay(100)
            Assert.DoesNotContain(tradeRec.UpdateEntryPriceCalls, Function(c) c.EntryPrice = 0D)
        End Function

        ' ── STRAT-41 — Pullback-gated scale-in (F4) ──────────────────────────

        ''' <summary>Captures (slot, addContracts) tuples passed to OnScaleInRequested.</summary>
        Private Class ScaleInCapture
            Public Calls As New List(Of (Slot As PositionSlot, AddContracts As Integer))
            Public Function HandleAsync(s As PositionSlot, n As Integer) As Task
                ' Snapshot the slot's Side + Contracts at the call moment.
                Calls.Add((s, n))
                Return Task.CompletedTask
            End Function
        End Class

        Private Shared Function MakeStrat41TickContext(bars As IList(Of MarketBar),
                                                         scaleInCapture As ScaleInCapture,
                                                         Optional pullbackEnabled As Boolean = True,
                                                         Optional maxAfterScaleIn As Integer = 2,
                                                         Optional pullbackContracts As Integer = 1,
                                                         Optional pullbackFactor As Decimal = 0.5D) As PositionManagementTickContext
            Return New PositionManagementTickContext With {
                .Bars = bars,
                .StrategyTimeframe = BarTimeframe.FifteenMinute,
                .AsOfUtc = DateTime.UtcNow,
                .StMultiplier = 3.0R,
                .ExitScoreThreshold = 7,
                .EarlyModeMaxAgeMinutes = 30,
                .IsPrimaryForBracketEdit = True,
                .IsDebugCaptureEnabled = False,
                .LeverageMultiplier = 1,
                .BandForAdx = Function(adx As Single)
                                  ' Synthetic ratchet: any positive ADX maps to a band so the
                                  ' legacy STRAT-31 path would have fired here. STRAT-41 F4-a
                                  ' verifies it no longer does.
                                  If adx >= 60.0F Then Return 3
                                  If adx >= 40.0F Then Return 2
                                  If adx >= 25.0F Then Return 1
                                  Return 0
                              End Function,
                .OnScaleInRequested = AddressOf scaleInCapture.HandleAsync,
                .PullbackScaleInEnabled = pullbackEnabled,
                .PullbackAtrFactor = pullbackFactor,
                .PullbackScaleInContracts = pullbackContracts,
                .MaxContractsAfterScaleIn = maxAfterScaleIn
            }
        End Function

        ''' <summary>Computes (stLine, atrNow) on the supplied bars using the same
        ''' indicator code the service uses, so tests can position LivePrice in/out of
        ''' the pullback band without depending on indicator internals.</summary>
        Private Shared Function ResolveStLineAndAtr(bars As IList(Of MarketBar)) As (StLine As Decimal, Atr As Decimal)
            Dim highs = bars.Select(Function(b) CDec(b.High)).ToList()
            Dim lows = bars.Select(Function(b) CDec(b.Low)).ToList()
            Dim closes = bars.Select(Function(b) CDec(b.Close)).ToList()
            Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=3.0R)
            Dim atrArr = TechnicalIndicators.ATR(highs, lows, closes, period:=14)
            Dim n = bars.Count - 1
            Return (CDec(st.Line(n)), CDec(atrArr(n)))
        End Function

        <Fact>
        Public Async Function S41a_Strat31Retired_NoScaleInOnRisingBands() As Task
            ' Rising ADX would have triggered the legacy STRAT-31 band ratchet — but with
            ' the new path the only way to scale in is pullback, and this fixture sits
            ' well above the ST line (no pullback). Expect zero scale-in calls.
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            ' Park LivePrice well above the ST line so distance >> 0.5 × ATR (trend extension).
            slot.LivePrice = sa.StLine + 5D * sa.Atr

            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(bars, cap)

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)
            Assert.Empty(cap.Calls)
            Assert.False(slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function S41b_PullbackHappyPath_AddsOneContract() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            ' Uptrend → DI+ > DI- and ST direction = Buy. Park LivePrice inside the
            ' pullback band (within 0.5 × ATR of ST line).
            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Assert.True(sa.Atr > 0D, "Fixture must produce a positive ATR.")

            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.Contracts = 1
            ' distance = LivePrice - stLine; want 0 < distance < 0.5 × ATR.
            slot.LivePrice = sa.StLine + 0.25D * sa.Atr

            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(bars, cap)

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)
            Assert.Single(cap.Calls)
            Assert.Equal(1, cap.Calls(0).AddContracts)
            Assert.True(slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function S41c_TrendExtension_NoScaleIn() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.Contracts = 1
            ' Way above the ST line — outside the pullback band.
            slot.LivePrice = sa.StLine + 3D * sa.Atr

            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(bars, cap)

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Empty(cap.Calls)
            Assert.False(slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function S41d_Idempotency_NoSecondScaleIn() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.Contracts = 1
            ' Already scaled-in once (HasScaledInOnPullback set by a prior tick).
            slot.HasScaledInOnPullback = True
            ' Even though we're sitting in a perfect pullback now, the latch blocks a 2nd add.
            slot.LivePrice = sa.StLine + 0.25D * sa.Atr

            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(bars, cap)

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Empty(cap.Calls)
            Assert.True(slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function S41e_CounterDi_VetoesScaleIn() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            ' Construct a slot whose side opposes the trend so the DI veto fires in
            ' isolation: an uptrend fixture (DI+ > DI-) but the slot is "Sell". To keep
            ' the geometry check passing (so the DI gate is what blocks, not geometry),
            ' park LivePrice just below the ST line — Sell distance = stLine - LivePrice
            ' is positive and within the threshold.
            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Dim highsDec = bars.Select(Function(b) CDec(b.High)).ToList()
            Dim lowsDec = bars.Select(Function(b) CDec(b.Low)).ToList()
            Dim closesDec = bars.Select(Function(b) CDec(b.Close)).ToList()
            Dim dmi = TechnicalIndicators.DMI(highsDec, lowsDec, closesDec, period:=14)
            Dim n = bars.Count - 1
            ' Sanity-check the fixture: on the uptrend, DI+ > DI- (so a Sell slot's
            ' minusOk gate is False → veto fires).
            Assert.True(dmi.PlusDI(n) > dmi.MinusDI(n),
                        $"Fixture invariant: expected DI+ > DI- on uptrend last bar; got +DI={dmi.PlusDI(n):F2} -DI={dmi.MinusDI(n):F2}.")

            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.Side = "Sell"
            slot.Contracts = 1
            ' Sell-side geometry: distance = stLine - currentClose. Park below the ST
            ' line so the *distance* check passes; the DI gate is then the sole vetoer.
            slot.LivePrice = sa.StLine - 0.25D * sa.Atr

            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(bars, cap)

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Empty(cap.Calls)
            Assert.False(slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function S41f_AtCap_NoScaleIn() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim barSvc As New StubBarService
            Dim tradeRec As New StubTradeRecordService
            Dim resolver As New StubContractResolver
            Dim svc = MakeService(orderSvc, barSvc, tradeRec, resolver)

            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.Contracts = 2   ' Already at MaxContractsAfterScaleIn = 2 default.
            slot.LivePrice = sa.StLine + 0.25D * sa.Atr

            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(bars, cap)

            Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Empty(cap.Calls)
            Assert.False(slot.HasScaledInOnPullback)
        End Function

        ' ── BUG-103 (LF-9) — Scale-ins gated by the daily-loss guard ─────────

        ''' <summary>Builds the S41b happy-path pullback fixture: an uptrend with the
        ''' slot's LivePrice parked inside the pullback band so the scale-in decision
        ''' fires and only the guard can stop it.</summary>
        Private Shared Function MakePullbackFixture() As (Slot As PositionSlot, Bars As IList(Of MarketBar))
            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29), count:=30, basePrice:=100D, priceStep:=0.5D)
            Dim sa = ResolveStLineAndAtr(bars)
            Dim slot = MakeOpenSlot()
            slot.IsEarlyModeEntry = False
            slot.Contracts = 1
            slot.LivePrice = sa.StLine + 0.25D * sa.Atr
            Return (slot, bars)
        End Function

        <Fact>
        Public Async Function B103a_GuardAllows_ScaleInProceeds() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim guard As New StubDailyLossGuard With {.CanEnter = True}
            Dim svc = MakeService(orderSvc, New StubBarService, New StubTradeRecordService, New StubContractResolver, guard:=guard)

            Dim f = MakePullbackFixture()
            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(f.Bars, cap)

            Await svc.UpdateAsync(f.Slot, ctx, CancellationToken.None)

            Assert.True(guard.CanEnterCallCount > 0, "Guard must be consulted before the scale-in.")
            Assert.Single(cap.Calls)
            Assert.True(f.Slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function B103b_HardHalted_ScaleInSuppressedAndLogged() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim guard As New StubDailyLossGuard With {
                .CanEnter = False,
                .State = New DailyLossGuardState With {
                    .IsHalted = True,
                    .Reason = RiskHaltReason.DailyLossLimit,
                    .CombinedDailyPnl = -510D,
                    .LimitDollars = 500D
                }
            }
            Dim logger As New CapturingLogger(Of PositionManagementService)
            Dim svc = MakeService(orderSvc, New StubBarService, New StubTradeRecordService, New StubContractResolver,
                                  logger:=logger, guard:=guard)

            Dim f = MakePullbackFixture()
            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(f.Bars, cap)

            Await svc.UpdateAsync(f.Slot, ctx, CancellationToken.None)

            Assert.Empty(cap.Calls)
            ' Latch stays clear: if the halt is released the slot may still scale in.
            Assert.False(f.Slot.HasScaledInOnPullback)
            Assert.Contains(logger.Infos,
                Function(m) m.IndexOf("scale-in suppressed", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso
                            m.IndexOf(NameOf(RiskHaltReason.DailyLossLimit), StringComparison.OrdinalIgnoreCase) >= 0)
        End Function

        <Fact>
        Public Async Function B103c_SoftHalted_ScaleInSuppressed() As Task
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            ' FEAT-73 soft halt: IsHalted stays False but CanEnterNewTrade() answers False.
            Dim guard As New StubDailyLossGuard With {
                .CanEnter = False,
                .State = New DailyLossGuardState With {
                    .IsHalted = False,
                    .SoftHalted = True,
                    .Reason = RiskHaltReason.ConsecutiveLosses
                }
            }
            Dim svc = MakeService(orderSvc, New StubBarService, New StubTradeRecordService, New StubContractResolver, guard:=guard)

            Dim f = MakePullbackFixture()
            Dim cap As New ScaleInCapture
            Dim ctx = MakeStrat41TickContext(f.Bars, cap)

            Await svc.UpdateAsync(f.Slot, ctx, CancellationToken.None)

            Assert.Empty(cap.Calls)
            Assert.False(f.Slot.HasScaledInOnPullback)
        End Function

        <Fact>
        Public Async Function B103d_StopManagementContinuesWhileHalted() As Task
            ' Re-runs the (b) phased-stop ratchet scenario with a halted guard injected:
            ' the guard blocks risk ADDS only — stop ratcheting must keep working.
            Dim orderSvc As New StubOrderService With {.SnapshotResult = MakeConfirmedOpenSnapshot()}
            Dim tradeRec As New StubTradeRecordService
            Dim guard As New StubDailyLossGuard With {
                .CanEnter = False,
                .State = New DailyLossGuardState With {.IsHalted = True, .Reason = RiskHaltReason.DailyLossLimit}
            }
            Dim svc = MakeService(orderSvc, New StubBarService, tradeRec, New StubContractResolver, guard:=guard)

            Dim slot = MakeOpenSlot()
            slot.TradeRecordId = 7L
            slot.LivePrice = 0D
            slot.StopPhase = StopPhase.Initial

            Dim bars = UptrendBars(DateTimeOffset.UtcNow.AddMinutes(-15 * 29))
            Dim ctx = MakeTickContext(bars)

            Dim r = Await svc.UpdateAsync(slot, ctx, CancellationToken.None)

            Assert.Equal(PositionManagementOutcome.Continue, r.Outcome)
            Assert.True(r.StopAdjusted, "Stop ratchet must keep operating while the guard is halted.")
            Assert.NotEmpty(orderSvc.EditCalls)
        End Function

    End Class

End Namespace
