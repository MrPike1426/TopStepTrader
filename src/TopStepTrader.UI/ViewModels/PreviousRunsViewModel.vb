Imports System.Collections.ObjectModel
Imports System.Windows
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for Tab 4 — Previous Runs.
    ''' Owns the BacktestRun history collection and the refresh command.
    ''' Extracted from BacktestViewModel as part of ARCH-02d.
    ''' </summary>
    Public Class PreviousRunsViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService As IBacktestService

        ' ══════════════════════════════════════════════════════════════════════
        ' COLLECTIONS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property PreviousRuns As New ObservableCollection(Of BacktestRunSummaryVm)()

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property LoadHistoryCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(backtestService As IBacktestService)
            _backtestService = backtestService
            LoadHistoryCommand = New RelayCommand(AddressOf LoadRuns)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' PUBLIC API — called by shell BacktestViewModel on initial navigation
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub LoadRunsOnActivation()
            LoadRuns()
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' IMPLEMENTATION
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub LoadRuns()
            Task.Run(Async Function()
                         Try
                             Dim runs = Await _backtestService.GetBacktestRunsAsync()
                             Dispatch(Sub()
                                          PreviousRuns.Clear()
                                          For Each r In runs.OrderByDescending(Function(x) x.Id)
                                              PreviousRuns.Add(New BacktestRunSummaryVm(r))
                                          Next
                                      End Sub)
                         Catch
                         End Try
                     End Function)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' PREVIOUS RUN SUMMARY ROW
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class BacktestRunSummaryVm
        Public Property Id As Long
        Public Property RunName As String
        Public Property StartDate As String
        Public Property EndDate As String
        Public Property Trades As Integer
        Public Property WinRate As String
        Public Property TotalPnL As String
        Public Property Sharpe As String

        Public Sub New(r As BacktestResult)
            Id = r.Id
            RunName = r.RunName
            StartDate = r.StartDate.ToString("MM/dd/yyyy")
            EndDate = r.EndDate.ToString("MM/dd/yyyy")
            Trades = r.TotalTrades
            WinRate = r.WinRate.ToString("P1")
            TotalPnL = r.TotalPnL.ToString("C0")
            Sharpe = If(r.SharpeRatio.HasValue, r.SharpeRatio.Value.ToString("F2"), "—")
        End Sub
    End Class

End Namespace
