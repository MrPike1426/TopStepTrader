Imports Moq
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' ARCH-07 Bug A regression coverage for
    ''' <see cref="ScalperTestViewModel.ResolveSeedEntryFromBrokerAsync"/>.
    ''' The pre-fix VM seeded the entry from <c>placed.FillPrice</c>, which
    ''' TopStepX REST always returns as null on PlaceOrder — leading to a stuck
    ''' <c>$0.00</c> P&amp;L. The retry mirrors the engine's <c>_syncMissCount</c>
    ''' pattern.
    ''' </summary>
    Public Class ScalperTestViewModelTests

        Private Shared Function CreateVm(orderSvc As Mock(Of IOrderService)) As ScalperTestViewModel
            Dim accountSvc As New Mock(Of IAccountService)()
            Dim session As New Mock(Of ITradingSessionContext)()
            Dim livePnL As New Mock(Of ILivePnLService)()
            Return New ScalperTestViewModel(orderSvc.Object, accountSvc.Object, session.Object, livePnL.Object)
        End Function

        <Fact>
        Public Async Function ResolveSeedEntry_NullTwiceThenOpenRate_ReturnsThirdValue() As Task
            Dim orderSvc As New Mock(Of IOrderService)()
            orderSvc.SetupSequence(Function(o) o.GetLivePositionSnapshotAsync(
                                       It.IsAny(Of Long), It.IsAny(Of String),
                                       It.IsAny(Of Long?), It.IsAny(Of System.Threading.CancellationToken))).
                     ReturnsAsync(CType(Nothing, LivePositionSnapshot)).
                     ReturnsAsync(CType(Nothing, LivePositionSnapshot)).
                     ReturnsAsync(New LivePositionSnapshot With {.OpenRate = 2350.5D, .IsBuy = True, .Units = 1})

            Dim vm = CreateVm(orderSvc)
            Dim seed = Await vm.ResolveSeedEntryFromBrokerAsync(123L, "CON.F.US.MGC.M26", TimeSpan.Zero)

            Assert.Equal(2350.5D, seed)
            orderSvc.Verify(Function(o) o.GetLivePositionSnapshotAsync(
                                It.IsAny(Of Long), It.IsAny(Of String),
                                It.IsAny(Of Long?), It.IsAny(Of System.Threading.CancellationToken)),
                            Times.Exactly(3))
        End Function

        <Fact>
        Public Async Function ResolveSeedEntry_AllAttemptsNull_ReturnsZero() As Task
            Dim orderSvc As New Mock(Of IOrderService)()
            orderSvc.Setup(Function(o) o.GetLivePositionSnapshotAsync(
                                It.IsAny(Of Long), It.IsAny(Of String),
                                It.IsAny(Of Long?), It.IsAny(Of System.Threading.CancellationToken))).
                     ReturnsAsync(CType(Nothing, LivePositionSnapshot))

            Dim vm = CreateVm(orderSvc)
            Dim seed = Await vm.ResolveSeedEntryFromBrokerAsync(123L, "CON.F.US.MGC.M26", TimeSpan.Zero)

            Assert.Equal(0D, seed)
            orderSvc.Verify(Function(o) o.GetLivePositionSnapshotAsync(
                                It.IsAny(Of Long), It.IsAny(Of String),
                                It.IsAny(Of Long?), It.IsAny(Of System.Threading.CancellationToken)),
                            Times.Exactly(3))
        End Function

    End Class

End Namespace
