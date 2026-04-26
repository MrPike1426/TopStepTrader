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
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>Tracks which work phase BacktestRunViewModel is in — replaces the three-boolean pattern.</summary>
    Public Enum WorkPhase
        Idle
        DownloadingBars
        Training
        Running
    End Enum

    ''' <summary>
    ''' ViewModel for Tab 1 — Run Backtest.
    ''' Owns contract selection, strategy selection, bar download, persona application,
    ''' multi-timeframe run, results display, and CSV export.
    ''' Extracted from BacktestViewModel as part of ARCH-02a.
    ''' </summary>
    Public Class BacktestRunViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private ReadOnly _backtestService As IBacktestService
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService

        Private _cancelSource As CancellationTokenSource

        ' ── Elapsed-time timer ─────────────────────────────────────────────────
        Private _elapsedTimer As DispatcherTimer   ' Nothing when no WPF dispatcher (unit tests)
        Private _workStartTime As DateTimeOffset

        ' ══════════════════════════════════════════════════════════════════════
        ' CONFIGURATION PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _contractIdText As String = ""
        ''' <summary>Long-form contract ID — set by ContractSelectorControl.</summary>
        Public Property ContractIdText As String
            Get
                Return _contractIdText
            End Get
            Set(value As String)
                Dim old = _contractIdText
                SetProperty(_contractIdText, value)
                If old <> value Then
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

        Public ReadOnly Property SelectedContractDisplay As String
            Get
                If String.IsNullOrEmpty(_contractIdText) Then Return String.Empty
                Dim fav = FavouriteContracts.GetDefaults().FirstOrDefault(
                    Function(f) String.Equals(f.ContractId, _contractIdText, StringComparison.OrdinalIgnoreCase))
                Return If(fav IsNot Nothing, fav.Name, _contractIdText)
            End Get
        End Property

        Private _selectedStrategyName As String
        Public Property SelectedStrategyName As String
            Get
                Return _selectedStrategyName
            End Get
            Set(value As String)
                Dim old = _selectedStrategyName
                SetProperty(_selectedStrategyName, value)
                If old <> value AndAlso Not String.IsNullOrEmpty(value) Then
                    ApplyStrategyDefaults(value)
                    OnPropertyChanged(NameOf(CanTrain))
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
        Public Property MinAdxThreshold As String
            Get
                Return _minAdxThreshold
            End Get
            Set(value As String)
                SetProperty(_minAdxThreshold, value)
            End Set
        End Property

        ' ── Dynamic exit management ────────────────────────────────────────────

        Private _trailingStopEnabled As Boolean = False
        Public Property TrailingStopEnabled As Boolean
            Get
                Return _trailingStopEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_trailingStopEnabled, value)
            End Set
        End Property

        Private _breakEvenEnabled As Boolean = False
        Public Property BreakEvenEnabled As Boolean
            Get
                Return _breakEvenEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_breakEvenEnabled, value)
            End Set
        End Property

        Private _extendTpEnabled As Boolean = False
        Public Property ExtendTpEnabled As Boolean
            Get
                Return _extendTpEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_extendTpEnabled, value)
            End Set
        End Property

        ' ── Train/test split ───────────────────────────────────────────────────

        Private _validateSplitEnabled As Boolean = False
        Public Property ValidateSplitEnabled As Boolean
            Get
                Return _validateSplitEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_validateSplitEnabled, value)
            End Set
        End Property

        ' ── Force Close ────────────────────────────────────────────────────────

        Private _forceCloseEnabled As Boolean = False
        Public Property ForceCloseEnabled As Boolean
            Get
                Return _forceCloseEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_forceCloseEnabled, value)
            End Set
        End Property

        Private _forceCloseAmount As String = "50"
        Public Property ForceCloseAmount As String
            Get
                Return _forceCloseAmount
            End Get
            Set(value As String)
                SetProperty(_forceCloseAmount, value)
            End Set
        End Property

        ' ── ATR-based SL/TP mode ───────────────────────────────────────────────

        Private _slAtrMultiple As String = "1.5"
        Public Property SlAtrMultiple As String
            Get
                Return _slAtrMultiple
            End Get
            Set(value As String)
                SetProperty(_slAtrMultiple, value)
            End Set
        End Property

        Private _tpAtrMultiple As String = "3.0"
        Public Property TpAtrMultiple As String
            Get
                Return _tpAtrMultiple
            End Get
            Set(value As String)
                SetProperty(_tpAtrMultiple, value)
            End Set
        End Property

        ' ── Persona selection ──────────────────────────────────────────────────
        Private _selectedPersona As String = "Damian"
        Private _personaExplicitlyChosen As Boolean = False
        Private _configMaxScaleIns As Integer = 2

        Public ReadOnly Property IsLewisSelected As Boolean
            Get
                Return _selectedPersona = "Lewis"
            End Get
        End Property

        Public ReadOnly Property IsDamianSelected As Boolean
            Get
                Return _selectedPersona = "Damian"
            End Get
        End Property

        Public ReadOnly Property IsJoeSelected As Boolean
            Get
                Return _selectedPersona = "Joe"
            End Get
        End Property

        Private _selectedInterval As String = "5 min"
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

        Private _workPhase As WorkPhase = WorkPhase.Idle

        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _workPhase = WorkPhase.Running
            End Get
        End Property

        Public ReadOnly Property NotIsRunning As Boolean
            Get
                Return _workPhase <> WorkPhase.Running
            End Get
        End Property

        Public ReadOnly Property IsTraining As Boolean
            Get
                Return _workPhase = WorkPhase.Training
            End Get
        End Property

        Public ReadOnly Property IsBarsDownloading As Boolean
            Get
                Return _workPhase = WorkPhase.DownloadingBars
            End Get
        End Property

        Private _barsAvailable As Boolean
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
        Public Property BarsStatusText As String
            Get
                Return _barsStatusText
            End Get
            Set(value As String)
                SetProperty(_barsStatusText, value)
            End Set
        End Property

        Private _barsStatusColor As String = "AccentBrush"
        Public Property BarsStatusColor As String
            Get
                Return _barsStatusColor
            End Get
            Set(value As String)
                SetProperty(_barsStatusColor, value)
            End Set
        End Property

        Private _hasBarsStatus As Boolean
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

        Public ReadOnly Property IsWorking As Boolean
            Get
                Return _workPhase <> WorkPhase.Idle
            End Get
        End Property

        Private _elapsedTimeText As String = ""
        Public Property ElapsedTimeText As String
            Get
                Return _elapsedTimeText
            End Get
            Private Set(value As String)
                SetProperty(_elapsedTimeText, value)
            End Set
        End Property

        Public ReadOnly Property ShowDescription As Boolean
            Get
                Return _hasStrategyDescription AndAlso _workPhase <> WorkPhase.Running
            End Get
        End Property

        Public ReadOnly Property IsIndeterminateProgress As Boolean
            Get
                Return _workPhase = WorkPhase.Training OrElse _workPhase = WorkPhase.DownloadingBars
            End Get
        End Property

        Public ReadOnly Property CanRun As Boolean
            Get
                Return _workPhase = WorkPhase.Idle AndAlso _barsAvailable
            End Get
        End Property

        Public ReadOnly Property CanCancel As Boolean
            Get
                Return _workPhase = WorkPhase.Running
            End Get
        End Property

        Public ReadOnly Property CanTrain As Boolean
            Get
                Return _workPhase = WorkPhase.Idle AndAlso
                       Not String.IsNullOrEmpty(_contractIdText) AndAlso
                       Not String.IsNullOrEmpty(_selectedStrategyName)
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' RESULTS PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property Trades As New ObservableCollection(Of BacktestTradeRowVm)()
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

        Public ReadOnly Property AvailableStrategies As New ObservableCollection(Of String)()
        Public ReadOnly Property AvailableIntervals As New ObservableCollection(Of String)()
        Public ReadOnly Property AvailableContracts As New ObservableCollection(Of Contract)()

        ' ══════════════════════════════════════════════════════════════════════
        ' STRATEGY PANEL PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _activeStrategyText As String = "None selected — click a card above"
        Public Property ActiveStrategyText As String
            Get
                Return _activeStrategyText
            End Get
            Set(value As String)
                SetProperty(_activeStrategyText, value)
            End Set
        End Property

        Private _strategyNakedDescription As String = String.Empty
        Public Property StrategyNakedDescription As String
            Get
                Return _strategyNakedDescription
            End Get
            Set(value As String)
                SetProperty(_strategyNakedDescription, value)
            End Set
        End Property

        Private _hasStrategyDescription As Boolean = False
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
        Public ReadOnly Property TrainModelCommand As RelayCommand
        Public ReadOnly Property ExportCsvCommand As RelayCommand
        Public ReadOnly Property SelectPersonaCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService)

            _backtestService = backtestService
            _barCollectionService = barCollectionService
            _session = session
            _personaService = personaService

            For Each f In FavouriteContracts.GetDefaults()
                AvailableContracts.Add(New Contract With {
                    .Id = f.ContractId,
                    .FriendlyName = f.Name
                })
            Next

            AvailableStrategies.Add("EMA/RSI Combined")
            AvailableStrategies.Add("Multi-Confluence Engine")
            AvailableStrategies.Add("LULT Divergence")
            AvailableStrategies.Add("BB Squeeze Scalper")
            AvailableStrategies.Add("VIDYA Cross")
            AvailableStrategies.Add("Naked Trader")
            AvailableStrategies.Add("Double Bubble Butt")
            AvailableStrategies.Add("Opening Range Breakout")

            AvailableIntervals.Add("1 min")
            AvailableIntervals.Add("5 min")
            AvailableIntervals.Add("15 min")
            AvailableIntervals.Add("30 min")
            AvailableIntervals.Add("1 hour")

            SelectEmaRsiCombinedCommand      = New RelayCommand(AddressOf ApplyEmaRsiCombined)
            SelectMultiConfluenceEngineCommand = New RelayCommand(AddressOf ApplyMultiConfluenceEngine)
            SelectLultDivergenceCommand      = New RelayCommand(AddressOf ApplyLultDivergence)
            SelectBbSqueezeScalperCommand    = New RelayCommand(AddressOf ApplyBbSqueezeScalper)
            SelectVidyaCommand               = New RelayCommand(AddressOf ApplyVidya)
            SelectNakedTraderCommand         = New RelayCommand(AddressOf ApplyNakedTrader)
            SelectDoubleBubbleButtCommand    = New RelayCommand(AddressOf ApplyDoubleBubbleButt)
            RunCommand      = New RelayCommand(AddressOf ExecuteRun, Function() CanRun)
            CancelCommand   = New RelayCommand(AddressOf ExecuteCancel, Function() CanCancel)
            TrainModelCommand = New RelayCommand(AddressOf ExecuteTrainModel, Function() CanTrain)
            ExportCsvCommand  = New RelayCommand(AddressOf ExecuteExportCsv)
            SelectPersonaCommand = New RelayCommand(Sub(p) ApplyPersona(CStr(p)))

            AddHandler _backtestService.ProgressUpdated, AddressOf OnProgress

            If Application.Current IsNot Nothing Then
                _elapsedTimer = New DispatcherTimer(DispatcherPriority.Normal,
                                                    Application.Current.Dispatcher)
                _elapsedTimer.Interval = TimeSpan.FromSeconds(1)
                AddHandler _elapsedTimer.Tick, AddressOf OnElapsedTick
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' STRATEGY WORKFLOW
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ApplyStrategyDefaults(strategyName As String)
            Dim defaults = StrategyDefaults.TryGet(strategyName)
            If defaults IsNot Nothing Then
                If Not _personaExplicitlyChosen Then
                    InitialCapital = defaults.Capital
                End If
                Quantity = defaults.Qty
            End If
        End Sub

        Private Sub ApplyPersona(personaName As String)
            Dim profile = _personaService.GetProfile(personaName)
            _selectedPersona = personaName.Split(" "c)(0)
            _personaExplicitlyChosen = True
            _configMaxScaleIns = profile.MaxScaleIns
            Quantity        = profile.PositionSize.ToString()
            MinAdxThreshold = CInt(profile.AdxThreshold).ToString()
            MinConfidence   = profile.DefaultConfidencePct.ToString()
            SlAtrMultiple   = profile.SlMultipleOfN.ToString("F2")
            TpAtrMultiple   = profile.TpMultipleOfN.ToString("F2")
            OnPropertyChanged(NameOf(IsLewisSelected))
            OnPropertyChanged(NameOf(IsDamianSelected))
            OnPropertyChanged(NameOf(IsJoeSelected))
        End Sub

        Private Sub ApplyEmaRsiCombined()
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

        Private Sub DownloadBarsAsync()
            BarsAvailable = False
            SetWorkPhase(WorkPhase.DownloadingBars)
            BarsStatusText = $"⏳ Checking {_selectedInterval} bars for {_contractIdText}..."
            BarsStatusColor = "AccentBrush"
            HasBarsStatus = True

            Dim contractId = _contractIdText
            Dim fromDate = _startDate
            Dim toDate = _endDate
            Dim timeframe = ParseIntervalToTimeframe(_selectedInterval)

            Task.Run(Async Function()
                         Try
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
                                           SetWorkPhase(WorkPhase.Idle)
                                           BarsAvailable = result.Success
                                           If result.Success Then
                                               BarsStatusText = If(result.WasCacheHit,
                                                   $"✓ {result.BarCount:N0} bars ready (cached)",
                                                   $"✓ {result.BarCount:N0} bars ready (downloaded)")
                                               BarsStatusColor = "BuyBrush"
                                           Else
                                               BarsStatusText = result.Message
                                               BarsStatusColor = "SellBrush"
                                           End If
                                       End Sub)

                         Catch ex As OperationCanceledException
                              Dispatch(Sub()
                                           SetWorkPhase(WorkPhase.Idle)
                                           BarsAvailable = False
                                           BarsStatusText = "✗ Bar download timed out (> 5 min)"
                                           BarsStatusColor = "SellBrush"
                                       End Sub)
                          Catch ex As Exception
                              Dispatch(Sub()
                                           SetWorkPhase(WorkPhase.Idle)
                                           BarsAvailable = False
                                           BarsStatusText = $"✗ Bar download failed: {ex.Message}"
                                           BarsStatusColor = "SellBrush"
                                       End Sub)
                         End Try
                     End Function)
        End Sub

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
        ' TRAIN MODEL
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteTrainModel()
            DownloadBarsAsync()
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' RUN BACKTEST
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
            conf = conf / 100.0F

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
                Case "Opening Range Breakout"  : strategyCondition = StrategyConditionType.OpeningRangeBreakout
                Case Else                      : strategyCondition = StrategyConditionType.EmaRsiWeightedScore
            End Select

            Dim slAtrMult As Decimal = 1.5D
            Decimal.TryParse(_slAtrMultiple, slAtrMult)
            Dim tpAtrMult As Decimal = 3.0D
            Decimal.TryParse(_tpAtrMultiple, tpAtrMult)

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
            Dim capValidateSplit = ValidateSplitEnabled
            Dim capStart = _startDate
            Dim capEnd = _endDate
            Dim capStrategy = _selectedStrategyName
            Dim capTick = GetTickSize(contractId)
            Dim capPoint = GetPointValue(contractId)

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
            SetWorkPhase(WorkPhase.Running)
            Progress = 0
            ClearResults()
            ProgressText = $"Running multi-timeframe analysis — {capStrategy} on {capContractId}..."

            Dim cts = _cancelSource
            Task.Run(Async Function()
                         Dim total = timeframes.Length
                         For i = 0 To total - 1
                             Dim tf = timeframes(i).Item1
                             Dim label = timeframes(i).Item2
                             Dim idx = i

                             If cts.IsCancellationRequested Then Exit For

                             Dispatch(Sub()
                                          ProgressText = $"[{idx + 1}/{total}] {label} — downloading bars..."
                                          Progress = CInt((idx * 100.0) / total)
                                      End Sub)

                             Try
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
                                     .CommissionPerSideUsd = GetCommissionPerSide(capContractId),
                                     .TrainTestSplit = If(capValidateSplit, 0.6, 0.0)
                                 }

                                 Dim result = Await _backtestService.RunBacktestAsync(config, cts.Token)
                                 Dispatch(Sub() TimeframeResults.Add(New TimeframeResultRowVm(label, result)))

                             Catch ex As OperationCanceledException
                                 Dispatch(Sub() TimeframeResults.Add(New TimeframeResultRowVm(label, "Cancelled")))
                                 Exit For
                             Catch ex As Exception
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
                                 End Try

                                 Dim full = $"{ex.GetType().Name}: {ex.Message}"
                                 If full.Length > 40 Then full = full.Substring(0, 40) & "..."
                                 Dispatch(Sub() TimeframeResults.Add(New TimeframeResultRowVm(label, full)))
                             End Try

                             Dispatch(Sub() Progress = CInt(((idx + 1) * 100.0) / total))
                         Next

                         Dispatch(Sub()
                                       SetWorkPhase(WorkPhase.Idle)
                                       Progress = 100
                                       Dim done = TimeframeResults.Count
                                       Dim ok = TimeframeResults.Where(Function(r) r.IsSuccess).Count()
                                       ProgressText = $"Complete — {ok}/{done} timeframes succeeded"
                                      cts?.Dispose()
                                      _cancelSource = Nothing
                                  End Sub)
                     End Function)
        End Sub

        ''' <summary>
        ''' Maximum lookback days supported by TopStepX for each timeframe.
        ''' 1-min: 7 days · 5/15/30-min: 60 days · 1-hr and above: 90 days.
        ''' </summary>
        Public Shared Function GetMaxLookbackDays(tf As BarTimeframe) As Integer
            Select Case tf
                Case BarTimeframe.OneMinute : Return 7
                Case BarTimeframe.FiveMinute, BarTimeframe.TenMinute,
                     BarTimeframe.FifteenMinute, BarTimeframe.ThirtyMinute : Return 60
                Case Else : Return 730
            End Select
        End Function

        Private Shared Function ParseIntervalToTimeframe(interval As String) As BarTimeframe
            Select Case interval
                Case "1 min"  : Return BarTimeframe.OneMinute
                Case "5 min"  : Return BarTimeframe.FiveMinute
                Case "15 min" : Return BarTimeframe.FifteenMinute
                Case "30 min" : Return BarTimeframe.ThirtyMinute
                Case "1 hour" : Return BarTimeframe.OneHour
                Case Else     : Return BarTimeframe.FiveMinute
            End Select
        End Function

        Private Function GetTickSize(contractId As String) As Decimal
            Dim fc = FavouriteContracts.TryGetBySymbol(contractId)
            If fc IsNot Nothing Then
                Dim ts = fc.GetTickSize(_session.ActiveBroker)
                If ts > 0D Then Return ts
            End If
            Return 0.01D
        End Function

        Private Function GetPointValue(contractId As String) As Decimal
            Dim fc = FavouriteContracts.TryGetBySymbol(contractId)
            If fc IsNot Nothing Then
                Dim pv = fc.GetPointValue(_session.ActiveBroker)
                If pv > 0D Then Return pv
            End If
            Return 1.0D
        End Function

        Private Function GetCommissionPerSide(contractId As String) As Decimal
            Dim fc = FavouriteContracts.TryGetBySymbol(contractId)
            If fc IsNot Nothing Then Return fc.RoundTripFee / 2D
            Return 0.40D  ' fallback: $0.80 round-trip / 2
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
        ' EXPORT CSV
        ' ══════════════════════════════════════════════════════════════════════

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
        ' ELAPSED TIMER
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub OnElapsedTick(sender As Object, e As EventArgs)
            Dim elapsed = DateTimeOffset.UtcNow - _workStartTime
            Dim mins = CInt(Math.Floor(elapsed.TotalMinutes))
            Dim secs = elapsed.Seconds
            ElapsedTimeText = $"{mins}:{secs:D2}"
        End Sub

        Private Sub StartElapsedTimer()
            _workStartTime = DateTimeOffset.UtcNow
            ElapsedTimeText = "0:00"
            If _elapsedTimer IsNot Nothing AndAlso Not _elapsedTimer.IsEnabled Then _elapsedTimer.Start()
        End Sub

        Private Sub StopElapsedTimerIfIdle()
            If Not IsWorking Then
                _elapsedTimer?.Stop()
                ElapsedTimeText = ""
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' INFRASTRUCTURE
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Central work-phase setter — fires all dependent property notifications and manages the elapsed timer.
        ''' Replaces the individual _isRunning / _isTraining / _isBarsDownloading boolean writes (ARCH-03).
        ''' </summary>
        Private Sub SetWorkPhase(phase As WorkPhase)
            _workPhase = phase
            OnPropertyChanged(NameOf(IsRunning))
            OnPropertyChanged(NameOf(IsTraining))
            OnPropertyChanged(NameOf(IsBarsDownloading))
            OnPropertyChanged(NameOf(IsWorking))
            OnPropertyChanged(NameOf(IsIndeterminateProgress))
            OnPropertyChanged(NameOf(CanRun))
            OnPropertyChanged(NameOf(CanCancel))
            OnPropertyChanged(NameOf(CanTrain))
            OnPropertyChanged(NameOf(ShowDescription))
            OnPropertyChanged(NameOf(NotIsRunning))
            If phase <> WorkPhase.Idle Then StartElapsedTimer() Else StopElapsedTimerIfIdle()
        End Sub

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

        ' ══════════════════════════════════════════════════════════════════════
        ' IDISPOSABLE — BUG-03
        ' ══════════════════════════════════════════════════════════════════════

        Private _disposed As Boolean = False

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                If _elapsedTimer IsNot Nothing Then
                    _elapsedTimer.Stop()
                    RemoveHandler _elapsedTimer.Tick, AddressOf OnElapsedTick
                End If
                RemoveHandler _backtestService.ProgressUpdated, AddressOf OnProgress
                _cancelSource?.Cancel()
                _cancelSource?.Dispose()
                _disposed = True
            End If
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
        Public ReadOnly Property EndOfDayCount As String
        Public ReadOnly Property CommissionPaid As String
        Public ReadOnly Property IsSuccess As Boolean
        ' Out-of-sample split columns (FEAT-13) — "—" when no split active
        Public ReadOnly Property TestPnL As String
        Public ReadOnly Property TestPnLRaw As Decimal
        Public ReadOnly Property TestPnLColor As String
        Public ReadOnly Property DegradationPct As String
        Public ReadOnly Property RowBackground As String

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
            EndOfDayCount = result.EndOfDayCloseCount.ToString()
            CommissionPaid = result.CommissionPaid.ToString("C0")
            IsSuccess = True
            If result.OutOfSampleResult IsNot Nothing Then
                Dim oos = result.OutOfSampleResult
                TestPnLRaw = oos.TotalPnL
                TestPnL = oos.TotalPnL.ToString("C0")
                TestPnLColor = If(oos.TotalPnL >= 0, "BuyBrush", "SellBrush")
                If result.TotalPnL <> 0D Then
                    Dim deg = (result.TotalPnL - oos.TotalPnL) / Math.Abs(result.TotalPnL) * 100D
                    DegradationPct = deg.ToString("F0") & "%"
                    If oos.TotalPnL < 0D Then
                        RowBackground = "#220000"
                    ElseIf deg > 50D Then
                        RowBackground = "#1A1200"
                    Else
                        RowBackground = "Transparent"
                    End If
                Else
                    DegradationPct = "—"
                    RowBackground = "Transparent"
                End If
            Else
                TestPnL = "—"
                TestPnLColor = "TextSecondaryBrush"
                DegradationPct = "—"
                RowBackground = "Transparent"
            End If
        End Sub

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
            EndOfDayCount = "—"
            CommissionPaid = "—"
            IsSuccess = False
            TestPnL = "—"
            TestPnLColor = "TextSecondaryBrush"
            DegradationPct = "—"
            RowBackground = "Transparent"
        End Sub
    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' TRADE ROW
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

End Namespace
