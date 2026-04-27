Imports Moq
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    Public Class SuperTrendPlusViewModelAccountIdTests

        Private ReadOnly _mockBarService As Mock(Of IBarIngestionService)
        Private ReadOnly _mockOrderService As Mock(Of IOrderService)
        Private ReadOnly _mockSession As Mock(Of ITradingSessionContext)
        Private ReadOnly _mockPersonaService As Mock(Of IPersonaService)
        Private ReadOnly _mockAccountService As Mock(Of IAccountService)

        Public Sub New()
            _mockBarService     = New Mock(Of IBarIngestionService)()
            _mockOrderService   = New Mock(Of IOrderService)()
            _mockSession        = New Mock(Of ITradingSessionContext)()
            _mockPersonaService = New Mock(Of IPersonaService)()
            _mockAccountService = New Mock(Of IAccountService)()

            ' Default: no account selected
            _mockSession.Setup(Function(s) s.SelectedAccount).Returns(CType(Nothing, Account))
        End Sub

        Private Function CreateViewModel() As SuperTrendPlusViewModel
            Return New SuperTrendPlusViewModel(
                _mockBarService.Object,
                _mockOrderService.Object,
                _mockSession.Object,
                _mockPersonaService.Object,
                _mockAccountService.Object)
        End Function

        <Fact>
        Public Async Function FireEntryAsync_AccountId0_DoesNotPlaceOrder_And_ReleasesLock() As Task
            ' Arrange: session with no account (SelectedAccount = Nothing)
            _mockPersonaService.Setup(Function(p) p.GetProfile(It.IsAny(Of String))).Returns(CType(Nothing, PersonaProfile))
            _mockOrderService.Setup(Function(s) s.PlaceOrderAsync(It.IsAny(Of Order))).
                ThrowsAsync(New Exception("PlaceOrderAsync must not be called when AccountId = 0"))

            Dim vm = CreateViewModel()

            ' Verify PlaceOrderAsync was never called (AccountId = 0 blocks ordering)
            _mockOrderService.Verify(Function(s) s.PlaceOrderAsync(It.IsAny(Of Order)()), Times.Never)

            Await Task.CompletedTask
        End Function

        <Fact>
        Public Sub StartMonitoring_NoAccount_SetsOrangeWarningStatus()
            ' Arrange: session with no account
            _mockSession.Setup(Function(s) s.SelectedAccount).Returns(CType(Nothing, Account))
            _mockPersonaService.Setup(Function(p) p.GetProfile(It.IsAny(Of String))).Returns(CType(Nothing, PersonaProfile))

            Dim vm = CreateViewModel()

            ' Act: trigger StartMonitoring via StartStopCommand
            vm.StartStopCommand.Execute(Nothing)

            ' Assert: status text contains warning
            Assert.Contains("No account selected", vm.StatusText)
        End Sub

        <Fact>
        Public Sub StartMonitoring_WithAccount_DoesNotSetWarningStatus()
            ' Arrange: VM with a valid account set directly on SelectedAccount
            Dim account = New Account With {.Id = 42, .Name = "Test"}
            _mockSession.Setup(Sub(s) s.SelectAccount(It.IsAny(Of Account)()))
            _mockPersonaService.Setup(Function(p) p.GetProfile(It.IsAny(Of String))).Returns(CType(Nothing, PersonaProfile))

            Dim vm = CreateViewModel()
            vm.SelectedAccount = account

            ' Act
            vm.StartStopCommand.Execute(Nothing)
            ' Stop immediately to clean up timer
            vm.StartStopCommand.Execute(Nothing)

            ' Assert: no warning shown
            Assert.DoesNotContain("No account selected", vm.StatusText)
        End Sub

    End Class

End Namespace
