Imports System.Collections.ObjectModel
Imports System.Windows
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for Tab 3 — Pinned Results.
    ''' Owns the pinned collection and the hardcoded 2025-07 baseline analysis text.
    ''' MaxEffortViewModel holds a direct reference to PinnedResults and appends rows via PinResultCommand.
    ''' Extracted from BacktestViewModel as part of ARCH-02c.
    ''' </summary>
    Public Class PinnedResultsViewModel
        Inherits ViewModelBase

        ' ══════════════════════════════════════════════════════════════════════
        ' COLLECTIONS
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Live-pinned results. Seeded with the 2025-07 Gold · Multi-Confluence · 1 hr baseline.
        ''' MaxEffortViewModel appends to this collection via PinResultCommand (direct reference).
        ''' </summary>
        Public ReadOnly Property PinnedResults As New ObservableCollection(Of MaxEffortRowVm)()

        ' ══════════════════════════════════════════════════════════════════════
        ' ANALYSIS TEXT
        ' ══════════════════════════════════════════════════════════════════════

        Private _pinnedAiAnalysis As String =
            "Top 3 Legitimate Combinations (recorded 2025-07 · Gold · Multi-Confluence · 1 hr)" &
            vbCrLf & vbCrLf &
            "1. Damian / Gold / Multi-Confluence / 1 hr" & vbCrLf &
            "   34 trades · 56% win rate · £17,746 P&L · Sharpe 7.71" & vbCrLf &
            "   Robust sample size; consistent edge; highest Sharpe of all 720 combinations." & vbCrLf & vbCrLf &
            "2. Joe / Gold / Multi-Confluence / 1 hr" & vbCrLf &
            "   39 trades · 54% win rate · £17,534 P&L · Sharpe 6.93" & vbCrLf &
            "   Largest trade count among top performers; high Sharpe; stable avg P&L (£450)." & vbCrLf & vbCrLf &
            "3. Lewis / Gold / Multi-Confluence / 1 hr" & vbCrLf &
            "   29 trades · 52% win rate · £15,744 P&L · Sharpe 7.55" & vbCrLf &
            "   Highest avg P&L per trade (£543); solid consistency." & vbCrLf & vbCrLf &
            "All three dominate via Multi-Confluence on 1-hr Gold. Clear signal."

        Public Property PinnedAiAnalysis As String
            Get
                Return _pinnedAiAnalysis
            End Get
            Set(value As String)
                SetProperty(_pinnedAiAnalysis, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property CopyPinnedAnalysisCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New()
            CopyPinnedAnalysisCommand = New RelayCommand(
                Sub()
                    If Not String.IsNullOrEmpty(_pinnedAiAnalysis) Then
                        Clipboard.SetText(_pinnedAiAnalysis)
                    End If
                End Sub,
                Function() Not String.IsNullOrEmpty(_pinnedAiAnalysis))

            ' ── Seed with 2025-07 Gold/Multi-Confluence/1hr baseline (permanent sanity-check)
            PinnedResults.Add(New MaxEffortRowVm("Damian", "Gold", "Multi-Confluence", "1 hr",
                                                 trades:=34, winRatePct:=56.0, totalPnLRaw:=17746D,
                                                 sharpe:=7.71, avgPnL:=522D, maxDD:=-1200D))
            PinnedResults.Add(New MaxEffortRowVm("Joe",    "Gold", "Multi-Confluence", "1 hr",
                                                 trades:=39, winRatePct:=54.0, totalPnLRaw:=17534D,
                                                 sharpe:=6.93, avgPnL:=450D, maxDD:=-1500D))
            PinnedResults.Add(New MaxEffortRowVm("Lewis",  "Gold", "Multi-Confluence", "1 hr",
                                                 trades:=29, winRatePct:=52.0, totalPnLRaw:=15744D,
                                                 sharpe:=7.55, avgPnL:=543D, maxDD:=-900D))
        End Sub

    End Class

End Namespace
