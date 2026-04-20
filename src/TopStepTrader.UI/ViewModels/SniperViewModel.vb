Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows
Imports System.Windows.Media
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for the Sniper view (3-EMA Cascade momentum trading).
    ''' Wires SniperExecutionEngine for live scaled-in trading and
    ''' IBacktestService (TripleEmaCascade) for the backtest tab.
    ''' </summary>
    Public Class SniperViewModel
        Inherits ViewModelBase
        Implements IDisposable

        ' ── Dependencies ───────────────────────────────────────────────────────
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _backtestService As IBacktestService
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _claudeReviewService As ClaudeReviewService
        Private ReadOnly _engine As ISniperExecutionEngine
        Private ReadOnly _session As ITradingSessionContext

        ' ── Internal state ─────────────────────────────────────────────────────
        Private _disposed As Boolean = False
        Private _cancelSource As CancellationTokenSource
        Private _winCount As Integer = 0
        Private _lossCount As Integer = 0
        Private _totalPnl As Decimal = 0D
        Private _btNetPnl As Decimal = 0D   ' for BtPnlBrush

        ' ══════════════════════════════════════════════════════════════════════
        ' ACCOUNTS
        ' ══════════════════════════════════════════════════════════════════════

        Public Property Accounts As New ObservableCollection(Of Account)

        Private _selectedAccount As Account
        Public Property SelectedAccount As Account
            Get
                Return _selectedAccount
            End Get
            Set(value As Account)
                SetProperty(_selectedAccount, value)
                NotifyPropertyChanged(NameOf(CanStart))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE TAB — SETUP
        ' ══════════════════════════════════════════════════════════════════════

        Private _contractId As String = ""
        Public Property ContractId As String
            Get
                Return _contractId
            End Get
            Set(value As String)
                SetProperty(_contractId, value)
                NotifyPropertyChanged(NameOf(CanStart))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Private _initialTpAmount As String = "20"
        Public Property InitialTpAmount As String
            Get
                Return _initialTpAmount
            End Get
            Set(value As String)
                SetProperty(_initialTpAmount, value)
            End Set
        End Property

        Private _initialSlAmount As String = "10"
        Public Property InitialSlAmount As String
            Get
                Return _initialSlAmount
            End Get
            Set(value As String)
                SetProperty(_initialSlAmount, value)
            End Set
        End Property

        Private _scaleInTriggerTicks As String = "5"
        Public Property ScaleInTriggerTicks As String
            Get
                Return _scaleInTriggerTicks
            End Get
            Set(value As String)
                SetProperty(_scaleInTriggerTicks, value)
            End Set
        End Property

        Private _maxRiskHeatTicks As String = "30"
        Public Property MaxRiskHeatTicks As String
            Get
                Return _maxRiskHeatTicks
            End Get
            Set(value As String)
                SetProperty(_maxRiskHeatTicks, value)
            End Set
        End Property

        Private _targetTotalSize As String = "10"
        Public Property TargetTotalSize As String
            Get
                Return _targetTotalSize
            End Get
            Set(value As String)
                SetProperty(_targetTotalSize, value)
            End Set
        End Property

        Private _coreSizeFraction As String = "0.55"
        Public Property CoreSizeFraction As String
            Get
                Return _coreSizeFraction
            End Get
            Set(value As String)
                SetProperty(_coreSizeFraction, value)
            End Set
        End Property

        Private _coreAddsCount As String = "2"
        Public Property CoreAddsCount As String
            Get
                Return _coreAddsCount
            End Get
            Set(value As String)
                SetProperty(_coreAddsCount, value)
            End Set
        End Property

        Private _momentumTierSize As String = "1"
        Public Property MomentumTierSize As String
            Get
                Return _momentumTierSize
            End Get
            Set(value As String)
                SetProperty(_momentumTierSize, value)
            End Set
        End Property

        Private _extensionAllowed As Boolean = False
        Public Property ExtensionAllowed As Boolean
            Get
                Return _extensionAllowed
            End Get
            Set(value As Boolean)
                SetProperty(_extensionAllowed, value)
            End Set
        End Property

        Private _extensionTierSize As String = "1"
        Public Property ExtensionTierSize As String
            Get
                Return _extensionTierSize
            End Get
            Set(value As String)
                SetProperty(_extensionTierSize, value)
            End Set
        End Property

        Private _enableStructureFailExit As Boolean = False
        Public Property EnableStructureFailExit As Boolean
            Get
                Return _enableStructureFailExit
            End Get
            Set(value As Boolean)
                SetProperty(_enableStructureFailExit, value)
            End Set
        End Property

        Private _ema21BreakTicks As String = "5"
        Public Property Ema21BreakTicks As String
            Get
                Return _ema21BreakTicks
            End Get
            Set(value As String)
                SetProperty(_ema21BreakTicks, value)
            End Set
        End Property

        Private _minBarsBeforeExit As String = "5"
        Public Property MinBarsBeforeExit As String
            Get
                Return _minBarsBeforeExit
            End Get
            Set(value As String)
                SetProperty(_minBarsBeforeExit, value)
            End Set
        End Property

        Private _durationHours As String = "8"
        Public Property DurationHours As String
            Get
                Return _durationHours
            End Get
            Set(value As String)
                SetProperty(_durationHours, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE TAB — ENGINE STATE
        ' ══════════════════════════════════════════════════════════════════════

        Private _isRunning As Boolean = False
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                NotifyPropertyChanged(NameOf(IsNotRunning))
                NotifyPropertyChanged(NameOf(CanStart))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property IsNotRunning As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Public ReadOnly Property CanStart As Boolean
            Get
                Return Not _isRunning AndAlso
                       Not String.IsNullOrEmpty(_contractId) AndAlso
                       _selectedAccount IsNot Nothing
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' TAB SELECTION (set from code-behind SelectionChanged)
        ' ══════════════════════════════════════════════════════════════════════

        Private _isLiveTabSelected As Boolean = True
        Public Property IsLiveTabSelected As Boolean
            Get
                Return _isLiveTabSelected
            End Get
            Set(value As Boolean)
                SetProperty(_isLiveTabSelected, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE TAB — POSITION DISPLAY
        ' ══════════════════════════════════════════════════════════════════════

        Private _positionDisplay As String = "Contracts: 0 / 10"
        Public Property PositionDisplay As String
            Get
                Return _positionDisplay
            End Get
            Set(value As String)
                SetProperty(_positionDisplay, value)
            End Set
        End Property

        Private _averageEntryDisplay As String = "—"
        Public Property AverageEntryDisplay As String
            Get
                Return _averageEntryDisplay
            End Get
            Set(value As String)
                SetProperty(_averageEntryDisplay, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE TAB — PERFORMANCE
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property WinLossDisplay As String
            Get
                Return $"Wins: {_winCount}   Losses: {_lossCount}"
            End Get
        End Property

        Public ReadOnly Property TotalPnlDisplay As String
            Get
                Dim sign = If(_totalPnl >= 0, "+", "")
                Return $"{sign}${_totalPnl:F2}"
            End Get
        End Property

        Public ReadOnly Property TotalPnlBrush As Brush
            Get
                Return If(_totalPnl >= 0,
                          DirectCast(New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61)), Brush),
                          New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A)))
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE TAB — FREE RIDE DISPLAY
        ' ══════════════════════════════════════════════════════════════════════

        Private _freeRideDisplay As String = ""
        Public Property FreeRideDisplay As String
            Get
                Return _freeRideDisplay
            End Get
            Set(value As String)
                SetProperty(_freeRideDisplay, value)
            End Set
        End Property

        Public ReadOnly Property FreeRideBrush As Brush
            Get
                Return New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61))
            End Get
        End Property

        Private _heatDisplay As String = ""
        Public Property HeatDisplay As String
            Get
                Return _heatDisplay
            End Get
            Set(value As String)
                SetProperty(_heatDisplay, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE TAB — SIGNAL LOG
        ' ══════════════════════════════════════════════════════════════════════

        Public Property LogEntries As New ObservableCollection(Of String)

        ' ══════════════════════════════════════════════════════════════════════
        ' BACKTEST TAB — SETUP
        ' ══════════════════════════════════════════════════════════════════════

        Private _backtestContractId As String = ""
        Public Property BacktestContractId As String
            Get
                Return _backtestContractId
            End Get
            Set(value As String)
                SetProperty(_backtestContractId, value)
            End Set
        End Property

        Private _backtestStartDate As Date = Date.Today.AddMonths(-1)
        Public Property BacktestStartDate As Date
            Get
                Return _backtestStartDate
            End Get
            Set(value As Date)
                SetProperty(_backtestStartDate, value)
            End Set
        End Property

        Private _backtestEndDate As Date = Date.Today
        Public Property BacktestEndDate As Date
            Get
                Return _backtestEndDate
            End Get
            Set(value As Date)
                SetProperty(_backtestEndDate, value)
            End Set
        End Property

        Private _btInitialTpAmount As String = "20"
        Public Property BtInitialTpAmount As String
            Get
                Return _btInitialTpAmount
            End Get
            Set(value As String)
                SetProperty(_btInitialTpAmount, value)
            End Set
        End Property

        Private _btInitialSlAmount As String = "10"
        Public Property BtInitialSlAmount As String
            Get
                Return _btInitialSlAmount
            End Get
            Set(value As String)
                SetProperty(_btInitialSlAmount, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' BACKTEST TAB — STATE
        ' ══════════════════════════════════════════════════════════════════════

        Private _isBacktesting As Boolean = False
        Public Property IsBacktesting As Boolean
            Get
                Return _isBacktesting
            End Get
            Set(value As Boolean)
                SetProperty(_isBacktesting, value)
                NotifyPropertyChanged(NameOf(IsNotBacktesting))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property IsNotBacktesting As Boolean
            Get
                Return Not _isBacktesting
            End Get
        End Property

        Private _backtestProgress As Integer = 0
        Public Property BacktestProgress As Integer
            Get
                Return _backtestProgress
            End Get
            Set(value As Integer)
                SetProperty(_backtestProgress, value)
            End Set
        End Property

        Private _hasBacktestResults As Boolean = False
        Public Property HasBacktestResults As Boolean
            Get
                Return _hasBacktestResults
            End Get
            Set(value As Boolean)
                SetProperty(_hasBacktestResults, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' BACKTEST TAB — RESULTS
        ' ══════════════════════════════════════════════════════════════════════

        Private _btTotalTrades As String = "0"
        Public Property BtTotalTrades As String
            Get
                Return _btTotalTrades
            End Get
            Set(value As String)
                SetProperty(_btTotalTrades, value)
            End Set
        End Property

        Private _btWinRateDisplay As String = "—"
        Public Property BtWinRateDisplay As String
            Get
                Return _btWinRateDisplay
            End Get
            Set(value As String)
                SetProperty(_btWinRateDisplay, value)
            End Set
        End Property

        Private _btNetPnlDisplay As String = "—"
        Public Property BtNetPnlDisplay As String
            Get
                Return _btNetPnlDisplay
            End Get
            Set(value As String)
                SetProperty(_btNetPnlDisplay, value)
                NotifyPropertyChanged(NameOf(BtPnlBrush))
            End Set
        End Property

        Private _btMaxDrawdownDisplay As String = "—"
        Public Property BtMaxDrawdownDisplay As String
            Get
                Return _btMaxDrawdownDisplay
            End Get
            Set(value As String)
                SetProperty(_btMaxDrawdownDisplay, value)
            End Set
        End Property

        Private _btAvgPnlDisplay As String = "—"
        Public Property BtAvgPnlDisplay As String
            Get
                Return _btAvgPnlDisplay
            End Get
            Set(value As String)
                SetProperty(_btAvgPnlDisplay, value)
            End Set
        End Property

        Public ReadOnly Property BtPnlBrush As Brush
            Get
                Return If(_btNetPnl >= 0,
                          DirectCast(New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61)), Brush),
                          New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A)))
            End Get
        End Property

        Public Property BacktestTrades As New ObservableCollection(Of SniperBacktestTradeRow)

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property StartCommand As RelayCommand
        Public ReadOnly Property StopCommand As RelayCommand
        Public ReadOnly Property RunBacktestCommand As RelayCommand
        Public ReadOnly Property AskClaudeCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        ' ══════════════════════════════════════════════════════════════════════
        ' CLAUDE ANALYSIS
        ' ══════════════════════════════════════════════════════════════════════

        Private _claudeAnalysis As String = String.Empty
        Public Property ClaudeAnalysis As String
            Get
                Return _claudeAnalysis
            End Get
            Set(value As String)
                SetProperty(_claudeAnalysis, value)
            End Set
        End Property

        Private _isClaudeAnalysing As Boolean = False
        Public Property IsClaudeAnalysing As Boolean
            Get
                Return _isClaudeAnalysing
            End Get
            Set(value As Boolean)
                If SetProperty(_isClaudeAnalysing, value) Then
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _hasClaudeAnalysis As Boolean = False
        Public Property HasClaudeAnalysis As Boolean
            Get
                Return _hasClaudeAnalysis
            End Get
            Set(value As Boolean)
                SetProperty(_hasClaudeAnalysis, value)
            End Set
        End Property

        Public Sub New(accountService As IAccountService,
                       backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       claudeReviewService As ClaudeReviewService,
                       engine As ISniperExecutionEngine,
                       session As ITradingSessionContext)
            _accountService = accountService
            _backtestService = backtestService
            _barCollectionService = barCollectionService
            _claudeReviewService = claudeReviewService
            _engine = engine
            _session = session

            StartCommand = New RelayCommand(AddressOf ExecuteStart, Function() CanStart)
            StopCommand = New RelayCommand(AddressOf ExecuteStop, Function() _isRunning)
            RunBacktestCommand = New RelayCommand(AddressOf ExecuteRunBacktest, Function() Not _isBacktesting)
            AskClaudeCommand = New RelayCommand(
                AddressOf ExecuteAskClaude,
                Function() _hasBacktestResults AndAlso Not _isClaudeAnalysing)

            ' Wire engine events
            AddHandler _engine.LogMessage, AddressOf OnEngineLog
            AddHandler _engine.ExecutionStopped, AddressOf OnEngineStopped
            AddHandler _engine.TradeOpened, AddressOf OnTradeOpened
            AddHandler _engine.TradeClosed, AddressOf OnTradeClosed
            AddHandler _engine.PositionChanged, AddressOf OnPositionChanged

            AddHandler _backtestService.ProgressUpdated, AddressOf OnBacktestProgress

            ' Populate initial strategy explanation (reversed so newest-first Insert reads top→bottom)
            AddLog("Setup: Select Account & Contract above, then click Start Sniper.")
            AddLog("—")
            AddLog("Safety Rails: Auto-cleanup of orphan orders; emergency flatten if SL rejected.")
            AddLog("Free Ride: At 3+ contracts, SLs automatically move to Breakeven (Avg Entry) or trail price.")
            AddLog("Risk Cap: Scaling blocked if Total Tick Risk (Heat) exceeds Max Heat limit.")
            AddLog("Management: Incremental brackets (one per scale-in). OCO ensures clean exits.")
            AddLog("Scale-In: If trend holds & price moves > ScaleInTicks, add +1 contract with NEW bracket.")
            AddLog("Enter: Market entry 1 contract (initial) with fixed TP/SL bracket (StopLimit).")
            AddLog("Wait: EMA8 crosses EMA21 + confirmation from EMA50 (aligned slope).")
            AddLog("📌 STRATEGY EXPLANATION: 3-EMA CASCADE Momentum")
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' DATA LOADING
        ' ══════════════════════════════════════════════════════════════════════

        Public Async Sub LoadDataAsync()
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In accountList
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 ' Prefer the account already chosen on the Dashboard (session context).
                                 Dim sessionAcc = _session.SelectedAccount
                                 Dim preferred = If(sessionAcc IsNot Nothing,
                                     Accounts.FirstOrDefault(Function(a) a.Id = sessionAcc.Id),
                                     Nothing)
                                 If preferred Is Nothing Then
                                     preferred = Accounts.FirstOrDefault(
                                         Function(a) a.Name IsNot Nothing AndAlso
                                                     a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase))
                                 End If
                                 SelectedAccount = If(preferred, Accounts(0))
                             End If
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() AddLog($"⚠ Account load error: {ex.Message}"))
            End Try
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' LIVE SNIPER — START / STOP
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteStart()
            If String.IsNullOrEmpty(_contractId) OrElse _selectedAccount Is Nothing Then Return

            Dim heat, targetSize, coreAdds, momSize, extSize As Integer
            Dim scaleK, coreFrac As Double
            Dim tpDollars As Decimal = 0D
            Dim slDollars As Decimal = 0D

            Decimal.TryParse(_initialTpAmount, tpDollars)
            Decimal.TryParse(_initialSlAmount, slDollars)
            Double.TryParse(_scaleInTriggerTicks, scaleK) ' 'k' factor (ATR multiplier)
            Integer.TryParse(_maxRiskHeatTicks, heat)

            Integer.TryParse(_targetTotalSize, targetSize)
            Double.TryParse(_coreSizeFraction, coreFrac)
            Integer.TryParse(_coreAddsCount, coreAdds)
            Integer.TryParse(_momentumTierSize, momSize)
            Integer.TryParse(_extensionTierSize, extSize)

            If tpDollars <= 0D Then tpDollars = 20D
            If slDollars <= 0D Then slDollars = 10D

            ' Resolve correct contract ID: TopStepX uses PX contract format (e.g. "CON.F.US.MCLE.K26"),
            ' not the eToro display symbol (e.g. "OIL"). This ensures IsTopStepX = True in the engine
            ' and that bar ingestion uses the right contract.
            Dim effectiveContractId As String = _contractId
            If _session.ActiveBroker = BrokerType.TopStepX Then
                Dim fav = FavouriteContracts.TryGetBySymbol(_contractId)
                If fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId) Then
                    effectiveContractId = fav.PxContractId
                End If
            End If

            ' Convert dollar TP/SL to ticks for ISniperExecutionEngine.Start()
            Dim tickVal = GetTickValue(effectiveContractId)
            Dim tp As Integer = If(tickVal > 0D, CInt(Math.Round(tpDollars / tickVal)), 20)
            Dim sl As Integer = If(tickVal > 0D, CInt(Math.Round(slDollars / tickVal)), 10)

            If scaleK <= 0 Then scaleK = 0.4
            If heat <= 0 Then heat = 30
            If targetSize <= 0 Then targetSize = 10
            If coreFrac <= 0 Then coreFrac = 0.55
            If coreAdds <= 0 Then coreAdds = 2
            If momSize <= 0 Then momSize = 1
            If extSize <= 0 Then extSize = 1

            Dim emaBreak, minBars As Integer
            Integer.TryParse(_ema21BreakTicks, emaBreak)
            Integer.TryParse(_minBarsBeforeExit, minBars)

            Dim dur As Double = 2.0
            Double.TryParse(_durationHours, dur)
            If dur <= 0 Then dur = 2.0

            ' Reset performance counters
            _winCount = 0
            _lossCount = 0
            _totalPnl = 0D
            NotifyPropertyChanged(NameOf(WinLossDisplay))
            NotifyPropertyChanged(NameOf(TotalPnlDisplay))
            NotifyPropertyChanged(NameOf(TotalPnlBrush))

            ' Reset position and log
            PositionDisplay = $"Contracts: 0 / {targetSize}"
            AverageEntryDisplay = "—"
            HeatDisplay = $"Heat: 0 / {heat}"
            FreeRideDisplay = ""
            LogEntries.Clear()

            AddLog($"   TP=${tpDollars:F0} SL=${slDollars:F0} → {tp}/{sl} ticks | Momentum Add={momSize}, Extension Add={extSize} (allowed={_extensionAllowed})")
            AddLog($"⚡ Starting Sniper: MaxSize={targetSize}, Core={coreFrac:P0} in {coreAdds} entries.")

            IsRunning = True

            _engine.Start(effectiveContractId, _selectedAccount.Id,
                          tp, sl, heat, scaleK,
                          targetSize, coreFrac, coreAdds, momSize, _extensionAllowed, extSize,
                          _enableStructureFailExit, emaBreak, minBars,
                          dur,
                          GetTickSize(effectiveContractId),
                          GetTickValue(effectiveContractId))
        End Sub

        Private Async Sub ExecuteStop()
            Await _engine.StopAsync("Stopped by user")
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' ENGINE EVENT HANDLERS
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub OnEngineLog(sender As Object, e As String)
            Dispatch(Sub() AddLog(e))
        End Sub

        Private Sub OnEngineStopped(sender As Object, e As String)
            Dispatch(Sub()
                         IsRunning = False
                         AddLog($"⏹ Sniper stopped: {e}")
                     End Sub)
        End Sub

        Private Sub OnTradeOpened(sender As Object, e As TradeOpenedEventArgs)
            Dispatch(Sub()
                         AddLog($"🔵 Trade opened — {e.Side} | confidence {e.ConfidencePct}%")
                     End Sub)
        End Sub

        Private Sub OnTradeClosed(sender As Object, e As TradeClosedEventArgs)
            Dispatch(Sub()
                         If e.PnL > 0 Then
                             _winCount += 1
                         ElseIf e.PnL < 0 Then
                             _lossCount += 1
                         End If
                         ' e.PnL = 0 (breakeven or estimation failed) → neither Win nor Loss
                         _totalPnl += e.PnL

                         Dim sign = If(e.PnL >= 0, "+", "")
                         AddLog($"🔴 Trade closed — {e.ExitReason}  P&L: {sign}${e.PnL:F2}")

                         NotifyPropertyChanged(NameOf(WinLossDisplay))
                         NotifyPropertyChanged(NameOf(TotalPnlDisplay))
                         NotifyPropertyChanged(NameOf(TotalPnlBrush))
                     End Sub)
        End Sub

        Private Sub OnPositionChanged(sender As Object, e As SniperPositionEventArgs)
            Dispatch(Sub()
                         PositionDisplay = $"Contracts: {e.CurrentQty} / {TargetTotalSize}"
                         AverageEntryDisplay = If(e.CurrentQty > 0,
                                                  e.AverageEntry.ToString("F2"),
                                                  "—")

                         HeatDisplay = If(e.CurrentQty > 0, $"Heat: {e.CurrentHeat:F0}/{MaxRiskHeatTicks}", "")

                         FreeRideDisplay = If(e.FreeRideActive,
                                              "🔒 Free ride active",
                                              "")
                     End Sub)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' SNIPER BACKTEST
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteRunBacktest()
            If String.IsNullOrEmpty(_backtestContractId) Then
                AddLog("⚠ Select a contract for the backtest first")
                Return
            End If

            Dim slMult, tpMult As Decimal
            If Not Decimal.TryParse(_btInitialSlAmount, slMult) OrElse slMult <= 0D Then slMult = 1.0D
            If Not Decimal.TryParse(_btInitialTpAmount, tpMult) OrElse tpMult <= 0D Then tpMult = 2.5D

            Dim config As New BacktestConfiguration With {
                .RunName = $"Sniper {DateTime.Now:yyyyMMdd-HHmm} — {_backtestContractId}",
                .ContractId = _backtestContractId,
                .Timeframe = 1,
                .StartDate = _backtestStartDate,
                .EndDate = _backtestEndDate,
                .InitialCapital = 50000D,
                .UseAtrMode = True,
                .SlAtrMultiple = slMult,
                .TpAtrMultiple = tpMult,
                .MinSignalConfidence = 0.8F,
                .Quantity = 1,
                .TickSize = GetTickSize(_backtestContractId),
                .PointValue = GetPointValue(_backtestContractId),
                .StrategyCondition = StrategyConditionType.TripleEmaCascade
            }

            _cancelSource = New CancellationTokenSource()
            IsBacktesting = True
            BacktestProgress = 0
            HasBacktestResults = False
            BacktestTrades.Clear()

            Task.Run(Async Function()
                         Dim ex As Exception = Nothing
                         Try
                             ' Ensure 1-min bars are available before running
                             Dim ensureResult = Await _barCollectionService.EnsureBarsAsync(
                                 _backtestContractId,
                                 _backtestStartDate,
                                 _backtestEndDate,
                                 BarTimeframe.OneMinute,
                                 Nothing,
                                 _cancelSource.Token)

                             If Not ensureResult.Success Then
                                 Dispatch(Sub()
                                              IsBacktesting = False
                                              AddLog($"✗ Bar download failed: {ensureResult.Message}")
                                          End Sub)
                                 Return
                             End If

                             Dim result = Await _backtestService.RunBacktestAsync(config, _cancelSource.Token)
                             Dispatch(Sub() ShowBacktestResult(result))

                         Catch innerEx As OperationCanceledException
                             Dispatch(Sub() AddLog("Backtest cancelled"))
                         Catch innerEx As Exception
                             ex = innerEx
                         End Try

                         ' VB.NET: cannot Await inside Catch — handle ex after the Try/Catch
                         If ex IsNot Nothing Then
                             Dispatch(Sub() AddLog($"✗ Backtest error: {ex.Message}"))
                         End If

                         Dispatch(Sub()
                                      IsBacktesting = False
                                      _cancelSource?.Dispose()
                                  End Sub)
                     End Function)
        End Sub

        Private Async Sub ExecuteAskClaude()
            If Not _hasBacktestResults Then Return

            IsClaudeAnalysing = True
            HasClaudeAnalysis = False
            ClaudeAnalysis = "🤖 Asking Claude Haiku…"

            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine($"Sniper (3-EMA Cascade) Backtest Results — {_backtestContractId}")
            sb.AppendLine($"Date range: {_backtestStartDate:yyyy-MM-dd} to {_backtestEndDate:yyyy-MM-dd}")
            sb.AppendLine($"Trades: {_btTotalTrades}  |  Win Rate: {_btWinRateDisplay}  |  Net P&L: {_btNetPnlDisplay}")
            sb.AppendLine($"Max Drawdown: {_btMaxDrawdownDisplay}  |  Avg P&L/Trade: {_btAvgPnlDisplay}")
            sb.AppendLine()
            sb.AppendLine("Strategy: 3-EMA Cascade (EMA8/EMA21/EMA50 momentum with scaled entries).")
            sb.AppendLine("Provide a concise assessment: what these numbers suggest about the strategy on this instrument,")
            sb.AppendLine("any overfitting warnings, and one improvement recommendation.")

            Dim analysis = Await _claudeReviewService.AnalyseBacktestResultsAsync(sb.ToString())
            Dispatch(Sub()
                         ClaudeAnalysis = analysis
                         HasClaudeAnalysis = True
                         IsClaudeAnalysing = False
                     End Sub)
        End Sub

        Private Sub ShowBacktestResult(result As BacktestResult)
            If result Is Nothing Then
                AddLog("⚠ Backtest returned no results")
                Return
            End If

            _btNetPnl = result.TotalPnL
            BtTotalTrades = result.TotalTrades.ToString()
            BtWinRateDisplay = If(result.TotalTrades > 0, $"{result.WinRate:P1}", "—")
            BtNetPnlDisplay = If(result.TotalPnL >= 0,
                                     $"+${result.TotalPnL:F2}",
                                     $"-${Math.Abs(result.TotalPnL):F2}")
            BtMaxDrawdownDisplay = $"${result.MaxDrawdown:F2}"
            BtAvgPnlDisplay = If(result.TotalTrades > 0,
                                     $"${result.AveragePnLPerTrade:F2}",
                                     "—")

            BacktestTrades.Clear()
            For Each t In result.Trades
                BacktestTrades.Add(New SniperBacktestTradeRow(t))
            Next

            HasBacktestResults = True
            HasClaudeAnalysis = False
            ClaudeAnalysis = String.Empty
            RelayCommand.RaiseCanExecuteChanged()
            AddLog($"✓ Backtest complete — {result.TotalTrades} trades | " &
                   $"Win: {result.WinRate:P1} | P&L: ${result.TotalPnL:F2}")
        End Sub

        Private Sub OnBacktestProgress(sender As Object, e As BacktestProgressEventArgs)
            Dispatch(Sub() BacktestProgress = e.PercentComplete)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' HELPERS
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub AddLog(message As String)
            ' Cap at 1,000 entries — remove oldest (tail) before inserting newest (head).
            If LogEntries.Count >= 1000 Then LogEntries.RemoveAt(LogEntries.Count - 1)
            LogEntries.Insert(0, message)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

        ''' <summary>Price units per tick for the contract — broker-aware.</summary>
        Private Function GetTickSize(contractId As String) As Decimal
            Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
            If fav Is Nothing Then Return 0.01D
            Return If(_session.ActiveBroker = BrokerType.TopStepX AndAlso fav.PxTickSize > 0,
                      fav.PxTickSize, fav.EToroTickSize)
        End Function

        Private Function GetTickValue(contractId As String) As Decimal
            Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
            If fav Is Nothing Then Return 0.01D
            Return If(_session.ActiveBroker = BrokerType.TopStepX AndAlso fav.PxTickValue > 0,
                      fav.PxTickValue, fav.EToroTickValue)
        End Function

        Private Shared Function GetPointValue(contractId As String) As Decimal
            Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
            Return If(fav IsNot Nothing, fav.EToroPointValue, 1.0D)
        End Function

        ' ══════════════════════════════════════════════════════════════════════
        ' DISPOSE
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _engine.LogMessage, AddressOf OnEngineLog
                RemoveHandler _engine.ExecutionStopped, AddressOf OnEngineStopped
                RemoveHandler _engine.TradeOpened, AddressOf OnTradeOpened
                RemoveHandler _engine.TradeClosed, AddressOf OnTradeClosed
                RemoveHandler _engine.PositionChanged, AddressOf OnPositionChanged
                RemoveHandler _backtestService.ProgressUpdated, AddressOf OnBacktestProgress
                _engine?.Dispose()
                _cancelSource?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

    ''' <summary>
    ''' Row data for the Sniper Backtest trade DataGrid.
    ''' Simple POCO — adapts BacktestTrade for display.
    ''' </summary>
    Public Class SniperBacktestTradeRow

        Public ReadOnly Property EntryTimeDisplay As String
        Public ReadOnly Property SideDisplay As String
        Public ReadOnly Property EntryPrice As Decimal
        Public ReadOnly Property ExitPrice As Decimal
        Public ReadOnly Property ExitReason As String
        Public ReadOnly Property PnLDisplay As String

        Public Sub New(trade As BacktestTrade)
            EntryTimeDisplay = trade.EntryTime.ToLocalTime().ToString("dd/MM HH:mm")
            SideDisplay = If(String.Equals(trade.Side, "Buy", StringComparison.OrdinalIgnoreCase),
                                  "BUY", "SELL")
            EntryPrice = trade.EntryPrice
            ExitPrice = trade.ExitPrice.GetValueOrDefault()
            ExitReason = trade.ExitReason

            Dim pnl = trade.PnL.GetValueOrDefault()
            Dim sign = If(pnl >= 0, "+", "")
            PnLDisplay = $"{sign}${pnl:F2}"
        End Sub

    End Class

End Namespace
