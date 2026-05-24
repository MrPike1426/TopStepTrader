Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trading

    ''' <summary>
    ''' BUG-93 F3a: regression tests for the SignalR <c>GatewayUserOrder</c> → <c>OrderFilled</c>
    ''' bridge inside <see cref="ProjectXOrderService"/>. The bridge state is exercised via
    ''' Friend Shared seams (<c>TryAcceptFillEvent</c>, <c>BuildFilledOrderForTestingAsync</c>,
    ''' <c>ResolvePositionIdForFillAsync</c>) so the test does not need to construct the full
    ''' service — <c>PXOrderClient</c> is concrete and not test-fakeable.
    ''' </summary>
    Public Class ProjectXOrderServiceFillBridgeTests

        Private Const ContractId As String = "CON.F.US.MGC.M26"
        Private Const AccountId As Long = 555L
        Private Const OrderId As Long = 3014320453L

        Private Shared Function MakeFillPush(orderId As Long,
                                              Optional status As Integer = 2,
                                              Optional fillPrice As Double? = 2050.4,
                                              Optional side As Integer = 1,
                                              Optional size As Integer = 1,
                                              Optional contract As String = ContractId) As PXUserOrderData
            Return New PXUserOrderData With {
                .Id = orderId,
                .AccountId = AccountId,
                .ContractId = contract,
                .Side = side,
                .OrderType = 4,
                .Size = size,
                .StopPrice = 2050D,
                .AvgFillPrice = fillPrice,
                .Status = status,
                .CreationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        End Function

        Private Shared Function MakePosition(id As Long,
                                              contract As String,
                                              Optional size As Integer = 1,
                                              Optional positionType As Integer = 2) As PXPositionDto
            Return New PXPositionDto With {
                .Id = id,
                .AccountId = AccountId,
                .ContractId = contract,
                .Size = size,
                .PositionType = positionType,   ' 1=long, 2=short
                .AveragePrice = 2050.4
            }
        End Function

        ' ─── TryAcceptFillEvent (status filter + dedup) ────────────────────

        <Fact>
        Public Sub OrderFilled_Raised_When_HubReportsFilled()
            ' Status=2 (Filled) for a never-seen order id is accepted.
            Dim dedup As New ConcurrentDictionary(Of Long, Boolean)()
            Assert.True(ProjectXOrderService.TryAcceptFillEvent(2, OrderId, dedup))
            Assert.True(dedup.ContainsKey(OrderId))
        End Sub

        <Fact>
        Public Sub OrderFilled_NotRaised_For_NonFilledStatus()
            ' Working (1), Cancelled (3), Rejected (4), Pending (6), unknown (99) must all be ignored.
            Dim dedup As New ConcurrentDictionary(Of Long, Boolean)()
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(1, OrderId, dedup))
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(3, OrderId, dedup))
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(4, OrderId, dedup))
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(6, OrderId, dedup))
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(99, OrderId, dedup))
            Assert.Empty(dedup)
        End Sub

        <Fact>
        Public Sub OrderFilled_Deduplicated_Within_SameOrderId()
            ' GatewayUserOrder may push multiple Filled events for the same order id (Working→Filled
            ' transitions, AvgFillPrice refreshes on partial-fill events). Only the first wins.
            Dim dedup As New ConcurrentDictionary(Of Long, Boolean)()
            Assert.True(ProjectXOrderService.TryAcceptFillEvent(2, OrderId, dedup))
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(2, OrderId, dedup))
            Assert.False(ProjectXOrderService.TryAcceptFillEvent(2, OrderId, dedup))
            ' A different order id must still be accepted.
            Assert.True(ProjectXOrderService.TryAcceptFillEvent(2, OrderId + 1L, dedup))
        End Sub

        ' ─── BuildFilledOrderForTestingAsync (DTO conversion + position resolution) ──

        <Fact>
        Public Async Function BuildFilledOrder_PopulatesPositionId_From_Position_Search() As Task
            Dim push = MakeFillPush(OrderId, status:=2, fillPrice:=2050.4, side:=1)
            Dim positionSearch As Func(Of Long, CancellationToken, Task(Of PXPositionSearchResponse)) =
                Function(acc, ct)
                    Assert.Equal(AccountId, acc)
                    Return Task.FromResult(New PXPositionSearchResponse With {
                        .Success = True,
                        .Positions = New List(Of PXPositionDto) From {MakePosition(9999L, ContractId)}
                    })
                End Function

            Dim order = Await ProjectXOrderService.BuildFilledOrderForTestingAsync(
                push, AccountId, positionSearch, NullLogger.Instance,
                retries:=3, retryDelay:=TimeSpan.FromMilliseconds(5))

            Assert.Equal(OrderId, order.ExternalOrderId.Value)
            Assert.Equal(ContractId, order.ContractId)
            Assert.Equal(OrderSide.Sell, order.Side)
            Assert.Equal(1, order.Quantity)
            Assert.Equal(2050.4D, order.FillPrice.Value)
            Assert.Equal(OrderStatus.Filled, order.Status)
            Assert.True(order.ExternalPositionId.HasValue)
            Assert.Equal(9999L, order.ExternalPositionId.Value)
        End Function

        <Fact>
        Public Async Function BuildFilledOrder_PopulatesNothingPositionId_OnLookupTimeout() As Task
            Dim push = MakeFillPush(OrderId, status:=2)
            Dim attemptCount = 0
            Dim positionSearch As Func(Of Long, CancellationToken, Task(Of PXPositionSearchResponse)) =
                Function(acc, ct)
                    attemptCount += 1
                    ' Always empty — simulates the broker not yet reporting the new position.
                    Return Task.FromResult(New PXPositionSearchResponse With {
                        .Success = True,
                        .Positions = New List(Of PXPositionDto)()
                    })
                End Function

            Dim order = Await ProjectXOrderService.BuildFilledOrderForTestingAsync(
                push, AccountId, positionSearch, NullLogger.Instance,
                retries:=5, retryDelay:=TimeSpan.FromMilliseconds(5))

            Assert.Equal(5, attemptCount)
            Assert.False(order.ExternalPositionId.HasValue)
            ' Even with no position id resolved, the Order itself is populated so the consumer
            ' (BrokerFillTradeLogger / strategy bridge) can still react to the fill.
            Assert.Equal(OrderId, order.ExternalOrderId.Value)
            Assert.Equal(OrderStatus.Filled, order.Status)
        End Function

        <Fact>
        Public Async Function ResolvePositionId_SkipsZeroNetPosRows() As Task
            ' Working bracket orders show up on /api/Position/searchOpen with size=0 / type=0.
            ' The resolver must ignore them so we don't return a bracket-order positionId.
            Dim flatRow As New PXPositionDto With {
                .Id = 1L,
                .AccountId = AccountId,
                .ContractId = ContractId,
                .Size = 0,
                .PositionType = 0,
                .AveragePrice = 0
            }
            Dim realPosition = MakePosition(7777L, ContractId)
            Dim positionSearch As Func(Of Long, CancellationToken, Task(Of PXPositionSearchResponse)) =
                Function(acc, ct) Task.FromResult(New PXPositionSearchResponse With {
                    .Success = True,
                    .Positions = New List(Of PXPositionDto) From {flatRow, realPosition}
                })

            Dim posId = Await ProjectXOrderService.ResolvePositionIdForFillAsync(
                ContractId, AccountId, positionSearch, NullLogger.Instance,
                retries:=1, retryDelay:=TimeSpan.FromMilliseconds(1))

            Assert.True(posId.HasValue)
            Assert.Equal(7777L, posId.Value)
        End Function

        <Fact>
        Public Async Function ResolvePositionId_RetriesOnException_ThenSucceeds() As Task
            Dim calls = 0
            Dim positionSearch As Func(Of Long, CancellationToken, Task(Of PXPositionSearchResponse)) =
                Function(acc, ct)
                    calls += 1
                    If calls = 1 Then Throw New InvalidOperationException("transient")
                    Return Task.FromResult(New PXPositionSearchResponse With {
                        .Success = True,
                        .Positions = New List(Of PXPositionDto) From {MakePosition(4242L, ContractId)}
                    })
                End Function

            Dim posId = Await ProjectXOrderService.ResolvePositionIdForFillAsync(
                ContractId, AccountId, positionSearch, NullLogger.Instance,
                retries:=3, retryDelay:=TimeSpan.FromMilliseconds(5))

            Assert.True(posId.HasValue)
            Assert.Equal(4242L, posId.Value)
            Assert.Equal(2, calls)
        End Function

    End Class

End Namespace
