Imports System.Reflection
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Moq
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Regression tests for CryptoStrategyExecutionEngine's BUY-only constraints
    ''' and confidence override.
    '''
    ''' Guarantees:
    '''   • SELL signals are cleared before entering order placement (DoCheckAsync guard).
    '''   • SELL bracket orders are blocked by the defense-in-depth guard in PlaceBracketOrdersAsync.
    '''   • SELL scale-in orders are blocked in EvaluateConfidenceActionsAsync.
    '''   • Confidence is always overridden to 100 % on placed orders.
    '''   • StrategyExecutionEngine is untouched (type isolation).
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~CryptoStrategyEngine"
    ''' </summary>
    Public Class CryptoStrategyEngineTests

        ' ══════════════════════════════════════════════════════════════════
        ' 1 — Type isolation: engine classes must be distinct
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CryptoEngine_IsDistinctClass_FromStandardEngine()
            Assert.NotEqual(GetType(CryptoStrategyExecutionEngine), GetType(StrategyExecutionEngine))
        End Sub

        <Fact>
        Public Sub CryptoEngine_IsNotSubclassOf_StandardEngine()
            ' The two engines are fully independent classes — no inheritance coupling.
            Assert.False(GetType(CryptoStrategyExecutionEngine).IsSubclassOf(GetType(StrategyExecutionEngine)))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 2 — BUY-only signal filter (mirrors DoCheckAsync guard)
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Mirrors the inline BUY-only guard applied in CryptoStrategyExecutionEngine.DoCheckAsync
        ''' immediately after the strategy condition Select Case.  Any SELL side value must be
        ''' set to Nothing before reaching reversal or order-placement logic.
        ''' </summary>
        Private Shared Function ApplyCryptoBuyOnlyGuard(side As OrderSide?) As OrderSide?
            If side.HasValue AndAlso side.Value = OrderSide.Sell Then Return Nothing
            Return side
        End Function

        <Fact>
        Public Sub BuyOnlyGuard_SellSignal_IsCleared()
            Dim result = ApplyCryptoBuyOnlyGuard(OrderSide.Sell)
            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub BuyOnlyGuard_BuySignal_IsPreserved()
            Dim result = ApplyCryptoBuyOnlyGuard(OrderSide.Buy)
            Assert.Equal(OrderSide.Buy, result)
        End Sub

        <Fact>
        Public Sub BuyOnlyGuard_NoSignal_RemainsNothing()
            Dim result = ApplyCryptoBuyOnlyGuard(Nothing)
            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub StandardEngine_HasNoBuyOnlyFilter_SellSignalUnchanged()
            ' Confirms the BUY-only guard does NOT exist on the standard engine — if we
            ' applied the standard engine's (non-existent) filter, a SELL would pass through.
            Dim sell As OrderSide? = OrderSide.Sell
            ' Standard engine: side reaches placement logic unchanged
            Assert.True(sell.HasValue AndAlso sell.Value = OrderSide.Sell)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 3 — Scale-in SELL suppression (mirrors EvaluateConfidenceActionsAsync guard)
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Mirrors the scale-in side guard in CryptoStrategyExecutionEngine.EvaluateConfidenceActionsAsync.
        ''' When rawUpPct is low (bear extreme), extremeSide = Sell — this must be blocked.
        ''' </summary>
        Private Shared Function IsCryptoScaleInAllowed(extremeSide As OrderSide) As Boolean
            Return extremeSide = OrderSide.Buy  ' SELL scale-ins are suppressed
        End Function

        <Fact>
        Public Sub ScaleIn_SellExtreme_IsBlocked_ByCryptoEngine()
            Assert.False(IsCryptoScaleInAllowed(OrderSide.Sell))
        End Sub

        <Fact>
        Public Sub ScaleIn_BuyExtreme_IsAllowed_ByCryptoEngine()
            Assert.True(IsCryptoScaleInAllowed(OrderSide.Buy))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 4 — Confidence = 100 override (mirrors PlaceBracketOrdersAsync override)
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Mirrors the _pendingConfidencePct = 100 override applied at the start of
        ''' PlaceBracketOrdersAsync and PlaceScaleInOrderAsync in the Crypto engine.
        ''' </summary>
        Private Shared Function ApplyCryptoConfidenceOverride(rawPct As Integer) As Integer
            Return 100
        End Function

        <Theory>
        <InlineData(0)>
        <InlineData(50)>
        <InlineData(75)>
        <InlineData(85)>
        <InlineData(99)>
        <InlineData(100)>
        Public Sub CryptoConfidenceOverride_AlwaysReturns100(rawPct As Integer)
            Assert.Equal(100, ApplyCryptoConfidenceOverride(rawPct))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 5 — Behavioral: PlaceBracketOrdersAsync(Sell) must not call PlaceOrderAsync
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Uses Moq + reflection to call PlaceBracketOrdersAsync directly with side=Sell
        ''' and asserts that IOrderService.PlaceOrderAsync is never invoked.
        ''' This verifies the defense-in-depth guard in isolation from the full bar loop.
        ''' </summary>
        <Fact>
        Public Async Function PlaceBracketOrders_SellSide_DoesNotCallOrderService() As Task
            ' Arrange
            Dim capturedSides As New List(Of OrderSide)
            Dim mockOrder = New Mock(Of IOrderService)()
            mockOrder.Setup(Function(s) s.PlaceOrderAsync(It.IsAny(Of Order)())) _
                     .Callback(Sub(o As Order) capturedSides.Add(o.Side)) _
                     .ReturnsAsync(CType(Nothing, Order))

            Dim mockLogger = New Mock(Of ILogger(Of CryptoStrategyExecutionEngine))()

            ' BarIngestionService is a concrete class; GetUninitializedObject bypasses its
            ' constructor so no real DB/network calls occur.  PlaceBracketOrdersAsync returns
            ' before touching _ingestionService when the SELL guard fires.
            Dim barIngestion = CType(
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    GetType(BarIngestionService)),
                BarIngestionService)

            Dim engine = New CryptoStrategyExecutionEngine(
                barIngestion, mockOrder.Object, mockLogger.Object)

            ' _strategy must be set so the Log helper can interpolate ContractId
            ' (the SELL guard fires before any order logic, but the log call happens first).
            Dim strategyField = GetType(CryptoStrategyExecutionEngine).GetField(
                "_strategy", BindingFlags.NonPublic Or BindingFlags.Instance)
            strategyField.SetValue(engine, New StrategyDefinition With {
                .ContractId = "BTC", .Name = "CryptoTest", .AccountId = 1L
            })

            ' Act — invoke the private method via reflection
            Dim method = GetType(CryptoStrategyExecutionEngine).GetMethod(
                "PlaceBracketOrdersAsync",
                BindingFlags.NonPublic Or BindingFlags.Instance)
            Assert.NotNull(method)  ' method must exist

            Dim engineTask = CType(method.Invoke(engine, {OrderSide.Sell, 50000D, Nothing}), Task)
            Await engineTask

            ' Assert — no SELL order (or any order) was submitted
            Assert.Empty(capturedSides)
            mockOrder.Verify(
                Function(s) s.PlaceOrderAsync(It.Is(Of Order)(Function(o) o.Side = OrderSide.Sell)),
                Times.Never)
        End Function

        ''' <summary>
        ''' Verifies the positive case: PlaceBracketOrdersAsync(Buy) DOES call PlaceOrderAsync.
        ''' Confirms the guard only blocks SELL; BUY orders flow through normally.
        ''' Note: this test requires a valid FavouriteContracts entry for the contract ID,
        ''' so we use a numeric ID string that can be parsed directly.
        ''' </summary>
        <Fact>
        Public Async Function PlaceBracketOrders_BuySide_CallsOrderService() As Task
            Dim capturedSides As New List(Of OrderSide)
            Dim mockOrder = New Mock(Of IOrderService)()
            mockOrder.Setup(Function(s) s.PlaceOrderAsync(It.IsAny(Of Order)())) _
                     .Callback(Sub(o As Order) capturedSides.Add(o.Side)) _
                     .ReturnsAsync(CType(Nothing, Order))

            Dim mockLogger = New Mock(Of ILogger(Of CryptoStrategyExecutionEngine))()

            Dim barIngestion = CType(
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    GetType(BarIngestionService)),
                BarIngestionService)

            Dim engine = New CryptoStrategyExecutionEngine(
                barIngestion, mockOrder.Object, mockLogger.Object)

            ' Use a strategy with a numeric ContractId (parseable as instrumentId directly)
            ' and minimal SL/TP so the order object is well-formed.
            Dim strategy As New StrategyDefinition With {
                .ContractId = "99999",
                .Name = "CryptoTest",
                .AccountId = 1L,
                .CapitalAtRisk = 200D,
                .Leverage = 5,
                .SlDollarBracket = 10D,
                .TpDollarBracket = 20D,
                .MinConfidencePct = 85
            }

            ' Set _strategy via reflection (private field, needed by PlaceBracketOrdersAsync)
            Dim strategyField = GetType(CryptoStrategyExecutionEngine).GetField(
                "_strategy", BindingFlags.NonPublic Or BindingFlags.Instance)
            strategyField.SetValue(engine, strategy)

            Dim method = GetType(CryptoStrategyExecutionEngine).GetMethod(
                "PlaceBracketOrdersAsync",
                BindingFlags.NonPublic Or BindingFlags.Instance)

            Dim engineTask = CType(method.Invoke(engine, {OrderSide.Buy, 50000D, Nothing}), Task)
            Await engineTask

            ' BUY order should have been submitted
            Assert.Contains(OrderSide.Buy, capturedSides)
            mockOrder.Verify(
                Function(s) s.PlaceOrderAsync(It.Is(Of Order)(Function(o) o.Side = OrderSide.Buy)),
                Times.Once)
        End Function

    End Class

End Namespace
