Imports Microsoft.Extensions.Logging.Abstractions
Imports Moq
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
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
                New ScalperExitManager(NullLogger(Of ScalperExitManager).Instance),
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
                New Mock(Of ITradeRecordService)().Object,
                New ScalperExitManager(NullLogger(Of ScalperExitManager).Instance))

            Dim ex As Exception = Nothing
            Try
                vm.IsDebugCaptureEnabled = True
            Catch e As Exception
                ex = e
            End Try
            Assert.Null(ex)
        End Sub

        ' ── FEAT-42: BarClose Notes contain exit engine score data ────────────

        <Fact>
        Public Sub BarCloseNotes_HealthyEval_ContainsScoreAndHealth()
            ' Simulate building the Notes string exactly as the ViewModel does.
            Dim eval As New ExitEvaluation With {
                .Score = 0,
                .ImmediateExit = False
            }
            Dim slot As New PositionSlot With {.ConsecutiveExitBars = 0}

            Dim notes = $"Score={eval.Score} [{String.Join(",", eval.ContributingSignals)}] Health={eval.RecommendedHealth} ConsecExit={slot.ConsecutiveExitBars}"

            Assert.Contains("Score=0", notes)
            Assert.Contains("Health=Healthy", notes)
            Assert.Contains("ConsecExit=0", notes)
        End Sub

        <Fact>
        Public Sub BarCloseNotes_FiringSignals_ContainsSignalList()
            Dim eval As New ExitEvaluation With {
                .Score = 5,
                .ImmediateExit = False
            }
            eval.ContributingSignals.Add("E2:3")
            eval.ContributingSignals.Add("E4:2")
            Dim slot As New PositionSlot With {.ConsecutiveExitBars = 1}

            Dim notes = $"Score={eval.Score} [{String.Join(",", eval.ContributingSignals)}] Health={eval.RecommendedHealth} ConsecExit={slot.ConsecutiveExitBars}"

            Assert.Contains("Score=5", notes)
            Assert.Contains("E2:3", notes)
            Assert.Contains("E4:2", notes)
            Assert.Contains("Health=Warning", notes)
            Assert.Contains("ConsecExit=1", notes)
        End Sub

    End Class

End Namespace
