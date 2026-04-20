Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Shell ViewModel for the Backtest page.
    ''' Owns the four sub-VMs (one per tab).
    '''   • RunVm          — Tab 1 (Run Backtest)         [BacktestRunViewModel]
    '''   • MaxEffortVm    — Tab 2 (Maximum Effort!)      [MaxEffortViewModel]
    '''   • PinnedVm       — Tab 3 (Pinned Results)       [PinnedResultsViewModel]
    '''   • PreviousRunsVm — Tab 4 (Previous Runs)        [PreviousRunsViewModel]
    ''' </summary>
    Public Class BacktestViewModel
        Inherits ViewModelBase
        Implements IDisposable

        ' ── Sub-VMs ───────────────────────────────────────────────────────────
        Public ReadOnly Property RunVm As BacktestRunViewModel
        Public ReadOnly Property MaxEffortVm As MaxEffortViewModel
        Public ReadOnly Property PinnedVm As PinnedResultsViewModel
        Public ReadOnly Property PreviousRunsVm As PreviousRunsViewModel

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       claudeReviewService As ClaudeReviewService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService)

            ' Create sub-VMs in dependency order:
            '   PinnedVm first (owns PinnedResults collection)
            '   RunVm next (Tab 1)
            '   MaxEffortVm last (references both PinnedVm.PinnedResults and RunVm)
            '   PreviousRunsVm last (independent of the others)
            PinnedVm       = New PinnedResultsViewModel()
            RunVm          = New BacktestRunViewModel(backtestService, barCollectionService, session, personaService)
            MaxEffortVm    = New MaxEffortViewModel(backtestService, barCollectionService, claudeReviewService,
                                                    session, personaService, PinnedVm.PinnedResults, RunVm)
            PreviousRunsVm = New PreviousRunsViewModel(backtestService)
        End Sub

        Public Sub LoadDataAsync()
            PreviousRunsVm.LoadRunsOnActivation()
        End Sub

        ' ── IDisposable ──────────────────────────────────────────────────────────

        Private _disposed As Boolean = False

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RunVm.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
