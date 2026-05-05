Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Shell ViewModel for the Backtest page.
    ''' Owns the five sub-VMs (one per tab).
    '''   • RunVm          — Tab 1 (Run Backtest)         [BacktestRunViewModel]
    '''   • StPlusVm       — Tab 2 (SuperTrend+)          [SuperTrendPlusBacktestViewModel]
    '''   • MaxEffortVm    — Tab 3 (Maximum Effort!)      [MaxEffortViewModel]
    '''   • PinnedVm       — Tab 4 (Pinned Results)       [PinnedResultsViewModel]
    '''   • PreviousRunsVm — Tab 5 (Previous Runs)        [PreviousRunsViewModel]
    ''' </summary>
    Public Class BacktestViewModel
        Inherits ViewModelBase
        Implements IDisposable

        ' ── Sub-VMs ───────────────────────────────────────────────────────────
        Public ReadOnly Property RunVm As BacktestRunViewModel
        Public ReadOnly Property StPlusVm As SuperTrendPlusBacktestViewModel
        Public ReadOnly Property MaxEffortVm As MaxEffortViewModel
        Public ReadOnly Property PinnedVm As PinnedResultsViewModel
        Public ReadOnly Property PreviousRunsVm As PreviousRunsViewModel

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       claudeReviewService As IClaudeReviewService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService,
                       slotStore As ProTraderSlotStore)

            ' Create sub-VMs in dependency order:
            '   PinnedVm first (owns PinnedResults — shared with StPlusVm and MaxEffortVm)
            '   StPlusVm next (Tab 2 — pins into PinnedVm.PinnedResults)
            '   RunVm next (Tab 1)
            '   MaxEffortVm next (references both PinnedVm.PinnedResults and RunVm)
            '   PreviousRunsVm last (independent of the others)
            PinnedVm       = New PinnedResultsViewModel()
            StPlusVm       = New SuperTrendPlusBacktestViewModel(backtestService, barCollectionService, personaService, PinnedVm.PinnedResults)
            RunVm          = New BacktestRunViewModel(backtestService, barCollectionService, session, personaService)
            MaxEffortVm    = New MaxEffortViewModel(backtestService, barCollectionService, claudeReviewService,
                                                    session, personaService, PinnedVm.PinnedResults, RunVm, slotStore)
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
