Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Dashboard — summary of account health, daily P&amp;L, drawdown, and balance history.
    ''' </summary>
    Public Class DashboardViewModel
        Inherits ViewModelBase

        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _authService As IAuthService
        Private ReadOnly _balanceHistoryService As IBalanceHistoryService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _riskSettings As RiskSettings
        Private ReadOnly _userPrefs As IUserPreferencesService
        Private ReadOnly _tradeRecord As ITradeRecordService
        Private ReadOnly _logger As ILogger(Of DashboardViewModel)

        ''' <summary>
        ''' BUG-86 F3: an open trade older than this is flagged as stale on the
        ''' Dashboard. Captures the long-tail case where the periodic reconciler
        ''' has run but the broker had no matching exit fill (e.g. position genuinely
        ''' still open per the broker, or a contract-roll mismatch).
        ''' </summary>
        Private Shared ReadOnly StaleOpenThreshold As TimeSpan = TimeSpan.FromHours(24)

        ' ── Broker label ─────────────────────────────────────────────────────
        Public ReadOnly Property ActiveBrokerLabel As String
            Get
                Return "TopStepX (Futures)"
            End Get
        End Property

        ' ── Bindable properties ──────────────────────────────────────────────

        Private _accountName As String = "—"
        Public Property AccountName As String
            Get
                Return _accountName
            End Get
            Set(value As String)
                SetProperty(_accountName, value)
            End Set
        End Property

        Private _balance As Decimal
        Public Property Balance As Decimal
            Get
                Return _balance
            End Get
            Set(value As Decimal)
                SetProperty(_balance, value)
            End Set
        End Property

        Private _accounts As New ObservableCollection(Of TopStepTrader.Core.Models.Account)
        Public Property Accounts As ObservableCollection(Of TopStepTrader.Core.Models.Account)
            Get
                Return _accounts
            End Get
            Set(value As ObservableCollection(Of TopStepTrader.Core.Models.Account))
                If Not Object.Equals(_accounts, value) Then
                    _accounts = value
                    OnPropertyChanged(NameOf(Accounts))
                End If
            End Set
        End Property

        Private _selectedAccount As TopStepTrader.Core.Models.Account
        Public Property SelectedAccount As TopStepTrader.Core.Models.Account
            Get
                Return _selectedAccount
            End Get
            Set(value As TopStepTrader.Core.Models.Account)
                If SetProperty(_selectedAccount, value) Then
                    ' Propagate to the global session so all other tabs see the same account/broker.
                    _session.SelectAccount(value)
                    OnPropertyChanged(NameOf(ActiveBrokerLabel))
                    If value IsNot Nothing Then
                        AccountName = value.Name
                        ' TotalValue may be 0 for TopStepX accounts; fall back to Balance
                        Balance = If(value.TotalValue > 0, value.TotalValue, value.Balance)
                    End If
                End If
            End Set
        End Property

        Private _dailyPnL As Decimal
        Public Property DailyPnL As Decimal
            Get
                Return _dailyPnL
            End Get
            Set(value As Decimal)
                SetProperty(_dailyPnL, value)
                OnPropertyChanged(NameOf(PnLColor))
            End Set
        End Property

        Private _drawdown As Decimal
        Public Property Drawdown As Decimal
            Get
                Return _drawdown
            End Get
            Set(value As Decimal)
                SetProperty(_drawdown, value)
                OnPropertyChanged(NameOf(DrawdownColor))
            End Set
        End Property

        Private _statusMessage As String = "Loading..."
        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        Private _balanceHistoryRows As New ObservableCollection(Of BalanceHistoryRow)
        Public Property BalanceHistoryRows As ObservableCollection(Of BalanceHistoryRow)
            Get
                Return _balanceHistoryRows
            End Get
            Set(value As ObservableCollection(Of BalanceHistoryRow))
                If Not Object.Equals(_balanceHistoryRows, value) Then
                    _balanceHistoryRows = value
                    OnPropertyChanged(NameOf(BalanceHistoryRows))
                End If
            End Set
        End Property

        ' ── Balance history column headers (actual dates) ────────────────────

        Public ReadOnly Property Date1Header As String
            Get
                Return DateTime.Today.AddDays(-1).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date2Header As String
            Get
                Return DateTime.Today.AddDays(-2).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date3Header As String
            Get
                Return DateTime.Today.AddDays(-3).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date4Header As String
            Get
                Return DateTime.Today.AddDays(-4).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date5Header As String
            Get
                Return DateTime.Today.AddDays(-5).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date6Header As String
            Get
                Return DateTime.Today.AddDays(-6).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date7Header As String
            Get
                Return DateTime.Today.AddDays(-7).ToString("ddd MM/dd")
            End Get
        End Property

        Public ReadOnly Property Date8Header As String
            Get
                Return DateTime.Today.AddDays(-8).ToString("ddd MM/dd")
            End Get
        End Property

        ' ── Derived display properties ───────────────────────────────────────

        Public ReadOnly Property PnLColor As String
            Get
                Return If(_dailyPnL >= 0, "BuyBrush", "SellBrush")
            End Get
        End Property

        Public ReadOnly Property DrawdownColor As String
            Get
                Return If(_drawdown <= _riskSettings.MaxDrawdownDollars * 0.5D, "SellBrush", "WarningBrush")
            End Get
        End Property

        Public ReadOnly Property DailyLossLimit As Decimal
            Get
                Return _riskSettings.DailyLossLimitDollars
            End Get
        End Property

        Public ReadOnly Property MaxDrawdownLimit As Decimal
            Get
                Return _riskSettings.MaxDrawdownDollars
            End Get
        End Property

        ' ── Settings (Auto-Execution & Risk Guard) ──────────────────────────

        Private _autoExecutionEnabled As Boolean = False
        Public Property AutoExecutionEnabled As Boolean
            Get
                Return _autoExecutionEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_autoExecutionEnabled, value) Then
                    _riskSettings.AutoExecutionEnabled = value
                    _userPrefs.AutoExecutionEnabled = value
                    _userPrefs.Save()
                    _session.SetAutoExecution(value)
                    Task.Run(AddressOf RefreshAccountsAsync)
                End If
            End Set
        End Property

        Private _dailyLossLimitEditable As String
        Public Property DailyLossLimitEditable As String
            Get
                Return _dailyLossLimitEditable
            End Get
            Set(value As String)
                SetProperty(_dailyLossLimitEditable, value)
            End Set
        End Property

        Private _maxPositionEditable As String
        Public Property MaxPositionEditable As String
            Get
                Return _maxPositionEditable
            End Get
            Set(value As String)
                SetProperty(_maxPositionEditable, value)
            End Set
        End Property

        Private _minConfidenceEditable As String
        Public Property MinConfidenceEditable As String
            Get
                Return _minConfidenceEditable
            End Get
            Set(value As String)
                SetProperty(_minConfidenceEditable, value)
            End Set
        End Property

        Private _isConnected As Boolean
        Public Property IsConnected As Boolean
            Get
                Return _isConnected
            End Get
            Set(value As Boolean)
                SetProperty(_isConnected, value)
                OnPropertyChanged(NameOf(ConnectionStatusText))
                OnPropertyChanged(NameOf(ConnectionStatusColor))
            End Set
        End Property

        Public ReadOnly Property ConnectionStatusText As String
            Get
                Return If(_isConnected, "Connected", "Disconnected")
            End Get
        End Property

        Public ReadOnly Property ConnectionStatusColor As String
            Get
                Return If(_isConnected, "BuyBrush", "SellBrush")
            End Get
        End Property

        ' ── BUG-86 F3 — Stale open trades indicator ────────────────────────────

        Private _staleOpenCount As Integer = 0
        Public Property StaleOpenCount As Integer
            Get
                Return _staleOpenCount
            End Get
            Set(value As Integer)
                If SetProperty(_staleOpenCount, value) Then
                    OnPropertyChanged(NameOf(HasStaleOpen))
                    OnPropertyChanged(NameOf(StaleOpenWarningText))
                End If
            End Set
        End Property

        Public ReadOnly Property HasStaleOpen As Boolean
            Get
                Return _staleOpenCount > 0
            End Get
        End Property

        Public ReadOnly Property StaleOpenWarningText As String
            Get
                If _staleOpenCount <= 0 Then Return String.Empty
                Dim suffix = If(_staleOpenCount = 1, "trade", "trades")
                Return $"⚠  {_staleOpenCount} open {suffix} with no live position (>24 h)"
            End Get
        End Property

        Private _isReconciling As Boolean = False
        Public Property IsReconciling As Boolean
            Get
                Return _isReconciling
            End Get
            Set(value As Boolean)
                If SetProperty(_isReconciling, value) Then
                    ' RelayCommand routes CanExecuteChanged through CommandManager.RequerySuggested,
                    ' so invalidating the manager is the correct way to refresh button enablement.
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property RefreshCommand As RelayCommand
        Public ReadOnly Property ApplyRiskCommand As RelayCommand
        Public ReadOnly Property ConnectCommand As RelayCommand
        Public ReadOnly Property ReconcileNowCommand As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(accountService As IAccountService,
                       authService As IAuthService,
                       balanceHistoryService As IBalanceHistoryService,
                       session As ITradingSessionContext,
                       riskOptions As IOptions(Of RiskSettings),
                       userPrefs As IUserPreferencesService,
                       tradeRecord As ITradeRecordService,
                       logger As ILogger(Of DashboardViewModel))
            _accountService = accountService
            _authService = authService
            _balanceHistoryService = balanceHistoryService
            _session = session
            _riskSettings = riskOptions.Value
            _userPrefs = userPrefs
            _tradeRecord = tradeRecord
            _logger = logger

            ' Initialize from persisted session state (loaded from user-prefs.json at app startup)
            _autoExecutionEnabled = _session.AutoExecutionEnabled
            _riskSettings.AutoExecutionEnabled = _autoExecutionEnabled
            _dailyLossLimitEditable = _riskSettings.DailyLossLimitDollars.ToString()
            _maxPositionEditable = _riskSettings.MaxPositionSizeContracts.ToString()
            _minConfidenceEditable = (_riskSettings.MinSignalConfidence * 100).ToString("F0")
            _isConnected = _authService.IsAuthenticated

            RefreshCommand = New RelayCommand(AddressOf LoadData)
            ApplyRiskCommand = New RelayCommand(AddressOf ExecuteApplyRisk)
            ConnectCommand = New RelayCommand(AddressOf ExecuteConnect)
            ReconcileNowCommand = New RelayCommand(AddressOf ExecuteReconcileNow,
                                                   Function() Not _isReconciling)
        End Sub

        ' ── Data loading ─────────────────────────────────────────────────────

        Public Sub LoadDataAsync()
            LoadData()
        End Sub

        Private Sub LoadData()
            Task.Run(AddressOf LoadDataInternal)
        End Sub

        Private Async Function RefreshAccountsAsync() As Task
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             _accounts.Clear()
                             For Each account In accountList
                                 _accounts.Add(account)
                             Next
                             Dim practiceAccount = _accounts.FirstOrDefault(Function(a) a.Name.StartsWith("PRAC-", StringComparison.OrdinalIgnoreCase))
                             SelectedAccount = If(practiceAccount, _accounts.FirstOrDefault())
                         End Sub)
            Catch ex As Exception
                _logger.LogError(ex, "RefreshAccounts failed")
            End Try
        End Function

        Private Async Function LoadDataInternal() As Task
            Try
                ' Load accounts
                Dim accountList = Await _accountService.GetActiveAccountsAsync()

                ' Populate accounts collection and select default (Practice account preferred)
                Dispatch(Sub()
                             _accounts.Clear()
                             For Each account In accountList
                                 _accounts.Add(account)
                             Next

                             ' Default to Practice account (PRAC-*) if available, otherwise first account
                             ' CRITICAL FIX (TICKET-021): Get reference from _accounts collection, not accountList
                             ' This ensures WPF binding recognizes the selected item as existing in ItemsSource
                             Dim practiceAccount = _accounts.FirstOrDefault(Function(a) a.Name.StartsWith("PRAC-", StringComparison.OrdinalIgnoreCase))
                             SelectedAccount = If(practiceAccount, _accounts.FirstOrDefault())
                         End Sub)

                ' Record current balance in history for selected account.
                ' TotalValue is 0 for TopStepX futures accounts — fall back to Balance.
                If SelectedAccount IsNot Nothing Then
                    Dim balanceToRecord = If(SelectedAccount.TotalValue > 0, SelectedAccount.TotalValue, SelectedAccount.Balance)
                    Await _balanceHistoryService.RecordBalanceAsync(
                        SelectedAccount.Id,
                        SelectedAccount.Name,
                        balanceToRecord,
                        DateTime.UtcNow)
                End If

                ' Load balance history (last 5 days)
                Await LoadBalanceHistoryAsync(accountList)

                ' Load risk metrics
                Dispatch(Sub()
                             DailyPnL = 0
                             Drawdown = 0
                         End Sub)

                ' BUG-86 F3: refresh stale-open badge alongside the rest of the dashboard.
                Await RefreshStaleOpenAsync()

                Dispatch(Sub() StatusMessage = $"Updated {DateTime.Now:HH:mm:ss}")

            Catch ex As Exception
                Dispatch(Sub() StatusMessage = $"Error: {ex.Message}")
            End Try
        End Function

        Private Async Function LoadBalanceHistoryAsync(accounts As IEnumerable(Of Account)) As Task
            Try
                ' Get last 8 days of history for all accounts
                Dim history = Await _balanceHistoryService.GetAllAccountsRecentHistoryAsync(8)

                Dispatch(Sub()
                             _balanceHistoryRows.Clear()

                             For Each account In accounts
                                 Dim row = New BalanceHistoryRow With {
                                     .AccountName = account.Name,
                                     .CurrentBalance = If(account.TotalValue > 0, account.TotalValue, account.Balance)
                                 }

                                 ' Populate history dates (last 8 days)
                                 If history.ContainsKey(account.Id) Then
                                     Dim accountHistory = history(account.Id).OrderByDescending(Function(h) h.RecordedDate).ToList()

                                     ' Get balances for past 8 days
                                     For i = 0 To 7
                                         Dim dayAgo = DateTime.UtcNow.AddDays(-(i + 1)).Date
                                         Dim balance = accountHistory.FirstOrDefault(Function(h) h.RecordedDate = dayAgo)
                                         Select Case i
                                             Case 0
                                                 row.Date1Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 1
                                                 row.Date2Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 2
                                                 row.Date3Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 3
                                                 row.Date4Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 4
                                                 row.Date5Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 5
                                                 row.Date6Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 6
                                                 row.Date7Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                             Case 7
                                                 row.Date8Balance = If(balance IsNot Nothing, balance.Balance, CDec(0))
                                         End Select
                                     Next
                                 End If

                                 _balanceHistoryRows.Add(row)
                             Next
                         End Sub)

            Catch ex As Exception
                _logger.LogError(ex, "Error loading balance history")
            End Try
        End Function

        ' ── Settings command handlers ────────────────────────────────────────

        Private Sub ExecuteApplyRisk(param As Object)
            Try
                ' Parse and apply risk settings
                Dim dllimit As Decimal
                If Decimal.TryParse(_dailyLossLimitEditable, dllimit) Then
                    _riskSettings.DailyLossLimitDollars = dllimit
                End If

                Dim maxpos As Integer
                If Integer.TryParse(_maxPositionEditable, maxpos) Then
                    _riskSettings.MaxPositionSizeContracts = maxpos
                End If

                Dim minconf As Decimal
                If Decimal.TryParse(_minConfidenceEditable, minconf) Then
                    _riskSettings.MinSignalConfidence = CSng(minconf / 100D)
                End If

                StatusMessage = "✓ Risk settings applied"
            Catch ex As Exception
                StatusMessage = $"Error applying settings: {ex.Message}"
            End Try
        End Sub

        ' ── BUG-86 F3 — Stale-open trade reconcile-now ───────────────────────

        Private Async Function RefreshStaleOpenAsync() As Task
            Try
                Dim openTrades = Await _tradeRecord.GetOpenTradesAsync()
                Dim cutoff = DateTimeOffset.UtcNow - StaleOpenThreshold
                Dim stale = openTrades.Where(Function(t) t.EntryTime <= cutoff).Count()
                Dispatch(Sub() StaleOpenCount = stale)
            Catch ex As Exception
                _logger.LogWarning(ex, "RefreshStaleOpen failed")
            End Try
        End Function

        Private Sub ExecuteReconcileNow(param As Object)
            Task.Run(Async Function() As Task
                         Try
                             Dispatch(Sub()
                                          IsReconciling = True
                                          StatusMessage = "Reconciling open trades…"
                                      End Sub)
                             Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
                             If accountId = 0 Then
                                 Dispatch(Sub() StatusMessage = "Cannot reconcile — no account selected")
                                 Return
                             End If
                             Await _tradeRecord.RecoverOpenTradesAsync(accountId)
                             Await RefreshStaleOpenAsync()
                             Dispatch(Sub()
                                          StatusMessage = If(StaleOpenCount = 0,
                                                             "✓ Reconcile complete — no stale trades remain",
                                                             $"Reconcile complete — {StaleOpenCount} still stale")
                                      End Sub)
                         Catch ex As Exception
                             _logger.LogWarning(ex, "ReconcileNow failed")
                             Dispatch(Sub() StatusMessage = $"Reconcile failed: {ex.Message}")
                         Finally
                             Dispatch(Sub() IsReconciling = False)
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteConnect(param As Object)
            Task.Run(Async Function()
                         Try
                             Dispatch(Sub() StatusMessage = "Connecting...")
                             Dim token = Await _authService.LoginAsync("", "")
                             Dispatch(Sub()
                                          IsConnected = _authService.IsAuthenticated
                                          StatusMessage = If(IsConnected, "✓ Connected successfully", "Connection failed")
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusMessage = $"Connection error: {ex.Message}")
                         End Try
                         Return Task.CompletedTask
                     End Function)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ''' <summary>
    ''' Represents a single row in the balance history table.
    ''' </summary>
    Public Class BalanceHistoryRow
        Public Property AccountName As String = String.Empty
        Public Property CurrentBalance As Decimal
        Public Property Date1Balance As Decimal
        Public Property Date2Balance As Decimal
        Public Property Date3Balance As Decimal
        Public Property Date4Balance As Decimal
        Public Property Date5Balance As Decimal
        Public Property Date6Balance As Decimal
        Public Property Date7Balance As Decimal
        Public Property Date8Balance As Decimal
    End Class

End Namespace
