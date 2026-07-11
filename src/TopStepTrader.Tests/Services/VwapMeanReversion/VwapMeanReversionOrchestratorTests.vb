Imports System.Collections.Generic
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.VwapMeanReversion
Imports Xunit

Namespace TopStepTrader.Tests.Services.VwapMeanReversion

    ''' <summary>
    ''' FEAT-75 F5: Orchestrator lifecycle tests with hand-rolled fakes (no mocking
    ''' framework, mirroring the repo's other service tests): start/stop, closed-contract
    ''' skip, daily-loss-guard suppression, and a full entry → T1 half-close → T2 exit cycle.
    ''' </summary>
    Public Class VwapMeanReversionOrchestratorTests

        ' Tuesday 2026-01-13 15:00 UTC — CME open, outside every time gate.
        Private Shared ReadOnly OpenUtc As New DateTime(2026, 1, 13, 15, 0, 0, DateTimeKind.Utc)
        ' Saturday 2026-01-17 — CME closed all day.
        Private Shared ReadOnly SaturdayUtc As New DateTime(2026, 1, 17, 12, 0, 0, DateTimeKind.Utc)

        Private Class Harness
            Public Config As New VwapMeanReversionConfig()
            Public Detector As New FakeDetector()
            Public Entry As New FakeEntryExecution()
            Public ExitSvc As New FakeExitExecution()
            Public Orders As New FakeOrderService()
            Public Feed As New FakeQuoteFeed()
            Public Guard As New FakeGuard()
            Public Session As New FakeSession()
            Public Orchestrator As VwapMeanReversionOrchestrator

            Public Sub New()
                Orchestrator = New VwapMeanReversionOrchestrator(
                    scopeFactory:=New FakeScopeFactory(Detector, Config),
                    session:=Session,
                    entryExecution:=Entry,
                    exitExecution:=ExitSvc,
                    orderService:=Orders,
                    marketHub:=Feed,
                    logger:=NullLogger(Of VwapMeanReversionOrchestrator).Instance,
                    dailyLossGuard:=Guard)
                Orchestrator.UtcNowProvider = Function() OpenUtc
            End Sub
        End Class

        Private Shared Function SignalEval(Optional symbol As String = "MES") As VwapMeanReversionEvaluation
            Return New VwapMeanReversionEvaluation With {
                .Symbol = symbol,
                .AsOf = New DateTimeOffset(OpenUtc).AddMinutes(-5),
                .Signal = VwapMeanReversionSignalSide.Bullish,
                .LastClose = 5000D,
                .Vwap = 5002D,
                .Sd = 1D,
                .DeviationSd = -2.5,
                .Adx = 10.0,
                .Atr = 2D,
                .SuggestedInitialStopPrice = 4998D,
                .T1Price = 5002D,
                .T2Price = 5003D,
                .ConfirmationCandle = True,
                .ConfirmationPattern = "Reversal",
                .BeyondEntryBand = True,
                .AdxVetoPassed = True,
                .InEntryWindow = True,
                .IsWarm = True,
                .BarsAvailable = 31
            }
        End Function

        ' ─── Start / stop ──────────────────────────────────────────────────────

        <Fact>
        Public Sub EnableDisable_TogglesStateAndRaisesEvents()
            Dim h As New Harness()
            Dim transitions As New List(Of Boolean)
            AddHandler h.Orchestrator.EnabledChanged, Sub(s, enabled) transitions.Add(enabled)

            Assert.False(h.Orchestrator.IsEnabled)   ' off by default — explicit opt-in
            h.Orchestrator.Enable()
            Assert.True(h.Orchestrator.IsEnabled)
            h.Orchestrator.Enable()                  ' idempotent — no duplicate event
            h.Orchestrator.Disable()
            Assert.False(h.Orchestrator.IsEnabled)

            Assert.Equal({True, False}, transitions)
        End Sub

        ' ─── Closed-contract skip ──────────────────────────────────────────────

        <Fact>
        Public Async Function ClosedContract_SkipsDetectorEntirely() As Task
            Dim h As New Harness()
            h.Orchestrator.UtcNowProvider = Function() SaturdayUtc
            h.Orchestrator.Enable()

            Await h.Orchestrator.ScanAllSymbolsAsync(CancellationToken.None)

            Assert.Equal(0, h.Detector.Calls)
        End Function

        <Fact>
        Public Async Function OpenContract_EvaluatesEveryWatchlistSymbol() As Task
            Dim h As New Harness()
            h.Orchestrator.Enable()

            Await h.Orchestrator.ScanAllSymbolsAsync(CancellationToken.None)

            Assert.Equal(VwapMeanReversionOrchestrator.WatchlistSymbols.Count, h.Detector.Calls)
        End Function

        ' ─── Daily-loss-guard suppression ──────────────────────────────────────

        <Fact>
        Public Sub GuardHalted_SuppressesEntry_ButStillRaisesSignalDetected()
            Dim h As New Harness()
            h.Guard.AllowEntries = False
            Dim signalSeen As Boolean = False
            AddHandler h.Orchestrator.SignalDetected, Sub(s, e) signalSeen = True
            h.Orchestrator.Enable()

            h.Orchestrator.DispatchEvaluation(SignalEval(), h.Config)

            Assert.True(signalSeen)
            Assert.Equal(0, h.Entry.PlaceCalls)
            Assert.False(h.Orchestrator.IsInPosition)
        End Sub

        ' ─── Full entry-to-exit cycle ──────────────────────────────────────────

        <Fact>
        Public Sub FullCycle_Entry_T1HalfClose_T2Exit()
            Dim h As New Harness()
            h.Orchestrator.Enable()
            Dim startTask = h.Orchestrator.StartAsync(CancellationToken.None)   ' attaches quote handler

            ' Entry: fires synchronously because every fake completes synchronously.
            h.Orchestrator.DispatchEvaluation(SignalEval(), h.Config)
            Assert.Equal(1, h.Entry.PlaceCalls)
            Assert.True(h.Orchestrator.IsInPosition)
            Assert.Equal("VwapMeanReversion", h.Entry.LastRequest.StrategyName)

            ' Sizing: balance 50 000 × 0.5% = 250 risk; stopDist 2 × pointValue 5 = 10 → 25 contracts.
            Assert.Equal(25, h.Entry.LastRequest.Slot.Contracts)

            Dim contractId = FavouriteContracts.TryGetBySymbolResolved("MES").PxContractId

            ' Quote at T1 (VWAP): half-close via PartialCloseContractAsync (fires on a worker task).
            h.Feed.Raise(contractId, 5002D)
            Dim deadline = DateTime.UtcNow.AddSeconds(5)
            While h.Orders.PartialCloseCalls = 0 AndAlso DateTime.UtcNow < deadline
                Thread.Sleep(10)
            End While
            Assert.Equal(1, h.Orders.PartialCloseCalls)
            Assert.Equal(12, h.Orders.LastPartialCloseSize)   ' floor(25 × 50%)
            Assert.True(h.Orchestrator.IsInPosition)          ' runner still open

            ' Quote at T2: full close through the exit pipeline; slot released.
            h.Feed.Raise(contractId, 5003D)
            deadline = DateTime.UtcNow.AddSeconds(5)
            While h.Orchestrator.IsInPosition AndAlso DateTime.UtcNow < deadline
                Thread.Sleep(10)
            End While
            Assert.False(h.Orchestrator.IsInPosition)
            Assert.Contains("T2Target", h.ExitSvc.Reasons)
        End Sub

        <Fact>
        Public Sub SecondSignalWhileInPosition_IsDropped()
            Dim h As New Harness()
            h.Orchestrator.Enable()

            h.Orchestrator.DispatchEvaluation(SignalEval(), h.Config)
            Assert.Equal(1, h.Entry.PlaceCalls)

            Dim second = SignalEval("MNQ")
            second.AsOf = second.AsOf.AddMinutes(5)
            h.Orchestrator.DispatchEvaluation(second, h.Config)

            Assert.Equal(1, h.Entry.PlaceCalls)   ' no DCA — single-position guard
        End Sub

        ' ─── Force-flat window math ────────────────────────────────────────────

        <Fact>
        Public Sub ForceFlatWindow_Math()
            Assert.False(VwapMeanReversionOrchestrator.IsAtOrPastForceFlat(
                New DateTime(2026, 1, 13, 21, 4, 0, DateTimeKind.Utc), "2105"))
            Assert.True(VwapMeanReversionOrchestrator.IsAtOrPastForceFlat(
                New DateTime(2026, 1, 13, 21, 5, 0, DateTimeKind.Utc), "2105"))
            Assert.False(VwapMeanReversionOrchestrator.IsAtOrPastForceFlat(
                New DateTime(2026, 1, 13, 22, 0, 0, DateTimeKind.Utc), "2105"))   ' new session
            Assert.False(VwapMeanReversionOrchestrator.IsAtOrPastForceFlat(
                New DateTime(2026, 1, 13, 21, 30, 0, DateTimeKind.Utc), ""))      ' disabled
        End Sub

        ' ═══ Fakes ═════════════════════════════════════════════════════════════

        Private Class FakeDetector
            Implements IVwapMeanReversionSignalDetector

            Public Property Calls As Integer
            Public Property Result As VwapMeanReversionEvaluation

            Public Function EvaluateAsync(symbol As String, ct As CancellationToken) _
                As Task(Of VwapMeanReversionEvaluation) Implements IVwapMeanReversionSignalDetector.EvaluateAsync
                Calls += 1
                Return Task.FromResult(If(Result, New VwapMeanReversionEvaluation With {.Symbol = symbol}))
            End Function
        End Class

        Private Class FakeEntryExecution
            Implements IEntryExecutionService

            Public Property PlaceCalls As Integer
            Public Property LastRequest As EntryExecutionRequest

            Public Function PlaceAsync(request As EntryExecutionRequest, ct As CancellationToken) _
                As Task(Of EntryExecutionResult) Implements IEntryExecutionService.PlaceAsync
                PlaceCalls += 1
                LastRequest = request
                ' Simulate a successful bracket placement + fill callback.
                Dim slot = request.Slot
                slot.EntryPrice = request.LastClose
                slot.PositionId = 42L
                slot.IsOpen = True
                request.OnSlotEntered?.Invoke(slot)
                Return Task.FromResult(New EntryExecutionResult With {.Success = True, .PlacedPositionId = 42L})
            End Function

            Public Function IsAiSuppressed(contractSymbol As String) As Boolean _
                Implements IEntryExecutionService.IsAiSuppressed
                Return False
            End Function
        End Class

        Private Class FakeExitExecution
            Implements IExitExecutionService

            Public ReadOnly Reasons As New List(Of String)

            Public Function CloseAsync(slot As PositionSlot, exitReason As String, trigger As String,
                                       timeframeMinutes As Integer, ct As CancellationToken) _
                As Task(Of ExitExecutionResult) Implements IExitExecutionService.CloseAsync
                SyncLock Reasons
                    Reasons.Add(exitReason)
                End SyncLock
                Return Task.FromResult(New ExitExecutionResult With {.Released = True})
            End Function
        End Class

        Private Class FakeQuoteFeed
            Implements IMarketQuoteFeed

            Public Event QuoteReceived As EventHandler(Of MarketQuoteEventArgs) _
                Implements IMarketQuoteFeed.QuoteReceived

            Public Sub Raise(contractId As String, price As Decimal)
                RaiseEvent QuoteReceived(Me, New MarketQuoteEventArgs(New Quote With {
                    .ContractId = contractId, .LastPrice = price,
                    .BidPrice = price, .AskPrice = price
                }))
            End Sub

            Public Function SubscribeContractAsync(contractId As String,
                                                   Optional cancel As CancellationToken = Nothing) As Task _
                Implements IMarketQuoteFeed.SubscribeContractAsync
                Return Task.CompletedTask
            End Function

            Public Function UnsubscribeContractAsync(contractId As String,
                                                     Optional cancel As CancellationToken = Nothing) As Task _
                Implements IMarketQuoteFeed.UnsubscribeContractAsync
                Return Task.CompletedTask
            End Function
        End Class

        Private Class FakeGuard
            Implements IDailyLossGuard

            Public Property AllowEntries As Boolean = True

            Public Event Halted As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.Halted
            Public Event Released As EventHandler Implements IDailyLossGuard.Released
            Public Event ForceFlattened As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.ForceFlattened

            Public Function GetState() As DailyLossGuardState Implements IDailyLossGuard.GetState
                Return New DailyLossGuardState With {.IsHalted = Not AllowEntries}
            End Function

            Public Function CanEnterNewTrade() As Boolean Implements IDailyLossGuard.CanEnterNewTrade
                Return AllowEntries
            End Function

            Public Function EvaluateAsync() As Task(Of DailyLossGuardState) Implements IDailyLossGuard.EvaluateAsync
                Return Task.FromResult(GetState())
            End Function

            Public Function ResetAsync(reason As String) As Task Implements IDailyLossGuard.ResetAsync
                Return Task.CompletedTask
            End Function

            Public Sub RegisterOpenSlotPnlSource(source As IOpenSlotPnlSource) _
                Implements IDailyLossGuard.RegisterOpenSlotPnlSource
            End Sub

            Public Sub UnregisterOpenSlotPnlSource(source As IOpenSlotPnlSource) _
                Implements IDailyLossGuard.UnregisterOpenSlotPnlSource
            End Sub
        End Class

        Private Class FakeSession
            Implements ITradingSessionContext

            Private _account As New Account With {.Id = 7L, .Balance = 50000D}

            Public Event AccountChanged As EventHandler(Of Account) Implements ITradingSessionContext.AccountChanged
            Public Event AutoExecutionChanged As EventHandler Implements ITradingSessionContext.AutoExecutionChanged

            Public ReadOnly Property SelectedAccount As Account Implements ITradingSessionContext.SelectedAccount
                Get
                    Return _account
                End Get
            End Property

            Public ReadOnly Property ActiveBroker As BrokerType Implements ITradingSessionContext.ActiveBroker
                Get
                    Return CType(0, BrokerType)
                End Get
            End Property

            Public ReadOnly Property AutoExecutionEnabled As Boolean Implements ITradingSessionContext.AutoExecutionEnabled
                Get
                    Return True
                End Get
            End Property

            Public Sub SelectAccount(account As Account) Implements ITradingSessionContext.SelectAccount
                _account = account
            End Sub

            Public Sub SetAutoExecution(enabled As Boolean) Implements ITradingSessionContext.SetAutoExecution
            End Sub
        End Class

        Private Class FakeOrderService
            Implements IOrderService

            Public Property PartialCloseCalls As Integer
            Public Property LastPartialCloseSize As Integer

            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

            Public Function PlaceOrderAsync(order As Order) As Task(Of Order) Implements IOrderService.PlaceOrderAsync
                Return Task.FromResult(order)
            End Function

            Public Function CancelOrderAsync(orderId As Long) As Task(Of Boolean) Implements IOrderService.CancelOrderAsync
                Return Task.FromResult(True)
            End Function

            Public Function CancelAllOpenOrdersAsync() As Task Implements IOrderService.CancelAllOpenOrdersAsync
                Return Task.CompletedTask
            End Function

            Public Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order)) _
                Implements IOrderService.GetOpenOrdersAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function

            Public Function GetOrderHistoryAsync(accountId As Long, from As DateTime, [to] As DateTime) _
                As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function

            Public Function TryGetOrderFillPriceAsync(externalOrderId As Long, accountId As Long,
                                                      Optional cancel As CancellationToken = Nothing) _
                As Task(Of Decimal?) Implements IOrderService.TryGetOrderFillPriceAsync
                Return Task.FromResult(Of Decimal?)(Nothing)
            End Function

            Public Function TryGetBracketStopPriceAsync(accountId As Long, contractId As String,
                                                        Optional cancel As CancellationToken = Nothing) _
                As Task(Of Decimal?) Implements IOrderService.TryGetBracketStopPriceAsync
                Return Task.FromResult(Of Decimal?)(Nothing)
            End Function

            Public Function GetLiveWorkingOrdersAsync(accountId As Long, contractId As String,
                                                      Optional cancel As CancellationToken = Nothing) _
                As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetLiveWorkingOrdersAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function

            Public Function GetLivePositionSnapshotAsync(accountId As Long, contractId As String,
                                                         Optional positionId As Long? = Nothing,
                                                         Optional bypassCache As Boolean = False,
                                                         Optional cancel As CancellationToken = Nothing) _
                As Task(Of LivePositionSnapshot) Implements IOrderService.GetLivePositionSnapshotAsync
                Return Task.FromResult(Of LivePositionSnapshot)(Nothing)
            End Function

            Public Function GetOpenPositionsAsync(accountId As Long,
                                                  Optional cancel As CancellationToken = Nothing) _
                As Task(Of IEnumerable(Of LivePositionSnapshot)) Implements IOrderService.GetOpenPositionsAsync
                Return Task.FromResult(Of IEnumerable(Of LivePositionSnapshot))(Array.Empty(Of LivePositionSnapshot)())
            End Function

            Public Function FlattenContractAsync(accountId As Long, contractId As String,
                                                 Optional cancel As CancellationToken = Nothing) _
                As Task(Of Boolean) Implements IOrderService.FlattenContractAsync
                Return Task.FromResult(True)
            End Function

            Public Function FlattenContractWithFillAsync(accountId As Long, contractId As String,
                                                         Optional cancel As CancellationToken = Nothing) _
                As Task(Of (Success As Boolean, Fill As BrokerCloseFill)) _
                Implements IOrderService.FlattenContractWithFillAsync
                Return Task.FromResult(Of (Success As Boolean, Fill As BrokerCloseFill))((True, Nothing))
            End Function

            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?,
                                                  Optional enableTsl As Boolean = False,
                                                  Optional cancel As CancellationToken = Nothing) _
                As Task(Of Boolean) Implements IOrderService.EditPositionSlTpAsync
                Return Task.FromResult(True)
            End Function

            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer,
                                                      Optional cancel As CancellationToken = Nothing) _
                As Task(Of Boolean) Implements IOrderService.PartialCloseContractAsync
                LastPartialCloseSize = size
                PartialCloseCalls += 1
                Return Task.FromResult(True)
            End Function
        End Class

        Private Class FakeScopeFactory
            Implements IServiceScopeFactory

            Private ReadOnly _detector As IVwapMeanReversionSignalDetector
            Private ReadOnly _config As VwapMeanReversionConfig

            Public Sub New(detector As IVwapMeanReversionSignalDetector, config As VwapMeanReversionConfig)
                _detector = detector
                _config = config
            End Sub

            Public Function CreateScope() As IServiceScope Implements IServiceScopeFactory.CreateScope
                Return New FakeScope(_detector, _config)
            End Function

            Private Class FakeScope
                Implements IServiceScope

                Private ReadOnly _provider As FakeProvider

                Public Sub New(detector As IVwapMeanReversionSignalDetector, config As VwapMeanReversionConfig)
                    _provider = New FakeProvider(detector, config)
                End Sub

                Public ReadOnly Property ServiceProvider As IServiceProvider Implements IServiceScope.ServiceProvider
                    Get
                        Return _provider
                    End Get
                End Property

                Public Sub Dispose() Implements IDisposable.Dispose
                End Sub
            End Class

            Private Class FakeProvider
                Implements IServiceProvider

                Private ReadOnly _detector As IVwapMeanReversionSignalDetector
                Private ReadOnly _config As VwapMeanReversionConfig

                Public Sub New(detector As IVwapMeanReversionSignalDetector, config As VwapMeanReversionConfig)
                    _detector = detector
                    _config = config
                End Sub

                Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
                    If serviceType Is GetType(IVwapMeanReversionSignalDetector) Then Return _detector
                    If serviceType Is GetType(VwapMeanReversionConfig) Then Return _config
                    Return Nothing
                End Function
            End Class
        End Class

    End Class

End Namespace
