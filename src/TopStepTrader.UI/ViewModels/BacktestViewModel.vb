Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Backtest configuration, execution, and results display.
    ''' Shows a trade-list grid and summary metrics.
    ''' Also provides the "Train Model" command to trigger initial / periodic
    ''' ML retraining from bar history stored in the database.
    '''
    ''' Tab 3 — Test Trade: fetches the past 24 bars, runs a combined
    ''' EMA 21/50 + RSI 14 trend analysis, and displays Up/Down probability
    ''' percentages.  The user can then choose to place a Test BUY or Test SELL.
    ''' </summary>
    Public Class BacktestViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService  As IBacktestService
        Private ReadOnly _trainingService  As IModelTrainingService
        Private ReadOnly _trendService     As TrendAnalysisService
        Private ReadOnly _orderService     As IOrderService
        Private ReadOnly _accountService   As IAccountService
        Private _cancelSource As CancellationTokenSource

        Private _accountId As Long = 0

        ' ══════════════════════════════════════════════════════════════════════
        '  TAB 1 — RUN BACKTEST
        ' ══════════════════════════════════════════════════════════════════════

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
                OnPropertyChanged(NameOf(CanTrain))
            End Set
        End Property

        Private _isTraining As Boolean
        Public Property IsTraining As Boolean
            Get
                Return _isTraining
            End Get
            Set(value As Boolean)
                SetProperty(_isTraining, value)
                OnPropertyChanged(NameOf(CanTrain))
                OnPropertyChanged(NameOf(CanRun))
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
                Return Not _isRunning AndAlso Not _isTraining
            End Get
        End Property

        Public ReadOnly Property CanCancel As Boolean
            Get
                Return _isRunning
            End Get
        End Property

        Public ReadOnly Property CanTrain As Boolean
            Get
                Return Not _isRunning AndAlso Not _isTraining
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

        ' ══════════════════════════════════════════════════════════════════════
        '  TAB 3 — TEST TRADE (EMA / RSI Trend Analysis)
        ' ══════════════════════════════════════════════════════════════════════

        Private _testTradeContractId As String = ""
        Public Property TestTradeContractId As String
            Get
                Return _testTradeContractId
            End Get
            Set(value As String)
                SetProperty(_testTradeContractId, value)
            End Set
        End Property

        Private _testTradeQuantity As String = "1"
        Public Property TestTradeQuantity As String
            Get
                Return _testTradeQuantity
            End Get
            Set(value As String)
                SetProperty(_testTradeQuantity, value)
            End Set
        End Property

        Private _hasTrendResult As Boolean = False
        Public Property HasTrendResult As Boolean
            Get
                Return _hasTrendResult
            End Get
            Set(value As Boolean)
                SetProperty(_hasTrendResult, value)
            End Set
        End Property

        Private _upProbabilityText As String = "—"
        Public Property UpProbabilityText As String
            Get
                Return _upProbabilityText
            End Get
            Set(value As String)
                SetProperty(_upProbabilityText, value)
            End Set
        End Property

        Private _downProbabilityText As String = "—"
        Public Property DownProbabilityText As String
            Get
                Return _downProbabilityText
            End Get
            Set(value As String)
                SetProperty(_downProbabilityText, value)
            End Set
        End Property

        Private _trendEMA21Text As String = "—"
        Public Property TrendEMA21Text As String
            Get
                Return _trendEMA21Text
            End Get
            Set(value As String)
                SetProperty(_trendEMA21Text, value)
            End Set
        End Property

        Private _trendEMA50Text As String = "—"
        Public Property TrendEMA50Text As String
            Get
                Return _trendEMA50Text
            End Get
            Set(value As String)
                SetProperty(_trendEMA50Text, value)
            End Set
        End Property

        Private _trendRSI14Text As String = "—"
        Public Property TrendRSI14Text As String
            Get
                Return _trendRSI14Text
            End Get
            Set(value As String)
                SetProperty(_trendRSI14Text, value)
            End Set
        End Property

        Private _trendLastCloseText As String = "—"
        Public Property TrendLastCloseText As String
            Get
                Return _trendLastCloseText
            End Get
            Set(value As String)
                SetProperty(_trendLastCloseText, value)
            End Set
        End Property

        Private _trendSummaryText As String = ""
        Public Property TrendSummaryText As String
            Get
                Return _trendSummaryText
            End Get
            Set(value As String)
                SetProperty(_trendSummaryText, value)
            End Set
        End Property

        Private _testTradeStatus As String = "Enter a Contract ID and click Analyse Trend, then choose Test BUY or Test SELL."
        Public Property TestTradeStatus As String
            Get
                Return _testTradeStatus
            End Get
            Set(value As String)
                SetProperty(_testTradeStatus, value)
            End Set
        End Property

        Public ReadOnly Property TrendSignals As New ObservableCollection(Of String)()

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property RunCommand          As RelayCommand
        Public ReadOnly Property CancelCommand       As RelayCommand
        Public ReadOnly Property LoadHistoryCommand  As RelayCommand
        Public ReadOnly Property TrainModelCommand   As RelayCommand
        Public ReadOnly Property AnalyseTrendCommand As RelayCommand
        Public ReadOnly Property TestBuyCommand      As RelayCommand
        Public ReadOnly Property TestSellCommand     As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(backtestService As IBacktestService,
                       trainingService As IModelTrainingService,
                       trendService As TrendAnalysisService,
                       orderService As IOrderService,
                       accountService As IAccountService)
            _backtestService = backtestService
            _trainingService = trainingService
            _trendService    = trendService
            _orderService    = orderService
            _accountService  = accountService

            RunCommand          = New RelayCommand(AddressOf ExecuteRun,        Function() CanRun)
            CancelCommand       = New RelayCommand(AddressOf ExecuteCancel,     Function() CanCancel)
            LoadHistoryCommand  = New RelayCommand(AddressOf LoadPreviousRuns)
            TrainModelCommand   = New RelayCommand(AddressOf ExecuteTrainModel, Function() CanTrain)
            AnalyseTrendCommand = New RelayCommand(AddressOf ExecuteAnalyseTrend)
            TestBuyCommand      = New RelayCommand(Sub() ExecuteTestTrade(OrderSide.Buy))
            TestSellCommand     = New RelayCommand(Sub() ExecuteTestTrade(OrderSide.Sell))

            AddHandler _backtestService.ProgressUpdated, AddressOf OnProgress
            AddHandler _orderService.OrderFilled,   AddressOf OnTestOrderFilled
            AddHandler _orderService.OrderRejected, AddressOf OnTestOrderRejected
        End Sub

        Public Sub LoadDataAsync()
            LoadPreviousRuns()
            Task.Run(AddressOf LoadAccountAsync)
        End Sub

        Private Async Function LoadAccountAsync() As Task
            Try
                Dim accounts = Await _accountService.GetActiveAccountsAsync()
                Dim first = accounts.FirstOrDefault()
                If first IsNot Nothing Then _accountId = first.Id
            Catch
                ' Silently ignore — account will be 0 until resolved
            End Try
        End Function

        ' ══════════════════════════════════════════════════════════════════════
        '  TAB 3 — TREND ANALYSIS + TEST TRADE EXECUTION
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteAnalyseTrend()
            Dim contractId = _testTradeContractId.Trim()
            If String.IsNullOrEmpty(contractId) Then
                TestTradeStatus = "Please enter a valid Contract ID."
                Return
            End If

            TestTradeStatus = "Analysing trend (fetching past 24 bars)..."
            HasTrendResult = False

            Task.Run(Async Function()
                         Try
                             Dim result = Await _trendService.AnalyseTrendAsync(contractId, 24, BarTimeframe.OneHour)

                             Dispatch(Sub()
                                          UpProbabilityText   = $"{result.UpProbability:F1}%"
                                          DownProbabilityText = $"{result.DownProbability:F1}%"
                                          TrendEMA21Text      = result.EMA21.ToString("F2")
                                          TrendEMA50Text      = result.EMA50.ToString("F2")
                                          TrendRSI14Text      = result.RSI14.ToString("F1")
                                          TrendLastCloseText  = result.LastClose.ToString("F2")
                                          TrendSummaryText    = result.Summary

                                          TrendSignals.Clear()
                                          For Each s In result.Signals
                                              TrendSignals.Add(s)
                                          Next

                                          HasTrendResult = True

                                          Dim direction = If(result.UpProbability > result.DownProbability, "UP", "DOWN")
                                          TestTradeStatus = $"Analysis complete — trend favours {direction} ({Math.Max(result.UpProbability, result.DownProbability):F1}%). " &
                                                            $"Choose Test BUY or Test SELL below."
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() TestTradeStatus = $"Analysis error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteTestTrade(side As OrderSide)
            Dim contractId = _testTradeContractId.Trim()
            Dim qty As Integer
            If String.IsNullOrEmpty(contractId) Then
                TestTradeStatus = "Please enter a valid Contract ID." : Return
            End If
            If Not Integer.TryParse(_testTradeQuantity.Trim(), qty) OrElse qty <= 0 Then
                TestTradeStatus = "Invalid quantity." : Return
            End If
            If _accountId = 0 Then
                TestTradeStatus = "No active account found. Check Settings → API connection." : Return
            End If

            Dim order As New Order With {
                .AccountId  = _accountId,
                .ContractId = contractId,
                .Side       = side,
                .OrderType  = OrderType.Market,
                .Quantity   = qty,
                .Status     = OrderStatus.Pending,
                .PlacedAt   = DateTimeOffset.UtcNow,
                .Notes      = $"Test Trade ({side}) via Trend Analysis tab"
            }

            TestTradeStatus = $"Placing test {side} order for {qty}x {contractId}..."

            Task.Run(Async Function()
                         Try
                             Dim placed = Await _orderService.PlaceOrderAsync(order)
                             Dispatch(Sub()
                                          TestTradeStatus = $"Test {side} order #{placed.Id} placed successfully for {qty}x {contractId}."
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() TestTradeStatus = $"Order error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub OnTestOrderFilled(sender As Object, e As OrderFilledEventArgs)
            Dispatch(Sub()
                         TestTradeStatus = $"Order #{e.Order.Id} FILLED @ {e.Order.FillPrice:F2}"
                     End Sub)
        End Sub

        Private Sub OnTestOrderRejected(sender As Object, e As OrderRejectedEventArgs)
            Dispatch(Sub()
                         TestTradeStatus = $"Order #{e.Order.Id} REJECTED: {e.Reason}"
                     End Sub)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        '  TAB 1 — TRAIN MODEL
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteTrainModel()
            IsTraining   = True
            ProgressText = "Training ML model on DB bars... (may take 30–60 s)"
            Progress     = 0

            Task.Run(Async Function()
                         Try
                             Dim cts = New CancellationTokenSource(TimeSpan.FromMinutes(5))
                             Dim metrics = Await _trainingService.RetrainAsync(cts.Token)

                             Dispatch(Sub()
                                          If metrics IsNot Nothing Then
                                              ProgressText = $"Model trained — Acc: {metrics.Accuracy:P1}  AUC: {metrics.AUC:F3}  F1: {metrics.F1Score:F3}  Samples: {metrics.TrainingSamples}"
                                          Else
                                              ProgressText = "Training skipped — insufficient bar data (need ≥ 200 bars)"
                                          End If
                                          Progress = 100
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() ProgressText = $"Training error: {ex.Message}")
                         Finally
                             Dispatch(Sub() IsTraining = False)
                         End Try
                     End Function)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        '  TAB 1 — BACKTEST RUN
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteRun()
            Dim contractId = _contractIdText.Trim()
            If String.IsNullOrEmpty(contractId) Then
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
