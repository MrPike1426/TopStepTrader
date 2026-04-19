Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports Microsoft.Win32
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Backtest page ViewModel — TICKET-006 Phase 2 update.
    '''
    ''' Workflow implemented:
    '''   1. User selects Contract  → resets strategy / clears results
    '''   2. User selects Strategy  → auto-adjusts Capital/Qty/TP/SL + starts bar download
    '''   3. Bar download completes → enables "Run Backtest" button
    '''   4. User clicks Run        → optionally trains EMA/RSI, then executes backtest
    '''   5. Results displayed      → metrics summary + trade list
    '''
    ''' Phase 2: DownloadBarsAsync calls IBarCollectionService.EnsureBarsAsync() (real download).
    ''' Phase 3: EMA/RSI training wired via IModelTrainingService.RetrainAsync().
    '''          Only combined multi-indicator strategies listed (TICKET-006 design decision).
    ''' Phase 4: CSV export implemented via SaveFileDialog + File.WriteAllText.
    ''' </summary>
    Public Class BacktestViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService As IBacktestService
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _claudeReviewService As ClaudeReviewService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService

        Private _cancelSource As CancellationTokenSource
        Private _maxEffortCancelSource As CancellationTokenSource

        ' ── Elapsed-time timer — ticks every second while any work is in progress ──────
        Private ReadOnly _elapsedTimer As DispatcherTimer
        Private _workStartTime As DateTimeOffset

        ' Strategy parameter defaults are defined in StrategyDefaults (TopStepTrader.Core.Trading).
        ' Only combined multi-indicator strategies are listed — single-indicator strategies excluded by design.

        ' ══════════════════════════════════════════════════════════════════════
        ' CONFIGURATION PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _contractIdText As String = ""
        ''' <summary>
        ''' Long-form contract ID (e.g. "CON.F.US.MES.H26") — set by ContractSelectorControl.
        ''' Changing contract resets strategy selection, clears bars status and results.
        ''' </summary>
        Public Property ContractIdText As String
            Get
                Return _contractIdText
            End Get
            Set(value As String)
                Dim old = _contractIdText
                SetProperty(_contractIdText, value)
                If old <> value Then
                    ' Step 1 of workflow: reset all downstream state
                    _selectedStrategyName = Nothing
                    OnPropertyChanged(NameOf(SelectedStrategyName))
                    OnPropertyChanged(NameOf(SelectedContractDisplay))
                    BarsAvailable = False
                    BarsStatusText = ""
                    HasBarsStatus = False
                    ClearResults()
                    OnPropertyChanged(NameOf(CanTrain))
                End If
            End Set
        End Property

        ''' <summary>Friendly name of the chosen contract, shown in white text next to the dropdown.</summary>
        Public ReadOnly Property SelectedContractDisplay As String
            Get
                If String.IsNullOrEmpty(_contractIdText) Then Return String.Empty
                Dim fav = FavouriteContracts.GetDefaults().FirstOrDefault(
                    Function(f) String.Equals(f.ContractId, _contractIdText, StringComparison.OrdinalIgnoreCase))
                Return If(fav IsNot Nothing, fav.Name, _contractIdText)
            End Get
        End Property

        Private _selectedStrategyName As String
        ''' <summary>
        ''' Strategy chosen from the dropdown.
        ''' On change: auto-applies parameter defaults and triggers bar download.
        ''' </summary>
        Public Property SelectedStrategyName As String
            Get
                Return _selectedStrategyName
            End Get
            Set(value As String)
                Dim old = _selectedStrategyName
                SetProperty(_selectedStrategyName, value)
                If old <> value AndAlso Not String.IsNullOrEmpty(value) Then
                    ' Step 2a: auto-populate Capital/Qty/TP/SL from strategy optimums
                    ApplyStrategyDefaults(value)
                    OnPropertyChanged(NameOf(CanTrain))
                    ' Step 2b: trigger bar availability check + download (if contract selected)
                    If Not String.IsNullOrEmpty(_contractIdText) Then
                        DownloadBarsAsync()
                    End If
                End If
            End Set
        End Property

        Private _startDate As Date = DateTime.Today.AddDays(-60)
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

        Private _initialCapital As String = "1000"
        Public Property InitialCapital As String
            Get
                Return _initialCapital
            End Get
            Set(value As String)
                SetProperty(_initialCapital, value)
            End Set
        End Property

        Private _quantity As String = "1"
        ''' <summary>Number of contracts per signal. Auto-populated by strategy defaults.</summary>
        Public Property Quantity As String
            Get
                Return _quantity
            End Get
            Set(value As String)
                SetProperty(_quantity, value)
            End Set
        End Property

        Private _minConfidence As String = "90"
        Public Property MinConfidence As String
            Get
                Return _minConfidence
            End Get
            Set(value As String)
                SetProperty(_minConfidence, value)
            End Set
        End Property

        Private _minAdxThreshold As String = "0"
        ''' <summary>
        ''' Minimum ADX value for entry. 0 = no ADX gate (default — see all raw signals).
        ''' Set to 25 to match live engine behaviour (strong-trend-only entries).
        ''' </summary>
        Public Property MinAdxThreshold As String
            Get
                Return _minAdxThreshold
            End Get
            Set(value As String)
                SetProperty(_minAdxThreshold, value)
            End Set
        End Property

        ' ── Dynamic exit management ─────────────────────────────────────────────

        Private _trailingStopEnabled As Boolean = False
        ''' <summary>When True, the SL trails the best close by the initial SL distance.</summary>
        Public Property TrailingStopEnabled As Boolean
            Get
                Return _trailingStopEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_trailingStopEnabled, value)
            End Set
        End Property

        Private _breakEvenEnabled As Boolean = False
        ''' <summary>When True, SL advances to break-even once price reaches 50% of TP.</summary>
        Public Property BreakEvenEnabled As Boolean
            Get
                Return _breakEvenEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_breakEvenEnabled, value)
            End Set
        End Property

        Private _extendTpEnabled As Boolean = False
        ''' <summary>When True, TP extends by one TP unit each time bar close surpasses the target (max 3×).</summary>
        Public Property ExtendTpEnabled As Boolean
            Get
                Return _extendTpEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_extendTpEnabled, value)
            End Set
        End Property

        ' ── Force Close (profit cap) ──────────────────────────────────────────────

        Private _forceCloseEnabled As Boolean = False
        ''' <summary>When True, close all legs when position P&amp;L ≥ ForceCloseAmount.</summary>
        Public Property ForceCloseEnabled As Boolean
            Get
                Return _forceCloseEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_forceCloseEnabled, value)
            End Set
        End Property

        Private _forceCloseAmount As String = "50"
        ''' <summary>Dollar profit cap per position. Default $50.</summary>
        Public Property ForceCloseAmount As String
            Get
                Return _forceCloseAmount
            End Get
            Set(value As String)
                SetProperty(_forceCloseAmount, value)
            End Set
        End Property

        ' ── ATR-based SL/TP mode ──────────────────────────────────────────────────

        Private _slAtrMultiple As String = "1.5"
        ''' <summary>SL as a multiple of ATR(14) at entry. Only used when UseAtrMode=True.</summary>
        Public Property SlAtrMultiple As String
            Get
                Return _slAtrMultiple
            End Get
            Set(value As String)
                SetProperty(_slAtrMultiple, value)
            End Set
        End Property

        Private _tpAtrMultiple As String = "3.0"
        ''' <summary>TP as a multiple of ATR(14) at entry. Only used when UseAtrMode=True.</summary>
        Public Property TpAtrMultiple As String
            Get
                Return _tpAtrMultiple
            End Get
            Set(value As String)
                SetProperty(_tpAtrMultiple, value)
            End Set
        End Property

        ' ── Persona selection ─────────────────────────────────────────────────
        Private _selectedPersona As String = "Damian"
        Private _personaExplicitlyChosen As Boolean = False  ' True once user clicks a persona button
        Private _configMaxScaleIns As Integer = 2   ' mirrors active persona; default = Damian

        ''' <summary>True when Lewis (risk-averse) persona is active.</summary>
        Public ReadOnly Property IsLewisSelected As Boolean
            Get
                Return _selectedPersona = "Lewis"
            End Get
        End Property

        ''' <summary>True when Damian (moderate) persona is active — default.</summary>
        Public ReadOnly Property IsDamianSelected As Boolean
            Get
                Return _selectedPersona = "Damian"
            End Get
        End Property

        ''' <summary>True when Joe (aggressive) persona is active.</summary>
        Public ReadOnly Property IsJoeSelected As Boolean
            Get
                Return _selectedPersona = "Joe"
            End Get
        End Property

        Private _selectedInterval As String = "5 min"
        ''' <summary>
        ''' Selected bar timeframe for backtest (display format: "1 min", "5 min", etc.).
        ''' Changing the interval resets bars status and triggers a new bar check/download
        ''' (since 5-min bars and 15-min bars are stored separately in SQLite).
        ''' </summary>
        Public Property SelectedInterval As String
            Get
                Return _selectedInterval
            End Get
            Set(value As String)
                Dim old = _selectedInterval
                SetProperty(_selectedInterval, value)
                If old <> value AndAlso
                   Not String.IsNullOrEmpty(_contractIdText) AndAlso
                   Not String.IsNullOrEmpty(_selectedStrategyName) Then
                    BarsAvailable = False
                    BarsStatusText = ""
                    HasBarsStatus = False
                    DownloadBarsAsync()
                End If
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' PROGRESS / STATE PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

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
                OnPropertyChanged(NameOf(IsWorking))
                OnPropertyChanged(NameOf(IsIndeterminateProgress))
                OnPropertyChanged(NameOf(ShowDescription))
                OnPropertyChanged(NameOf(NotIsRunning))
                If value Then StartElapsedTimer() Else StopElapsedTimerIfIdle()
            End Set
        End Property

        ''' <summary>Inverse of IsRunning — keeps ProgressText visible during download/training but hidden during run.</summary>
        Public ReadOnly Property NotIsRunning As Boolean
            Get
                Return Not _isRunning
            End Get
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
                OnPropertyChanged(NameOf(IsWorking))
                OnPropertyChanged(NameOf(IsIndeterminateProgress))
                If value Then StartElapsedTimer() Else StopElapsedTimerIfIdle()
            End Set
        End Property

        Private _isBarsDownloading As Boolean
        ''' <summary>True while the bar availability check / download is in progress.</summary>
        Public Property IsBarsDownloading As Boolean
            Get
                Return _isBarsDownloading
            End Get
            Set(value As Boolean)
                SetProperty(_isBarsDownloading, value)
                OnPropertyChanged(NameOf(IsWorking))
                OnPropertyChanged(NameOf(IsIndeterminateProgress))
                OnPropertyChanged(NameOf(CanTrain))
                If value Then StartElapsedTimer() Else StopElapsedTimerIfIdle()
            End Set
        End Property

        Private _barsAvailable As Boolean
        ''' <summary>
        ''' True when bars for the selected contract are confirmed available.
        ''' Controls whether "Run Backtest" is enabled (CanRun checks this).
        ''' </summary>
        Public Property BarsAvailable As Boolean
            Get
                Return _barsAvailable
            End Get
            Set(value As Boolean)
                SetProperty(_barsAvailable, value)
                OnPropertyChanged(NameOf(CanRun))
            End Set
        End Property

        Private _barsStatusText As String = ""
        ''' <summary>Human-readable bar-download status shown below the config form.</summary>
        Public Property BarsStatusText As String
            Get
                Return _barsStatusText
            End Get
            Set(value As String)
                SetProperty(_barsStatusText, value)
            End Set
        End Property

        Private _barsStatusColor As String = "AccentBrush"
        ''' <summary>BrushKeyConverter key: "AccentBrush" = neutral, "BuyBrush" = ok, "SellBrush" = error.</summary>
        Public Property BarsStatusColor As String
            Get
                Return _barsStatusColor
            End Get
            Set(value As String)
                SetProperty(_barsStatusColor, value)
            End Set
        End Property

        Private _hasBarsStatus As Boolean
        ''' <summary>Controls Visibility of the BarsStatusText TextBlock.</summary>
        Public Property HasBarsStatus As Boolean
            Get
                Return _hasBarsStatus
            End Get
            Set(value As Boolean)
                SetProperty(_hasBarsStatus, value)
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

        ''' <summary>True when any async work is active — drives progress bar Visibility.</summary>
        Public ReadOnly Property IsWorking As Boolean
            Get
                Return _isRunning OrElse _isTraining OrElse _isBarsDownloading
            End Get
        End Property

        Private _elapsedTimeText As String = ""
        ''' <summary>Live elapsed time shown while any work is in progress, e.g. "1:23".</summary>
        Public Property ElapsedTimeText As String
            Get
                Return _elapsedTimeText
            End Get
            Private Set(value As String)
                SetProperty(_elapsedTimeText, value)
            End Set
        End Property

        ''' <summary>True when description panel should show — hidden while backtest is running.</summary>
        Public ReadOnly Property ShowDescription As Boolean
            Get
                Return _hasStrategyDescription AndAlso Not _isRunning
            End Get
        End Property

        Private Sub OnElapsedTick(sender As Object, e As EventArgs)
            Dim elapsed = DateTimeOffset.UtcNow - _workStartTime
            Dim mins = CInt(Math.Floor(elapsed.TotalMinutes))
            Dim secs = elapsed.Seconds
            ElapsedTimeText = $"{mins}:{secs:D2}"
        End Sub

        ''' <summary>Starts the elapsed timer; safe to call multiple times.</summary>
        Private Sub StartElapsedTimer()
            _workStartTime = DateTimeOffset.UtcNow
            ElapsedTimeText = "0:00"
            If Not _elapsedTimer.IsEnabled Then _elapsedTimer.Start()
        End Sub

        ''' <summary>Stops the elapsed timer when no work remains active.</summary>
        Private Sub StopElapsedTimerIfIdle()
            If Not IsWorking Then
                _elapsedTimer.Stop()
                ElapsedTimeText = ""
            End If
        End Sub

        ''' <summary>
        ''' True during bar download or model training (indeterminate progress);
        ''' False during backtest execution (has a deterministic 0–100 value).
        ''' </summary>
        Public ReadOnly Property IsIndeterminateProgress As Boolean
            Get
                Return _isTraining OrElse _isBarsDownloading
            End Get
        End Property

        ''' <summary>
        ''' Run Backtest is enabled only when not already running/training AND bars are confirmed available.
        ''' This enforces the workflow: Contract → Strategy → Download → Run.
        ''' </summary>
        Public ReadOnly Property CanRun As Boolean
            Get
                Return Not _isRunning AndAlso Not _isTraining AndAlso _barsAvailable
            End Get
        End Property

        Public ReadOnly Property CanCancel As Boolean
            Get
                Return _isRunning
            End Get
        End Property

        Public ReadOnly Property CanTrain As Boolean
            Get
                Return Not _isRunning AndAlso Not _isTraining AndAlso Not _isBarsDownloading AndAlso
                       Not String.IsNullOrEmpty(_contractIdText) AndAlso
                       Not String.IsNullOrEmpty(_selectedStrategyName)
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' RESULTS PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property Trades As New ObservableCollection(Of BacktestTradeRowVm)()

        ''' <summary>One row per timeframe — populated as each backtest completes during a multi-timeframe run.</summary>
        Public ReadOnly Property TimeframeResults As New ObservableCollection(Of TimeframeResultRowVm)()

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

        ' ══════════════════════════════════════════════════════════════════════
        ' COLLECTIONS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property PreviousRuns As New ObservableCollection(Of BacktestRunSummaryVm)()

        ''' <summary>
        ''' Combined multi-indicator strategies shown in the dropdown.
        ''' Single-indicator strategies (pure RSI, pure EMA, etc.) are excluded by design —
        ''' backtesting a single-indicator strategy does not produce reliable live trading signals.
        ''' </summary>
        Public ReadOnly Property AvailableStrategies As New ObservableCollection(Of String)()

        ''' <summary>
        ''' Bar timeframe options for backtest (display format: "1 min", "5 min", etc.).
        ''' Currently 5-minute bars are cached; other intervals are for future use.
        ''' </summary>
        Public ReadOnly Property AvailableIntervals As New ObservableCollection(Of String)()

        ''' <summary>
        ''' Legacy collection — retained for compatibility; not bound in the new XAML.
        ''' ContractSelectorControl is self-contained and does not use this list.
        ''' </summary>
        Public ReadOnly Property AvailableContracts As New ObservableCollection(Of Contract)()

        ' ══════════════════════════════════════════════════════════════════════
        ' STRATEGY PANEL PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _activeStrategyText As String = "None selected — click a card above"
        ''' <summary>Shown inline next to the STRATEGY heading. Updates when a card is clicked.</summary>
        Public Property ActiveStrategyText As String
            Get
                Return _activeStrategyText
            End Get
            Set(value As String)
                SetProperty(_activeStrategyText, value)
            End Set
        End Property

        Private _strategyNakedDescription As String = String.Empty
        ''' <summary>Plain-English description shown in the “WHAT THIS STRATEGY DOES” panel.</summary>
        Public Property StrategyNakedDescription As String
            Get
                Return _strategyNakedDescription
            End Get
            Set(value As String)
                SetProperty(_strategyNakedDescription, value)
            End Set
        End Property

        Private _hasStrategyDescription As Boolean = False
        ''' <summary>True once a strategy card has been selected and the description is populated.
        ''' Drives Visibility of the description panel in the XAML.</summary>
        Public Property HasStrategyDescription As Boolean
            Get
                Return _hasStrategyDescription
            End Get
            Set(value As Boolean)
                SetProperty(_hasStrategyDescription, value)
                OnPropertyChanged(NameOf(StrategyDescriptionPanelVisible))
                OnPropertyChanged(NameOf(ShowDescription))
            End Set
        End Property

        ''' <summary>True when a strategy has been selected. Drives Visibility of the description panel.</summary>
        Public ReadOnly Property StrategyDescriptionPanelVisible As Boolean
            Get
                Return _hasStrategyDescription
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property SelectEmaRsiCombinedCommand As RelayCommand
        Public ReadOnly Property SelectMultiConfluenceEngineCommand As RelayCommand
        Public ReadOnly Property SelectLultDivergenceCommand As RelayCommand
        Public ReadOnly Property SelectBbSqueezeScalperCommand As RelayCommand
        Public ReadOnly Property SelectVidyaCommand As RelayCommand
        Public ReadOnly Property SelectNakedTraderCommand As RelayCommand
        Public ReadOnly Property SelectDoubleBubbleButtCommand As RelayCommand
        Public ReadOnly Property RunCommand As RelayCommand
        Public ReadOnly Property CancelCommand As RelayCommand
        Public ReadOnly Property LoadHistoryCommand As RelayCommand
        Public ReadOnly Property TrainModelCommand As RelayCommand
        Public ReadOnly Property ExportCsvCommand As RelayCommand
        Public ReadOnly Property MaximumEffortCommand As RelayCommand
        Public ReadOnly Property MaximumEffortCancelCommand As RelayCommand
        Public ReadOnly Property SelectPersonaCommand As RelayCommand
        Public ReadOnly Property CopyAiAnalysisCommand As RelayCommand
        Public ReadOnly Property CopyPinnedAnalysisCommand As RelayCommand
        Public ReadOnly Property PinResultCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       claudeReviewService As ClaudeReviewService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService)

            _backtestService = backtestService
            _barCollectionService = barCollectionService
            _claudeReviewService = claudeReviewService
            _session = session
            _personaService = personaService

            ' Populate legacy contract collection (not used by new XAML)
            For Each f In FavouriteContracts.GetDefaults()
                AvailableContracts.Add(New Contract With {
                    .Id = f.ContractId,
                    .FriendlyName = f.Name
                })
            Next

            ' Populate strategy dropdown
            AvailableStrategies.Add("EMA/RSI Combined")
            AvailableStrategies.Add("Multi-Confluence Engine")
            AvailableStrategies.Add("LULT Divergence")
            AvailableStrategies.Add("BB Squeeze Scalper")
            AvailableStrategies.Add("VIDYA Cross")
            AvailableStrategies.Add("Naked Trader")
            AvailableStrategies.Add("Double Bubble Butt")

            ' Populate interval dropdown — only natively-supported ProjectX API timeframes.
            ' Removed: "3 min" (API falls back to 1-min), "10 min" (no API code), "4 hours" (no API code).
            ' Added:   "30 min" (API unit=4, fully supported).
            AvailableIntervals.Add("1 min")
            AvailableIntervals.Add("5 min")
            AvailableIntervals.Add("15 min")
            AvailableIntervals.Add("30 min")
            AvailableIntervals.Add("1 hour")

            SelectEmaRsiCombinedCommand = New RelayCommand(AddressOf ApplyEmaRsiCombined)
            SelectMultiConfluenceEngineCommand = New RelayCommand(AddressOf ApplyMultiConfluenceEngine)
            SelectLultDivergenceCommand = New RelayCommand(AddressOf ApplyLultDivergence)
            SelectBbSqueezeScalperCommand = New RelayCommand(AddressOf ApplyBbSqueezeScalper)
            SelectVidyaCommand = New RelayCommand(AddressOf ApplyVidya)
            SelectNakedTraderCommand = New RelayCommand(AddressOf ApplyNakedTrader)
            SelectDoubleBubbleButtCommand = New RelayCommand(AddressOf ApplyDoubleBubbleButt)
            RunCommand = New RelayCommand(AddressOf ExecuteRun, Function() CanRun)
            CancelCommand = New RelayCommand(AddressOf ExecuteCancel, Function() CanCancel)
            LoadHistoryCommand = New RelayCommand(AddressOf LoadPreviousRuns)
            TrainModelCommand = New RelayCommand(AddressOf ExecuteTrainModel, Function() CanTrain)
            ExportCsvCommand = New RelayCommand(AddressOf ExecuteExportCsv)
            MaximumEffortCommand = New RelayCommand(AddressOf ExecuteMaximumEffort, Function() Not _maxEffortIsRunning)
            MaximumEffortCancelCommand = New RelayCommand(Sub() _maxEffortCancelSource?.Cancel(), Function() _maxEffortIsRunning)
            SelectPersonaCommand = New RelayCommand(Sub(p) ApplyPersona(CStr(p)))
            CopyAiAnalysisCommand = New RelayCommand(
                Sub()
                    If Not String.IsNullOrEmpty(_maxEffortAiAnalysis) Then
                        Clipboard.SetText(_maxEffortAiAnalysis)
                    End If
                End Sub,
                Function() Not String.IsNullOrEmpty(_maxEffortAiAnalysis))
            CopyPinnedAnalysisCommand = New RelayCommand(
                Sub()
                    If Not String.IsNullOrEmpty(_pinnedAiAnalysis) Then
                        Clipboard.SetText(_pinnedAiAnalysis)
                    End If
                End Sub,
                Function() Not String.IsNullOrEmpty(_pinnedAiAnalysis))
            PinResultCommand = New RelayCommand(
                Sub(param)
                    Dim row = TryCast(param, MaxEffortRowVm)
                    If row IsNot Nothing AndAlso Not PinnedResults.Contains(row) Then
                        PinnedResults.Add(row)
                    End If
                End Sub,
                Function(param) param IsNot Nothing)

            AddHandler _backtestService.ProgressUpdated, AddressOf OnProgress

            ' ── Pinned top-3 Maximum Effort results (Gold · Multi-Confluence · 1 hr)
            '    Recorded 2025-07 — permanent baseline; never cleared by a new run.
            PinnedResults.Add(New MaxEffortRowVm("Damian", "Gold", "Multi-Confluence", "1 hr",
                                                 trades:=34, winRatePct:=56.0, totalPnLRaw:=17746D,
                                                 sharpe:=7.71, avgPnL:=522D, maxDD:=-1200D))
            PinnedResults.Add(New MaxEffortRowVm("Joe",    "Gold", "Multi-Confluence", "1 hr",
                                                 trades:=39, winRatePct:=54.0, totalPnLRaw:=17534D,
                                                 sharpe:=6.93, avgPnL:=450D, maxDD:=-1500D))
            PinnedResults.Add(New MaxEffortRowVm("Lewis",  "Gold", "Multi-Confluence", "1 hr",
                                                 trades:=29, winRatePct:=52.0, totalPnLRaw:=15744D,
                                                 sharpe:=7.55, avgPnL:=543D, maxDD:=-900D))

            ' Elapsed-time ticker — must be created on the UI thread with Normal priority
            ' so ticks are not starved behind rendering/layout work.
            _elapsedTimer = New DispatcherTimer(DispatcherPriority.Normal,
                                                Application.Current.Dispatcher)
            _elapsedTimer.Interval = TimeSpan.FromSeconds(1)
            AddHandler _elapsedTimer.Tick, AddressOf OnElapsedTick
        End Sub

        Public Sub LoadDataAsync()
            LoadPreviousRuns()
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' STRATEGY WORKFLOW — Steps 2a, 2b, 3
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Step 2a: Auto-populate Capital/Qty/TP/SL from the strategy's optimum defaults.
        ''' Delegates to <see cref="StrategyDefaults.TryGet"/> in TopStepTrader.Core.
        ''' The user may override the values after auto-adjust.
        ''' Only combined multi-indicator strategies are registered — per TICKET-006 design.
        ''' </summary>
        Private Sub ApplyStrategyDefaults(strategyName As String)
            Dim defaults = StrategyDefaults.TryGet(strategyName)
            If defaults IsNot Nothing Then
                ' Only apply the strategy's capital default when no persona has been explicitly
                ' chosen — once the user picks Lewis/Damian/Joe the persona capital takes priority.
                If Not _personaExplicitlyChosen Then
                    InitialCapital = defaults.Capital
                End If
                Quantity = defaults.Qty
            End If
        End Sub

        ''' <summary>
        ''' Applies a risk persona's parameters to the backtest configuration fields.
        ''' Sets Capital from TradeAmount, MinAdxThreshold and MinConfidence from the profile.
        ''' When UseAtrMode is True, also sets SlAtrMultiple / TpAtrMultiple from the profile.
        ''' </summary>
        Private Sub ApplyPersona(personaName As String)
            Dim profile = _personaService.GetProfile(personaName)
            _selectedPersona = personaName.Split(" "c)(0)   ' "Lewis", "Damian", or "Joe"
            _personaExplicitlyChosen = True
            _configMaxScaleIns = profile.MaxScaleIns
            InitialCapital  = profile.TradeAmount.ToString("F0")
            MinAdxThreshold = CInt(profile.AdxThreshold).ToString()
            MinConfidence   = profile.DefaultConfidencePct.ToString()
            SlAtrMultiple = profile.SlMultipleOfN.ToString("F2")
            TpAtrMultiple = profile.TpMultipleOfN.ToString("F2")
            OnPropertyChanged(NameOf(IsLewisSelected))
            OnPropertyChanged(NameOf(IsDamianSelected))
            OnPropertyChanged(NameOf(IsJoeSelected))
        End Sub

        ''' <summary>
        ''' One-click activate for EMA/RSI Combined on the strategy card panel.
        ''' Sets SelectedStrategyName (which triggers parameter auto-fill and bar download),
        ''' then populates the plain-English description panel.
        ''' </summary>
        Private Sub ApplyEmaRsiCombined()
            ' Setting SelectedStrategyName triggers ApplyStrategyDefaults + DownloadBarsAsync
            SelectedStrategyName = "EMA/RSI Combined"

            ActiveStrategyText = "✔  EMA/RSI Combined  (5-min bars · EMA21/EMA50/RSI14)"

            StrategyNakedDescription =
                "Every 5 minutes, this strategy looks at the latest completed bar and runs six quick checks, " &
                "tallying a bull score from 0 to 100." & vbLf & vbLf &
                "It awards 25 points if EMA21 sits above EMA50 (uptrend sign), 20 points if price is above EMA21, " &
                "and 15 more if price is above EMA50. The RSI14 contributes up to 20 points when in the 50–70 range " &
                "(trending bullishly but not overbought). Then 10 points if EMA21 is rising, and a final 10 " &
                "if at least two of the last three candles closed higher." & vbLf & vbLf &
                "When the ADX is above 25 (trending market) and the bull score meets your Min Confidence threshold, " &
                "a Long signal fires. When the bear score (100 − bull) meets the threshold, a Short fires." & vbLf & vbLf &
                "Defaults: 5-min bars · 0.65 confidence threshold (65%)."

            HasStrategyDescription = True
        End Sub

        ''' <summary>
        ''' One-click activate for Multi-Confluence Engine on the strategy card panel.
        ''' Sets SelectedStrategyName (which triggers parameter auto-fill and bar download),
        ''' then populates the plain-English description panel.
        ''' Designed for 15-minute commodity bars: Ichimoku + EMA21/50 + MACD + StochRSI + ADX.
        ''' ALL seven long or short conditions must align before a signal fires.
        ''' </summary>
        Private Sub ApplyMultiConfluenceEngine()
            SelectedStrategyName = "Multi-Confluence Engine"

            ActiveStrategyText = "✔  Multi-Confluence Engine  (15-min bars · Ichimoku · EMA21/50 · MACD · StochRSI · ADX)"

            StrategyNakedDescription =
                "A high-conviction strategy for commodities that requires seven independent conditions to align simultaneously." & vbLf & vbLf &
                "It checks the Ichimoku Cloud first: price must be entirely above (Long) or below (Short) both Span A and Span B. " &
                "If price is inside the cloud — the 'fog zone' — no signal fires, regardless of other conditions. " &
                "The Tenkan-sen (fast line) must also be above the Kijun-sen (slow line) for Long, and below for Short. " &
                "The Lagging Span (current close) must clear the price level from 26 bars ago, confirming the move is genuine." & vbLf & vbLf &
                "Layer two adds trend strength: ADX must be above 25 (trending market) and DI+ must exceed DI- for Long. " &
                "Layer three adds momentum: the MACD histogram must be positive and rising for Long. " &
                "Finally, the Stochastic RSI is checked to avoid entering an overbought market: K must be below 0.8 for Long." & vbLf & vbLf &
                "Exits are ATR-based: Stop Loss at the closer of 1.5×ATR14 or the Ichimoku cloud edge; " &
                "Take Profit at 2:1 reward-to-risk based on the actual SL distance." & vbLf & vbLf &
                "Recommended for: 15-min bars on commodity contracts (Gold, Oil). Signals are rare but high-quality."

            HasStrategyDescription = True
        End Sub

        ''' <summary>
        ''' One-click activate for BB Squeeze Scalper on the strategy card panel.
        ''' Dual-mode Bollinger Band scalper on 1-minute bars with 15-second polling.
        ''' Sets SelectedStrategyName (which triggers parameter auto-fill and bar download),
        ''' then populates the plain-English description panel.
        ''' </summary>
        Private Sub ApplyBbSqueezeScalper()
            SelectedStrategyName = "BB Squeeze Scalper"

            ActiveStrategyText = "✔  BB Squeeze Scalper  (1-min bars · BB12 · %B · RSI7 · EMA5 · ATR10)"

            StrategyNakedDescription =
                "A dual-mode Bollinger Band scalping strategy designed for high-frequency, high-leverage micro-trades on 1-minute bars." & vbLf & vbLf &
                "Every 15 seconds the strategy examines the latest completed 1-minute bar and calculates Bollinger Band Width " &
                "(BBW = (Upper − Lower) / Middle × 100). If BBW has been below its 20-bar SMA for 3 or more consecutive bars, " &
                "a SQUEEZE is active. When the squeeze fires and price closes outside the outer band — with the 5-period EMA slope " &
                "confirming direction and RSI(7) above 50 for Long (below 50 for Short) — a BREAKOUT trade is placed in the " &
                "breakout direction. This is Mode A: momentum scalp riding the volatility expansion." & vbLf & vbLf &
                "When no squeeze is present (bands are wide), the strategy switches to Mode B: BAND BOUNCE. " &
                "It calculates %B (position within the bands: 0 = lower, 1 = upper). " &
                "If %B drops below 0 (price below the lower band), RSI(7) is below 25, and the bar has a " &
                "rejection wick covering 60%+ of the range, a Long fires. The Short mirror applies above the upper band. " &
                "This is mean-reversion — fading the extreme back toward the middle band." & vbLf & vbLf &
                "Both modes share fixed exits: 0.4% TP and 0.2% SL (2:1 R:R). 5× leverage amplifies the scalp targets." & vbLf & vbLf &
                "Defaults: 1-min bars · BB(12, 2.0) · 0.4% TP · 0.2% SL · 5× leverage."

            HasStrategyDescription = True
        End Sub

        Private Sub ApplyVidya()
            SelectedStrategyName = "VIDYA Cross"

            ActiveStrategyText = "✔  VIDYA Cross  (5-min bars · VIDYA(14) · CMO(9) · ΔVol ±20% gate)"

            ' Default Min Confidence to 20% — matches the ±20% delta threshold in the engine.
            ' Signals range from 20 (minimum delta) to ~80 (very strong volume conviction).
            MinConfidence = "20"

            StrategyNakedDescription =
                "VIDYA (Variable Index Dynamic Average) is an adaptive moving average that adjusts its smoothing speed " &
                "based on trend strength. It uses the Chande Momentum Oscillator (CMO) as a dynamic alpha: in a strong " &
                "trend VIDYA tracks price tightly; in a choppy market it almost flatlines, suppressing false signals." & vbLf & vbLf &
                "ENTRY RULE — two conditions must both be true:" & vbLf &
                "  1. Price crosses the VIDYA line (Long: close crosses above; Short: close crosses below)." & vbLf &
                "  2. The 6-bar volume delta meets the conviction threshold:" & vbLf &
                "       Long  → bull volume exceeds bear volume by ≥ 20%  (ΔVol ≥ +0.20)" & vbLf &
                "       Short → bear volume exceeds bull volume by ≥ 20%  (ΔVol ≤ −0.20)" & vbLf & vbLf &
                "CONFIDENCE = |ΔVol| × 100. A 20% volume imbalance scores 20; a 60% imbalance scores 60. " &
                "This directly measures how much buying (or selling) pressure is behind the crossover — not just " &
                "whether the trend is moving, but whether real money is flowing in the right direction." & vbLf & vbLf &
                "WHY ±20%? Crossovers on thin, indecisive volume are the primary source of whipsaws. " &
                "Requiring at least a 20% volume imbalance filters out low-conviction noise while still " &
                "capturing the majority of meaningful trend-initiation moves." & vbLf & vbLf &
                "Defaults: VIDYA(14) · CMO(9) · 5-min bars · Min Confidence 20%."

            HasStrategyDescription = True
        End Sub

        Private Sub ApplyLultDivergence()
            SelectedStrategyName = "LULT Divergence"
            ActiveStrategyText = "✔  LULT Divergence  (5-min bars · WaveTrend · Market Cipher B · 6-step gate)"
            MinConfidence = "90"

            StrategyNakedDescription =
                "LULT Divergence is a 6-step Market Cipher B momentum-price divergence strategy that detects high-probability " &
                "reversal points using WaveTrend oscillator waves." & vbLf & vbLf &
                "STEP 1 — ANCHOR WAVE: WaveTrend WT1 must breach an extreme level (≥+60 for bear, ≤−60 for bull). " &
                "This marks the 'anchor' swing — a significant over-extension." & vbLf &
                "STEP 2 — TRIGGER WAVE: A subsequent WT1 extreme forms that is SHALLOWER than the anchor " &
                "(classic momentum divergence — price makes a new extreme but momentum weakens). " &
                "If the trigger overshoots the anchor, the setup resets." & vbLf &
                "STEP 3 — PRICE DIVERGENCE: Price at the trigger swing must diverge from the anchor swing price " &
                "(higher low for bull, lower high for bear) — confirming the momentum-price split." & vbLf &
                "STEP 4 — WT1 × WT2 CROSS: The WaveTrend fast line crosses the signal line in the reversal direction, " &
                "confirming momentum has turned (Green Dot for long, Red Dot for short)." & vbLf &
                "STEP 5 — ENGULFING VOLUME CANDLE: The entry bar must show decisive volume — a bullish/bearish " &
                "engulfing candle with above-average volume confirms real money flow behind the reversal." & vbLf & vbLf &
                "EXIT RULE: SL = trigger wave extreme ± ATR-scaled tick buffer. TP = 2R from entry." & vbLf &
                "TIME FILTER: 11:00–17:00 UTC (London + NY pre-market). Optimised for NQ (NSDQ100)." & vbLf & vbLf &
                "Defaults: 5-min bars · WaveTrend(10,21) · confidence ≥ 65%."

            HasStrategyDescription = True
        End Sub

        Private Sub ApplyNakedTrader()
            SelectedStrategyName = "Naked Trader"
            ActiveStrategyText = "✔  Naked Trader  (5-min bars · EMA(9/21) · MACD(8,17,9) · DMI/ADX(14) · VWAP)"
            MinConfidence = "90"
            SelectedInterval = "5 min"

            StrategyNakedDescription =
                "Naked Trader runs four independent directional gauges on every 5-minute bar and tallies their votes before deciding whether to enter." & vbLf & vbLf &
                "THE FOUR VOTES:" & vbLf &
                "1. EMA — EMA(9) > EMA(21) is bullish; below is bearish." & vbLf &
                "2. MACD — histogram > 0 is bullish; a positive MACD line is used as fallback." & vbLf &
                "3. DMI/ADX — +DI > -DI is bullish; -DI > +DI is bearish." & vbLf &
                "4. VWAP — close > VWAP is bullish; below is bearish (skipped if volume unavailable)." & vbLf & vbLf &
                "CONFIDENCE:" & vbLf &
                "Low (ADX < 20, or tie vote) → no signal, stay flat." & vbLf &
                "Medium (3+ votes agree, ADX 20–24) → enters at 60% confidence." & vbLf &
                "High (all votes agree, ADX ≥ 25 + volume OK) → enters at 90% confidence." & vbLf & vbLf &
                "Minimum bars required: 28. Recommended: 40 for a stable ADX reading."

            HasStrategyDescription = True
        End Sub

        Private Sub ApplyDoubleBubbleButt()
            SelectedStrategyName = "Double Bubble Butt"
            ActiveStrategyText = "✔  Double Bubble Butt  (5-min bars · BB(20,1.0 SD) inner · BB(20,2.0 SD) outer)"
            SelectedInterval = "5 min"

            StrategyNakedDescription =
                "Double Bubble Butt (Double Bollinger Band) plots two concurrent BB sets over the same SMA(20) — " &
                "an inner set at ±1.0 SD and an outer set at ±2.0 SD — creating three distinct trading zones." & vbLf & vbLf &
                "THE THREE ZONES:" & vbLf &
                "Buy Zone  — between upper 1.0 SD and upper 2.0 SD: strong uptrend / high momentum." & vbLf &
                "Sell Zone — between lower 1.0 SD and lower 2.0 SD: strong downtrend / high momentum." & vbLf &
                "Neutral Zone — between upper 1.0 SD and lower 1.0 SD: ranging / no clear direction." & vbLf & vbLf &
                "ENTRY RULES:" & vbLf &
                "Long  — close above the upper 1.0 SD band (enters Buy Zone)." & vbLf &
                "Short — close below the lower 1.0 SD band (enters Sell Zone)." & vbLf & vbLf &
                "EXIT RULES:" & vbLf &
                "Exit Long  — close back below the upper 1.0 SD band (returns to Neutral Zone)." & vbLf &
                "Exit Short — close back above the lower 1.0 SD band (returns to Neutral Zone)." & vbLf &
                "Hard SL = outer 2.0 SD band level at entry. TP = 2× ATR(20) from entry." & vbLf & vbLf &
                "Developed by Kathy Lien. Works across Forex, equities and crypto. Recommended: 5-min or 15-min bars."

            HasStrategyDescription = True
        End Sub

        ''' <summary>
        ''' contract and date range.  Calls BarCollectionService.EnsureBarsAsync() which:
        '''   - Returns immediately if ≥ 50 bars already exist (cache hit)
        '''   - Otherwise pages the ProjectX API in 500-bar batches and stores to SQLite
        '''   - Reports progress after each batch via BarsStatusText
        ''' Enables "Run Backtest" on success; disables with an error message on failure.
        ''' </summary>
        Private Sub DownloadBarsAsync()
            BarsAvailable = False
            IsBarsDownloading = True
            BarsStatusText = $"⏳ Checking {_selectedInterval} bars for {_contractIdText}..."
            BarsStatusColor = "AccentBrush"
            HasBarsStatus = True

            ' Capture mutable fields so the background closure uses their values at call time
            Dim contractId = _contractIdText
            Dim fromDate = _startDate
            Dim toDate = _endDate
            Dim timeframe = ParseIntervalToTimeframe(_selectedInterval)

            Task.Run(Async Function()
                         Try
                             ' Progress reporter — marshals each status string to the UI thread
                             Dim prog = New Progress(Of String)(
                                 Sub(msg) Dispatch(Sub()
                                                       BarsStatusText = msg
                                                       BarsStatusColor = If(msg.StartsWith("✓"), "BuyBrush",
                                                                         If(msg.StartsWith("✗"), "SellBrush",
                                                                            "AccentBrush"))
                                                   End Sub))

                             Dim cts = New CancellationTokenSource(TimeSpan.FromMinutes(5))
                             Dim result = Await _barCollectionService.EnsureBarsAsync(
                                              contractId, fromDate, toDate, timeframe, prog, cts.Token)

                             Dispatch(Sub()
                                          IsBarsDownloading = False
                                          BarsAvailable = result.Success
                                          BarsStatusText = result.Message
                                          BarsStatusColor = If(result.Success, "BuyBrush", "SellBrush")
                                      End Sub)

                         Catch ex As OperationCanceledException
                             Dispatch(Sub()
                                          IsBarsDownloading = False
                                          BarsAvailable = False
                                          BarsStatusText = "✗ Bar download timed out (> 5 min)"
                                          BarsStatusColor = "SellBrush"
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub()
                                          IsBarsDownloading = False
                                          BarsAvailable = False
                                          BarsStatusText = $"✗ Bar download failed: {ex.Message}"
                                          BarsStatusColor = "SellBrush"
                                      End Sub)
                         End Try
                     End Function)
        End Sub

        ''' <summary>Reset all results to empty/default state (called on contract change).</summary>
        Private Sub ClearResults()
            TotalTrades = 0
            WinRate = "—"
            TotalPnL = 0
            MaxDrawdown = 0
            Sharpe = "—"
            AvgPnL = 0
            Trades.Clear()
            TimeframeResults.Clear()
            ProgressText = "Ready"
            Progress = 0
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' TRAIN MODEL — Step 4 (optional, ML-based strategies only)
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Pre-calculates EMA/RSI indicator values on the downloaded bars.
        ''' Rule-based strategies (RSI Reversal, Double Bottom, etc.) do not need training.
        ''' The "Train Model" button is always available for the user to force a retrain.
        '''
        ''' UAT-BUG-004: Pass the user-selected ContractId so ModelTrainingService fetches bars
        ''' for the same contract the user downloaded, not TradingSettings.ActiveContractIds
        ''' (which is the live-trading config and may be empty or contain different contracts).
        ''' </summary>
        Private Sub ExecuteTrainModel()
            DownloadBarsAsync()
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' RUN BACKTEST — Steps 5, 6, 7
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteRun()
            Dim contractId = _contractIdText.Trim()
            If String.IsNullOrEmpty(contractId) Then
                ProgressText = "Select a contract first" : Return
            End If

            If String.IsNullOrEmpty(_selectedStrategyName) Then
                ProgressText = "Select a strategy first" : Return
            End If

            Dim capital As Decimal
            If Not Decimal.TryParse(_initialCapital, capital) OrElse capital <= 0 Then
                ProgressText = "Invalid initial capital" : Return
            End If

            Dim qty As Integer
            Integer.TryParse(_quantity, qty)

            Dim conf As Single
            Single.TryParse(_minConfidence, conf)
            If conf <= 0 OrElse conf > 100 Then
                ProgressText = $"Min confidence must be between 1 and 100 (got {_minConfidence})" : Return
            End If
            conf = conf / 100.0F   ' UI stores as whole % (e.g. 90); engine expects 0.0–1.0

            Dim minAdx As Single
            Single.TryParse(_minAdxThreshold, minAdx)

            Dim strategyCondition As StrategyConditionType
            Select Case _selectedStrategyName
                Case "Multi-Confluence Engine" : strategyCondition = StrategyConditionType.MultiConfluence
                Case "LULT Divergence"         : strategyCondition = StrategyConditionType.LultDivergence
                Case "BB Squeeze Scalper"      : strategyCondition = StrategyConditionType.BbSqueezeScalper
                Case "VIDYA Cross"             : strategyCondition = StrategyConditionType.VidyaCross
                Case "Naked Trader"            : strategyCondition = StrategyConditionType.NakedTrader
                Case "Double Bubble Butt"      : strategyCondition = StrategyConditionType.DoubleBubbleButt
                Case Else                      : strategyCondition = StrategyConditionType.EmaRsiWeightedScore
            End Select

            ' ATR mode fields
            Dim slAtrMult As Decimal = 1.5D
            Decimal.TryParse(_slAtrMultiple, slAtrMult)
            Dim tpAtrMult As Decimal = 3.0D
            Decimal.TryParse(_tpAtrMultiple, tpAtrMult)

            ' Capture mutable locals for the async closure
            Dim capContractId = contractId
            Dim capCapital = capital
            Dim capConf = If(conf > 0, conf, 0.65F)
            Dim capQty = If(qty > 0, qty, 1)
            Dim capMinAdx = Math.Max(0.0F, minAdx)
            Dim capCondition = strategyCondition
            Dim capTrailingStop = TrailingStopEnabled
            Dim capBreakEven    = BreakEvenEnabled
            Dim capExtendTp     = ExtendTpEnabled
            Dim capSlAtrMult    = If(slAtrMult > 0, slAtrMult, 1.5D)
            Dim capTpAtrMult    = If(tpAtrMult > 0, tpAtrMult, 3.0D)
            Dim capForceClose   = ForceCloseEnabled
            Dim capForceAmt     As Decimal = 50D
            Decimal.TryParse(_forceCloseAmount, capForceAmt)
            If capForceAmt <= 0 Then capForceAmt = 50D
            Dim capStart = _startDate
            Dim capEnd = _endDate
            Dim capStrategy = _selectedStrategyName
            Dim capTick = GetTickSize(contractId)
            Dim capPoint = GetPointValue(contractId)

            ' 8 timeframes: 5 natively supported by Yahoo + 3 that use closest native interval
            ' TenMinute → 5m source bars stored under its own key
            ' TwoHour/FourHour → 1hr source bars stored under their own key
            Dim timeframes = New (BarTimeframe, String)() {
                (BarTimeframe.OneMinute,    "1 min"),
                (BarTimeframe.FiveMinute,   "5 min"),
                (BarTimeframe.TenMinute,    "10 min"),
                (BarTimeframe.FifteenMinute,"15 min"),
                (BarTimeframe.ThirtyMinute, "30 min"),
                (BarTimeframe.OneHour,      "1 hr"),
                (BarTimeframe.TwoHour,      "2 hr"),
                (BarTimeframe.FourHour,     "4 hr")
            }

            _cancelSource = New CancellationTokenSource()
            IsRunning = True
            Progress = 0
            ClearResults()
            ProgressText = $"Running multi-timeframe analysis — {capStrategy} on {capContractId}..."

            Dim cts = _cancelSource
            Task.Run(Async Function()
                         Dim total = timeframes.Length
                         For i = 0 To total - 1
                             Dim tf = timeframes(i).Item1
                             Dim label = timeframes(i).Item2
                             Dim idx = i  ' capture for closure

                             If cts.IsCancellationRequested Then Exit For

                             Dispatch(Sub()
                                          ProgressText = $"[{idx + 1}/{total}] {label} — downloading bars..."
                                          Progress = CInt((idx * 100.0) / total)
                                      End Sub)

                             Try
                                 ' Clamp start date to Yahoo Finance's per-timeframe lookback limit
                                 Dim maxDays = GetMaxLookbackDays(tf)
                                 Dim earliestAllowed = DateTime.Today.AddDays(-maxDays)
                                 Dim effectiveStart = If(capStart < earliestAllowed, earliestAllowed, capStart)

                                 Dim barResult = Await _barCollectionService.EnsureBarsAsync(
                                     capContractId, effectiveStart, capEnd, tf,
                                     cancel:=cts.Token)

                                 If Not barResult.Success Then
                                     Dispatch(Sub() TimeframeResults.Add(
                                                  New TimeframeResultRowVm(label, "No bars")))
                                     Continue For
                                 End If

                                 Dispatch(Sub()
                                              ProgressText = $"[{idx + 1}/{total}] {label} — running backtest ({effectiveStart:MMM d} – {capEnd:MMM d})..."
                                          End Sub)

                                 Dim config As New BacktestConfiguration With {
                                     .RunName = $"{capStrategy} · {label} · {DateTime.Now:yyyyMMdd-HHmm}",
                                     .ContractId = capContractId,
                                     .Timeframe = CInt(tf),
                                     .StartDate = effectiveStart,
                                     .EndDate = capEnd,
                                     .InitialCapital = capCapital,
                                     .MinSignalConfidence = capConf,
                                     .Quantity = capQty,
                                     .TickSize = capTick,
                                     .PointValue = capPoint,
                                     .MinAdxThreshold = capMinAdx,
                                     .MaxScaleIns = _configMaxScaleIns,
                                     .StrategyCondition = capCondition,
                                     .TrailingStopEnabled = capTrailingStop,
                                     .BreakEvenOnHalfTpEnabled = capBreakEven,
                                     .ExtendTpEnabled = capExtendTp,
                                     .UseAtrMode = True,
                                     .SlAtrMultiple = capSlAtrMult,
                                     .TpAtrMultiple = capTpAtrMult,
                                     .ForceCloseEnabled = capForceClose,
                                     .ForceCloseAmount = capForceAmt,
                                     .SlippageTicks = 1,
                                     .CommissionPerSideUsd = GetCommissionPerSide(capContractId)
                                 }

                                 Dim result = Await _backtestService.RunBacktestAsync(config, cts.Token)
                                 Dispatch(Sub() TimeframeResults.Add(New TimeframeResultRowVm(label, result)))

                             Catch ex As OperationCanceledException
                                 Dispatch(Sub() TimeframeResults.Add(New TimeframeResultRowVm(label, "Cancelled")))
                                 Exit For
                             Catch ex As Exception
                                 ' Diagnostic dump — write full stack trace to a file so we can pinpoint the
                                 ' failing line (the UI cell can only fit ~40 chars).
                                 Try
                                     Dim logDir = Path.Combine(
                                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "TopStepTrader", "Logs")
                                     Directory.CreateDirectory(logDir)
                                     Dim logPath = Path.Combine(logDir, "backtest-errors.log")
                                     Dim sb As New StringBuilder()
                                     sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  Timeframe={label} ====")
                                     sb.AppendLine($"Contract={capContractId}  Strategy={capStrategy}  Persona={_selectedPersona}")
                                     sb.AppendLine($"Range={capStart:yyyy-MM-dd}..{capEnd:yyyy-MM-dd}  Capital={capCapital}  Qty={capQty}  PointValue={capPoint}")
                                     Dim cur As Exception = ex
                                     Dim depth As Integer = 0
                                     While cur IsNot Nothing
                                         sb.AppendLine($"--- Exception[{depth}]: {cur.GetType().FullName} ---")
                                         sb.AppendLine(cur.Message)
                                         sb.AppendLine(cur.StackTrace)
                                         cur = cur.InnerException
                                         depth += 1
                                     End While
                                     sb.AppendLine()
                                     File.AppendAllText(logPath, sb.ToString())
                                 Catch
                                     ' Never let logging break the loop.
                                 End Try

                                 ' Show exception type + message in the UI cell so the user can see at a glance
                                 ' which exception is fired (still truncated for layout).
                                 Dim full = $"{ex.GetType().Name}: {ex.Message}"
                                 If full.Length > 40 Then full = full.Substring(0, 40) & "..."
                                 Dispatch(Sub() TimeframeResults.Add(New TimeframeResultRowVm(label, full)))
                             End Try

                             Dispatch(Sub() Progress = CInt(((idx + 1) * 100.0) / total))
                         Next

                         Dispatch(Sub()
                                      IsRunning = False
                                      Progress = 100
                                      Dim done = TimeframeResults.Count
                                      Dim ok = TimeframeResults.Where(Function(r) r.IsSuccess).Count()
                                      ProgressText = $"Complete — {ok}/{done} timeframes succeeded"
                                      cts?.Dispose()
                                  End Sub)
                     End Function)
        End Sub

        ''' <summary>
        ''' Returns the maximum historical lookback in days for bar downloads from TopStepX.
        ''' ProjectX supports up to 60 days of intraday history across all timeframes.
        ''' </summary>
        Private Shared Function GetMaxLookbackDays(tf As BarTimeframe) As Integer
            Return 60
        End Function

        ''' <summary>
        ''' Converts the display-format interval string (e.g. "5 min", "1 hour") to the
        ''' <see cref="BarTimeframe"/> enum value used by BarCollectionService and BacktestEngine.
        ''' Falls back to FiveMinute for any unrecognised string.
        ''' </summary>
        Private Shared Function ParseIntervalToTimeframe(interval As String) As BarTimeframe
            Select Case interval
                Case "1 min" : Return BarTimeframe.OneMinute
                Case "5 min" : Return BarTimeframe.FiveMinute
                Case "15 min" : Return BarTimeframe.FifteenMinute
                Case "30 min" : Return BarTimeframe.ThirtyMinute
                Case "1 hour" : Return BarTimeframe.OneHour
                Case Else : Return BarTimeframe.FiveMinute
            End Select
        End Function

        ''' <summary>
        ''' Returns the price-units-per-tick for the given contract.
        ''' Used to convert tick counts (SL/TP) into exact price levels in BacktestMetrics.
        ''' MES/MNQ: 0.25 (quarter-point ticks)
        ''' MGC (Micro Gold): 0.10 (dime ticks)
        ''' MCL (Micro Crude): 0.01 (cent ticks)
        ''' </summary>
        ''' <summary>
        ''' Returns the tick size for the given contract using the active broker.
        ''' TopStepX → PxTickSize; eToro → EToroTickSize.
        ''' Falls back to 0.01 if the contract is not in the master list.
        ''' </summary>
        Private Function GetTickSize(contractId As String) As Decimal
            Dim fc = FavouriteContracts.TryGetBySymbol(contractId)
            If fc IsNot Nothing Then
                Dim ts = fc.GetTickSize(_session.ActiveBroker)
                If ts > 0D Then Return ts
            End If
            Return 0.01D   ' fallback
        End Function

        ''' <summary>
        ''' Returns the dollar-per-point value for the given contract using the active broker.
        ''' TopStepX: MGC=$10, MES=$5, MNQ=$2, MCL=$100 (PxPointValue).
        ''' eToro CFDs: $1 per point for all instruments (EToroPointValue).
        ''' Falls back to 1.0 if the contract is not in the master list.
        ''' </summary>
        Private Function GetPointValue(contractId As String) As Decimal
            Dim fc = FavouriteContracts.TryGetBySymbol(contractId)
            If fc IsNot Nothing Then
                Dim pv = fc.GetPointValue(_session.ActiveBroker)
                If pv > 0D Then Return pv
            End If
            Return 1.0D   ' fallback
        End Function

        ''' <summary>
        ''' Returns the commission per side (in USD) for the given contract on TopStepX.
        ''' Falls back to $4.50 — the standard CME Globex micro futures rate — when not found.
        ''' </summary>
        Private Function GetCommissionPerSide(contractId As String) As Decimal
            Dim fc = FavouriteContracts.TryGetBySymbol(contractId)
            If fc IsNot Nothing Then Return fc.PxCommissionPerSide
            Return 4.5D
        End Function

        Private Sub ExecuteCancel(param As Object)
            _cancelSource?.Cancel()
        End Sub

        Private Sub ShowResult(result As BacktestResult)
            TotalTrades = result.TotalTrades
            WinRate = result.WinRate.ToString("P1")
            TotalPnL = result.TotalPnL
            MaxDrawdown = result.MaxDrawdown
            AvgPnL = result.AveragePnLPerTrade
            Sharpe = If(result.SharpeRatio.HasValue, result.SharpeRatio.Value.ToString("F2"), "—")
            ProgressText = $"Complete — {result.TotalTrades} trades · {result.WinRate:P0} win rate · {result.TotalPnL:C0} P&L"
            Progress = 100

            Trades.Clear()
            For Each t In result.Trades
                Trades.Add(New BacktestTradeRowVm(t))
            Next
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' EXPORT CSV — Phase 4
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Exports backtest results to a CSV file chosen via SaveFileDialog.
        ''' Columns match the trade DataGrid: Entry Time, Exit Time, Side, Entry Price,
        ''' Exit Price, P&amp;L, Exit Reason, Confidence.
        ''' All values are double-quoted to handle commas in currency-formatted P&amp;L.
        ''' </summary>
        Private Sub ExecuteExportCsv()
            If Trades.Count = 0 Then
                ProgressText = "No results to export — run a backtest first"
                Return
            End If

            Dim dlg As New SaveFileDialog() With {
                .Title = "Export Backtest Results",
                .Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                .FileName = $"Backtest_{If(_selectedStrategyName, "Results")}_{DateTime.Now:yyyyMMdd-HHmm}.csv"
            }

            If dlg.ShowDialog() <> True Then Return

            Try
                Dim sb As New StringBuilder()
                ' Note: Backtest scale-in confirmation is bar-based, not 30-second tick-based.
                ' Each row is one entry/scale-in leg. Rows sharing a Group ID are the same position.
                ' Metrics (win rate, avg P&L) are position-level, not per-row.
                sb.AppendLine("Group,Entry Time,Exit Time,Side,Entry Price,Exit Price,P&L,Exit Reason,Confidence")
                For Each t In Trades
                    sb.AppendLine($"""{t.PositionGroupId}"",""{t.EntryTime}"",""{t.ExitTime}"",""{t.Side}""," &
                                  $"""{t.EntryPrice}"",""{t.ExitPrice}"",""{t.PnL}"",""{t.ExitReason}"",""{t.Confidence}""")
                Next
                File.WriteAllText(dlg.FileName, sb.ToString())
                ProgressText = $"✓ Exported {Trades.Count} entries → {Path.GetFileName(dlg.FileName)}"
            Catch ex As Exception
                ProgressText = $"✗ Export failed: {ex.Message}"
            End Try
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' MAXIMUM EFFORT — Tab 3
        ' Runs every contract × every strategy × every timeframe and sorts by P&L.
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property MaxEffortResults As New ObservableCollection(Of MaxEffortRowVm)()

        ''' <summary>
        ''' Permanently pinned best-known combinations — never cleared by a new run.
        ''' Populated once in the constructor from hard-coded data recorded 2025-07.
        ''' </summary>
        Public ReadOnly Property PinnedResults As New ObservableCollection(Of MaxEffortRowVm)()

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

        Private _maxEffortIsRunning As Boolean
        Public Property MaxEffortIsRunning As Boolean
            Get
                Return _maxEffortIsRunning
            End Get
            Set(value As Boolean)
                SetProperty(_maxEffortIsRunning, value)
                OnPropertyChanged(NameOf(MaxEffortCanRun))
                OnPropertyChanged(NameOf(MaxEffortCanCancel))
            End Set
        End Property

        Public ReadOnly Property MaxEffortCanRun As Boolean
            Get
                Return Not _maxEffortIsRunning
            End Get
        End Property

        Public ReadOnly Property MaxEffortCanCancel As Boolean
            Get
                Return _maxEffortIsRunning
            End Get
        End Property

        Private _maxEffortProgressText As String = "Click Maximum Effort! to run all 840 combinations (3 personas × 5 instruments × 7 strategies × 8 timeframes)."
        Public Property MaxEffortProgressText As String
            Get
                Return _maxEffortProgressText
            End Get
            Set(value As String)
                SetProperty(_maxEffortProgressText, value)
            End Set
        End Property

        Private _maxEffortProgress As Integer
        Public Property MaxEffortProgress As Integer
            Get
                Return _maxEffortProgress
            End Get
            Set(value As Integer)
                SetProperty(_maxEffortProgress, value)
            End Set
        End Property

        Private _maxEffortAiAnalysis As String = ""
        Public Property MaxEffortAiAnalysis As String
            Get
                Return _maxEffortAiAnalysis
            End Get
            Set(value As String)
                SetProperty(_maxEffortAiAnalysis, value)
            End Set
        End Property

        Private _maxEffortAiIsLoading As Boolean
        Public Property MaxEffortAiIsLoading As Boolean
            Get
                Return _maxEffortAiIsLoading
            End Get
            Set(value As Boolean)
                SetProperty(_maxEffortAiIsLoading, value)
            End Set
        End Property

        Private Sub ExecuteMaximumEffort()
            MaxEffortResults.Clear()
            MaxEffortAiAnalysis = ""
            MaxEffortIsRunning = True
            MaxEffortProgress = 0
            MaxEffortProgressText = "Starting Maximum Effort run..."

            Dim contracts = FavouriteContracts.GetDefaults()

            Dim strategies = New (String, StrategyConditionType)() {
                ("EMA/RSI Combined",       StrategyConditionType.EmaRsiWeightedScore),
                ("Multi-Confluence",       StrategyConditionType.MultiConfluence),
                ("BB Squeeze",             StrategyConditionType.BbSqueezeScalper),
                ("VIDYA Cross",            StrategyConditionType.VidyaCross),
                ("Naked Trader",           StrategyConditionType.NakedTrader),
                ("LULT Divergence",        StrategyConditionType.LultDivergence),
                ("Double Bubble Butt",     StrategyConditionType.DoubleBubbleButt)
            }

            Dim timeframes = New (BarTimeframe, String)() {
                (BarTimeframe.OneMinute,    "1 min"),
                (BarTimeframe.FiveMinute,   "5 min"),
                (BarTimeframe.TenMinute,    "10 min"),
                (BarTimeframe.FifteenMinute,"15 min"),
                (BarTimeframe.ThirtyMinute, "30 min"),
                (BarTimeframe.OneHour,      "1 hr"),
                (BarTimeframe.TwoHour,      "2 hr"),
                (BarTimeframe.FourHour,     "4 hr")
            }

            ' All three personas — each combination is run once per persona
            ' Uses IPersonaService so saved overrides from the Persona config page are respected.
            Dim personas = _personaService.GetAllProfiles()

            Dim totalRuns = contracts.Count * strategies.Length * timeframes.Length * personas.Count
            Dim capStart = DateTime.Today.AddDays(-180)
            Dim capEnd = DateTime.Today

            _maxEffortCancelSource = New CancellationTokenSource()
            Dim cts = _maxEffortCancelSource

            Task.Run(Async Function()
                Dim runIndex = 0
                Dim rawResults As New List(Of MaxEffortRowVm)()

                For Each persona In personas
                    Dim personaShort = persona.Name.Split(" "c)(0)  ' "Lewis", "Damian", or "Joe"
                    For Each contract In contracts
                        For Each strat In strategies
                            For Each tf In timeframes
                                If cts.IsCancellationRequested Then Exit For

                                runIndex += 1
                                Dim contractId = contract.ContractId
                                Dim contractName = contract.Name
                                Dim stratName = strat.Item1
                                Dim stratCondition = strat.Item2
                                Dim tfEnum = tf.Item1
                                Dim tfLabel = tf.Item2
                                Dim ri = runIndex
                                Dim pShort = personaShort

                                Dispatch(Sub()
                                    MaxEffortProgressText =
                                        $"[{ri}/{totalRuns}]  {pShort}  ·  {contractName}  ·  {stratName}  ·  {tfLabel}"
                                    MaxEffortProgress = CInt((ri * 100.0) / totalRuns)
                                End Sub)

                                Try
                                    Dim maxDays = GetMaxLookbackDays(tfEnum)
                                    Dim earliestAllowed = DateTime.Today.AddDays(-maxDays)
                                    Dim effectiveStart = If(capStart < earliestAllowed, earliestAllowed, capStart)

                                    Dim barResult = Await _barCollectionService.EnsureBarsAsync(
                                        contractId, effectiveStart, capEnd, tfEnum, cancel:=cts.Token)

                                    If Not barResult.Success Then Continue For

                                    ' Broker-aware specs
                                    Dim tickSize = contract.GetTickSize(_session.ActiveBroker)
                                    Dim pointValue = contract.GetPointValue(_session.ActiveBroker)
                                    If tickSize <= 0D Then tickSize = 0.01D
                                    If pointValue <= 0D Then pointValue = 1.0D

                                    Dim config As New BacktestConfiguration With {
                                        .RunName = $"ME · {pShort} · {contractName} · {stratName} · {tfLabel}",
                                        .ContractId = contractId,
                                        .Timeframe = CInt(tfEnum),
                                        .StartDate = effectiveStart,
                                        .EndDate = capEnd,
                                        .InitialCapital = persona.TradeAmount,
                                        .Quantity = 1,
                                        .TickSize = tickSize,
                                        .PointValue = pointValue,
                                        .MinSignalConfidence = CSng(persona.DefaultConfidencePct) / 100.0F,
                                        .MinAdxThreshold = persona.AdxThreshold,
                                        .MaxScaleIns = persona.MaxScaleIns,
                                        .StrategyCondition = stratCondition,
                                        .UseAtrMode = True,
                                        .SlAtrMultiple = persona.SlMultipleOfN,
                                        .TpAtrMultiple = persona.TpMultipleOfN,
                                        .ForceCloseEnabled = _forceCloseEnabled,
                                        .ForceCloseAmount = If(Decimal.TryParse(_forceCloseAmount, Nothing), CDec(_forceCloseAmount), 50D),
                                        .SlippageTicks = 1,
                                        .CommissionPerSideUsd = contract.PxCommissionPerSide
                                    }

                                    Dim result = Await _backtestService.RunBacktestAsync(config, cts.Token)
                                    Dim row = New MaxEffortRowVm(pShort, contractName, stratName, tfLabel, result)

                                    rawResults.Add(row)
                                    Dispatch(Sub()
                                        ' Insert in sorted position by P&L descending for live view
                                        Dim insertAt = MaxEffortResults.Count
                                        For j = 0 To MaxEffortResults.Count - 1
                                            If MaxEffortResults(j).TotalPnLRaw < row.TotalPnLRaw Then
                                                insertAt = j
                                                Exit For
                                            End If
                                        Next
                                        MaxEffortResults.Insert(insertAt, row)
                                    End Sub)

                                Catch ex As OperationCanceledException
                                    Exit For
                                Catch
                                    ' Skip combinations that fail (insufficient bars, etc.)
                                End Try
                            Next
                            If cts.IsCancellationRequested Then Exit For
                        Next
                        If cts.IsCancellationRequested Then Exit For
                    Next
                    If cts.IsCancellationRequested Then Exit For
                Next

                ' Kick off Claude Haiku analysis on top 20 results
                Dim top20 = rawResults.OrderByDescending(Function(r) r.TotalPnLRaw).Take(20).ToList()
                If top20.Count > 0 AndAlso Not cts.IsCancellationRequested Then
                    Dispatch(Sub()
                        MaxEffortProgress = 100
                        MaxEffortProgressText = $"Complete — {rawResults.Count} combinations ran. Asking Claude Haiku for analysis..."
                        MaxEffortAiIsLoading = True
                    End Sub)

                    Dim sb As New System.Text.StringBuilder()
                    sb.AppendLine($"Backtest results — {rawResults.Count} combinations across {contracts.Count} instruments, 7 strategies, 8 timeframes, 3 personas (Lewis/Damian/Joe).")
                    sb.AppendLine($"Date range: 60 days (TopStepX API limit). Commission $4.50/side modelled.")
                    sb.AppendLine($"ATR-based stops: Lewis SL=1.5×/TP=3.0×N  Damian SL=1.0×/TP=2.0×N  Joe SL=0.75×/TP=2.0×N  (N = ATR14 × point value).")
                    sb.AppendLine()
                    sb.AppendLine("TOP 20 BY TOTAL P&L:")
                    sb.AppendLine("Rank | Persona | Contract | Strategy | Timeframe | Trades | Win% | P&L | Sharpe | Avg P&L")
                    sb.AppendLine("-----|---------|----------|----------|-----------|--------|------|-----|--------|--------")
                    For rank = 1 To top20.Count
                        Dim r = top20(rank - 1)
                        sb.AppendLine($"{rank,4} | {r.Persona,-7} | {r.Contract,-12} | {r.Strategy,-17} | {r.Timeframe,8} | {r.Trades,6} | {r.WinRate,5} | {r.TotalPnL,8} | {r.Sharpe,6} | {r.AvgPnL}")
                    Next

                    Dim analysis = Await _claudeReviewService.AnalyseBacktestResultsAsync(sb.ToString(), cts.Token)
                    Dispatch(Sub()
                        MaxEffortAiAnalysis = analysis
                        MaxEffortAiIsLoading = False
                    End Sub)
                Else
                    Dispatch(Sub()
                        MaxEffortProgress = 100
                        MaxEffortProgressText = If(cts.IsCancellationRequested,
                            $"Cancelled — {rawResults.Count} combinations ran.",
                            $"Complete — {rawResults.Count} combinations ran.")
                    End Sub)
                End If

                Dispatch(Sub()
                    MaxEffortIsRunning = False
                    cts?.Dispose()
                End Sub)
            End Function)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' PREVIOUS RUNS (Tab 2)
        ' ══════════════════════════════════════════════════════════════════════

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

        ' ══════════════════════════════════════════════════════════════════════
        ' INFRASTRUCTURE
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub OnProgress(sender As Object, e As BacktestProgressEventArgs)
            Dispatch(Sub()
                         Progress = e.PercentComplete
                         ProgressText = $"{e.PercentComplete}% — {e.CurrentDate:MM/dd/yyyy} — {e.TradesExecuted} trades"
                     End Sub)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' MAXIMUM EFFORT ROW
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class MaxEffortRowVm
        Public ReadOnly Property Persona As String
        Public ReadOnly Property Contract As String
        Public ReadOnly Property Strategy As String
        Public ReadOnly Property Timeframe As String
        Public ReadOnly Property Trades As String
        Public ReadOnly Property WinRate As String
        Public ReadOnly Property TotalPnL As String
        Public ReadOnly Property TotalPnLRaw As Decimal
        Public ReadOnly Property TotalPnLColor As String
        Public ReadOnly Property Sharpe As String
        Public ReadOnly Property AvgPnL As String
        Public ReadOnly Property MaxDD As String

        Public Sub New(personaName As String, contractName As String, strategyName As String,
                       timeframeLabel As String, result As BacktestResult)
            Persona = personaName
            Contract = contractName
            Strategy = strategyName
            Timeframe = timeframeLabel
            Trades = result.TotalTrades.ToString()
            WinRate = result.WinRate.ToString("P0")
            TotalPnLRaw = result.TotalPnL
            TotalPnL = result.TotalPnL.ToString("C0")
            TotalPnLColor = If(result.TotalPnL >= 0, "BuyBrush", "SellBrush")
            Sharpe = If(result.SharpeRatio.HasValue, result.SharpeRatio.Value.ToString("F2"), "—")
            AvgPnL = result.AveragePnLPerTrade.ToString("C0")
            MaxDD = result.MaxDrawdown.ToString("C0")
        End Sub

        ''' <summary>Pinned / pre-seeded row — accepts raw display values directly.</summary>
        Public Sub New(personaName As String, contractName As String, strategyName As String,
                       timeframeLabel As String, trades As Integer, winRatePct As Double,
                       totalPnLRaw As Decimal, sharpe As Double, avgPnL As Decimal, maxDD As Decimal)
            Persona = personaName
            Contract = contractName
            Strategy = strategyName
            Timeframe = timeframeLabel
            Me.Trades = trades.ToString()
            WinRate = (winRatePct / 100.0).ToString("P0")
            TotalPnLRaw = totalPnLRaw
            TotalPnL = totalPnLRaw.ToString("C0")
            TotalPnLColor = If(totalPnLRaw >= 0, "BuyBrush", "SellBrush")
            Me.Sharpe = sharpe.ToString("F2")
            AvgPnL = avgPnL.ToString("C0")
            MaxDD = maxDD.ToString("C0")
        End Sub
    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' MULTI-TIMEFRAME RESULTS ROW
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class TimeframeResultRowVm
        Public ReadOnly Property Timeframe As String
        Public ReadOnly Property Trades As String
        Public ReadOnly Property WinRate As String
        Public ReadOnly Property TotalPnL As String
        Public ReadOnly Property TotalPnLRaw As Decimal
        Public ReadOnly Property TotalPnLColor As String
        Public ReadOnly Property Sharpe As String
        Public ReadOnly Property AvgPnLPerTrade As String
        Public ReadOnly Property MaxDrawdown As String
        Public ReadOnly Property IsSuccess As Boolean

        ''' <summary>Success row — populated from a BacktestResult.</summary>
        Public Sub New(label As String, result As BacktestResult)
            Timeframe = label
            Trades = result.TotalTrades.ToString()
            WinRate = result.WinRate.ToString("P1")
            TotalPnLRaw = result.TotalPnL
            TotalPnL = result.TotalPnL.ToString("C0")
            TotalPnLColor = If(result.TotalPnL >= 0, "BuyBrush", "SellBrush")
            Sharpe = If(result.SharpeRatio.HasValue, result.SharpeRatio.Value.ToString("F2"), "—")
            AvgPnLPerTrade = result.AveragePnLPerTrade.ToString("C0")
            MaxDrawdown = result.MaxDrawdown.ToString("C0")
            IsSuccess = True
        End Sub

        ''' <summary>Error / skipped row.</summary>
        Public Sub New(label As String, errorMessage As String)
            Timeframe = label
            Trades = "—"
            WinRate = "—"
            TotalPnLRaw = 0
            TotalPnL = errorMessage
            TotalPnLColor = "TextSecondaryBrush"
            Sharpe = "—"
            AvgPnLPerTrade = "—"
            MaxDrawdown = "—"
            IsSuccess = False
        End Sub
    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' ROW VIEW-MODELS
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class BacktestTradeRowVm
        Public Property PositionGroupId As String
        Public Property EntryTime As String
        Public Property ExitTime As String
        Public Property Side As String
        Public Property EntryPrice As String
        Public Property ExitPrice As String
        Public Property PnL As String
        Public Property ExitReason As String
        Public Property Confidence As String

        Private ReadOnly _rawPnL As Decimal

        ''' <summary>
        ''' Derived from the raw decimal PnL — not from the formatted currency string,
        ''' which uses "($n)" notation in en-US (StartsWith("-") would misidentify losses as wins).
        ''' </summary>
        Public ReadOnly Property PnLColor As String
            Get
                Return If(_rawPnL >= 0D, "BuyBrush", "SellBrush")
            End Get
        End Property

        Public Sub New(t As BacktestTrade)
            _rawPnL = t.PnL.GetValueOrDefault()
            PositionGroupId = $"#{t.PositionGroupId}"
            EntryTime = t.EntryTime.LocalDateTime.ToString("MM/dd HH:mm")
            ExitTime = If(t.ExitTime.HasValue, t.ExitTime.Value.LocalDateTime.ToString("MM/dd HH:mm"), "—")
            Side = t.Side
            EntryPrice = t.EntryPrice.ToString("F2")
            ExitPrice = If(t.ExitPrice.HasValue, t.ExitPrice.Value.ToString("F2"), "—")
            PnL = If(t.PnL.HasValue, t.PnL.Value.ToString("C0"), "—")
            ExitReason = t.ExitReason
            Confidence = t.SignalConfidence.ToString("P0")
        End Sub
    End Class

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
