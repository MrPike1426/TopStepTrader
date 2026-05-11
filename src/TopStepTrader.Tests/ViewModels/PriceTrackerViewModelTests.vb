Imports Moq
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' FEAT-53 — unit tests for <see cref="PriceTrackerViewModel"/>.
    ''' Verify subscription lifecycle (price-only → P&amp;L on capture → price-only on
    ''' reset) and the TickDelta / UnrealisedPnL cross-check.
    ''' </summary>
    Public Class PriceTrackerViewModelTests

        Private Class FakeDisposable
            Implements IDisposable
            Public Disposed As Boolean = False
            Public Sub Dispose() Implements IDisposable.Dispose
                Disposed = True
            End Sub
        End Class

        Private Class Harness
            Public ReadOnly Mock As New Mock(Of ILivePnLService)()
            Public PriceCallback As Action(Of LivePriceTick) = Nothing
            Public PnLCallback As Action(Of LivePnLTick) = Nothing
            Public ReadOnly PriceSubscriptions As New List(Of FakeDisposable)
            Public ReadOnly PnLSubscriptions As New List(Of FakeDisposable)
            Public PnLSubscribeCalls As Integer = 0
            Public LastPnLEntryPrice As Decimal = 0D
            Public LastPnLSignedSize As Integer = 0
            Public LastPnLAccountId As Long = -1L

            Public Sub New()
                Mock.Setup(Function(s) s.SubscribePrice(It.IsAny(Of String), It.IsAny(Of Action(Of LivePriceTick)))).
                     Returns(Function(sym As String, cb As Action(Of LivePriceTick)) As IDisposable
                                 PriceCallback = cb
                                 Dim d As New FakeDisposable()
                                 PriceSubscriptions.Add(d)
                                 Return d
                             End Function)

                Mock.Setup(Function(s) s.Subscribe(It.IsAny(Of String), It.IsAny(Of Decimal),
                                                    It.IsAny(Of Integer), It.IsAny(Of Action(Of LivePnLTick)),
                                                    It.IsAny(Of Long))).
                     Returns(Function(sym As String, entry As Decimal, size As Integer,
                                       cb As Action(Of LivePnLTick), acct As Long) As IDisposable
                                 PnLSubscribeCalls += 1
                                 LastPnLEntryPrice = entry
                                 LastPnLSignedSize = size
                                 LastPnLAccountId = acct
                                 PnLCallback = cb
                                 Dim d As New FakeDisposable()
                                 PnLSubscriptions.Add(d)
                                 Return d
                             End Function)

                Mock.Setup(Function(s) s.GetDiagnostics(It.IsAny(Of String))).
                     Returns(New LivePnLDiagnostics())
            End Sub

            Public Function CreateAndLoad() As PriceTrackerViewModel
                Dim vm As New PriceTrackerViewModel(Mock.Object)
                vm.LoadAsync()
                Return vm
            End Function
        End Class

        <Fact>
        Public Sub CurrentPrice_UpdatesFrom_PriceTick()
            Dim h As New Harness()
            Dim vm = h.CreateAndLoad()

            h.PriceCallback(New LivePriceTick With {
                .ContractId = "MBT",
                .Price = 108_500D,
                .Source = LivePriceSource.Quote,
                .AgeMs = 42,
                .TimestampUtc = DateTime.UtcNow})

            Assert.Equal(108_500D, vm.CurrentPrice)
            Assert.Equal("Quote", vm.PriceSource)
            Assert.Equal(42L, vm.PriceAgeMs)
        End Sub

        <Fact>
        Public Sub CaptureEntry_SetsEntryPrice_To_CurrentPrice()
            Dim h As New Harness()
            Dim vm = h.CreateAndLoad()
            h.PriceCallback(New LivePriceTick With {.Price = 108_500D, .Source = LivePriceSource.Quote})

            Assert.True(vm.CaptureEntryCommand.CanExecute(Nothing))
            vm.CaptureEntryCommand.Execute(Nothing)

            Assert.Equal(108_500D, vm.EntryPrice)
            Assert.True(vm.HasEntry)
        End Sub

        <Fact>
        Public Sub CaptureEntry_DisposesPriceSubscription_AndStartsPnLSubscription()
            Dim h As New Harness()
            Dim vm = h.CreateAndLoad()
            h.PriceCallback(New LivePriceTick With {.Price = 108_500D, .Source = LivePriceSource.Quote})

            Assert.Single(h.PriceSubscriptions)
            Assert.False(h.PriceSubscriptions(0).Disposed)
            Assert.Equal(0, h.PnLSubscribeCalls)

            vm.CaptureEntryCommand.Execute(Nothing)

            Assert.True(h.PriceSubscriptions(0).Disposed)
            Assert.Equal(1, h.PnLSubscribeCalls)
            Assert.Equal(108_500D, h.LastPnLEntryPrice)
            Assert.Equal(1, h.LastPnLSignedSize)
            Assert.Equal(0L, h.LastPnLAccountId)
        End Sub

        <Fact>
        Public Sub Reset_ClearsEntry_AndRestartsPriceOnlySubscription()
            Dim h As New Harness()
            Dim vm = h.CreateAndLoad()
            h.PriceCallback(New LivePriceTick With {.Price = 108_500D, .Source = LivePriceSource.Quote})
            vm.CaptureEntryCommand.Execute(Nothing)

            Assert.True(vm.HasEntry)
            Assert.Single(h.PnLSubscriptions)
            Assert.False(h.PnLSubscriptions(0).Disposed)

            Assert.True(vm.ResetCommand.CanExecute(Nothing))
            vm.ResetCommand.Execute(Nothing)

            Assert.Equal(0D, vm.EntryPrice)
            Assert.False(vm.HasEntry)
            Assert.True(h.PnLSubscriptions(0).Disposed)
            Assert.Equal(2, h.PriceSubscriptions.Count)
            Assert.False(h.PriceSubscriptions(1).Disposed)
        End Sub

        <Fact>
        Public Sub TickDelta_AndPnL_AreConsistent()
            Dim h As New Harness()
            Dim vm = h.CreateAndLoad()
            h.PriceCallback(New LivePriceTick With {.Price = 108_500D, .Source = LivePriceSource.Quote})
            vm.CaptureEntryCommand.Execute(Nothing)

            ' Service-side P&L for current=108_535 against entry=108_500:
            ' delta = 35 BTC pts × $0.10/pt = $3.50 ; tick = 5 pts → 7 ticks.
            h.PnLCallback(New LivePnLTick With {
                .ContractId = "MBT",
                .Price = 108_535D,
                .Source = LivePriceSource.Quote,
                .EntryPrice = 108_500D,
                .Size = 1,
                .UnrealisedPnL = 3.5D,
                .DollarPerPoint = 0.1D})

            Assert.Equal(108_535D, vm.CurrentPrice)
            Assert.Equal(7, vm.TickDelta)
            Assert.Equal(3.5D, vm.UnrealisedPnL)
        End Sub

    End Class

End Namespace
