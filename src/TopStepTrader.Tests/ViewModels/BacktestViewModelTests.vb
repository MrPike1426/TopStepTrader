Option Strict On
Option Explicit On

Imports Moq
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' Unit tests for BacktestRunViewModel input-validation properties.
    ''' TEST-06: CanRun reflects BarsAvailable and WorkPhase.Idle state.
    '''
    ''' BacktestRunViewModel is constructed without a WPF Application — the null guard
    ''' added to the constructor means _elapsedTimer stays Nothing in this process,
    ''' which is safe because the timer methods are also guarded.
    ''' </summary>
    Public Class BacktestViewModelTests
        Implements IDisposable

        Private ReadOnly _mockBacktestService As New Mock(Of IBacktestService)
        Private ReadOnly _mockBarService As New Mock(Of IBarCollectionService)
        Private ReadOnly _mockSession As New Mock(Of ITradingSessionContext)
        Private ReadOnly _mockPersona As New Mock(Of IPersonaService)
        Private ReadOnly _vm As BacktestRunViewModel

        Public Sub New()
            _vm = New BacktestRunViewModel(
                _mockBacktestService.Object,
                _mockBarService.Object,
                _mockSession.Object,
                _mockPersona.Object)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _vm.Dispose()
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' TEST-06 — CanRun / BarsAvailable validation

        ''' <summary>
        ''' Immediately after construction BarsAvailable defaults to False, so
        ''' CanRun must also be False.
        ''' </summary>
        <Fact>
        Public Sub CanRun_Initially_IsFalse()
            Assert.False(_vm.CanRun)
        End Sub

        ''' <summary>
        ''' Setting BarsAvailable = True while the engine is Idle makes CanRun True.
        ''' </summary>
        <Fact>
        Public Sub CanRun_WhenBarsAvailable_IsTrue()
            _vm.BarsAvailable = True

            Assert.True(_vm.CanRun)
        End Sub

    End Class

End Namespace
