Imports Microsoft.Extensions.Logging.Abstractions
Imports Moq
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' FEAT-52: MarketHub quote-stream wiring on SuperTrendPlusViewModel.
    ''' These tests verify the safety contract — when no MarketHubClient is supplied
    ''' (test/no-DI context), Start/Stop monitoring must remain side-effect-free for
    ''' the quote stream and never throw.
    ''' </summary>
    Public Class SuperTrendPlusFeat52Tests

        Private ReadOnly _mockBarService As New Mock(Of IBarIngestionService)()
        Private ReadOnly _mockOrderService As New Mock(Of IOrderService)()
        Private ReadOnly _mockSession As New Mock(Of ITradingSessionContext)()
        Private ReadOnly _mockPersonaService As New Mock(Of IPersonaService)()
        Private ReadOnly _mockAccountService As New Mock(Of IAccountService)()
        Private ReadOnly _mockContractResolver As New Mock(Of IContractResolutionService)()

        Public Sub New()
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
        Public Sub Construct_NoMarketHub_DoesNotThrow()
            Dim ex As Exception = Nothing
            Try
                Dim vm = CreateViewModel()
                Assert.NotNull(vm)
            Catch e As Exception
                ex = e
            End Try
            Assert.Null(ex)
        End Sub

        <Fact>
        Public Sub StartThenStopMonitoring_NoMarketHub_DoesNotThrow()
            Dim vm = CreateViewModel()

            Dim ex As Exception = Nothing
            Try
                vm.StartStopCommand.Execute(Nothing) ' Start
                vm.StartStopCommand.Execute(Nothing) ' Stop
            Catch e As Exception
                ex = e
            End Try
            Assert.Null(ex)
            Assert.False(vm.IsMonitoring)
        End Sub

    End Class

End Namespace
