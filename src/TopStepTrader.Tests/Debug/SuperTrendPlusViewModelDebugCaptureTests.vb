Imports Microsoft.Extensions.Logging.Abstractions
Imports Moq
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Services.Market
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.Debug

    Public Class SuperTrendPlusViewModelDebugCaptureTests

        Private ReadOnly _mockBarService As New Mock(Of IBarIngestionService)()
        Private ReadOnly _mockOrderService As New Mock(Of IOrderService)()
        Private ReadOnly _mockSession As New Mock(Of ITradingSessionContext)()
        Private ReadOnly _mockPersonaService As New Mock(Of IPersonaService)()
        Private ReadOnly _mockAccountService As New Mock(Of IAccountService)()
        Private ReadOnly _mockContractResolver As New Mock(Of IContractResolutionService)()
        Private ReadOnly _mockDebugCapture As New Mock(Of IDebugTradeCaptureService)()

        Public Sub New()
            _mockSession.Setup(Function(s) s.SelectedAccount).Returns(CType(Nothing, Account))
            _mockDebugCapture.SetupProperty(Function(d) d.IsEnabled)
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
                Nothing,
                _mockDebugCapture.Object)
        End Function

        ' ── IsDebugCaptureEnabled is False after construction ──

        <Fact>
        Public Sub IsDebugCaptureEnabled_DefaultFalse()
            Dim vm = CreateViewModel()
            Assert.False(vm.IsDebugCaptureEnabled)
        End Sub

        ' ── Toggling IsDebugCaptureEnabled forwards to service.IsEnabled ──

        <Fact>
        Public Sub IsDebugCaptureEnabled_Toggle_ForwardsToService()
            Dim vm = CreateViewModel()

            vm.IsDebugCaptureEnabled = True
            _mockDebugCapture.VerifySet(Sub(d) d.IsEnabled = True, Times.Once)

            vm.IsDebugCaptureEnabled = False
            _mockDebugCapture.VerifySet(Sub(d) d.IsEnabled = False, Times.Once)
        End Sub

        ' ── When service is Nothing, IsDebugCaptureEnabled toggle does not throw ──

        <Fact>
        Public Sub IsDebugCaptureEnabled_NoService_DoesNotThrow()
            Dim vm = New SuperTrendPlusViewModel(
                _mockBarService.Object,
                _mockOrderService.Object,
                _mockSession.Object,
                _mockPersonaService.Object,
                _mockAccountService.Object,
                _mockContractResolver.Object,
                New Mock(Of IClaudeReviewService)().Object,
                NullLogger(Of SuperTrendPlusViewModel).Instance,
                New Mock(Of ITradeRecordService)().Object)

            Dim ex As Exception = Nothing
            Try
                vm.IsDebugCaptureEnabled = True
            Catch e As Exception
                ex = e
            End Try
            Assert.Null(ex)
        End Sub

    End Class

End Namespace
