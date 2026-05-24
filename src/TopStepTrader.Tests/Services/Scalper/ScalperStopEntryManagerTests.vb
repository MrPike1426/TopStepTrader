Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Scalper
Imports Xunit

Namespace TopStepTrader.Tests.Services.Scalper

    ''' <summary>
    ''' FEAT-69: state-machine + rate-budget tests for <see cref="ScalperStopEntryManager"/>.
    ''' Uses a hand-rolled <see cref="FakeOrderService"/> instead of a mocking framework to
    ''' match the repo's existing scalper-test conventions.
    ''' </summary>
    Public Class ScalperStopEntryManagerTests

        Private Const MesSymbol As String = "MES"
        Private Const AccountId As Long = 999L

        ' ─── Test doubles ──────────────────────────────────────────────────

        Private Class FakeOrderService
            Implements IOrderService

            Public ReadOnly PlaceCalls As New List(Of Order)()
            Public ReadOnly CancelCalls As New List(Of Long)()
            Public NextExternalOrderId As Long = 1000L

            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

            Public Function PlaceOrderAsync(order As Order) As Task(Of Order) Implements IOrderService.PlaceOrderAsync
                PlaceCalls.Add(order)
                Dim assigned = NextExternalOrderId
                NextExternalOrderId += 1L
                order.ExternalOrderId = assigned
                Return Task.FromResult(order)
            End Function

            Public Function CancelOrderAsync(orderId As Long) As Task(Of Boolean) Implements IOrderService.CancelOrderAsync
                CancelCalls.Add(orderId)
                Return Task.FromResult(True)
            End Function

            Public Sub RaiseOrderFilled(orderId As Long,
                                         Optional fillPrice As Decimal = 0D,
                                         Optional positionId As Long? = Nothing)
                Dim ord As New Order With {
                    .ExternalOrderId = orderId,
                    .FillPrice = fillPrice,
                    .ExternalPositionId = positionId
                }
                RaiseEvent OrderFilled(Me, New OrderFilledEventArgs(ord))
            End Sub

            ' ── unused IOrderService members ─────────────────────────────
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
            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?, Optional enableTsl As Boolean = False, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.EditPositionSlTpAsync
                Throw New NotImplementedException()
            End Function
            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer, Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.PartialCloseContractAsync
                Throw New NotImplementedException()
            End Function
            Public Function GetOpenPositionsAsync(accountId As Long, Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot)) Implements IOrderService.GetOpenPositionsAsync
                Return Task.FromResult(Of IEnumerable(Of LivePositionSnapshot))(New List(Of LivePositionSnapshot)())
            End Function

            ' Suppress 42024 (unused event) for events VB requires we declare for Implements.
#Disable Warning BC42024
            Private Sub SuppressEventWarnings()
                RaiseEvent OrderRejected(Me, Nothing)
                RaiseEvent PositionUpdated(Me, Nothing)
            End Sub
#Enable Warning BC42024
        End Class

        ' ─── Fixture helpers ───────────────────────────────────────────────

        Private Shared Function MakeConfig(Optional preStagedEnabled As Boolean = True,
                                            Optional offsetTicks As Integer = 1,
                                            Optional repriceThresholdTicks As Integer = 2,
                                            Optional staleMin As Integer = 30,
                                            Optional debounceSec As Integer = 30,
                                            Optional callCap As Integer = 80,
                                            Optional leverage As Integer = 1) As UltimateScalperConfig
            Return New UltimateScalperConfig() With {
                .PreStagedEntriesEnabled = preStagedEnabled,
                .EntryTriggerOffsetTicks = offsetTicks,
                .RepriceThresholdTicks = repriceThresholdTicks,
                .ArmStaleMinutes = staleMin,
                .ReArmDebounceSeconds = debounceSec,
                .MaxBrokerCallsPerMinute = callCap,
                .Leverage = leverage
            }
        End Function

        Private Shared Function MakeEval(Optional primed As UltimateScalperSignalSide = UltimateScalperSignalSide.Bullish,
                                          Optional barHigh As Decimal = 4500.5D,
                                          Optional barLow As Decimal = 4499.5D,
                                          Optional asOf As DateTimeOffset = Nothing) As UltimateScalperEvaluation
            Return New UltimateScalperEvaluation With {
                .Symbol = MesSymbol,
                .AsOf = If(asOf = DateTimeOffset.MinValue, DateTimeOffset.UtcNow, asOf),
                .LastClose = (barHigh + barLow) / 2D,
                .LastBarHigh = barHigh,
                .LastBarLow = barLow,
                .Ma200 = 4500D,
                .Vwap = 4500D,
                .Rsi = 30.0,
                .BarsSinceCrossAbove = 5,
                .BarsSinceCrossBelow = Int32.MaxValue,
                .IsWarm = True,
                .Signal = UltimateScalperSignalSide.None,
                .PrimedSide = primed
            }
        End Function

        Private Shared Function MakeManager(svc As FakeOrderService) As ScalperStopEntryManager
            Return New ScalperStopEntryManager(svc, NullLogger(Of ScalperStopEntryManager).Instance)
        End Function

        ' ─── Tests ─────────────────────────────────────────────────────────

        <Fact>
        Public Async Function ArmsOnPrimedBullish_PlacesStopOrderAtBarHighPlusOffset() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1)
                Dim eval = MakeEval(primed:=UltimateScalperSignalSide.Bullish, barHigh:=4500.5D)

                Await mgr.OnEvaluationAsync(eval, cfg, AccountId, CancellationToken.None)

                Assert.Single(svc.PlaceCalls)
                Dim placed = svc.PlaceCalls(0)
                Assert.Equal(OrderType.StopOrder, placed.OrderType)
                Assert.Equal(OrderSide.Buy, placed.Side)
                ' MES tick size = 0.25 → 4500.5 + 1*0.25 = 4500.75
                Assert.Equal(4500.75D, placed.StopPrice)
                Assert.Equal(MesSymbol, placed.ContractId)
                Assert.Equal(AccountId, placed.AccountId)

                Dim armed = mgr.ArmedSymbols
                Assert.Single(armed)
                Assert.Equal(ScalperArmedPhase.Primed, armed(0).Phase)
                Assert.Equal(svc.NextExternalOrderId - 1, armed(0).BrokerOrderId)
            End Using
        End Function

        <Fact>
        Public Async Function ArmsOnPrimedBearish_PlacesStopOrderAtBarLowMinusOffset() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1)
                Dim eval = MakeEval(primed:=UltimateScalperSignalSide.Bearish, barLow:=4499.5D)

                Await mgr.OnEvaluationAsync(eval, cfg, AccountId, CancellationToken.None)

                Assert.Single(svc.PlaceCalls)
                Dim placed = svc.PlaceCalls(0)
                Assert.Equal(OrderSide.Sell, placed.Side)
                Assert.Equal(4499.25D, placed.StopPrice)
            End Using
        End Function

        <Fact>
        Public Async Function Arm_PlacesStopOrderWithProtectiveBracket_Long() As Task
            ' BUG-95: the pre-staged stop-entry must carry InitialStopTicks so PlaceOrderAsync
            ' attaches a stopLossBracket and the broker enforces protection atomically with fill.
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1)
                Dim eval = MakeEval(primed:=UltimateScalperSignalSide.Bullish, barHigh:=4500.5D)

                Await mgr.OnEvaluationAsync(eval, cfg, AccountId, CancellationToken.None)

                Assert.Single(svc.PlaceCalls)
                Dim placed = svc.PlaceCalls(0)
                Assert.Equal(OrderType.StopOrder, placed.OrderType)
                Assert.Equal(OrderSide.Buy, placed.Side)
                Assert.True(placed.InitialStopTicks.HasValue,
                            "Expected InitialStopTicks to be populated — without it PlaceOrderAsync sends a naked stop-entry.")

                ' Compute the expected tick count using the same formula the production code uses,
                ' so the test stays correct if the MES profile or tick value ever changes.
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(MesSymbol)
                Dim profile = cfg.GetProfile(MesSymbol)
                Dim expectedTicks = CInt(Math.Ceiling(CDbl(profile.InitialStopDollars / contract.PxTickValue)))
                Assert.Equal(expectedTicks, placed.InitialStopTicks.Value)
            End Using
        End Function

        <Fact>
        Public Async Function Arm_PlacesStopOrderWithProtectiveBracket_Short() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1)
                Dim eval = MakeEval(primed:=UltimateScalperSignalSide.Bearish, barLow:=4499.5D)

                Await mgr.OnEvaluationAsync(eval, cfg, AccountId, CancellationToken.None)

                Assert.Single(svc.PlaceCalls)
                Dim placed = svc.PlaceCalls(0)
                Assert.Equal(OrderSide.Sell, placed.Side)
                Assert.True(placed.InitialStopTicks.HasValue)

                ' Tick count is unsigned here — directional sign is applied downstream in PlaceOrderAsync
                ' (signedSlTicks = If(isBuy, -validatedTicks, validatedTicks)).
                Dim contract = FavouriteContracts.TryGetBySymbolResolved(MesSymbol)
                Dim profile = cfg.GetProfile(MesSymbol)
                Dim expectedTicks = CInt(Math.Ceiling(CDbl(profile.InitialStopDollars / contract.PxTickValue)))
                Assert.Equal(expectedTicks, placed.InitialStopTicks.Value)
            End Using
        End Function

        <Fact>
        Public Async Function Reprice_PreservesBracket() As Task
            ' BUG-95: re-price uses the same BuildStopEntryOrder helper as arm, so the bracket
            ' must persist across re-price cycles too.
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1, repriceThresholdTicks:=2)
                Dim evalArm = MakeEval(barHigh:=4500.5D)
                Await mgr.OnEvaluationAsync(evalArm, cfg, AccountId, CancellationToken.None)

                ' Drift the trigger beyond the threshold (3 ticks) to force a re-price.
                Dim evalShift = MakeEval(barHigh:=4501.25D, asOf:=evalArm.AsOf.AddMinutes(5))
                Await mgr.OnEvaluationAsync(evalShift, cfg, AccountId, CancellationToken.None)

                Assert.Equal(2, svc.PlaceCalls.Count)
                Dim repriced = svc.PlaceCalls(1)
                Assert.True(repriced.InitialStopTicks.HasValue,
                            "Expected InitialStopTicks on the re-priced order — the second placement is just as exposed to a naked fill as the first.")
            End Using
        End Function

        <Fact>
        Public Async Function UnPrimedAfterArm_CancelsTheOrder() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig()
                Dim evalArm = MakeEval(primed:=UltimateScalperSignalSide.Bullish)
                Await mgr.OnEvaluationAsync(evalArm, cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.PlaceCalls)

                ' Next tick: PrimedSide = None — manager should cancel.
                Dim evalNone = MakeEval(primed:=UltimateScalperSignalSide.None,
                                         asOf:=evalArm.AsOf.AddMinutes(5))
                Await mgr.OnEvaluationAsync(evalNone, cfg, AccountId, CancellationToken.None)

                Assert.Single(svc.CancelCalls)
                Assert.Equal(ScalperArmedPhase.Idle, mgr.ArmedSymbols.Count)  ' 0 primed
            End Using
        End Function

        <Fact>
        Public Async Function RepriceOnBarClose_TriggersCancelThenPlace_WhenDriftAboveThreshold() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1, repriceThresholdTicks:=2)
                Dim evalArm = MakeEval(barHigh:=4500.5D)
                Await mgr.OnEvaluationAsync(evalArm, cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.PlaceCalls)

                ' New bar with high drift = 3 ticks above prior trigger (4500.75 → 4501.5).
                Dim evalShift = MakeEval(barHigh:=4501.25D, asOf:=evalArm.AsOf.AddMinutes(5))
                Await mgr.OnEvaluationAsync(evalShift, cfg, AccountId, CancellationToken.None)

                ' Cancel + Place each fired once for the re-price.
                Assert.Single(svc.CancelCalls)
                Assert.Equal(2, svc.PlaceCalls.Count)
                ' VB = is numeric-equality for Decimal — sidesteps xUnit's scale-sensitive default.
                Assert.True(svc.PlaceCalls(1).StopPrice = 4501.5D,
                            $"expected stop price 4501.5, got {svc.PlaceCalls(1).StopPrice}")
            End Using
        End Function

        <Fact>
        Public Async Function RepriceSkipped_WhenDriftBelowThreshold() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(offsetTicks:=1, repriceThresholdTicks:=2)
                Dim evalArm = MakeEval(barHigh:=4500.5D)
                Await mgr.OnEvaluationAsync(evalArm, cfg, AccountId, CancellationToken.None)

                ' New bar shifts trigger by only 1 tick (below threshold of 2).
                Dim evalShift = MakeEval(barHigh:=4500.75D, asOf:=evalArm.AsOf.AddMinutes(5))
                Await mgr.OnEvaluationAsync(evalShift, cfg, AccountId, CancellationToken.None)

                Assert.Empty(svc.CancelCalls)
                Assert.Single(svc.PlaceCalls)
            End Using
        End Function

        <Fact>
        Public Async Function ReArmDebounce_BlocksImmediateReArmAfterCancel() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(debounceSec:=30)
                Dim evalArm = MakeEval()
                Await mgr.OnEvaluationAsync(evalArm, cfg, AccountId, CancellationToken.None)
                Dim evalNone = MakeEval(primed:=UltimateScalperSignalSide.None,
                                         asOf:=evalArm.AsOf.AddMinutes(5))
                Await mgr.OnEvaluationAsync(evalNone, cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.CancelCalls)

                ' Now PrimedSide flips back on — but the debounce window has not elapsed.
                Dim evalReArm = MakeEval(asOf:=evalArm.AsOf.AddMinutes(10))
                Await mgr.OnEvaluationAsync(evalReArm, cfg, AccountId, CancellationToken.None)

                ' Still only 1 place call — re-arm was blocked by debounce.
                Assert.Single(svc.PlaceCalls)
            End Using
        End Function

        <Fact>
        Public Async Function RateBudgetExhaustion_DefersArm_StillAllowsCancel() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                ' Cap of 1: the first arm uses the budget; a second arm of a different symbol defers.
                Dim cfg = MakeConfig(callCap:=1, debounceSec:=0)

                Dim eval1 = MakeEval()
                Await mgr.OnEvaluationAsync(eval1, cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.PlaceCalls)

                ' Second arm attempt for MNQ — should defer (cap reached) → no new place call.
                Dim eval2 = New UltimateScalperEvaluation With {
                    .Symbol = "MNQ",
                    .AsOf = DateTimeOffset.UtcNow,
                    .LastBarHigh = 18000.5D,
                    .LastBarLow = 17999.5D,
                    .Ma200 = 18000D, .Vwap = 18000D, .Rsi = 28.0,
                    .BarsSinceCrossAbove = 3, .BarsSinceCrossBelow = Int32.MaxValue,
                    .IsWarm = True, .PrimedSide = UltimateScalperSignalSide.Bullish
                }
                Await mgr.OnEvaluationAsync(eval2, cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.PlaceCalls)  ' deferred, count unchanged

                ' But a cancel still proceeds (strict policy: never defer cancels). Un-prime MES.
                Dim evalNoneMes = MakeEval(primed:=UltimateScalperSignalSide.None,
                                            asOf:=DateTimeOffset.UtcNow.AddMinutes(5))
                Await mgr.OnEvaluationAsync(evalNoneMes, cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.CancelCalls)
            End Using
        End Function

        <Fact>
        Public Async Function DisarmAllExceptAsync_CancelsAllOtherPrimedSymbols() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(debounceSec:=0)
                Await mgr.OnEvaluationAsync(MakeEval(), cfg, AccountId, CancellationToken.None)
                Dim mnqEval = New UltimateScalperEvaluation With {
                    .Symbol = "MNQ", .AsOf = DateTimeOffset.UtcNow,
                    .LastBarHigh = 18000.5D, .LastBarLow = 17999.5D,
                    .Ma200 = 18000D, .Vwap = 18000D, .Rsi = 28.0,
                    .BarsSinceCrossAbove = 3, .BarsSinceCrossBelow = Int32.MaxValue,
                    .IsWarm = True, .PrimedSide = UltimateScalperSignalSide.Bullish
                }
                Await mgr.OnEvaluationAsync(mnqEval, cfg, AccountId, CancellationToken.None)
                Assert.Equal(2, svc.PlaceCalls.Count)

                Await mgr.DisarmAllExceptAsync(MesSymbol, CancellationToken.None)

                Assert.Single(svc.CancelCalls)
                ' MES remains primed; MNQ disarmed.
                Dim stillArmed = mgr.ArmedSymbols
                Assert.Single(stillArmed)
                Assert.Equal(MesSymbol, stillArmed(0).Symbol)
            End Using
        End Function

        <Fact>
        Public Async Function OrderFilledMatchingBrokerOrderId_RaisesFilledIntoPositionOnce() As Task
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig()
                Await mgr.OnEvaluationAsync(MakeEval(), cfg, AccountId, CancellationToken.None)
                Dim orderId = svc.PlaceCalls(0).ExternalOrderId.Value

                Dim filledEvents As New List(Of ScalperArmedState)()
                AddHandler mgr.FilledIntoPosition, Sub(s, st) filledEvents.Add(st)

                svc.RaiseOrderFilled(orderId, fillPrice:=4500.75D, positionId:=42L)
                ' A second fill for the same orderId should not re-fire (state is now Idle).
                svc.RaiseOrderFilled(orderId, fillPrice:=4500.75D, positionId:=42L)

                Assert.Single(filledEvents)
                Assert.Equal(MesSymbol, filledEvents(0).Symbol)
                Assert.Equal(4500.75D, filledEvents(0).FillPrice)
                Assert.Equal(42L, filledEvents(0).PositionId.Value)
                ' After the fill the matched state's BrokerOrderId is no longer treated as Primed.
                Assert.Empty(mgr.ArmedSymbols)
            End Using
        End Function

        <Fact>
        Public Async Function StaleOrder_CancelledOnNextEvaluation() As Task
            ' Force a 1-minute stale window then arm using a manually-aged ArmedAtUtc.
            Dim svc As New FakeOrderService()
            Using mgr = MakeManager(svc)
                Dim cfg = MakeConfig(staleMin:=1)
                Await mgr.OnEvaluationAsync(MakeEval(), cfg, AccountId, CancellationToken.None)
                Assert.Single(svc.PlaceCalls)

                ' Age the armed state by reaching into its public mutable ArmedAtUtc.
                Dim st = mgr.ArmedSymbols(0)
                st.ArmedAtUtc = DateTime.UtcNow.AddMinutes(-5)

                ' Next eval still primed — but stale TTL fires first → cancel.
                Dim evalStill = MakeEval(asOf:=DateTimeOffset.UtcNow.AddMinutes(1))
                Await mgr.OnEvaluationAsync(evalStill, cfg, AccountId, CancellationToken.None)

                Assert.Single(svc.CancelCalls)
                Assert.Empty(mgr.ArmedSymbols)
            End Using
        End Function

    End Class

End Namespace
