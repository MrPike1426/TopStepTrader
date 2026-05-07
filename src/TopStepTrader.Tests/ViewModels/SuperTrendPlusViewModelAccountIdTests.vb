Imports Moq
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    Public Class SuperTrendPlusViewModelAccountIdTests

        Private ReadOnly _mockBarService As Mock(Of IBarIngestionService)
        Private ReadOnly _mockOrderService As Mock(Of IOrderService)
        Private ReadOnly _mockSession As Mock(Of ITradingSessionContext)
        Private ReadOnly _mockPersonaService As Mock(Of IPersonaService)
        Private ReadOnly _mockAccountService As Mock(Of IAccountService)
        Private ReadOnly _mockContractResolver As Mock(Of IContractResolutionService)

        Public Sub New()
            _mockBarService     = New Mock(Of IBarIngestionService)()
            _mockOrderService   = New Mock(Of IOrderService)()
            _mockSession        = New Mock(Of ITradingSessionContext)()
            _mockPersonaService = New Mock(Of IPersonaService)()
            _mockAccountService = New Mock(Of IAccountService)()
            _mockContractResolver = New Mock(Of IContractResolutionService)()
            _mockSession.Setup(Function(s) s.SelectedAccount).Returns(CType(Nothing, Account))
        End Sub

        Private Function CreateViewModel() As SuperTrendPlusViewModel
            Return New SuperTrendPlusViewModel(
                _mockBarService.Object,
                _mockOrderService.Object,
                _mockSession.Object,
                _mockPersonaService.Object,
                _mockAccountService.Object,
                _mockContractResolver.Object,
                New Mock(Of IClaudeReviewService)().Object,
                NullLogger(Of SuperTrendPlusViewModel).Instance,
                New Mock(Of ITradeRecordService)().Object,
                New ScalperExitManager(NullLogger(Of ScalperExitManager).Instance))
        End Function

        <Fact>
        Public Async Function FireEntryAsync_AccountId0_DoesNotPlaceOrder() As Task
            _mockOrderService.Setup(Function(s) s.PlaceOrderAsync(It.IsAny(Of Order))).
                ThrowsAsync(New Exception("PlaceOrderAsync must not be called when AccountId = 0"))

            Dim vm = CreateViewModel()

            _mockOrderService.Verify(Function(s) s.PlaceOrderAsync(It.IsAny(Of Order)()), Times.Never)
            Await Task.CompletedTask
        End Function

        <Fact>
        Public Sub StartMonitoring_NoAccount_SetsOrangeWarningStatus()
            _mockSession.Setup(Function(s) s.SelectedAccount).Returns(CType(Nothing, Account))

            Dim vm = CreateViewModel()
            vm.StartStopCommand.Execute(Nothing)

            Assert.Contains("No account selected", vm.StatusText)
        End Sub

        <Fact>
        Public Sub StartMonitoring_WithAccount_DoesNotSetWarningStatus()
            Dim account = New Account With {.Id = 42, .Name = "Test"}
            _mockSession.Setup(Sub(s) s.SelectAccount(It.IsAny(Of Account)()))

            Dim vm = CreateViewModel()
            vm.SelectedAccount = account
            vm.StartStopCommand.Execute(Nothing)

            Assert.DoesNotContain("No account selected", vm.StatusText)
        End Sub

        <Fact>
        Public Sub ViewModel_HasThreeSlotBoxes_WithCorrectLabels()
            Dim vm = CreateViewModel()
            Assert.Equal("Slot 1", vm.Slot1.SlotLabel)
            Assert.Equal("Slot 2", vm.Slot2.SlotLabel)
            Assert.Equal("Slot 3", vm.Slot3.SlotLabel)
        End Sub

    End Class

End Namespace
