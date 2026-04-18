Imports Moq
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    Public Class SniperViewModelTests

        Private ReadOnly _mockAccountService As Mock(Of IAccountService)
        Private ReadOnly _mockBacktestService As Mock(Of IBacktestService)
        Private ReadOnly _mockBarService As Mock(Of IBarCollectionService)
        Private ReadOnly _mockEngine As Mock(Of ISniperExecutionEngine)
        Private ReadOnly _mockSession As Mock(Of ITradingSessionContext)
        Private ReadOnly _claudeReviewService As ClaudeReviewService

        Public Sub New()
            _mockAccountService = New Mock(Of IAccountService)()
            _mockBacktestService = New Mock(Of IBacktestService)()
            _mockBarService = New Mock(Of IBarCollectionService)()
            _mockEngine = New Mock(Of ISniperExecutionEngine)()
            _mockSession = New Mock(Of ITradingSessionContext)()
            Dim claudeOptions As IOptions(Of ClaudeSettings) = Options.Create(New ClaudeSettings())
            Dim apiKeyStore = New Mock(Of IApiKeyStore)()
            apiKeyStore.Setup(Function(s) s.Load()).Returns(New ApiKeySettings())
            _claudeReviewService = New ClaudeReviewService(claudeOptions, apiKeyStore.Object,
                New Mock(Of ILogger(Of ClaudeReviewService))().Object)
        End Sub

        <Fact>
        Public Sub CanStart_ShouldBeFalse_Initially()
            ' Arrange
            Dim vm = CreateViewModel()

            ' Act
            Dim canStart = vm.CanStart

            ' Assert
            Assert.False(canStart)
        End Sub

        <Fact>
        Public Sub CanStart_ShouldBeTrue_WhenAllConditionsMet()
            ' Arrange
            Dim vm = CreateViewModel()
            Dim account = New Account With {.Id = 123, .Name = "Test Account"}

            ' Simulate account loading (or just manually set it since we can't await LoadDataAsync easily in synchronous tests without care)
            vm.SelectedAccount = account
            vm.ContractId = "CON.Test"

            ' Act
            Dim canStart = vm.CanStart

            ' Assert
            Assert.True(canStart)
        End Sub

        <Fact>
        Public Sub StartCommand_ShouldCallEngineStart()
            ' Arrange
            Dim vm = CreateViewModel()
            Dim account = New Account With {.Id = 123, .Name = "Test Account"}
            vm.SelectedAccount = account
            vm.ContractId = "CON.Test"

            ' Act
            If vm.StartCommand.CanExecute(Nothing) Then
                vm.StartCommand.Execute(Nothing)
            End If

            ' Assert
            _mockEngine.Verify(Sub(e) e.Start(It.IsAny(Of String),
                                              It.IsAny(Of Long),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Double),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Double),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Boolean),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Boolean),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Integer),
                                              It.IsAny(Of Double),
                                              It.IsAny(Of Decimal),
                                              It.IsAny(Of Decimal)),
                               Times.Once)
            Assert.True(vm.IsRunning)
        End Sub

        <Fact>
        Public Sub StopCommand_ShouldCallEngineStop()
            ' Arrange
            Dim vm = CreateViewModel()
            vm.IsRunning = True

            ' Setup StopAsync to return a completed task
            _mockEngine.Setup(Function(e) e.StopAsync(It.IsAny(Of String))).Returns(Task.CompletedTask)

            ' Act
            If vm.StopCommand.CanExecute(Nothing) Then
                vm.StopCommand.Execute(Nothing)
            End If

            ' Assert
            _mockEngine.Verify(Function(e) e.StopAsync(It.IsAny(Of String)), Times.Once)
        End Sub

        Private Function CreateViewModel() As SniperViewModel
            Dim vm = New SniperViewModel(_mockAccountService.Object,
                                         _mockBacktestService.Object,
                                         _mockBarService.Object,
                                         _claudeReviewService,
                                         _mockEngine.Object,
                                         _mockSession.Object)
            Return vm
        End Function

    End Class

End Namespace
