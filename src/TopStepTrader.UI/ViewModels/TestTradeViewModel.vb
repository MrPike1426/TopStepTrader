Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows
Imports TopStepTrader.API.Http
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Logging
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for the Test Trade page.
    ''' Combines EMA / RSI trend analysis with one-click test BUY / SELL order placement.
    ''' Ingests fresh 1-hour bars before running the weighted multi-indicator scoring system.
    ''' </summary>
    Public Class TestTradeViewModel
        Inherits ViewModelBase
        Implements IDisposable

        ' ── Dependencies ──────────────────────────────────────────────────────────
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _pxHistoryClient As PXHistoryClient
        Private ReadOnly _catalog As TopStepXInstrumentCatalog
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _claudeService As IClaudeReviewService

        Private _isBarsLoaded As Boolean = False

        Public Property IsBarsLoaded As Boolean
            Get
                Return _isBarsLoaded
            End Get
            Set(value As Boolean)
                If SetProperty(_isBarsLoaded, value) Then
                    NotifyPropertyChanged(NameOf(CanPlaceTestTrade))
                    NotifyPropertyChanged(NameOf(BarStatusText))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _isLoadingBars As Boolean = False

        Public Property IsLoadingBars As Boolean
            Get
                Return _isLoadingBars
            End Get
            Set(value As Boolean)
                If SetProperty(_isLoadingBars, value) Then
                    NotifyPropertyChanged(NameOf(BarStatusText))
                End If
            End Set
        End Property

        ''' <summary>Human-readable bar loading status shown beneath the Claude Haiku button.</summary>
        Public ReadOnly Property BarStatusText As String
            Get
                If IsLoadingBars Then Return "⏳ Downloading bar history…"
                If IsBarsLoaded Then Return "✅ Bar history ready — click to request advice."
                Return "Select a contract to begin downloading bar history."
            End Get
        End Property

        Public Sub New(orderService As IOrderService,
                       accountService As IAccountService,
                       pxHistoryClient As PXHistoryClient,
                       catalog As TopStepXInstrumentCatalog,
                       session As ITradingSessionContext,
                       claudeService As IClaudeReviewService)
            _orderService = orderService
            _accountService = accountService
            _pxHistoryClient = pxHistoryClient
            _catalog = catalog
            _session = session
            _claudeService = claudeService

            AddHandler _orderService.OrderFilled, AddressOf OnTestOrderFilled
            AddHandler _orderService.OrderRejected, AddressOf OnTestOrderRejected
            AddHandler _orderService.PositionUpdated, AddressOf OnPositionUpdated
            AddHandler DebugLog.MessageLogged, AddressOf OnDebugLog

            ' Initialize strategy commands
            StartStrategyBuyCommand = New RelayCommand(Sub() StartStrategy(OrderSide.Buy), Function() CanStartStrategy)
            StartStrategySellCommand = New RelayCommand(Sub() StartStrategy(OrderSide.Sell), Function() CanStartStrategy)
            StopStrategyCommand = New RelayCommand(AddressOf StopStrategy, Function() IsStrategyRunning)
            CloseTradeCommand = New RelayCommand(Sub() Task.Run(AddressOf CloseTradeAsync), Function() IsStrategyRunning AndAlso Not _isClosingTrade)
            SelectOilCommand = New RelayCommand(Sub() Task.Run(AddressOf SelectOilAsync))

            NudgeBracketCommand = New RelayCommand(AddressOf NudgeBracket, Function() IsBarsLoaded)
            SelectAtrTierCommand = New RelayCommand(Of String)(AddressOf ApplyAtrTier)

            ClearDebugCommand = New RelayCommand(AddressOf ClearDebug)

            AnalyseCommand = New RelayCommand(AddressOf RunNakedTraderAnalysis,
                                              Function() CanPlaceTestTrade AndAlso Not IsAnalysing)

            GetClaudeAdviceCommand = New RelayCommand(AddressOf GetClaudeAdvice, Function() IsBarsLoaded AndAlso Not IsClaudeAdvising)
            AddHandler _session.AutoExecutionChanged, AddressOf OnAutoExecutionChanged
        End Sub

        Public Property ClearDebugCommand As RelayCommand

        Public Async Function LoadDataAsync() As Task
            Try
                Dim accountsList = Await _accountService.GetActiveAccountsAsync()
                Dim count = accountsList.Count()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In accountsList
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 ' Prefer the account already chosen on the Dashboard (session context).
                                 Dim sessionAcc = _session.SelectedAccount
                                 Dim preferred = If(sessionAcc IsNot Nothing,
                                     Accounts.FirstOrDefault(Function(a) a.Id = sessionAcc.Id),
                                     Nothing)
                                 SelectedAccount = If(preferred, Accounts(0))
                                 ' Apply broker-appropriate defaults on first load
                                 If IsTopStepX Then
                                     TestTradeAmount = "1"        ' contracts
                                     TestTradeStopLoss = "20"     ' ticks
                                     TestTradeTakeProfit = "60"   ' ticks (3:1 R:R)
                                 End If
                                 AddDebugMessage($"✅ {count} account(s) loaded. Selected: {SelectedAccount.DisplayName}")
                             Else
                                 AddDebugMessage("⚠️ No accounts returned. Check credentials and broker selection in API Keys.")
                             End If
                         End Sub)
                ' Populate LIVE POSITION panel if a contract is already selected
                Await RefreshPositionDisplayAsync()
            Catch ex As Exception
                DebugLog.Log($"Error loading account data: {ex.Message}")
                Dispatch(Sub() AddDebugMessage($"❌ Account load error: {ex.Message}"))
            End Try
        End Function

        Private Sub OnAutoExecutionChanged(sender As Object, e As EventArgs)
            Task.Run(AddressOf RefreshAccountsAsync)
        End Sub

        Private Async Function RefreshAccountsAsync() As Task
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In accountList
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 Dim preferred = Accounts.FirstOrDefault(
                                     Function(a) a.Name IsNot Nothing AndAlso
                                                 a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase))
                                 SelectedAccount = If(preferred, Accounts(0))
                             End If
                         End Sub)
            Catch
            End Try
        End Function

        ' ── Internal state ────────────────────────────────────────────────────────
        Private _disposed As Boolean = False
        Private ReadOnly _debugMessages As New List(Of String)()

        ' Cached bars for the selected contract (populated by LoadBarsAsync, used by Claude + NakedTrader)
        Private _cachedBars As List(Of MarketBar) = Nothing
        Private Const CLAUDE_MIN_BARS As Integer = 48   ' 48 × 5-min = 4 hours

        ' ── ATR bracket management ────────────────────────────────────────────────
        Private _slMultipleOfN As Decimal = 1.5D   ' Standard tier default (Damian)
        Private _tpMultipleOfN As Decimal = 3.0D
        Private _extendTpOnClose As Boolean = True
        Private _selectedAtrTier As String = "Standard"
        ' Per-trade state — reset on every position close
        Private _initialSlPrice As Decimal = 0D   ' guards against premature trail on first bar
        Private _tpAdvanceCount As Integer = 0    ' max 3 TP advances per trade

        ' ── Account selection ─────────────────────────────────────────────────────
        Public Property Accounts As New ObservableCollection(Of Account)()

        Private _selectedAccount As Account
        Public Property SelectedAccount As Account
            Get
                Return _selectedAccount
            End Get
            Set(value As Account)
                If SetProperty(_selectedAccount, value) Then
                    NotifyPropertyChanged(NameOf(CanPlaceTestTrade))
                    NotifyPropertyChanged(NameOf(IsTopStepX))
                    NotifyPropertyChanged(NameOf(IsNotTopStepX))
                    NotifyPropertyChanged(NameOf(AmountLabel))
                    NotifyPropertyChanged(NameOf(SlLabel))
                    NotifyPropertyChanged(NameOf(TpLabel))
                    NotifyPropertyChanged(NameOf(TickSizeHintText))
                    RelayCommand.RaiseCanExecuteChanged()
                    ' Refresh live position panel immediately for the new account
                    ClearLivePositionDisplay()
                    Task.Run(AddressOf RefreshPositionDisplayAsync)
                    ' Start loading bars if contract is already selected
                    If Not String.IsNullOrWhiteSpace(_testTradeContractId) Then
                        _cachedBars = Nothing
                        IsBarsLoaded = False
                        Task.Run(AddressOf LoadBarsAsync)
                    End If
                End If
            End Set
        End Property

        ' ── Contract selection ─────────────────────────────────────────────────────
        Public Property AvailableContracts As New ObservableCollection(Of Contract)()

        Private _testTradeSelectedContract As Contract
        Public Property TestTradeSelectedContract As Contract
            Get
                Return _testTradeSelectedContract
            End Get
            Set(value As Contract)
                If SetProperty(_testTradeSelectedContract, value) Then
                    UpdateTestTradeContractDisplay()
                End If
            End Set
        End Property

        Private _testTradeContractId As String = String.Empty
        Public Property TestTradeContractId As String
            Get
                Return _testTradeContractId
            End Get
            Set(value As String)
                If SetProperty(_testTradeContractId, value) Then
                    UpdateTestTradeContractDisplay()
                    NotifyPropertyChanged(NameOf(CanPlaceTestTrade))
                    RelayCommand.RaiseCanExecuteChanged()
                    ' Clear stale live-position display immediately, then repopulate for new contract
                    ClearLivePositionDisplay()
                    _cachedBars = Nothing
                    ' Reset Claude section to a neutral waiting state — only the button can trigger a call
                    ClaudeAdvice = "—"
                    ClaudeRationale = "Waiting for bar history to load…"
                    If _selectedAccount IsNot Nothing Then
                        IsBarsLoaded = False
                        Task.Run(AddressOf LoadBarsAsync)
                        RunNakedTraderAnalysis()
                        Task.Run(AddressOf RefreshPositionDisplayAsync)
                    End If
                End If
            End Set
        End Property

        Private _testTradeInstrumentId As Integer = 0
        Public Property TestTradeInstrumentId As Integer
            Get
                Return _testTradeInstrumentId
            End Get
            Set(value As Integer)
                SetProperty(_testTradeInstrumentId, value)
            End Set
        End Property

        Private _testTradeContractLongId As String = String.Empty
        Public Property TestTradeContractLongId As String
            Get
                Return _testTradeContractLongId
            End Get
            Set(value As String)
                SetProperty(_testTradeContractLongId, value)
            End Set
        End Property

        Private _testTradeContractDisplay As String = "—"
        Public Property TestTradeContractDisplay As String
            Get
                Return _testTradeContractDisplay
            End Get
            Set(value As String)
                SetProperty(_testTradeContractDisplay, value)
            End Set
        End Property

        ' ── Trade parameters ──────────────────────────────────────────────────────
        Private _testTradeQuantity As String = "1"
        Public Property TestTradeQuantity As String
            Get
                Return _testTradeQuantity
            End Get
            Set(value As String)
                SetProperty(_testTradeQuantity, value)
            End Set
        End Property

        Private _testTradeAmount As String = "500"
        Public Property TestTradeAmount As String
            Get
                Return _testTradeAmount
            End Get
            Set(value As String)
                SetProperty(_testTradeAmount, value)
            End Set
        End Property

        Private _testTradeStopLoss As String = "20"
        Public Property TestTradeStopLoss As String
            Get
                Return _testTradeStopLoss
            End Get
            Set(value As String)
                SetProperty(_testTradeStopLoss, value)
            End Set
        End Property

        Private _testTradeTakeProfit As String = "60"
        Public Property TestTradeTakeProfit As String
            Get
                Return _testTradeTakeProfit
            End Get
            Set(value As String)
                SetProperty(_testTradeTakeProfit, value)
            End Set
        End Property

        Private _testTradeStatus As String
        Public Property TestTradeStatus As String
            Get
                Return _testTradeStatus
            End Get
            Set(value As String)
                SetProperty(_testTradeStatus, value)
            End Set
        End Property

        Private _debugText As String = String.Empty
        Public Property DebugText As String
            Get
                Return _debugText
            End Get
            Set(value As String)
                SetProperty(_debugText, value)
            End Set
        End Property

        Private _pendingEntryOrderId As Long? = Nothing
        Private _pendingEntryCorrelationId As String = String.Empty

        ' ── Strategy Control ──────────────────────────────────────────────────────
        Private _isStrategyRunning As Boolean
        Public Property IsStrategyRunning As Boolean
            Get
                Return _isStrategyRunning
            End Get
            Set(value As Boolean)
                If SetProperty(_isStrategyRunning, value) Then
                    RelayCommand.RaiseCanExecuteChanged()
                    Dispatch(Sub() TestTradeStatus = If(value, "Strategy: RUNNING - Monitoring 5s bars...", "Strategy: STOPPED"))
                End If
            End Set
        End Property

        Public Property StartStrategyBuyCommand As RelayCommand
        Public Property StartStrategySellCommand As RelayCommand
        Public Property StopStrategyCommand As RelayCommand
        Public Property CloseTradeCommand As RelayCommand
        Public Property SelectOilCommand As RelayCommand
        Public Property NudgeBracketCommand As RelayCommand
        Public Property SelectAtrTierCommand As RelayCommand(Of String)

        ' ── ATR bracket management properties ────────────────────────────────────
        Public Property SlMultipleOfN As Decimal
            Get
                Return _slMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_slMultipleOfN, value)
            End Set
        End Property

        Public Property TpMultipleOfN As Decimal
            Get
                Return _tpMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_tpMultipleOfN, value)
            End Set
        End Property

        Public Property ExtendTpOnClose As Boolean
            Get
                Return _extendTpOnClose
            End Get
            Set(value As Boolean)
                SetProperty(_extendTpOnClose, value)
            End Set
        End Property

        Public ReadOnly Property IsAtrTightSelected As Boolean
            Get
                Return _selectedAtrTier = "Tight"
            End Get
        End Property

        Public ReadOnly Property IsAtrStandardSelected As Boolean
            Get
                Return _selectedAtrTier = "Standard"
            End Get
        End Property

        Public ReadOnly Property IsAtrWideSelected As Boolean
            Get
                Return _selectedAtrTier = "Wide"
            End Get
        End Property

        Private Sub ApplyAtrTier(tier As String)
            _selectedAtrTier = tier
            Select Case tier
                Case "Tight"
                    SlMultipleOfN = 0.75D
                    TpMultipleOfN = 1.5D
                Case "Standard"
                    SlMultipleOfN = 1.5D
                    TpMultipleOfN = 3.0D
                Case "Wide"
                    SlMultipleOfN = 2.5D
                    TpMultipleOfN = 5.0D
            End Select
            NotifyPropertyChanged(NameOf(IsAtrTightSelected))
            NotifyPropertyChanged(NameOf(IsAtrStandardSelected))
            NotifyPropertyChanged(NameOf(IsAtrWideSelected))
            AddDebugMessage($"📐 ATR tier: {tier} — SL={SlMultipleOfN:F2}×N  TP={TpMultipleOfN:F2}×N")
        End Sub

        Private _oilContractLabel As String = "click to activate"
        Public Property OilContractLabel As String
            Get
                Return _oilContractLabel
            End Get
            Set(value As String)
                SetProperty(_oilContractLabel, value)
            End Set
        End Property
        Public Property AnalyseCommand As RelayCommand
        Public Property GetClaudeAdviceCommand As RelayCommand

        Public ReadOnly Property CanStartStrategy As Boolean
            Get
                Return Not IsStrategyRunning AndAlso CanPlaceTestTrade
            End Get
        End Property

        ' ── Naked Trader analysis ─────────────────────────────────────────────

        Private _isAnalysing As Boolean
        Public Property IsAnalysing As Boolean
            Get
                Return _isAnalysing
            End Get
            Set(value As Boolean)
                If SetProperty(_isAnalysing, value) Then
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _nakedTraderDirection As String = "—"
        Public Property NakedTraderDirection As String
            Get
                Return _nakedTraderDirection
            End Get
            Set(value As String)
                SetProperty(_nakedTraderDirection, value)
            End Set
        End Property

        Private _nakedTraderConfidence As String = "—"
        Public Property NakedTraderConfidence As String
            Get
                Return _nakedTraderConfidence
            End Get
            Set(value As String)
                SetProperty(_nakedTraderConfidence, value)
            End Set
        End Property

        Private _nakedTraderSummary As String = String.Empty
        Public Property NakedTraderSummary As String
            Get
                Return _nakedTraderSummary
            End Get
            Set(value As String)
                SetProperty(_nakedTraderSummary, value)
            End Set
        End Property

        Public ReadOnly Property NakedTraderDirectionColor As String
            Get
                If _nakedTraderDirection.Contains("BUY") Then Return "BuyBrush"
                If _nakedTraderDirection.Contains("SELL") Then Return "SellBrush"
                Return "NeutralBrush"
            End Get
        End Property

        ' ── Claude Haiku trade advice ──────────────────────────────────────────
        Private _claudeAdvice As String = "—"
        Public Property ClaudeAdvice As String
            Get
                Return _claudeAdvice
            End Get
            Set(value As String)
                SetProperty(_claudeAdvice, value)
            End Set
        End Property

        Private _claudeRationale As String = "Select a contract, then click 'Request Trade Advice' once bar history is ready."
        Public Property ClaudeRationale As String
            Get
                Return _claudeRationale
            End Get
            Set(value As String)
                SetProperty(_claudeRationale, value)
            End Set
        End Property

        Private _isClaudeAdvising As Boolean
        Public Property IsClaudeAdvising As Boolean
            Get
                Return _isClaudeAdvising
            End Get
            Set(value As Boolean)
                SetProperty(_isClaudeAdvising, value)
            End Set
        End Property

        ' ── Force Close at $X profit ─────────────────────────────────────────────
        Private _forceCloseEnabled As Boolean = False
        Public Property ForceCloseEnabled As Boolean
            Get
                Return _forceCloseEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_forceCloseEnabled, value)
            End Set
        End Property

        Private _forceCloseAmount As Decimal = 100D
        Public Property ForceCloseAmount As Decimal
            Get
                Return _forceCloseAmount
            End Get
            Set(value As Decimal)
                SetProperty(_forceCloseAmount, Math.Max(0D, value))
            End Set
        End Property

        ' ── P&L Monitoring ────────────────────────────────────────────────────────
        Private _currentPnL As Decimal = 0
        Public Property CurrentPnL As Decimal
            Get
                Return _currentPnL
            End Get
            Set(value As Decimal)
                If SetProperty(_currentPnL, value) Then
                    OnPropertyChanged(NameOf(CurrentPnLText))
                    OnPropertyChanged(NameOf(PnLColor))
                End If
            End Set
        End Property

        Public ReadOnly Property CurrentPnLText As String
            Get
                If _currentPnL = 0 Then Return "—"
                Return If(_currentPnL >= 0, $"+${_currentPnL:F2}", $"-${Math.Abs(_currentPnL):F2}")
            End Get
        End Property

        Public ReadOnly Property PnLColor As String
            Get
                Return If(_currentPnL >= 0, "BuyBrush", "SellBrush")
            End Get
        End Property

        ' ── Live position display (updated every 5s by MonitorPnL) ───────────────
        Private _liveDollarPerPoint As Decimal = 0D
        Private _liveCurrentPrice As Decimal = 0D
        Private _liveSlPrice As Decimal = 0D
        Private _liveTpPrice As Decimal = 0D

        Public Property LiveCurrentPrice As Decimal
            Get
                Return _liveCurrentPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_liveCurrentPrice, value) Then
                    OnPropertyChanged(NameOf(LiveCurrentPriceText))
                    OnPropertyChanged(NameOf(LiveSlDollarText))
                    OnPropertyChanged(NameOf(LiveTpDollarText))
                End If
            End Set
        End Property

        Public Property LiveSlPrice As Decimal
            Get
                Return _liveSlPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_liveSlPrice, value) Then
                    OnPropertyChanged(NameOf(LiveSlPriceText))
                    OnPropertyChanged(NameOf(LiveSlDollarText))
                End If
            End Set
        End Property

        Public Property LiveTpPrice As Decimal
            Get
                Return _liveTpPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_liveTpPrice, value) Then
                    OnPropertyChanged(NameOf(LiveTpPriceText))
                    OnPropertyChanged(NameOf(LiveTpDollarText))
                End If
            End Set
        End Property

        Public ReadOnly Property LiveCurrentPriceText As String
            Get
                Return If(_liveCurrentPrice > 0D, $"{_liveCurrentPrice:F2}", "—")
            End Get
        End Property

        Public ReadOnly Property LiveSlPriceText As String
            Get
                Return If(_liveSlPrice > 0D, $"{_liveSlPrice:F2}", "—")
            End Get
        End Property

        Public ReadOnly Property LiveTpPriceText As String
            Get
                Return If(_liveTpPrice > 0D, $"{_liveTpPrice:F2}", "—")
            End Get
        End Property

        Public ReadOnly Property LiveSlDollarText As String
            Get
                If _liveSlPrice <= 0D OrElse _liveCurrentPrice <= 0D OrElse _liveDollarPerPoint <= 0D Then Return "—"
                Return $"-${Math.Round(Math.Abs(_liveCurrentPrice - _liveSlPrice) * _liveDollarPerPoint, 2):F2}"
            End Get
        End Property

        Public ReadOnly Property LiveTpDollarText As String
            Get
                If _liveTpPrice <= 0D OrElse _liveCurrentPrice <= 0D OrElse _liveDollarPerPoint <= 0D Then Return "—"
                Return $"+${Math.Round(Math.Abs(_liveCurrentPrice - _liveTpPrice) * _liveDollarPerPoint, 2):F2}"
            End Get
        End Property

        Private _liveEntryPrice As Decimal = 0D
        Public Property LiveEntryPrice As Decimal
            Get
                Return _liveEntryPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_liveEntryPrice, value) Then
                    OnPropertyChanged(NameOf(LiveEntryPriceText))
                End If
            End Set
        End Property

        Public ReadOnly Property LiveEntryPriceText As String
            Get
                Return If(_liveEntryPrice > 0D, $"{_liveEntryPrice:F2}", "—")
            End Get
        End Property

        Private _liveEntryOrderIdText As String = "—"
        Public Property LiveEntryOrderIdText As String
            Get
                Return _liveEntryOrderIdText
            End Get
            Set(value As String)
                SetProperty(_liveEntryOrderIdText, value)
            End Set
        End Property

        Private _entryOrderId As Long? = Nothing
        Private _pnlMonitoringTimer As System.Threading.Timer
        Private _tradeManagementTimer As System.Threading.Timer
        Private _tradeManagementRunning As Integer = 0  ' Interlocked reentrancy guard
        Private ReadOnly _pnlLastCheckTime As DateTimeOffset = DateTimeOffset.UtcNow

        ' ── Bar Aggregation ──────────────────────────────────────────────────────
        Private ReadOnly _strategyLock As New Object()

        Private Sub StartStrategy(side As OrderSide)
            If Not CanStartStrategy Then Return

            IsStrategyRunning = True
            _entrySide = side          ' Store early so PositionUpdated handler knows direction
            CurrentPnL = 0  ' Reset P&L display
            _entryOrderId = Nothing

            SyncLock _strategyLock
                _isOrderPending = True
                _isPositionActive = False
            End SyncLock

            Dim t2 = ExecuteTestTrade(side)

            ' Start P&L monitoring (will be picked up after order fills)
            StartPnLMonitoring()
        End Sub

        ''' <summary>
        ''' Clears all live-position display fields synchronously on the UI thread.
        ''' Called immediately when account or contract selection changes so stale data
        ''' from a previous contract is never visible while the async API call is in flight.
        ''' </summary>
        Private Sub ClearLivePositionDisplay()
            Dispatch(Sub()
                         CurrentPnL = 0
                         LiveCurrentPrice = 0D
                         LiveSlPrice = 0D
                         LiveTpPrice = 0D
                         LiveEntryPrice = 0D
                         LiveEntryOrderIdText = "—"
                         _liveDollarPerPoint = 0D
                     End Sub)
        End Sub

        ''' <summary>
        ''' One-shot read-only snapshot of the broker position for the currently selected
        ''' account/contract.  Populates the LIVE POSITION panel without touching any
        ''' strategy-state fields (<c>_isPositionActive</c>, <c>_currentSlPrice</c>, etc.).
        ''' Safe to call at any time — skips silently if account or contract is not set.
        ''' </summary>
        Private Async Function RefreshPositionDisplayAsync() As Task
            Try
                If _selectedAccount Is Nothing OrElse String.IsNullOrWhiteSpace(_testTradeContractId) Then Return

                Dim snap = Await _orderService.GetLivePositionSnapshotAsync(
                    _selectedAccount.Id, _testTradeContractId)

                If snap Is Nothing Then Return   ' no open position — display already cleared

                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                Dim tv = If(fav IsNot Nothing AndAlso fav.PxTickValue > 0, fav.PxTickValue, 1.25D)
                Dim ts = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0, fav.PxTickSize, 0.25D)
                Dim units = If(snap.Units > 0D, snap.Units, snap.Amount)
                Dim dpp = If(ts > 0D, units * tv / ts, 0D)

                Dim currentPrice = snap.OpenRate
                If snap.UnrealizedPnlUsd <> 0D AndAlso dpp > 0D Then
                    Dim pnlPts = snap.UnrealizedPnlUsd / dpp
                    currentPrice = snap.OpenRate + If(snap.IsBuy, pnlPts, -pnlPts)
                End If

                ' Only show SL/TP if this session is actively tracking a position
                ' (i.e. the tracked bracket prices belong to the current contract).
                Dim showSl = If(_isPositionActive AndAlso _currentSlPrice > 0D, _currentSlPrice, 0D)
                Dim showTp = If(_isPositionActive AndAlso _currentTpPrice > 0D, _currentTpPrice, 0D)

                Dispatch(Sub()
                             _liveDollarPerPoint = dpp
                             CurrentPnL = snap.UnrealizedPnlUsd
                             LiveCurrentPrice = currentPrice
                             LiveSlPrice = showSl
                             LiveTpPrice = showTp
                         End Sub)

                ' Position found — start continuous P&L monitoring if not already running
                ' so the panel keeps updating for the life of the trade without requiring
                ' the user to click BUY or SELL first.
                If _pnlMonitoringTimer Is Nothing Then StartPnLMonitoring()
            Catch ex As Exception
                DebugLog.Log($"RefreshPositionDisplayAsync error: {ex.Message}")
            End Try
        End Function

        Private Sub StartPnLMonitoring()
            ' Stop any existing timer
            _pnlMonitoringTimer?.Dispose()

            ' P&L monitoring every 5s — price sourced from 15-sec bar via FetchCurrentPriceAsync.
            ' TopStepX openPnL is always 0 for futures so we compute from price delta × DPP.
            _pnlMonitoringTimer = New System.Threading.Timer(
                AddressOf MonitorPnL,
                Nothing,
                TimeSpan.FromSeconds(1),  ' Initial delay
                TimeSpan.FromSeconds(5))  ' Period

            ' Trade management timer (bracket creation / nudge checks)
            _tradeManagementTimer?.Dispose()
            System.Threading.Interlocked.Exchange(_tradeManagementRunning, 0)
            _tradeManagementTimer = New System.Threading.Timer(
                AddressOf ManageTradeCallback,
                Nothing,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15))
        End Sub

        Private Sub MonitorPnL(state As Object)
            Task.Run(Async Function() As Task
                         Try
                             If _selectedAccount Is Nothing OrElse
                       String.IsNullOrWhiteSpace(_testTradeContractId) Then Return

                             Dim snap = Await _orderService.GetLivePositionSnapshotAsync(
                        _selectedAccount.Id, _testTradeContractId)

                             If snap Is Nothing Then
                                 ' No live position found — reset all position state so the next fill
                                 ' event seeds bracket prices correctly (prevents stuck _isPositionActive).
                                 If _isPositionActive Then
                                     SyncLock _strategyLock
                                         _isPositionActive = False
                                         _currentSlPrice = 0D
                                         _currentTpPrice = 0D
                                         _initialSlPrice = 0D
                                         _tpAdvanceCount = 0
                                     End SyncLock
                                     _livePositionId = Nothing
                                     Dispatch(Sub()
                                                  CurrentPnL = 0
                                                  LiveCurrentPrice = 0D
                                                  LiveSlPrice = 0D
                                                  LiveTpPrice = 0D
                                                  _liveDollarPerPoint = 0D
                                              End Sub)
                                     ' If we started monitoring via RefreshPositionDisplayAsync
                                     ' (not via BUY/SELL) stop the timer now the position is gone.
                                     If Not _isStrategyRunning Then StopPnLMonitoring()
                                 End If
                                 Return
                             End If

                             ' ── Fallback: auto-seed position state when SignalR fill event was missed ──
                             ' GatewayUserPosition is not guaranteed to fire (hub lag, reconnect, etc.).
                             ' The first API poll tick that finds a live position seeds _isPositionActive,
                             ' _entryPrice, and bracket prices so CreateBracket and Nudge work correctly.
                             If Not _isPositionActive AndAlso snap.OpenRate > 0D Then
                                 SyncLock _strategyLock
                                     If Not _isPositionActive Then
                                         _isPositionActive = True
                                         _isOrderPending = False
                                         _entryPrice = snap.OpenRate
                                         _entrySide = If(snap.IsBuy, OrderSide.Buy, OrderSide.Sell)
                                         If _currentSlPrice <= 0D AndAlso _pendingSlTicks > 0 Then
                                             Dim favBracket = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                                             Dim tsBracket = If(favBracket IsNot Nothing AndAlso favBracket.PxTickSize > 0,
                                                       favBracket.PxTickSize, 0.25D)
                                             _currentSlPrice = If(snap.IsBuy,
                                        snap.OpenRate - _pendingSlTicks * tsBracket,
                                        snap.OpenRate + _pendingSlTicks * tsBracket)
                                             _currentTpPrice = If(snap.IsBuy,
                                        snap.OpenRate + _pendingTpTicks * tsBracket,
                                        snap.OpenRate - _pendingTpTicks * tsBracket)
                                         End If
                                         Dispatch(Sub() AddDebugMessage(
                                    $"🔗 Position detected via API poll — entry={snap.OpenRate:F4} " &
                                    $"side={If(snap.IsBuy, "BUY", "SELL")} | " &
                                    $"SL≈{_currentSlPrice:F4} TP≈{_currentTpPrice:F4} | Nudge ready."))
                                     End If
                                 End SyncLock
                             End If

                             ' DPP = (tickValue / tickSize) × contracts
                             Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                             Dim tv = If(fav IsNot Nothing AndAlso fav.PxTickValue > 0, fav.PxTickValue, 1.25D)
                             Dim ts = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0, fav.PxTickSize, 0.25D)
                             Dim units = If(snap.Units > 0D, snap.Units, snap.Amount)
                             Dim dpp = If(ts > 0D, units * tv / ts, 0D)
                             _liveDollarPerPoint = dpp

                             ' Fetch current market price via 15-sec bar (TopStepX openPnL is always 0 for futures)
                             Dim currentPrice As Decimal = Await FetchCurrentPriceAsync(CancellationToken.None)
                             If currentPrice <= 0D Then currentPrice = snap.OpenRate  ' fallback to entry price

                             ' Compute P&L from bar price delta (avoids relying on broker-supplied openPnL=0)
                             Dim computedPnl As Decimal = 0D
                             If currentPrice > 0D AndAlso snap.OpenRate > 0D AndAlso dpp > 0D Then
                                 Dim priceDiff = If(snap.IsBuy, currentPrice - snap.OpenRate, snap.OpenRate - currentPrice)
                                 computedPnl = priceDiff * dpp
                             End If

                             ' Seed entry price display on first detection
                             If _liveEntryPrice <= 0D AndAlso snap.OpenRate > 0D Then
                                 Dispatch(Sub()
                                              LiveEntryPrice = snap.OpenRate
                                              If _entryOrderId.HasValue Then
                                                  LiveEntryOrderIdText = $"Entry #{_entryOrderId.Value}"
                                              End If
                                          End Sub)
                             End If

                             _livePositionId = snap.PositionId   ' cache for CreateBracket / NudgeBracket
                             ' ── Force Close at $X profit check ────────────────────
                             If _forceCloseEnabled AndAlso computedPnl >= _forceCloseAmount AndAlso _isPositionActive Then
                                 Try
                                     Dim ok = Await _orderService.FlattenContractAsync(_selectedAccount.Id, _testTradeContractId)
                                     Dispatch(Sub() AddDebugMessage(
                                         $"💰 Force-close triggered — P&L=${computedPnl:F2} >= ${_forceCloseAmount:F2} — closed: {ok}"))
                                     If ok Then
                                         SyncLock _strategyLock
                                             _isPositionActive = False
                                             _isOrderPending = False
                                             _entryPrice = 0
                                             _currentSlPrice = 0D
                                             _currentTpPrice = 0D
                                             _tpOrderId = Nothing
                                             _slOrderId = Nothing
                                             _livePositionId = Nothing
                                             _initialSlPrice = 0D
                                             _tpAdvanceCount = 0
                                         End SyncLock
                                         Dispatch(Sub()
                                                      IsStrategyRunning = False
                                                      RelayCommand.RaiseCanExecuteChanged()
                                                  End Sub)
                                         StopPnLMonitoring()
                                         Return
                                     End If
                                 Catch fcEx As Exception
                                     Dispatch(Sub() AddDebugMessage($"❌ Force-close error: {fcEx.Message}"))
                                 End Try
                             End If

                             Dispatch(Sub()
                                           CurrentPnL = computedPnl
                                           LiveCurrentPrice = currentPrice
                                           LiveSlPrice = _currentSlPrice
                                           LiveTpPrice = _currentTpPrice
                                       End Sub)
                         Catch ex As Exception
                              DebugLog.Log($"MonitorPnL error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub StopPnLMonitoring()
            If _pnlMonitoringTimer IsNot Nothing Then
                _pnlMonitoringTimer.Dispose()
                _pnlMonitoringTimer = Nothing
            End If
            If _tradeManagementTimer IsNot Nothing Then
                _tradeManagementTimer.Dispose()
                _tradeManagementTimer = Nothing
            End If
            Dispatch(Sub()
                         CurrentPnL = 0
                         LiveCurrentPrice = 0D
                         LiveSlPrice = 0D
                         LiveTpPrice = 0D
                         LiveEntryPrice = 0D
                         LiveEntryOrderIdText = "—"
                         _liveDollarPerPoint = 0D
                     End Sub)
        End Sub

        ' ── 15-second active trade management ─────────────────────────────────────

        Private Sub ManageTradeCallback(state As Object)
            If System.Threading.Interlocked.CompareExchange(_tradeManagementRunning, 1, 0) <> 0 Then Return
            Task.Run(Async Function() As Task
                         Try
                             Await ManageTradeAsync()
                         Finally
                             System.Threading.Interlocked.Exchange(_tradeManagementRunning, 0)
                         End Try
                     End Function)
        End Sub

        ''' <summary>
        ''' Runs every 15 seconds while a position is open.
        ''' ATR-based SL ratchet: trails the stop at SlMultipleOfN × ATR(14) behind current price.
        ''' ExtendTP: when ExtendTpOnClose=True and the last bar closed beyond the TP, advances
        ''' the TP by TpMultipleOfN × ATR (up to 3 times per trade).
        ''' </summary>
        Private Async Function ManageTradeAsync() As Task
            Try
                If Not _isPositionActive Then Return
                If _selectedAccount Is Nothing OrElse String.IsNullOrWhiteSpace(_testTradeContractId) Then Return
                If Not _livePositionId.HasValue OrElse _livePositionId.Value = 0 Then Return
                If _entryPrice <= 0D OrElse _currentSlPrice <= 0D Then Return

                ' Seed initial-SL guard on first management tick
                If _initialSlPrice <= 0D Then _initialSlPrice = _currentSlPrice

                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                Dim tickSize As Decimal = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0, fav.PxTickSize, 0.25D)
                Dim isBuy = (_entrySide = OrderSide.Buy)

                ' ── Compute ATR(14) and last bar close from cached bars ────────────
                Dim atr14 As Decimal = 0D
                Dim lastBarClose As Decimal = 0D
                If _cachedBars IsNot Nothing AndAlso _cachedBars.Count >= 14 Then
                    Dim highs = _cachedBars.Select(Function(b) CDec(b.High)).ToList()
                    Dim lows = _cachedBars.Select(Function(b) CDec(b.Low)).ToList()
                    Dim closes = _cachedBars.Select(Function(b) CDec(b.Close)).ToList()
                    Dim atrArr = TechnicalIndicators.ATR(highs, lows, closes, 14)
                    atr14 = CDec(TechnicalIndicators.LastValid(atrArr))
                    lastBarClose = CDec(_cachedBars(_cachedBars.Count - 1).Close)
                End If

                ' ── Current market price (15-sec bar) ────────────────────────────
                Dim currentPrice As Decimal = Await FetchCurrentPriceAsync(CancellationToken.None)
                If currentPrice <= 0D Then Return

                ' ── ATR-based trailing SL ─────────────────────────────────────────
                If atr14 > 0D AndAlso _slMultipleOfN > 0D Then
                    Dim atrDistance = _slMultipleOfN * atr14
                    Dim rawCandidate = If(isBuy, currentPrice - atrDistance, currentPrice + atrDistance)
                    Dim newSlCandidate As Decimal
                    If isBuy Then
                        newSlCandidate = CDec(Math.Floor(CDbl(rawCandidate / tickSize))) * tickSize
                    Else
                        newSlCandidate = CDec(Math.Ceiling(CDbl(rawCandidate / tickSize))) * tickSize
                    End If

                    ' Ratchet: only advance in profitable direction
                    Dim shouldUpdate = If(isBuy, newSlCandidate > _currentSlPrice, newSlCandidate < _currentSlPrice)

                    ' Initial-SL guard: wait until candidate clears the ATR-derived entry risk
                    If shouldUpdate AndAlso _initialSlPrice > 0D Then
                        Dim cleared = If(isBuy, newSlCandidate > _initialSlPrice, newSlCandidate < _initialSlPrice)
                        If Not cleared Then
                            Dispatch(Sub() AddDebugMessage(
                                $"⏱ [15s] Trail hold — candidate SL={newSlCandidate:F4} hasn't cleared initial={_initialSlPrice:F4} | ATR={atr14:F4}"))
                            shouldUpdate = False
                        End If
                    End If

                    If shouldUpdate Then
                        Dim improveTicks = Math.Abs(TickMath.TicksBetween(_currentSlPrice, newSlCandidate, tickSize))
                        If improveTicks >= 1 Then
                            Dim ok = Await _orderService.EditPositionSlTpAsync(_livePositionId.Value, newSlCandidate, Nothing)
                            If ok Then
                                Dim prevSl = _currentSlPrice
                                _currentSlPrice = newSlCandidate
                                Dim isFreeRide = If(isBuy, newSlCandidate >= _entryPrice, newSlCandidate <= _entryPrice)
                                Dispatch(Sub()
                                             LiveSlPrice = _currentSlPrice
                                             AddDebugMessage(
                                                 $"🎯 [15s] ATR trail SL: {prevSl:F4} → {newSlCandidate:F4} (+{improveTicks}t) " &
                                                 $"ATR={atr14:F4} × {_slMultipleOfN:F2}N{If(isFreeRide, " 🔒 FREE RIDE", "")}")
                                         End Sub)
                            Else
                                Dispatch(Sub() AddDebugMessage($"⚠️ [15s] ATR trail update failed — will retry next tick"))
                            End If
                        Else
                            Dispatch(Sub() AddDebugMessage(
                                $"⏱ [15s] ATR trail hold — <1t improvement | price={currentPrice:F4} candidate={newSlCandidate:F4} SL={_currentSlPrice:F4} ATR={atr14:F4}"))
                        End If
                    Else
                        If atr14 > 0D Then
                            Dispatch(Sub() AddDebugMessage(
                                $"⏱ [15s] ATR trail hold | price={currentPrice:F4} SL={_currentSlPrice:F4} ATR={atr14:F4} × {_slMultipleOfN:F2}N"))
                        End If
                    End If
                Else
                    Dispatch(Sub() AddDebugMessage($"⏱ [15s] ATR trail skip — ATR={atr14:F4} (need ≥14 bars)"))
                End If

                ' ── Extend TP on bar-close beyond target ──────────────────────────
                If _extendTpOnClose AndAlso atr14 > 0D AndAlso _currentTpPrice > 0D AndAlso
                   lastBarClose > 0D AndAlso _tpAdvanceCount < 3 Then
                    Dim closedBeyond = If(isBuy, lastBarClose >= _currentTpPrice, lastBarClose <= _currentTpPrice)
                    If closedBeyond Then
                        Dim tpAdvance = _tpMultipleOfN * atr14
                        Dim newTp As Decimal
                        If isBuy Then
                            newTp = CDec(Math.Ceiling(CDbl((_currentTpPrice + tpAdvance) / tickSize))) * tickSize
                        Else
                            newTp = CDec(Math.Floor(CDbl((_currentTpPrice - tpAdvance) / tickSize))) * tickSize
                        End If
                        Dim ok = Await _orderService.EditPositionSlTpAsync(_livePositionId.Value, Nothing, newTp)
                        If ok Then
                            _tpAdvanceCount += 1
                            Dim prevTp = _currentTpPrice
                            _currentTpPrice = newTp
                            Dispatch(Sub()
                                         LiveTpPrice = _currentTpPrice
                                         AddDebugMessage(
                                             $"🏃 [15s] Extend TP {_tpAdvanceCount}/3: {prevTp:F4} → {newTp:F4} " &
                                             $"(bar closed at {lastBarClose:F4})")
                                     End Sub)
                        End If
                    End If
                End If

            Catch ex As Exception
                DebugLog.Log($"ManageTradeAsync error: {ex.Message}")
            End Try
        End Function

        Private Sub StopStrategy()
            IsStrategyRunning = False
            StopPnLMonitoring()
        End Sub

        ''' <summary>
        ''' Resolves the live front-month MCLE contract ID from TopStepX once and stores it
        ''' directly in _testTradeContractId, eliminating repeated symbol→catalog lookups.
        ''' </summary>
        Private Async Function SelectOilAsync() As Task
            Dim fav = FavouriteContracts.TryGetBySymbolResolved("OIL")
            If fav Is Nothing Then
                Dispatch(Sub() AddDebugMessage("⚠️ OIL not found in FavouriteContracts"))
                Return
            End If
            Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav)
            If String.IsNullOrEmpty(resolved) Then resolved = fav.PxContractId
            ' Extract the short expiry code (e.g. "K26") for the label
            Dim shortId = resolved.Split("."c).LastOrDefault()
            Dispatch(Sub()
                         TestTradeContractId = resolved
                         OilContractLabel = $"{shortId} ✓"
                         AddDebugMessage($"✅ OIL selected: {resolved}")
                     End Sub)
        End Function

        Private _isClosingTrade As Boolean = False

        Private Async Function CloseTradeAsync() As Task
            If _selectedAccount Is Nothing OrElse String.IsNullOrWhiteSpace(_testTradeContractId) Then
                Dispatch(Sub() AddDebugMessage("⚠️ Close Trade: no account or contract selected."))
                Return
            End If

            _isClosingTrade = True
            Dispatch(Sub()
                         RelayCommand.RaiseCanExecuteChanged()
                         AddDebugMessage($"⏳ Closing position on {_testTradeContractId}…")
                     End Sub)

            Try
                Dim ok = Await _orderService.FlattenContractAsync(_selectedAccount.Id, _testTradeContractId)
                Dispatch(Sub()
                             If ok Then
                                 AddDebugMessage($"✅ Position closed: {_testTradeContractId}")
                             Else
                                 AddDebugMessage($"⚠️ FlattenContractAsync returned false for {_testTradeContractId} — check broker logs.")
                             End If
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() AddDebugMessage($"❌ Close Trade error: {ex.Message}"))
            Finally
                SyncLock _strategyLock
                    _isPositionActive = False
                    _isOrderPending = False
                    _entryPrice = 0
                    _currentSlPrice = 0D
                    _currentTpPrice = 0D
                    _tpOrderId = Nothing
                    _slOrderId = Nothing
                    _livePositionId = Nothing
                    _initialSlPrice = 0D
                    _tpAdvanceCount = 0
                End SyncLock
                _isClosingTrade = False
                IsStrategyRunning = False   ' also calls StopPnLMonitoring via setter
                StopPnLMonitoring()
                Dispatch(Sub() RelayCommand.RaiseCanExecuteChanged())
            End Try
        End Function

        Private _isOrderPending As Boolean = False
        Private _isPositionActive As Boolean = False
        Private _entryPrice As Decimal = 0
        Private _entrySide As OrderSide

        Private ReadOnly _lastKnownPrice As Decimal = 0

        Private _tpOrderId As Long?
        Private _slOrderId As Long?
        Private _currentSlPrice As Decimal = 0D
        Private _currentTpPrice As Decimal = 0D
        Private _pendingSlTicks As Integer = 0   ' TopStepX: ticks submitted with the entry order
        Private _pendingTpTicks As Integer = 0
        Private _livePositionId As Long? = Nothing  ' cached broker positionId — used by NudgeBracket to bypass contract-ID resolution

        ' ── Test trade placement ──────────────────────────────────────────────────

        Private Async Function ExecuteTestTrade(side As OrderSide) As Task
            Dim contractId = _testTradeContractId?.Trim()
            If String.IsNullOrWhiteSpace(contractId) Then
                Dispatch(Sub() TestTradeStatus = "⚠ No contract selected")
                SyncLock _strategyLock : _isOrderPending = False : End SyncLock
                Return
            End If
            If _selectedAccount Is Nothing Then
                Dispatch(Sub() TestTradeStatus = "⚠ No account selected")
                SyncLock _strategyLock : _isOrderPending = False : End SyncLock
                Return
            End If

            Dim sideLabel = If(side = OrderSide.Buy, "BUY", "SELL")
            Dim correlationId = Guid.NewGuid().ToString("N")
            SyncLock _strategyLock
                _pendingEntryCorrelationId = correlationId
            End SyncLock

            Try
                Await ExecuteTopStepXTestTrade(side, contractId, sideLabel, correlationId)
            Catch ex As Exception
                Dim errMsg = ex.Message
                SyncLock _strategyLock : _isOrderPending = False : End SyncLock
                Dispatch(Sub() TestTradeStatus = $"❌ Order error: {errMsg}")
                DebugLog.Log($"Order error: {errMsg}")
            End Try
        End Function

        ''' <summary>
        ''' TopStepX path: builds a market entry order with tick-based SL+TP brackets
        ''' submitted at placement time.  Fill detection comes via UserHub PositionUpdated
        ''' event — REST polling is not used (TryGetOrderFillPriceAsync returns Nothing).
        ''' </summary>
        Private Async Function ExecuteTopStepXTestTrade(side As OrderSide, contractId As String,
                                                         sideLabel As String,
                                                         correlationId As String) As Task
            Dim contracts As Integer = 1
            If Not Integer.TryParse(_testTradeAmount, contracts) OrElse contracts <= 0 Then contracts = 1

            ' Resolve the PX contract ID (e.g. "CON.F.US.MES.M26") from the short symbol
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
            Dim pxContractId = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId),
                                  fav.PxContractId, contractId)

            ' SL/TP are entered directly in ticks
            Dim slTicks As Integer = 20
            Dim tpTicks As Integer = 60
            If Not Integer.TryParse(_testTradeStopLoss, slTicks) OrElse slTicks <= 0 Then slTicks = 20
            If Not Integer.TryParse(_testTradeTakeProfit, tpTicks) OrElse tpTicks <= 0 Then tpTicks = 60
            _pendingSlTicks = slTicks
            _pendingTpTicks = tpTicks

            Dispatch(Sub() TestTradeStatus =
                $"📤 Placing TopStepX {sideLabel} {contracts}× {pxContractId} | SL={slTicks}t TP={tpTicks}t...")
            Dispatch(Sub() AddDebugMessage(
                $"TopStepX order: {sideLabel} {contracts}× {pxContractId} | SL={slTicks}t  TP={tpTicks}t  (R:R≈{tpTicks / slTicks:F1}:1)"))

            Dim order As New Order With {
                .AccountId = _selectedAccount.Id,
                .Broker = BrokerType.TopStepX,
                .ContractId = pxContractId,
                .Side = side,
                .Quantity = contracts,
                .OrderType = OrderType.Market,
                .InitialStopTicks = slTicks,
                .InitialTakeProfitTicks = tpTicks,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"Test Trade — {sideLabel} [{correlationId}]"
            }

            Dim placedOrder = Await _orderService.PlaceOrderAsync(order)

            SyncLock _strategyLock
                _pendingEntryOrderId = placedOrder.ExternalOrderId
            End SyncLock

            If placedOrder.Status = OrderStatus.Rejected Then
                SyncLock _strategyLock : _isOrderPending = False : End SyncLock
                Dim reason = If(String.IsNullOrWhiteSpace(placedOrder.Notes), "unknown reason", placedOrder.Notes)
                Dispatch(Sub() TestTradeStatus = $"❌ {sideLabel} rejected: {reason}")
            Else
                Dispatch(Sub() TestTradeStatus =
                    $"✔ TopStepX {sideLabel} submitted — Order #{placedOrder.ExternalOrderId}. " &
                    "Waiting for UserHub fill event...")
                Dispatch(Sub() AddDebugMessage(
                    "ℹ️  SL+TP brackets were submitted with the entry order. " &
                    "Fill confirmation arrives via UserHub — no REST polling needed."))
                ' No bracket placement needed — SL+TP are attached to the entry order by ProjectXOrderService.
                ' OnPositionUpdated will fire via UserHub when the exchange processes the fill.
                ' Reset pending flag after a 15-second safety window so the UI isn't permanently locked.
                Await Task.Delay(TimeSpan.FromSeconds(15))
                SyncLock _strategyLock
                    If _isOrderPending Then
                        _isOrderPending = False
                        Dispatch(Sub() AddDebugMessage(
                            "⏱ 15s safety window elapsed — order is working; position event will arrive shortly."))
                    End If
                End SyncLock
            End If
        End Function


        Private Async Function PlaceBracketsAsync(entryOrder As Order, explicitFillPrice As Decimal) As Task
            Try
                Dispatch(Sub() AddDebugMessage($"Strategy: Calculating brackets for Entry @ {explicitFillPrice}..."))

                ' Wait 1 second before placing brackets to ensure the exchange has processed the fill fully
                Await Task.Delay(1000)

                Dim slTicks As Integer = 60
                Dim tpTicks As Integer = 30

                ' Use local variables to avoid threading issues with property access (though properties are on UI thread usually)
                ' Better to parse on UI thread, but we are in async task.
                ' We'll parse properties again or assume they haven't changed.
                If Not Integer.TryParse(_testTradeStopLoss, slTicks) Then slTicks = 20
                If Not Integer.TryParse(_testTradeTakeProfit, tpTicks) Then tpTicks = 30

                ' Determine Tick Size based on contract
                Dim tickSize As Decimal = 0.25D ' Default
                Dim cId = entryOrder.ContractId.ToUpper()
                If cId.Contains("MGC") OrElse cId.Contains("GC") Then
                    tickSize = 0.1D
                ElseIf cId.Contains("MCL") OrElse cId.Contains("CL") Then
                    tickSize = 0.01D
                ElseIf cId.Contains("MNQ") OrElse cId.Contains("NQ") Then
                    tickSize = 0.25D
                ElseIf cId.Contains("MES") OrElse cId.Contains("ES") Then
                    tickSize = 0.25D
                End If

                If explicitFillPrice <= 0 Then
                    Dispatch(Sub() AddDebugMessage("Cannot place brackets: Invalid Entry Price (0)"))
                    Return
                End If

                Dim exitSide = If(entryOrder.Side = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)

                Dim slPrice As Decimal
                Dim tpPrice As Decimal
                Dim slOffset = slTicks * tickSize
                Dim tpOffset = tpTicks * tickSize

                If entryOrder.Side = OrderSide.Buy Then
                    slPrice = explicitFillPrice - slOffset
                    tpPrice = explicitFillPrice + tpOffset
                Else
                    slPrice = explicitFillPrice + slOffset
                    tpPrice = explicitFillPrice - tpOffset
                End If

                ' Round prices to valid tick size to avoid rejection
                slPrice = Math.Round(slPrice / tickSize) * tickSize
                tpPrice = Math.Round(tpPrice / tickSize) * tickSize

                ' Place TP (Limit)
                Dim tpOrder As New Order With {
                    .AccountId = entryOrder.AccountId,
                    .ContractId = entryOrder.ContractId,
                    .Side = exitSide,
                    .OrderType = OrderType.Limit,
                    .Quantity = entryOrder.Quantity,
                    .LimitPrice = tpPrice,
                    .Status = OrderStatus.Pending,
                    .PlacedAt = DateTimeOffset.UtcNow,
                    .Notes = $"Test TP (+{tpTicks}t)"
                }
                Try
                    Dim tpPlaced = Await _orderService.PlaceOrderAsync(tpOrder)
                    _tpOrderId = tpPlaced.ExternalOrderId
                    _currentTpPrice = tpPrice
                    Dispatch(Sub() AddDebugMessage($"Placed TP Limit @ {tpPrice}"))
                Catch ex As Exception
                    Dispatch(Sub() AddDebugMessage($"TP Place Failed: {ex.Message}"))
                End Try

                ' Place SL (Stop Market)
                Dim slOrder As New Order With {
                    .AccountId = entryOrder.AccountId,
                    .ContractId = entryOrder.ContractId,
                    .Side = exitSide,
                    .OrderType = OrderType.StopOrder,
                    .Quantity = entryOrder.Quantity,
                    .StopPrice = slPrice,
                    .Status = OrderStatus.Pending,
                    .PlacedAt = DateTimeOffset.UtcNow,
                    .Notes = $"Test SL (-{slTicks}t)"
                }
                Try
                    Dim slPlaced = Await _orderService.PlaceOrderAsync(slOrder)
                    _slOrderId = slPlaced.ExternalOrderId
                    _currentSlPrice = slPrice
                    Dispatch(Sub() AddDebugMessage($"Placed SL Stop Market @ {slPrice}"))
                Catch ex As Exception
                    Dispatch(Sub() AddDebugMessage($"SL Place Failed: {ex.Message}"))
                End Try

            Catch ex As Exception
                DebugLog.Log($"Error placing brackets: {ex.Message}")
            End Try
        End Function

        ''' <summary>
        ''' Tightens the resting stop-loss order on TopStepX by 10% of its distance to entry,
        ''' guaranteeing at least 1 tick of movement per click.  Works regardless of whether
        ''' <c>_currentSlPrice</c> has been locally seeded — falls back to <c>_pendingSlTicks</c>
        ''' then to the SL-tick input field to derive the current stop level.
        ''' </summary>
        Private Async Sub NudgeBracket()
            Try
                If _selectedAccount Is Nothing Then
                    Dispatch(Sub() AddDebugMessage("❌ Nudge: no account selected."))
                    Return
                End If
                If String.IsNullOrWhiteSpace(_testTradeContractId) Then
                    Dispatch(Sub() AddDebugMessage("❌ Nudge: no contract selected."))
                    Return
                End If

                ' Fetch live position — cached positionId bypasses contract-ID resolution
                ' (prevents false-Nothing on quarterly contract rolls).  Retry once after 1s.
                Dim snapshot As LivePositionSnapshot = Nothing
                For attempt As Integer = 1 To 2
                    snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                        _selectedAccount.Id, _testTradeContractId, _livePositionId)
                    If snapshot IsNot Nothing Then Exit For
                    If attempt = 1 Then Await Task.Delay(1000)
                Next
                If snapshot Is Nothing Then
                    Dispatch(Sub() AddDebugMessage(
                        "❌ Nudge: no live position found — place an order first."))
                    Return
                End If
                _livePositionId = snapshot.PositionId

                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                Dim tickSize As Decimal = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0, fav.PxTickSize, 0.25D)
                Dim isBuy = snapshot.IsBuy
                Dim entryPrice = If(_entryPrice > 0, _entryPrice, snapshot.OpenRate)

                ' ── Resolve current SL price ─────────────────────────────────────────
                ' Use the locally-tracked value if available; otherwise compute it from
                ' _pendingSlTicks (set at order placement) or the SL tick input field.
                ' This lets Nudge work on the very first click even in a fresh app session.
                Dim resolvedSl As Decimal = _currentSlPrice
                If resolvedSl <= 0D Then
                    Dim slTk As Integer = _pendingSlTicks
                    If slTk <= 0 Then Integer.TryParse(_testTradeStopLoss, slTk)
                    If slTk <= 0 Then slTk = 20
                    resolvedSl = If(isBuy,
                        entryPrice - slTk * tickSize,
                        entryPrice + slTk * tickSize)
                    Dispatch(Sub() AddDebugMessage(
                        $"ℹ️ Nudge: SL not tracked — computed from {slTk}t → {resolvedSl:F4}"))
                End If

                ' ── Compute new SL: 10% closer to entry, minimum 1 tick ──────────────
                Dim slGap = If(isBuy, entryPrice - resolvedSl, resolvedSl - entryPrice)
                Dim slStep = Math.Max(Math.Round(slGap * 0.1D / tickSize) * tickSize, tickSize)
                Dim newSl As Decimal = Math.Round(
                    If(isBuy, resolvedSl + slStep, resolvedSl - slStep) / tickSize) * tickSize

                ' ── Resolve current TP price (optional — not required for SL nudge) ──
                Dim newTp As Decimal? = Nothing
                If _currentTpPrice > 0D Then
                    Dim tpGap = If(isBuy, _currentTpPrice - entryPrice, entryPrice - _currentTpPrice)
                    Dim tpStep = Math.Max(Math.Round(tpGap * 0.1D / tickSize) * tickSize, tickSize)
                    newTp = Math.Round(
                        If(isBuy, _currentTpPrice - tpStep, _currentTpPrice + tpStep) / tickSize) * tickSize
                End If

                Dispatch(Sub() AddDebugMessage(
                    $"🎯 Nudge SL 10% → entry={entryPrice:F4} | " &
                    $"SL {resolvedSl:F4} → {newSl:F4} (step={slStep:G}t)" &
                    If(newTp.HasValue, $" | TP {_currentTpPrice:F4} → {newTp:F4}", String.Empty)))

                Dim ok = Await _orderService.EditPositionSlTpAsync(snapshot.PositionId, newSl, newTp)

                If ok Then
                    _currentSlPrice = newSl
                    If newTp.HasValue Then _currentTpPrice = newTp.Value
                    Dispatch(Sub()
                                 LiveSlPrice = _currentSlPrice
                                 AddDebugMessage(
                                     $"✅ Nudge applied — SL={_currentSlPrice:F4}" &
                                     If(newTp.HasValue, $" | TP={_currentTpPrice:F4}", String.Empty))
                             End Sub)
                Else
                    Dispatch(Sub() AddDebugMessage(
                        "❌ Nudge failed — ensure a resting SL bracket order exists on the broker."))
                End If

            Catch ex As Exception
                Dispatch(Sub() AddDebugMessage($"❌ Nudge error: {ex.Message}"))
            End Try
        End Sub

        Public ReadOnly Property CanPlaceTestTrade As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_testTradeContractId) AndAlso _selectedAccount IsNot Nothing AndAlso IsBarsLoaded
            End Get
        End Property

        ''' <summary>True when the selected account belongs to TopStepX (drives label and order-building fork).</summary>
        Public ReadOnly Property IsTopStepX As Boolean
            Get
                Return _selectedAccount IsNot Nothing AndAlso _selectedAccount.Broker = BrokerType.TopStepX
            End Get
        End Property

        Public ReadOnly Property IsNotTopStepX As Boolean
            Get
                Return Not IsTopStepX
            End Get
        End Property

        Public ReadOnly Property AmountLabel As String
            Get
                Return If(IsTopStepX, "Contracts:", "Amount ($):")
            End Get
        End Property

        Public ReadOnly Property SlLabel As String
            Get
                Return If(IsTopStepX, "SL ticks:", "SL %:")
            End Get
        End Property

        Public ReadOnly Property TpLabel As String
            Get
                Return If(IsTopStepX, "TP ticks:", "TP %:")
            End Get
        End Property

        Private Sub LoadAvailableContracts()
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each f In FavouriteContracts.GetDefaults()
                Dim cid = f.PxContractId
                If seen.Add(cid) Then
                    AvailableContracts.Add(New Contract With {
                        .Id = cid,
                        .FriendlyName = f.Name
                    })
                End If
            Next
        End Sub

        Private Sub UpdateTestTradeContractDisplay()
            Dim c = _testTradeSelectedContract
            Dim fav As FavouriteContract = Nothing
            If c IsNot Nothing Then
                TestTradeContractLongId = c.Id
                TestTradeContractDisplay = If(Not String.IsNullOrWhiteSpace(c.FriendlyName),
                                              c.FriendlyName, c.Id)
                fav = FavouriteContracts.TryGetBySymbolResolved(c.Id)
            ElseIf Not String.IsNullOrWhiteSpace(_testTradeContractId) Then
                TestTradeContractLongId = _testTradeContractId
                fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                TestTradeContractDisplay = If(fav IsNot Nothing, fav.Name, _testTradeContractId)
            End If
            NotifyPropertyChanged(NameOf(TickSizeHintText))
        End Sub

        ''' <summary>
        ''' Returns a human-readable tick spec for the selected contract, e.g.
        ''' "MGC · 0.10 pts/tick · $1.00/tick"  — shown in the UI near the SL/TP inputs.
        ''' </summary>
        Public ReadOnly Property TickSizeHintText As String
            Get
                If String.IsNullOrWhiteSpace(_testTradeContractId) Then Return String.Empty
                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                If fav Is Nothing Then Return String.Empty
                Return $"{fav.Name} · {fav.PxTickSize:G} pts/tick · ${fav.PxTickValue:F2}/tick"
            End Get
        End Property

        Private Sub OnTestOrderRejected(sender As Object, e As OrderRejectedEventArgs)
            Dim reason = e.Reason
            DebugLog.Log($"Order rejected: {reason}")
            Dispatch(Sub() TestTradeStatus = $"❌ Rejected: {reason}")
        End Sub

        ''' <summary>
        ''' Fires via SignalR (UserHub) the moment the exchange creates/updates our position.
        ''' This is the most reliable fill signal — OrderFilled is never raised by OrderService.
        ''' </summary>
        Private Sub OnPositionUpdated(sender As Object, e As PositionUpdateEventArgs)
            If _isPositionActive Then Return  ' already tracking a position — ignore duplicates
            If e.NetPosition = 0 Then Return  ' position closed — not an entry event

            ' Match against the short symbol, the full PX contract ID, OR any same-root contract
            ' (e.g. CON.F.US.MYM.M26 when we track CON.F.US.MYM.U26 — quarterly roll mismatch).
            Dim favMatch = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
            Dim pxId = If(favMatch IsNot Nothing AndAlso Not String.IsNullOrEmpty(favMatch.PxContractId),
                          favMatch.PxContractId, _testTradeContractId)
            Dim rootPrefix = If(favMatch IsNot Nothing AndAlso Not String.IsNullOrEmpty(favMatch.PxRootSymbol),
                                $"CON.F.US.{favMatch.PxRootSymbol}.", String.Empty)
            If Not String.Equals(e.ContractId, _testTradeContractId, StringComparison.OrdinalIgnoreCase) AndAlso
               Not String.Equals(e.ContractId, pxId, StringComparison.OrdinalIgnoreCase) AndAlso
               (String.IsNullOrEmpty(rootPrefix) OrElse
                Not e.ContractId.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) Then Return

            Dim isMyEntry As Boolean = False
            Dim fillPrice = e.AveragePrice
            Dim capturedSide = _entrySide
            Dim capturedAccount = _selectedAccount
            Dim capturedContractId = _testTradeContractId
            Dim capturedQty As Integer = 1
            If Not Integer.TryParse(_testTradeQuantity, capturedQty) OrElse capturedQty <= 0 Then capturedQty = 1

            SyncLock _strategyLock
                If Not _isPositionActive Then
                    _isPositionActive = True
                    _isOrderPending = False
                    _entryPrice = fillPrice
                    isMyEntry = True
                End If
            End SyncLock

            If isMyEntry Then
                _entryOrderId = _pendingEntryOrderId
                _pendingEntryOrderId = Nothing
                _pendingEntryCorrelationId = String.Empty

                ' Seed bracket price tracking for TopStepX (SL/TP placed inline with the order).
                If IsTopStepX AndAlso _pendingSlTicks > 0 Then
                    Dim favBracket = FavouriteContracts.TryGetBySymbolResolved(capturedContractId)
                    Dim ts = If(favBracket IsNot Nothing AndAlso favBracket.PxTickSize > 0,
                                favBracket.PxTickSize, 0.25D)
                    Dim isBuyPos = (capturedSide = OrderSide.Buy)
                    _currentSlPrice = If(isBuyPos,
                        fillPrice - _pendingSlTicks * ts, fillPrice + _pendingSlTicks * ts)
                    _currentTpPrice = If(isBuyPos,
                        fillPrice + _pendingTpTicks * ts, fillPrice - _pendingTpTicks * ts)
                    Dispatch(Sub() AddDebugMessage(
                        $"🔗 Bracket prices seeded — SL={_currentSlPrice:F4} | TP={_currentTpPrice:F4} | Nudge ready."))
                End If

                ' Update entry price and order ID display immediately on fill
                Dim capturedEntryOrderId = _entryOrderId
                Dispatch(Sub()
                             LiveEntryPrice = fillPrice
                             LiveEntryOrderIdText = If(capturedEntryOrderId.HasValue,
                                                       $"Entry #{capturedEntryOrderId.Value}",
                                                       "Pending…")
                         End Sub)

                ' Resolve positionId in background (4×750ms) so NudgeBracket is ready immediately
                ' without waiting for the first MonitorPnL tick (5s).
                Dim resolveAccId = If(capturedAccount IsNot Nothing, capturedAccount.Id, 0L)
                Dim resolveContId = capturedContractId
                Task.Run(Async Function() As Task
                             For attempt As Integer = 1 To 4
                                 Await Task.Delay(750)
                                 Dim ps = Await _orderService.GetLivePositionSnapshotAsync(
                                     resolveAccId, resolveContId)
                                 If ps IsNot Nothing AndAlso ps.PositionId <> 0 Then
                                     _livePositionId = ps.PositionId
                                     Dispatch(Sub() AddDebugMessage(
                                         $"🔗 Position ID resolved: {ps.PositionId} — Nudge SL/TP is ready."))
                                     Exit For
                                 End If
                             Next
                         End Function)

                Dim entryOrder As New Order With {
                    .AccountId = If(capturedAccount IsNot Nothing, capturedAccount.Id, 0L),
                    .ContractId = capturedContractId,
                    .Side = capturedSide,
                    .OrderType = OrderType.Market,
                    .Quantity = capturedQty,
                    .FillPrice = fillPrice,
                    .Status = OrderStatus.Filled
                }

                Dispatch(Sub() AddDebugMessage($"📡 Position filled @ {fillPrice}"))
                Dispatch(Sub() TestTradeStatus = $"✅ Filled @ {fillPrice}")
            End If
        End Sub

        Private Sub OnTestOrderFilled(sender As Object, e As OrderFilledEventArgs)
            Dim fillPrice = e.Order.FillPrice
            Dim orderId = e.Order.ExternalOrderId

            DebugLog.Log($"Order filled: #{orderId} {e.Order.Side} {e.Order.Quantity} × {e.Order.ContractId} @ {fillPrice}")
            Dispatch(Sub() TestTradeStatus = $"✅ Filled: #{orderId} @ {fillPrice}")

            ' Check if this is our entry order
            Dim isMyEntry = False
            Dim actualOrderId = orderId.GetValueOrDefault()

            SyncLock _strategyLock
                ' Match by ID OR by Correlation ID in Notes
                Dim matchesId = (_pendingEntryOrderId.HasValue AndAlso orderId.HasValue AndAlso _pendingEntryOrderId.Value = orderId.Value)
                Dim matchesCorrelation = (Not String.IsNullOrEmpty(_pendingEntryCorrelationId) AndAlso e.Order.Notes.Contains(_pendingEntryCorrelationId))

                If matchesId OrElse matchesCorrelation Then
                    _isPositionActive = True
                    _isOrderPending = False ' Entry filled, no longer pending
                    _pendingEntryOrderId = Nothing
                    _pendingEntryCorrelationId = String.Empty
                    isMyEntry = True
                End If
            End SyncLock

            If isMyEntry Then
                _entryPrice = If(fillPrice, 0D)
                _entrySide = e.Order.Side
                _entryOrderId = orderId  ' Capture for P&L monitoring

                Dim priceToUse = _entryPrice
                Task.Run(Function() PlaceBracketsAsync(e.Order, priceToUse))
                Dispatch(Sub() AddDebugMessage($"Strategy: Entered Trade. Monitoring trend... Order #{orderId}"))
            End If

            ' Check if this is one of our bracket orders closing the trade
            If (_tpOrderId.HasValue AndAlso orderId.HasValue AndAlso _tpOrderId.Value = orderId.Value) OrElse
               (_slOrderId.HasValue AndAlso orderId.HasValue AndAlso _slOrderId.Value = orderId.Value) Then

                SyncLock _strategyLock
                    _isPositionActive = False
                    _isOrderPending = False ' Just in case
                End SyncLock

                _tpOrderId = Nothing
                _slOrderId = Nothing

                Dispatch(Sub() AddDebugMessage("Strategy: Trade Closed."))
                ' Cancel the other bracket if not filled (OCO simulation) — handled by order service; basic reset here.
                ' For now, just reset state.
            End If
        End Sub

        Private Sub OnDebugLog(message As String)
            _debugMessages.Add(message)
            ' Keep only the last 100 messages to avoid unbounded growth
            If _debugMessages.Count > 100 Then
                _debugMessages.RemoveAt(0)
            End If
            Dim text = String.Join(Environment.NewLine, _debugMessages)
            Dispatch(Sub() DebugText = text)
        End Sub

        Private Sub ClearDebug(Optional param As Object = Nothing)
            _debugMessages.Clear()
            Dispatch(Sub() DebugText = String.Empty)
        End Sub

        Private Sub AddDebugMessage(message As String)
            Dispatch(Sub()
                         _debugMessages.Add(message)
                         If _debugMessages.Count > 100 Then _debugMessages.RemoveAt(0)
                         DebugText = String.Join(Environment.NewLine, _debugMessages)
                     End Sub)
        End Sub

        ' ── Naked Trader analysis implementation ──────────────────────────────

        ''' <summary>
        ''' Fetches the most recent 5-minute bars for the selected contract from the
        ''' appropriate broker API, sorted oldest-first for indicator calculation.
        ''' </summary>
        ''' <summary>
        ''' Fetches the most recent close price via a 3-second bar (no DB write).
        ''' Returns 0 on any failure so callers fall back to entry price gracefully.
        ''' Mirrors <c>TopStepXBarIngestionService.GetLatestPriceAsync</c>.
        ''' Using 3-second bars (down from 15s) reduces maximum price staleness from
        ''' ~20 seconds to ~8 seconds (3s bar age + 5s poll interval).
        ''' </summary>
        Private Async Function FetchCurrentPriceAsync(cancel As CancellationToken) As Task(Of Decimal)
            Try
                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                If fav Is Nothing OrElse String.IsNullOrEmpty(fav.PxContractId) Then Return 0D
                Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav, cancel)
                Dim pxId = If(Not String.IsNullOrEmpty(resolved), resolved, fav.PxContractId)
                Dim response = Await _pxHistoryClient.RetrieveBarsAsync(
                    pxId, unit:=1, unitNumber:=3, limit:=3,
                    live:=False,
                    startTime:=DateTimeOffset.UtcNow.AddMinutes(-1),
                    endTime:=DateTimeOffset.UtcNow,
                    cancel:=cancel)
                If response Is Nothing OrElse response.Bars Is Nothing OrElse response.Bars.Count = 0 Then Return 0D
                Return CDec(response.Bars.Last().Close)
            Catch ex As Exception
                DebugLog.Log($"FetchCurrentPriceAsync failed: {ex.Message}")
                Return 0D
            End Try
        End Function

        Private Async Function FetchFiveMinBarsAsync(count As Integer, Optional daysBack As Integer = 3) As Task(Of List(Of MarketBar))
            ' PX API: unit=2 (AggregateBarUnit.Minute), unitNumber=5 (5 minutes per bar)

            Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
            Dim bars As New List(Of MarketBar)

            Try
                Dim dtos As List(Of API.Models.Responses.BarDto)

                If IsTopStepX Then
                    ' Resolve the active front-month contract ID via the catalog (same as TopStepXBarIngestionService).
                    ' Without this, FavouriteContracts.PxContractId may be an expired or non-front-month expiry
                    ' (e.g. "CON.F.US.MCLE.Q26" when the live front-month is K26/M26), returning 0 bars.
                    Dim pxId As String
                    If fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId) Then
                        Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav)
                        pxId = If(Not String.IsNullOrEmpty(resolved), resolved, fav.PxContractId)
                    Else
                        pxId = _testTradeContractId
                    End If
                    Dim endTime = DateTimeOffset.UtcNow
                    Dim startTime = endTime.AddDays(-daysBack)
                    Dim resp = Await _pxHistoryClient.RetrieveBarsAsync(pxId, 2, 5, count, live:=False, startTime:=startTime, endTime:=endTime)
                    dtos = If(resp?.Bars, New List(Of API.Models.Responses.BarDto)())
                Else
                    dtos = New List(Of API.Models.Responses.BarDto)()
                End If

                For Each dto In dtos
                    Dim ts As DateTimeOffset
                    If Not DateTimeOffset.TryParse(dto.Timestamp, Nothing,
                            Globalization.DateTimeStyles.RoundtripKind, ts) Then
                        ts = DateTimeOffset.UtcNow
                    End If
                    bars.Add(New MarketBar With {
                        .Timestamp = ts,
                        .Open = CDec(dto.Open),
                        .High = CDec(dto.High),
                        .Low = CDec(dto.Low),
                        .Close = CDec(dto.Close),
                        .Volume = dto.Volume
                    })
                Next

                ' Ensure oldest-first order regardless of API response ordering
                bars = bars.OrderBy(Function(b) b.Timestamp).ToList()

            Catch ex As Exception
                DebugLog.Log($"Naked Trader bar fetch error: {ex.Message}")
            End Try

            Return bars
        End Function

        ''' <summary>
        ''' Fetches the full bar history needed by NakedTrader (40 bars) and Claude Haiku (12 bars = 60 min).
        ''' Performs a backfill pass if the first fetch returns fewer bars than the Claude minimum:
        ''' retries with a 14-day window to recover bars across weekends / session gaps.
        ''' Results are cached in _cachedBars so neither NakedTrader nor Claude need a second API call.
        ''' </summary>
        Private Async Function LoadBarsAsync() As Task
            Dispatch(Sub()
                         IsLoadingBars = True
                         IsBarsLoaded = False
                         AddDebugMessage("Downloading bar history…")
                     End Sub)
            Try
                ' First pass — 3-day window. Fetch CLAUDE_MIN_BARS (48) so both NakedTrader (needs 40)
                ' and Claude Haiku (needs 48 = 4 hours of 5-min bars) are satisfied in one call.
                Dim bars = Await FetchFiveMinBarsAsync(CLAUDE_MIN_BARS)

                ' Backfill pass — if we got fewer than 4 hours of data, widen the window to 14 days
                ' to recover bars across weekend/holiday/session gaps from previous sessions
                If bars.Count < CLAUDE_MIN_BARS Then
                    Dispatch(Sub() AddDebugMessage($"Only {bars.Count} bars in 3-day window — backfilling from 14 days…"))
                    bars = Await FetchFiveMinBarsAsync(CLAUDE_MIN_BARS, daysBack:=14)
                End If

                _cachedBars = bars
                Dim loaded = bars.Count >= CLAUDE_MIN_BARS
                Dispatch(Sub()
                             IsLoadingBars = False
                             IsBarsLoaded = loaded
                             If loaded Then
                                 AddDebugMessage($"✅ {bars.Count} bars loaded (covers {bars.Count * 5} min / 4 hours). 'Request Trade Advice' button enabled.")
                                 ClaudeRationale = "Bar history ready. Click 'Request Trade Advice' to analyse."
                             Else
                                 Dim msg = $"⚠️ Only {bars.Count} bars available — need at least {CLAUDE_MIN_BARS} (4 hours) for Claude Haiku."
                                 AddDebugMessage(msg)
                                 ClaudeRationale = $"Could not load enough bar history ({bars.Count}/{CLAUDE_MIN_BARS} bars). Check the Status Log for details."
                             End If
                         End Sub)
            Catch ex As Exception
                _cachedBars = Nothing
                Dispatch(Sub()
                             IsLoadingBars = False
                             IsBarsLoaded = False
                             ClaudeRationale = $"Bar history load failed: {ex.Message}"
                         End Sub)
                DebugLog.Log($"Bar load error: {ex.Message}")
            End Try
        End Function

        Private Async Sub RunNakedTraderAnalysis()
            If IsAnalysing Then Return
            If _selectedAccount Is Nothing OrElse String.IsNullOrWhiteSpace(_testTradeContractId) Then Return

            IsAnalysing = True
            Dispatch(Sub()
                         NakedTraderDirection = "Loading"
                         NakedTraderConfidence = "Analysing"
                         NakedTraderSummary = "Fetching 5-min bars"
                     End Sub)

            Try
                ' Use cached bars if LoadBarsAsync has already populated them, else fetch fresh.
                ' Never write to _cachedBars here — LoadBarsAsync owns the cache.
                Dim bars As List(Of MarketBar)
                If _cachedBars IsNot Nothing AndAlso _cachedBars.Count >= NakedTraderAnalyser.MIN_BARS Then
                    bars = _cachedBars
                Else
                    bars = Await FetchFiveMinBarsAsync(NakedTraderAnalyser.RECOMMENDED_BARS)
                End If

                If bars.Count < NakedTraderAnalyser.MIN_BARS Then
                    Dispatch(Sub()
                                 NakedTraderDirection = "-"
                                 NakedTraderConfidence = "LOW"
                                 NakedTraderSummary = $"Only {bars.Count} bars available (need {NakedTraderAnalyser.MIN_BARS}+)"
                             End Sub)
                    Return
                End If

                Dim snapshot = NakedTraderAnalyser.Analyse(bars)

                Dispatch(Sub()
                             Dim dirText = If(snapshot.Direction = TrendDirection.Up, "BUY", "SELL")
                             Dim confText = snapshot.Confidence.ToString().ToUpper()
                             NakedTraderDirection = dirText
                             NakedTraderConfidence = confText
                             NakedTraderSummary = snapshot.Summary
                             NotifyPropertyChanged(NameOf(NakedTraderDirectionColor))
                             AddDebugMessage($"Naked Trader: {dirText} | {confText} | {snapshot.Summary}")
                         End Sub)

            Catch ex As Exception
                Dispatch(Sub()
                             NakedTraderDirection = "ERROR"
                             NakedTraderConfidence = "ERROR"
                             NakedTraderSummary = ex.Message
                         End Sub)
            Finally
                IsAnalysing = False
            End Try
        End Sub

        ''' <summary>
        ''' Manually triggers Claude Haiku AI analysis for trade advice.
        ''' Requires bars to be loaded and not currently analyzing.
        ''' </summary>
        Private Async Sub GetClaudeAdvice()
            If Not IsBarsLoaded OrElse IsClaudeAdvising Then Return
            If _selectedAccount Is Nothing OrElse String.IsNullOrWhiteSpace(_testTradeContractId) Then Return

            IsClaudeAdvising = True
            Dispatch(Sub()
                         ClaudeAdvice = "Loading"
                         ClaudeRationale = "Requesting Claude Haiku analysis"
                     End Sub)

            Try
                ' Use the bars already downloaded by LoadBarsAsync.
                ' Fall back to a fresh fetch (with 14-day backfill) only if the cache is missing.
                Dim bars As List(Of MarketBar)
                If _cachedBars IsNot Nothing AndAlso _cachedBars.Count >= CLAUDE_MIN_BARS Then
                    bars = _cachedBars
                    AddDebugMessage($"Claude Haiku: using {bars.Count} cached bars.")
                Else
                    AddDebugMessage("Claude Haiku: cache miss — fetching bars…")
                    bars = Await FetchFiveMinBarsAsync(NakedTraderAnalyser.RECOMMENDED_BARS, daysBack:=14)
                    _cachedBars = bars
                End If

                If bars.Count < CLAUDE_MIN_BARS Then
                    Dispatch(Sub()
                                 ClaudeAdvice = "-"
                                 ClaudeRationale = $"Insufficient bar data ({bars.Count} bars, need {CLAUDE_MIN_BARS} = 4 hours)."
                             End Sub)
                    Return
                End If

                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_testTradeContractId)
                Dim contractName = If(fav IsNot Nothing, fav.Name, _testTradeContractId)
                ' Send the most recent 4 hours (last 48 × 5-min bars) to Claude Haiku
                Dim oneHourBars = bars.TakeLast(CLAUDE_MIN_BARS).ToList()
                Dim advice = Await _claudeService.TradeAdviceAsync(contractName, oneHourBars)

                Dispatch(Sub()
                             ClaudeAdvice = advice.Direction
                             ClaudeRationale = advice.Rationale
                             AddDebugMessage($"Claude Haiku: {advice.Direction} - {advice.Rationale}")
                         End Sub)

            Catch ex As Exception
                Dispatch(Sub()
                             ClaudeAdvice = "ERROR"
                             ClaudeRationale = $"AI analysis error: {ex.Message}"
                         End Sub)
            Finally
                IsClaudeAdvising = False
            End Try
        End Sub

        Private Sub DisposeManaged()
            StopStrategy()
        End Sub

        Protected Overrides Sub Finalize()
            DisposeManaged()
            MyBase.Finalize()
        End Sub

        ' ── Housekeeping ──────────────────────────────────────────────────────────

        Public Sub Dispose() Implements IDisposable.Dispose
            DisposeManaged()
            GC.SuppressFinalize(Me)
        End Sub

        Private Shared Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

End Namespace
