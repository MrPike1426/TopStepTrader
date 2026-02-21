Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Backtest configuration, execution, and results display.
    ''' Shows a trade-list grid and summary metrics.
    ''' </summary>
    Public Class BacktestViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService As IBacktestService
        Private _cancelSource As CancellationTokenSource

        ' ── Configuration form ───────────────────────────────────────────────

        Private _runName As String = $"Run {DateTime.Now:yyyyMMdd-HHmm}"
        Public Property RunName As String
            Get
                Return _runName
            End Get
            Set(value As String)
                SetProperty(_runName, value)
            End Set
        End Property

        Private _contractIdText As String = ""
        Public Property ContractIdText As String
            Get
                Return _contractIdText
            End Get
            Set(value As String)
                SetProperty(_contractIdText, value)
            End Set
        End Property

        Private _startDate As Date = DateTime.Today.AddMonths(-3)
        Public Property StartDate As Date
            Get
                Return _startDate
            End Get
            Set(value As Date)
                SetProperty(_startDate, value)
            End Set
        End Property

        Private _endDate As Date = DateTime.Today
        Public Property EndDate As Date
            Get
                Return _endDate
            End Get
            Set(value As Date)
                SetProperty(_endDate, value)
            End Set
        End Property

        Private _initialCapital As String = "50000"
        Public Property InitialCapital As String
            Get
                Return _initialCapital
            End Get
            Set(value As String)
                SetProperty(_initialCapital, value)
            End Set
        End Property

        Private _stopLossTicks As String = "10"
        Public Property StopLossTicks As String
            Get
                Return _stopLossTicks
            End Get
            Set(value As String)
                SetProperty(_stopLossTicks, value)
            End Set
        End Property

        Private _takeProfitTicks As String = "20"
        Public Property TakeProfitTicks As String
            Get
                Return _takeProfitTicks
            End Get
            Set(value As String)
                SetProperty(_takeProfitTicks, value)
            End Set
        End Property

        Private _minConfidence As String = "0.65"
        Public Property MinConfidence As String
            Get
                Return _minConfidence
            End Get
            Set(value As String)
                SetProperty(_minConfidence, value)
            End Set
        End Property

        ' ── Progress ─────────────────────────────────────────────────────────

        Private _isRunning As Boolean
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                OnPropertyChanged(NameOf(CanRun))
                OnPropertyChanged(NameOf(CanCancel))
            End Set
        End Property

        Private _progress As Integer
        Public Property Progress As Integer
            Get
                Return _progress
            End Get
            Set(value As Integer)
                SetProperty(_progress, value)
            End Set
        End Property

        Private _progressText As String = "Ready"
        Public Property ProgressText As String
            Get
                Return _progressText
            End Get
            Set(value As String)
                SetProperty(_progressText, value)
            End Set
        End Property

        Public ReadOnly Property CanRun As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Public ReadOnly Property CanCancel As Boolean
            Get
                Return _isRunning
            End Get
        End Property

        ' ── Results ──────────────────────────────────────────────────────────

        Public ReadOnly Property Trades As New ObservableCollection(Of BacktestTradeRowVm)()

        Private _totalTrades As Integer
        Public Property TotalTrades As Integer
            Get
                Return _totalTrades
            End Get
            Set(value As Integer)
                SetProperty(_totalTrades, value)
            End Set
        End Property

        Private _winRate As String = "—"
        Public Property WinRate As String
            Get
                Return _winRate
            End Get
            Set(value As String)
                SetProperty(_winRate, value)
            End Set
        End Property

        Private _totalPnL As Decimal
        Public Property TotalPnL As Decimal
            Get
                Return _totalPnL
            End Get
            Set(value As Decimal)
                SetProperty(_totalPnL, value)
                OnPropertyChanged(NameOf(PnLColor))
            End Set
        End Property

        Private _sharpe As String = "—"
        Public Property Sharpe As String
            Get
                Return _sharpe
            End Get
            Set(value As String)
                SetProperty(_sharpe, value)
            End Set
        End Property

        Private _maxDrawdown As Decimal
        Public Property MaxDrawdown As Decimal
            Get
                Return _maxDrawdown
            End Get
            Set(value As Decimal)
                SetProperty(_maxDrawdown, value)
            End Set
        End Property

        Private _avgPnL As Decimal
        Public Property AvgPnL As Decimal
            Get
                Return _avgPnL
            End Get
            Set(value As Decimal)
                SetProperty(_avgPnL, value)
            End Set
        End Property

        Public ReadOnly Property PnLColor As String
            Get
                Return If(_totalPnL >= 0, "BuyBrush", "SellBrush")
            End Get
        End Property

        ' ── Previous runs ────────────────────────────────────────────────────

        Public ReadOnly Property PreviousRuns As New ObservableCollection(Of BacktestRunSummaryVm)()

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property RunCommand          As RelayCommand
        Public ReadOnly Property CancelCommand       As RelayCommand
        Public ReadOnly Property LoadHistoryCommand  As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(backtestService As IBacktestService)
            _backtestService = backtestService

            RunCommand         = New RelayCommand(AddressOf ExecuteRun,    Function() CanRun)
            CancelCommand      = New RelayCommand(AddressOf ExecuteCancel, Function() CanCancel)
            LoadHistoryCommand = New RelayCommand(AddressOf LoadPreviousRuns)

            AddHandler _backtestService.ProgressUpdated, AddressOf OnProgress
        End Sub

        Public Sub LoadDataAsync()
            LoadPreviousRuns()
        End Sub

        Private Sub ExecuteRun()
            Dim contractId As Integer
            If Not Integer.TryParse(_contractIdText.Trim(), contractId) OrElse contractId <= 0 Then
                ProgressText = "Invalid Contract ID" : Return
            End If
            Dim capital As Decimal
            If Not Decimal.TryParse(_initialCapital, capital) OrElse capital <= 0 Then
                ProgressText = "Invalid initial capital" : Return
            End If
            Dim slTicks, tpTicks As Integer
            Integer.TryParse(_stopLossTicks, slTicks)
            Integer.TryParse(_takeProfitTicks, tpTicks)
            Dim conf As Single
            Single.TryParse(_minConfidence, conf)

            Dim config As New BacktestConfiguration With {
                .RunName             = _runName,
                .ContractId          = contractId,
                .StartDate           = _startDate,
                .EndDate             = _endDate,
                .InitialCapital      = capital,
                .StopLossTicks       = If(slTicks > 0, slTicks, 10),
                .TakeProfitTicks     = If(tpTicks > 0, tpTicks, 20),
                .MinSignalConfidence = If(conf > 0, conf, 0.65F)
            }

            _cancelSource = New CancellationTokenSource()
            IsRunning = True
            Progress = 0

            Task.Run(Async Function()
                         Try
                             Dim result = Await _backtestService.RunBacktestAsync(config, _cancelSource.Token)
                             Dispatch(Sub() ShowResult(result))
                         Catch ex As OperationCanceledException
                             Dispatch(Sub() ProgressText = "Backtest cancelled")
                         Catch ex As Exception
                             Dispatch(Sub() ProgressText = $"Error: {ex.Message}")
                         Finally
                             Dispatch(Sub()
                                          IsRunning = False
                                          _cancelSource?.Dispose()
                                      End Sub)
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteCancel(param As Object)
            _cancelSource?.Cancel()
        End Sub

        Private Sub ShowResult(result As BacktestResult)
            TotalTrades  = result.TotalTrades
            WinRate      = result.WinRate.ToString("P1")
            TotalPnL     = result.TotalPnL
            MaxDrawdown  = result.MaxDrawdown
            AvgPnL       = result.AveragePnLPerTrade
            Sharpe       = If(result.SharpeRatio.HasValue, result.SharpeRatio.Value.ToString("F2"), "—")
            ProgressText = $"Complete — {result.TotalTrades} trades"
            Progress = 100

            Trades.Clear()
            For Each t In result.Trades
                Trades.Add(New BacktestTradeRowVm(t))
            Next
        End Sub

        Private Sub LoadPreviousRuns()
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
                             ' Silently ignore history load errors
                         End Try
                     End Function)
        End Sub

        Private Sub OnProgress(sender As Object, e As BacktestProgressEventArgs)
            Dispatch(Sub()
                         Progress = e.PercentComplete
                         ProgressText = $"{e.PercentComplete}%  |  {e.CurrentDate:MM/dd/yyyy}  |  {e.TradesExecuted} trades"
                     End Sub)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    Public Class BacktestTradeRowVm
        Public Property EntryTime  As String
        Public Property ExitTime   As String
        Public Property Side       As String
        Public Property EntryPrice As String
        Public Property ExitPrice  As String
        Public Property PnL        As String
        Public Property ExitReason As String
        Public Property Confidence As String

        Public ReadOnly Property PnLColor As String
            Get
                Return If(PnL.StartsWith("-"), "SellBrush", "BuyBrush")
            End Get
        End Property

        Public Sub New(t As BacktestTrade)
            EntryTime  = t.EntryTime.LocalDateTime.ToString("MM/dd HH:mm")
            ExitTime   = If(t.ExitTime.HasValue, t.ExitTime.Value.LocalDateTime.ToString("MM/dd HH:mm"), "—")
            Side       = t.Side
            EntryPrice = t.EntryPrice.ToString("F2")
            ExitPrice  = If(t.ExitPrice.HasValue, t.ExitPrice.Value.ToString("F2"), "—")
            PnL        = If(t.PnL.HasValue, t.PnL.Value.ToString("C0"), "—")
            ExitReason = t.ExitReason
            Confidence = t.SignalConfidence.ToString("P0")
        End Sub
    End Class

    Public Class BacktestRunSummaryVm
        Public Property Id         As Long
        Public Property RunName    As String
        Public Property StartDate  As String
        Public Property EndDate    As String
        Public Property Trades     As Integer
        Public Property WinRate    As String
        Public Property TotalPnL   As String
        Public Property Sharpe     As String

        Public Sub New(r As BacktestResult)
            Id        = r.Id
            RunName   = r.RunName
            StartDate = r.StartDate.ToString("MM/dd/yyyy")
            EndDate   = r.EndDate.ToString("MM/dd/yyyy")
            Trades    = r.TotalTrades
            WinRate   = r.WinRate.ToString("P1")
            TotalPnL  = r.TotalPnL.ToString("C0")
            Sharpe    = If(r.SharpeRatio.HasValue, r.SharpeRatio.Value.ToString("F2"), "—")
        End Sub
    End Class

End Namespace
