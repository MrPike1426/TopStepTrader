Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for the Hydra multi-asset monitoring view.
    ''' Runs 5 independent EMA/RSI Combined sessions (one per asset) concurrently and
    ''' surfaces per-asset confidence snapshots in real time.  No session expiry — monitors 24/7.
    ''' Each engine runs in its own DI scope so BarIngestionService and IOrderService are
    ''' fully isolated between assets.
    ''' </summary>
    Public Class HydraViewModel
        Inherits TradingViewModelBase
        Implements IDisposable

        ' ── Dependencies ──────────────────────────────────────────────────────────
        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService

        ' ── Per-asset card ViewModels ─────────────────────────────────────────────
        Public Property Assets As New ObservableCollection(Of HydraAssetViewModel)

        ''' <summary>
        ''' True when any of the 5 Hydra asset engines has an open position.
        ''' Used by the IsOrderingAllowed lambda to lock out new entries on all other contracts
        ''' while one is being actively managed — prevents breaching the 100 req/min rate limit.
        ''' </summary>
        Public ReadOnly Property AnyEngineHasOpenPosition As Boolean
            Get
                If Assets Is Nothing Then Return False
                Return Assets.Any(Function(a) a.HasOpenPosition)
            End Get
        End Property

        ''' <summary>Returns the ContractId of whichever asset currently has an open position, or Nothing.</summary>
        Public ReadOnly Property OpenPositionContractId As String
            Get
                If Assets Is Nothing Then Return Nothing
                Return Assets.FirstOrDefault(Function(a) a.HasOpenPosition)?.ContractId
            End Get
        End Property

        ' ── Per-asset scope + engine ──────────────────────────────────────────────
        Private ReadOnly _assetScopes(4) As IServiceScope
        Private ReadOnly _engines(4) As StrategyExecutionEngine

        ' ── Internal state ────────────────────────────────────────────────────────
        Private _currentStrategy As StrategyDefinition
        Private _disposed As Boolean = False

        ' ── Bar-check batching: suppress per-contract "bar Checked" lines and
        '    update the ATR-tier panel label once all 5 assets report. ──
        Private _barCheckCount As Integer = 0
        Private ReadOnly _barCheckLock As New Object()

        ' Force-close UI controls
        Private _forceCloseEnabled As Boolean = False
        Public Property ForceCloseEnabled As Boolean
            Get
                Return _forceCloseEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_forceCloseEnabled, value) Then
                    ' If toggled while running, start or stop the monitor loop dynamically
                    If IsRunning Then
                        If _forceCloseEnabled Then
                            If _forceCloseCts Is Nothing OrElse _forceCloseCts.IsCancellationRequested Then
                                _forceCloseCts = New CancellationTokenSource()
                                _forceCloseTask = Task.Run(Function() ForceCloseMonitorLoopAsync(_forceCloseCts.Token))
                            End If
                        Else
                            Try
                                If _forceCloseCts IsNot Nothing Then _forceCloseCts.Cancel()
                            Catch
                            End Try
                        End If
                    End If
                End If
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

        ' ── Risk / quantity (defaults = Damian Moderate profile) ──────────────────
        Private _capitalAtRisk As Decimal = 500D
        Public Property CapitalAtRisk As Decimal
            Get
                Return _capitalAtRisk
            End Get
            Set(value As Decimal)
                SetProperty(_capitalAtRisk, value)
            End Set
        End Property

        Private _leverage As Integer = 10
        Public Property Leverage As Integer
            Get
                Return _leverage
            End Get
            Set(value As Integer)
                SetProperty(_leverage, Math.Max(1, value))
            End Set
        End Property

        Private _TpDollarBracket As Decimal = 20D
        ''' <summary>Initial take-profit in dollars. Turtle bracket first TP level. Default $20.</summary>
        Public Property TpDollarBracket As Decimal
            Get
                Return _TpDollarBracket
            End Get
            Set(value As Decimal)
                SetProperty(_TpDollarBracket, Math.Max(0D, value))
            End Set
        End Property

        Private _SlDollarBracket As Decimal = 10D
        ''' <summary>Initial stop-loss in dollars. Turtle bracket first SL level. Default $10.</summary>
        Public Property SlDollarBracket As Decimal
            Get
                Return _SlDollarBracket
            End Get
            Set(value As Decimal)
                SetProperty(_SlDollarBracket, Math.Max(0D, value))
            End Set
        End Property

        Private _minConfidencePct As Integer = 80  ' Damian default; overwritten by ApplyRiskProfile
        Public Property MinConfidencePct As Integer
            Get
                Return _minConfidencePct
            End Get
            Set(value As Integer)
                SetProperty(_minConfidencePct, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property


        Private _adxThreshold As Single = 25.0F
        ''' <summary>
        ''' Minimum ADX required to pass the trend-strength gate.
        ''' Default = 25 (Lewis level — required to filter chop on equity index futures).
        ''' Overridden by ApplyRiskProfile: Lewis=25, Damian=20, Joe=15.
        ''' </summary>
        Public Property AdxThreshold As Single
            Get
                Return _adxThreshold
            End Get
            Set(value As Single)
                SetProperty(_adxThreshold, Math.Max(1.0F, value))
            End Set
        End Property

        Private _maxScaleIns As Integer = 2
        ''' <summary>
        ''' Maximum additional positions after the initial entry.
        ''' Damian default = 2.  Lewis = 1.  Joe = 3.
        ''' Note: LULT Divergence and BB Squeeze Scalper override this to 0 in their StrategyDefinition.
        ''' </summary>
        Public Property MaxScaleIns As Integer
            Get
                Return _maxScaleIns
            End Get
            Set(value As Integer)
                SetProperty(_maxScaleIns, Math.Max(0, value))
            End Set
        End Property

        Private _macdHistMinAtrFraction As Double = 0.05   ' Minimum MACD histogram magnitude as fraction of ATR(14). Lewis=0.07 Damian=0.05 Joe=0.03

        Private _slMultipleOfN As Decimal = 2.5D   ' Wide tier default — survives bar noise on index futures
        ''' <summary>Initial SL as a multiple of N (ATR × DollarPerPoint). Wide=2.5, Standard=1.5, Tight=0.75.</summary>
        Public Property SlMultipleOfN As Decimal
            Get
                Return _slMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_slMultipleOfN, Math.Max(0.1D, value))
            End Set
        End Property

        Private _tpMultipleOfN As Decimal = 5.0D   ' Wide tier default — 1:2 R:R maintained
        ''' <summary>Initial TP as a multiple of N (ATR × DollarPerPoint). Wide=5.0, Standard=3.0, Tight=1.5.</summary>
        Public Property TpMultipleOfN As Decimal
            Get
                Return _tpMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_tpMultipleOfN, Math.Max(0.1D, value))
            End Set
        End Property

        ' ── Risk profile selection ─────────────────────────────────────────────────
        ''' <summary>
        ''' Name of the currently selected risk persona ("Lewis", "Damian", or "Joe").
        ''' Updated by ApplyRiskProfile().  Drives IsLewisSelected / IsDamianSelected / IsJoeSelected.
        ''' </summary>
        Private _selectedProfileName As String = "Damian"

        Public ReadOnly Property IsLewisSelected As Boolean
            Get
                Return _selectedProfileName = "Lewis"
            End Get
        End Property

        Public ReadOnly Property IsDamianSelected As Boolean
            Get
                Return _selectedProfileName = "Damian"
            End Get
        End Property

        Public ReadOnly Property IsJoeSelected As Boolean
            Get
                Return _selectedProfileName = "Joe"
            End Get
        End Property

        ' ── ATR risk tier ─────────────────────────────────────────────────────────
        ''' <summary>
        ''' Selected ATR risk tier ("Tight", "Standard", or "Wide").
        ''' Controls SlMultipleOfN / TpMultipleOfN independently of the persona.
        ''' Persona governs capital, leverage, ADX, confidence and scale-in count.
        ''' ATR tier governs how many ATR units the bracket spans — elasticity.
        '''   Tight    : SL=0.75×N  TP=1.5×N  — tight stops, higher trade frequency
        '''   Standard : SL=1.5×N   TP=3.0×N  — balanced, matches Lewis profile
        '''   Wide     : SL=2.5×N   TP=5.0×N  — wide stops, fewer whipsaws, patient
        ''' All three tiers maintain a 1:2 R:R ratio.
        ''' </summary>
        Private _selectedAtrTier As String = "Wide"

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

        ''' <summary>
        ''' When True, the live engine advances the TP by one TpMultipleOfN × ATR unit each time
        ''' a bar closes at or beyond the current TP price (up to 3 advances per trade).
        ''' Default True — backtest winner config had this ON.
        ''' </summary>
        Private _extendTpOnClose As Boolean = True
        Public Property ExtendTpOnClose As Boolean
            Get
                Return _extendTpOnClose
            End Get
            Set(value As Boolean)
                SetProperty(_extendTpOnClose, value)
            End Set
        End Property

        ' ── STRAT-30: Regime override ──────────────────────────────────────────
        Public ReadOnly Property AvailableRegimeOverrides As New List(Of String) From {
            "— Off —",
            "Multi-Confluence",
            "BB Squeeze Scalper",
            "EMA/RSI Combined",
            "Naked Trader",
            "VIDYA Cross",
            "BB+RSI Reversion",
            "Opening Range Breakout"
        }

        Private _selectedTrendingOverrideName As String = "— Off —"
        Public Property SelectedTrendingOverrideName As String
            Get
                Return _selectedTrendingOverrideName
            End Get
            Set(value As String)
                SetProperty(_selectedTrendingOverrideName, value)
            End Set
        End Property

        Private _selectedRangingOverrideName As String = "— Off —"
        Public Property SelectedRangingOverrideName As String
            Get
                Return _selectedRangingOverrideName
            End Get
            Set(value As String)
                SetProperty(_selectedRangingOverrideName, value)
            End Set
        End Property

        Private Shared Function ParseRegimeOverride(name As String) As Core.Enums.StrategyConditionType?
            Select Case name
                Case "Multi-Confluence"       : Return Core.Enums.StrategyConditionType.MultiConfluence
                Case "BB Squeeze Scalper"     : Return Core.Enums.StrategyConditionType.BbSqueezeScalper
                Case "EMA/RSI Combined"       : Return Core.Enums.StrategyConditionType.EmaRsiWeightedScore
                Case "Naked Trader"           : Return Core.Enums.StrategyConditionType.NakedTrader
                Case "VIDYA Cross"            : Return Core.Enums.StrategyConditionType.VidyaCross
                Case "BB+RSI Reversion"       : Return Core.Enums.StrategyConditionType.BbRsiMeanReversion
                Case "Opening Range Breakout" : Return Core.Enums.StrategyConditionType.OpeningRangeBreakout
                Case Else                     : Return Nothing
            End Select
        End Function

        ' ── Bars-updated status label ─────────────────────────────────────────────
        Private _barsUpdatedText As String = "Bars updated @ --:--:--"
        ''' <summary>Displayed in the ATR-tier panel. Updated each time all assets complete a bar check.</summary>
        Public Property BarsUpdatedText As String
            Get
                Return _barsUpdatedText
            End Get
            Set(value As String)
                SetProperty(_barsUpdatedText, value)
            End Set
        End Property

        Private _isBarsUpdatedFlashing As Boolean = False
        ''' <summary>True for 500 ms after each bar-update to drive a bold-white flash in the UI.</summary>
        Public Property IsBarsUpdatedFlashing As Boolean
            Get
                Return _isBarsUpdatedFlashing
            End Get
            Set(value As Boolean)
                SetProperty(_isBarsUpdatedFlashing, value)
            End Set
        End Property

        ''' <summary>Sets BarsUpdatedText, then flashes the label bold-white for 500 ms.</summary>
        Private Async Sub FlashBarsUpdated()
            BarsUpdatedText = $"Bars updated @ {DateTime.Now:HH:mm:ss}"
            IsBarsUpdatedFlashing = True
            Await Task.Delay(500)
            IsBarsUpdatedFlashing = False
        End Sub

        ''' <summary>True when the form is ready to trade (account selected).</summary>
        Public Overrides ReadOnly Property IsFormReady As Boolean
            Get
                Return SelectedAccount IsNot Nothing
            End Get
        End Property

        ' ── Running state ─────────────────────────────────────────────────────────
        Private _isRunning As Boolean = False
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                OnPropertyChanged(NameOf(IsNotRunning))
                NotifyPropertyChanged(NameOf(IsVidyaDescriptionVisible))
                NotifyPropertyChanged(NameOf(IsVidyaGridVisible))
                NotifyPropertyChanged(NameOf(LastUpdatedDisplay))
                NotifyPropertyChanged(NameOf(IsStrategyDescriptionVisible))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property IsNotRunning As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        ' ── Strategy selection ────────────────────────────────────────────────────
        Private _hasParsedStrategy As Boolean = False
        Public Property HasParsedStrategy As Boolean
            Get
                Return _hasParsedStrategy
            End Get
            Set(value As Boolean)
                SetProperty(_hasParsedStrategy, value)
                NotifyPropertyChanged(NameOf(IsStrategyDescriptionVisible))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Private _activeStrategyText As String = "None selected — click a card above"
        Public Property ActiveStrategyText As String
            Get
                Return _activeStrategyText
            End Get
            Set(value As String)
                SetProperty(_activeStrategyText, value)
            End Set
        End Property

        ''' <summary>
        ''' Plain-English Naked Trader-style explanation of the selected strategy.
        ''' Populated by each Apply* method. Empty until a card is clicked.
        ''' </summary>
        Private _strategyDescription As String = String.Empty
        Public Property StrategyDescription As String
            Get
                Return _strategyDescription
            End Get
            Set(value As String)
                SetProperty(_strategyDescription, value)
            End Set
        End Property

        Public ReadOnly Property StrategyDescriptionHeading As String
            Get
                Select Case _activeStrategyKind
                    Case "EmaRsi"          : Return "WHAT THE EMA/RSI COMBINED STRATEGY DOES"
                    Case "MultiConfluence"  : Return "WHAT THE MULTI-CONFLUENCE STRATEGY DOES"
                    Case "Lult"            : Return "WHAT THE LULT DIVERGENCE STRATEGY DOES"
                    Case "BbSqueeze"       : Return "WHAT THE BB SQUEEZE SCALPER STRATEGY DOES"
                    Case "Vidya"           : Return "WHAT THE VIDYA CROSS STRATEGY DOES"
                    Case "NakedTrader"     : Return "WHAT THE NAKED TRADER STRATEGY DOES"
                    Case "DoubleBubbleButt": Return "WHAT THE DOUBLE BUBBLE BUTT STRATEGY DOES"
                    Case Else              : Return "WHAT THIS STRATEGY DOES"
                End Select
            End Get
        End Property

        ''' <summary>True when a strategy has been selected but the engines have not started yet.</summary>
        Public ReadOnly Property IsStrategyDescriptionVisible As Boolean
            Get
                Return _hasParsedStrategy AndAlso Not _isRunning
            End Get
        End Property

        ' ── Active strategy kind (drives DataGrid column switching in the view) ──
        Private _activeStrategyKind As String = "MultiConfluence"
        Public Property ActiveStrategyKind As String
            Get
                Return _activeStrategyKind
            End Get
            Set(value As String)
                If SetProperty(_activeStrategyKind, value) Then
                    NotifyPropertyChanged(NameOf(IsEmaRsiActive))
                    NotifyPropertyChanged(NameOf(IsMultiConfluenceActive))
                    NotifyPropertyChanged(NameOf(IsVidyaActive))
                    NotifyPropertyChanged(NameOf(IsVidyaDescriptionVisible))
                    NotifyPropertyChanged(NameOf(IsVidyaGridVisible))
                    NotifyPropertyChanged(NameOf(IsLultActive))
                    NotifyPropertyChanged(NameOf(IsBbSqueezeActive))
                    NotifyPropertyChanged(NameOf(IsNakedTraderActive))
                    NotifyPropertyChanged(NameOf(IsDoubleBubbleButtActive))
                    NotifyPropertyChanged(NameOf(IsOtherStrategyActive))
                    NotifyPropertyChanged(NameOf(HasNoStrategySelected))
                    NotifyPropertyChanged(NameOf(StrategyDescriptionHeading))
                End If
            End Set
        End Property

        Public ReadOnly Property IsEmaRsiActive As Boolean
            Get
                Return _activeStrategyKind = "EmaRsi"
            End Get
        End Property

        Public ReadOnly Property IsMultiConfluenceActive As Boolean
            Get
                Return _activeStrategyKind = "MultiConfluence"
            End Get
        End Property

        Public ReadOnly Property IsVidyaActive As Boolean
            Get
                Return _activeStrategyKind = "Vidya"
            End Get
        End Property

        ''' <summary>True when VIDYA is selected but engines have not started yet — shows the strategy description panel.</summary>
        Public ReadOnly Property IsVidyaDescriptionVisible As Boolean
            Get
                Return _activeStrategyKind = "Vidya" AndAlso Not _isRunning
            End Get
        End Property

        ''' <summary>True when VIDYA is selected and engines are running — shows the live indicator grid.</summary>
        Public ReadOnly Property IsVidyaGridVisible As Boolean
            Get
                Return _activeStrategyKind = "Vidya" AndAlso _isRunning
            End Get
        End Property

        Public ReadOnly Property IsLultActive As Boolean
            Get
                Return _activeStrategyKind = "Lult"
            End Get
        End Property

        Public ReadOnly Property IsBbSqueezeActive As Boolean
            Get
                Return _activeStrategyKind = "BbSqueeze"
            End Get
        End Property

        Public ReadOnly Property IsNakedTraderActive As Boolean
            Get
                Return _activeStrategyKind = "NakedTrader"
            End Get
        End Property

        Public ReadOnly Property IsDoubleBubbleButtActive As Boolean
            Get
                Return _activeStrategyKind = "DoubleBubbleButt"
            End Get
        End Property

        Public ReadOnly Property IsOtherStrategyActive As Boolean
            Get
                Return _activeStrategyKind = "Other" OrElse
                       _activeStrategyKind = "Lult" OrElse
                       _activeStrategyKind = "BbSqueeze"
            End Get
        End Property

        ''' <summary>True when no strategy card has been selected yet. Drives the "Select a strategy" placeholder.</summary>
        Public ReadOnly Property HasNoStrategySelected As Boolean
            Get
                Return _activeStrategyKind = "None"
            End Get
        End Property

        ' ── Status / Log ──────────────────────────────────────────────────────────
        Private _statusText As String = "● Select a strategy"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _lastUpdatedAt As String = String.Empty
        Public Property LastUpdatedAt As String
            Get
                Return _lastUpdatedAt
            End Get
            Set(value As String)
                If SetProperty(_lastUpdatedAt, value) Then
                    NotifyPropertyChanged(NameOf(LastUpdatedDisplay))
                End If
            End Set
        End Property

        ''' <summary>
        ''' Shows "  Last Updated: HH:mm:ss" next to the running status; empty when not running.
        ''' </summary>
        Public ReadOnly Property LastUpdatedDisplay As String
            Get
                If _isRunning AndAlso Not String.IsNullOrEmpty(_lastUpdatedAt) Then
                    Return $"   Last Updated: {_lastUpdatedAt}"
                End If
                Return String.Empty
            End Get
        End Property

        Public Property LogEntries As New ObservableCollection(Of String)

        ' ── Commands ├─────────────────────────────────────────────────────────────
        Public ReadOnly Property SelectProfileCommand As RelayCommand
        Public ReadOnly Property SelectAtrTierCommand As RelayCommand
        Public ReadOnly Property SelectEmaRsiCombinedCommand As RelayCommand
        Public ReadOnly Property SelectMultiConfluenceEngineCommand As RelayCommand
        Public ReadOnly Property SelectLultDivergenceCommand As RelayCommand
        Public ReadOnly Property SelectBbSqueezeScalperCommand As RelayCommand
        Public ReadOnly Property SelectVidyaCommand As RelayCommand
        Public ReadOnly Property SelectNakedTraderCommand As RelayCommand
        Public ReadOnly Property SelectDoubleBubbleButtCommand As RelayCommand
        Public ReadOnly Property StartCommand As RelayCommand
        Public ReadOnly Property StopCommand As RelayCommand
        Private _forceCloseCts As CancellationTokenSource
        Private _forceCloseTask As Task

        ' ── Constructor ───────────────────────────────────────────────────────────

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       accountService As IAccountService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService)
            _scopeFactory = scopeFactory
            _accountService = accountService
            _session = session
            _personaService = personaService

            ' Build 5 per-asset ViewModels — fixed roster:
            '   0: OIL (crude oil — commodity, low equity correlation)
            '   1: GOLD (safe haven — low equity correlation)
            '   2: SPX500 (S&P 500 / MES — primary equity index)
            '   3: EURUSD (EUR/USD / M6E — replaces NSDQ100 to eliminate MES/MNQ correlation)
            '   4: BTC (/MBT on TopStepX, BTC CFD on eToro)
            ' NSDQ100 excluded: moves ~0.97 correlated with SPX500 — redundant exposure.
            Dim activeBroker = _session.ActiveBroker
            Dim allContracts = FavouriteContracts.GetDefaults(activeBroker)

            Dim roster As New List(Of FavouriteContract)
            Dim oilEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "OIL")
            Dim goldEntry  = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "GOLD.24-7")
            Dim spxEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "SPX500")
            Dim fxEntry    = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "EURUSD")
            Dim btcEntry   = allContracts.FirstOrDefault(Function(f) f.EToroContractId = "BTC")
            For Each entry In {oilEntry, goldEntry, spxEntry, fxEntry, btcEntry}
                If entry IsNot Nothing Then roster.Add(entry)
            Next

            ' Emoji icons by position (🛢️ Oil · 🥇 Gold · 📈 S&P · 💶 EUR/USD · ₿ BTC)
            Dim icons = {"🛢️", "🥇", "📈", "💶", "₿"}
            For i = 0 To Math.Min(roster.Count, 5) - 1
                Dim fav = roster(i)
                Dim contractId = fav.GetActiveContractId(activeBroker)
                Assets.Add(New HydraAssetViewModel(fav.Name, icons(i), contractId))
            Next
            ' Pad to 5 if fewer instruments returned (shouldn't happen in normal config)
            While Assets.Count < 5
                Assets.Add(New HydraAssetViewModel("—", "?", "—"))
            End While

            ' Create 5 independent DI scopes so each engine gets its own
            ' BarIngestionService and IOrderService (both Scoped).
            For i = 0 To 4
                _assetScopes(i) = _scopeFactory.CreateScope()
                _engines(i) = _assetScopes(i).ServiceProvider _
                                  .GetRequiredService(Of StrategyExecutionEngine)()
                ' Capture loop variable for the lambda — VB.NET closes over the variable
                ' itself, not the value, so an explicit capture is required.
                Dim capturedVm = Assets(i)
                Dim capturedIndex = i
                Dim capturedHydraVm As HydraViewModel = Me
                _engines(i).IsOrderingAllowed = Function()
                    ' Standard market-hours + equity session blackout check
                    If Not capturedVm.IsMarketOpen OrElse IsInEquitySessionBlackout(capturedVm.ContractId) Then Return False
                    ' Cross-engine suppression: only allow orders when no other contract has an open
                    ' position, OR when this engine's contract is the one that owns the position.
                    ' This prevents multiple simultaneous positions breaking the 100 req/min limit.
                    Return Not capturedHydraVm.AnyEngineHasOpenPosition OrElse
                           String.Equals(capturedHydraVm.OpenPositionContractId, capturedVm.ContractId,
                                         StringComparison.OrdinalIgnoreCase)
                End Function
                WireEngineEvents(_engines(i), Assets(i))
                ' Per-asset bracket management commands — captured index routes to the right scope.
                capturedVm.CloseCommand = New RelayCommand(
                    Sub() Task.Run(Sub() CloseAssetPositionAsync(capturedIndex)),
                    Function() capturedVm.HasOpenPosition)
                capturedVm.NudgeBracketCommand = New RelayCommand(
                    Sub() Task.Run(Sub() NudgeAssetBracketAsync(capturedIndex)),
                    Function() capturedVm.HasOpenPosition)
            Next

            ' Subscribe to session account changes so SelectedAccount tracks dashboard choice.
            AddHandler _session.AccountChanged, AddressOf OnSessionAccountChanged

            ' Risk profile — CommandParameter is the profile name string ("Lewis"/"Damian"/"Joe").
            ' Disabled when running so the profile cannot be changed mid-session.
            SelectProfileCommand = New RelayCommand(
                Sub(p) ApplyRiskProfile(CStr(p)),
                Function(p) IsNotRunning)

            ' ATR tier — CommandParameter is the tier name ("Tight"/"Standard"/"Wide").
            ' Disabled when running so the tier cannot be changed mid-session.
            SelectAtrTierCommand = New RelayCommand(
                Sub(p) ApplyAtrTier(CStr(p)),
                Function(p) IsNotRunning)

            SelectEmaRsiCombinedCommand = New RelayCommand(
                Sub(p) ApplyEmaRsiCombined(),
                Function(p) IsNotRunning)

            SelectMultiConfluenceEngineCommand = New RelayCommand(
                Sub(p) ApplyMultiConfluenceEngine(),
                Function(p) IsNotRunning)

            SelectLultDivergenceCommand = New RelayCommand(
                Sub(p) ApplyLultDivergence(),
                Function(p) IsNotRunning)

            SelectBbSqueezeScalperCommand = New RelayCommand(
                Sub(p) ApplyBbSqueezeScalper(),
                Function(p) IsNotRunning)

            SelectVidyaCommand = New RelayCommand(
                Sub(p) ApplyVidya(),
                Function(p) IsNotRunning)

            SelectNakedTraderCommand = New RelayCommand(
                Sub(p) ApplyNakedTrader(),
                Function(p) IsNotRunning)

            SelectDoubleBubbleButtCommand = New RelayCommand(
                Sub(p) ApplyDoubleBubbleButt(),
                Function(p) IsNotRunning)

            StartCommand = New RelayCommand(
                AddressOf ExecuteStart,
                Function(p) HasParsedStrategy AndAlso IsNotRunning AndAlso SelectedAccount IsNot Nothing)

            StopCommand = New RelayCommand(
                AddressOf ExecuteStop,
                Function(p) IsRunning)

            ' Pre-select Wide ATR tier and Multi-Confluence strategy as defaults.
            ' Wide tier (SL=2.5×N / TP=5.0×N) survives intrabar noise on index futures.
            ' Multi-Confluence requires 7 simultaneous conditions — far fewer false signals
            ' than EMA/RSI Combined on choppy RTH opens.
            ApplyAtrTier("Wide")
            ApplyMultiConfluenceEngine()
        End Sub

        ' ── Session account sync ──────────────────────────────────────────────────

        Private Sub OnSessionAccountChanged(sender As Object, account As Account)
            Dispatch(Sub()
                         If account IsNot Nothing Then
                             Dim match = Accounts.FirstOrDefault(Function(a) a.Id = account.Id)
                             If match IsNot Nothing Then SelectedAccount = match
                         End If
                     End Sub)
        End Sub

        ' ── Engine event wiring ───────────────────────────────────────────────────

        Private Sub WireEngineEvents(engine As StrategyExecutionEngine,
                                     assetVm As HydraAssetViewModel)
            AddHandler engine.BarPriceUpdated,
                Sub(s As Object, price As Decimal)
                    Dispatch(Sub() assetVm.UpdateLivePrice(price))
                End Sub

            AddHandler engine.ConfidenceUpdated,
                Sub(s As Object, e As ConfidenceUpdatedEventArgs)
                    Dispatch(Sub()
                                 assetVm.ApplyConfidence(e)
                                 LastUpdatedAt = DateTime.Now.ToString("HH:mm:ss")
                             End Sub)
                End Sub

            AddHandler engine.LogMessage,
                Sub(s As Object, msg As String)
                    ' Batch routine "bar checked" messages — update the ATR-tier label (not the log)
                    If msg.StartsWith("Bar checked", StringComparison.OrdinalIgnoreCase) Then
                        Dim shouldEmit As Boolean = False
                        SyncLock _barCheckLock
                            _barCheckCount += 1
                            If _barCheckCount >= Assets.Count Then
                                shouldEmit = True
                                _barCheckCount = 0
                            End If
                        End SyncLock
                        If shouldEmit Then
                            Dispatch(Sub() FlashBarsUpdated())
                        End If
                    Else
                        Dispatch(Sub() LogLine($"[{assetVm.Symbol}] {msg}"))
                    End If
                End Sub

            AddHandler engine.ExecutionStopped,
                Sub(s As Object, reason As String)
                    Dispatch(Sub() LogLine($"[{assetVm.Symbol}] ■ Stopped: {reason}"))
                End Sub

            AddHandler engine.TradeOpened,
                Sub(s As Object, e As TradeOpenedEventArgs)
                    Dispatch(Sub()
                                 assetVm.OpenTrade(e.Side, e.EntryPrice, e.Amount, e.Leverage)
                                 LogLine($"[{assetVm.Symbol}] 🟢 Trade opened — {e.Side} @ {e.EntryPrice:F4} | amt={e.Amount} lev={e.Leverage}")
                             End Sub)
                End Sub

            AddHandler engine.TradeClosed,
                Sub(s As Object, e As TradeClosedEventArgs)
                    Dispatch(Sub()
                                 assetVm.CloseTrade(e.ExitReason, e.PnL)
                                 LogLine($"[{assetVm.Symbol}] 🔴 Trade closed — {e.ExitReason} | P&L={If(e.PnL >= 0D, "+", "")}${e.PnL:F2}")
                             End Sub)
                End Sub

            AddHandler engine.PositionSynced,
                Sub(s As Object, e As PositionSyncedEventArgs)
                    Dispatch(Sub() assetVm.UpdateTradePnl(e.UnrealizedPnlUsd, e.Amount, e.IsBuy, e.PositionCount))
                End Sub

            AddHandler engine.TurtleBracketChanged,
                Sub(s As Object, e As TurtleBracketChangedEventArgs)
                    Dispatch(Sub()
                                 assetVm.ApplySl(e.SlPrice, e.TpPrice, e.IsAdvance, e.IsFreeRide)
                                 If e.IsAdvance Then
                                     LogLine($"[{assetVm.Symbol}] 🐢 Bracket #{e.BracketNumber} — SL={e.SlPrice:F2}  TP={e.TpPrice:F2}")
                                 End If
                             End Sub)
                End Sub
        End Sub

        ' ── Data loading ──────────────────────────────────────────────────────────

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
                Await CheckExistingPositionsAsync()
            Catch ex As Exception
                ' Offline / API unreachable — strategy selection still works; trading requires an account.
                Dispatch(Sub() LogLine($"⚠ Account load failed (offline?): {ex.Message} — strategy cards are still selectable."))
            End Try
        End Sub

        ''' <summary>
        ''' On view load, queries the eToro portfolio API for each of the 5 monitored assets
        ''' and pre-populates any asset tile that already has an open position.
        ''' This surfaces trades that were placed outside the engine (manual trades or a
        ''' previous session) so the UI is never left showing "No position" incorrectly.
        ''' </summary>
        Private Async Function CheckExistingPositionsAsync() As Task
            Dim accountId As Long = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
            Dispatch(Sub() LogLine($"📡 Pre-populating tiles (accountId={accountId})…"))
            For i = 0 To 4
                Try
                    Dim orderService = _assetScopes(i).ServiceProvider.GetRequiredService(Of IOrderService)()
                    Dim snapshot = Await orderService.GetLivePositionSnapshotAsync(accountId, Assets(i).ContractId)
                    If snapshot IsNot Nothing Then
                        Dim side = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)
                        Dim capturedVm = Assets(i)
                        Dim capturedSnap = snapshot
                        Dispatch(Sub()
                                     LogLine($"[{capturedVm.Symbol}] 📡 Pre-pop: {side} amt={capturedSnap.Amount} units={capturedSnap.Units} " &
                                             $"entry={capturedSnap.OpenRate:F4} lev={capturedSnap.Leverage} pnl={capturedSnap.UnrealizedPnlUsd:F2} " &
                                             $"posCount={capturedSnap.PositionCount} posId={capturedSnap.PositionId}")
                                     capturedVm.OpenTrade(side, capturedSnap.OpenRate, capturedSnap.Amount, capturedSnap.Leverage)
                                     ' Mark as pre-populated so the engine's startup TradeOpened resets
                                     ' count to 1 instead of stacking on top (which would show ×2).
                                     ' Do NOT display the one-shot snapshot P&L here — it is stale by
                                     ' the time the user clicks Start; the engine's 30-second PositionSynced
                                     ' poll is the authoritative live P&L source.
                                     capturedVm.MarkAsPrePopulated()
                                 End Sub)
                    End If
                Catch ex As Exception
                    ' Non-fatal — a single asset failing should not block the others
                    Dim sym = Assets(i).Symbol
                    Dispatch(Sub() LogLine($"[{sym}] ⚠️ Pre-pop failed: {ex.Message}"))
                End Try
            Next
        End Function

        ' ── Risk profile activation ────────────────────────────────────────────────

        ''' <summary>
        ''' Applies the selected risk profile (Lewis / Damian / Joe) to all backing fields:
        ''' TradeAmount → CapitalAtRisk, Leverage, SlMultipleOfN, TpMultipleOfN, AdxThreshold,
        ''' MaxScaleIns, DefaultConfidencePct → MinConfidencePct.
        ''' Then re-applies the current strategy template (if one is active and the engine is
        ''' not running) so the StrategyDefinition immediately reflects the new profile settings.
        ''' </summary>
        ''' <summary>
        ''' Returns True when equity index futures (MES, MNQ, MYM) should be blocked from
        ''' new entries due to thin/volatile session conditions.  Does NOT affect commodities,
        ''' EUR/USD, or crypto — only equity indices suffer from pre-open noise.
        '''
        ''' Blocked windows (UTC):
        '''   • Before 08:30       — pre-London thin Asian session (historically high false-signal rate)
        '''   • 08:00–08:30        — London open first 30 min (initial spike/gap fill)
        '''   • 13:30–14:00        — US open first 30 min (09:30–10:00 ET EDT)
        '''   • 20:00 and later    — post-US-close (thin overnight electronic session)
        ''' Permitted windows: 08:30–13:30 UTC (London main) and 14:00–20:00 UTC (US main).
        ''' </summary>
        Private Shared Function IsInEquitySessionBlackout(contractId As String) As Boolean
            ' Check whether the contract is an equity index future or eToro equity index CFD
            Dim isEquity = contractId.Contains("MES") OrElse
                           contractId.Contains("MNQ") OrElse
                           contractId.Contains("MYM") OrElse
                           String.Equals(contractId, "SPX500",  StringComparison.OrdinalIgnoreCase) OrElse
                           String.Equals(contractId, "NSDQ100", StringComparison.OrdinalIgnoreCase) OrElse
                           String.Equals(contractId, "US30",    StringComparison.OrdinalIgnoreCase)
            If Not isEquity Then Return False

            Dim now = DateTime.UtcNow
            Dim totalMins = now.Hour * 60 + now.Minute

            Dim inLondonMain = totalMins >= 8 * 60 + 30 AndAlso totalMins < 13 * 60 + 30
            Dim inUsMain     = totalMins >= 14 * 60      AndAlso totalMins < 20 * 60
            ' Allow orders only during the two main session windows; block everything else
            Return Not (inLondonMain OrElse inUsMain)
        End Function

        Private Sub ApplyRiskProfile(profileName As String)
            Dim profile = _personaService.GetProfile(profileName)

            _selectedProfileName = profileName
            CapitalAtRisk = CDec(profile.PositionSize)
            Leverage = profile.Leverage
            AdxThreshold = profile.AdxThreshold
            MaxScaleIns = profile.MaxScaleIns
            MinConfidencePct = profile.DefaultConfidencePct
            SlMultipleOfN = profile.SlMultipleOfN
            TpMultipleOfN = profile.TpMultipleOfN
            _macdHistMinAtrFraction = profile.MacdHistMinAtrFraction

            NotifyPropertyChanged(NameOf(IsLewisSelected))
            NotifyPropertyChanged(NameOf(IsDamianSelected))
            NotifyPropertyChanged(NameOf(IsJoeSelected))

            ' Damian + Gold: auto-select Standard tier. Damian's SlMultipleOfN=1.0 on Tight (0.75×) places
            ' the stop within Gold's normal 15-min bar range, causing routine whipsaw exits.
            If profile.Name.Contains("Damian") AndAlso
               Assets.Any(Function(a) a.Symbol.Equals("Gold", StringComparison.OrdinalIgnoreCase) OrElse
                                      a.ContractId.Contains("MGC")) Then
                ApplyAtrTier("Standard")
            End If

            ' Re-apply the current strategy template so the StrategyDefinition immediately
            ' picks up the new N-multiples, leverage, ADX gate, and confidence threshold.
            If HasParsedStrategy AndAlso IsNotRunning AndAlso _currentStrategy IsNot Nothing Then
                Select Case _activeStrategyKind
                    Case "EmaRsi" : ApplyEmaRsiCombined()
                    Case "MultiConfluence" : ApplyMultiConfluenceEngine()
                    Case "Vidya" : ApplyVidya()
                    Case "NakedTrader" : ApplyNakedTrader()
                    Case "DoubleBubbleButt" : ApplyDoubleBubbleButt()
                    Case "Other"
                        Select Case _currentStrategy.Name
                            Case "LULT Divergence" : ApplyLultDivergence()
                            Case "BB Squeeze Scalper" : ApplyBbSqueezeScalper()
                        End Select
                End Select
            End If
        End Sub

        ' ── ATR tier activation ────────────────────────────────────────────────────

        ''' <summary>
        ''' Applies the selected ATR risk tier to SlMultipleOfN / TpMultipleOfN.
        ''' All three tiers maintain a 1:2 R:R ratio.
        '''   Tight    : SL=0.75×ATR  TP=1.5×ATR  — tight brackets, higher signal frequency
        '''   Standard : SL=1.5×ATR   TP=3.0×ATR  — balanced (Lewis-equivalent)
        '''   Wide     : SL=2.5×ATR   TP=5.0×ATR  — wide brackets, fewer whipsaws, patient exits
        ''' The active persona (Lewis/Damian/Joe) controls all other parameters.
        ''' </summary>
        Private Sub ApplyAtrTier(tierName As String)
            _selectedAtrTier = tierName
            Select Case tierName
                Case "Tight"
                    SlMultipleOfN = 0.75D
                    TpMultipleOfN = 1.5D
                Case "Wide"
                    SlMultipleOfN = 2.5D
                    TpMultipleOfN = 5.0D
                Case Else   ' "Standard" or unrecognised
                    _selectedAtrTier = "Standard"
                    SlMultipleOfN = 1.5D
                    TpMultipleOfN = 3.0D
            End Select
            NotifyPropertyChanged(NameOf(IsAtrTightSelected))
            NotifyPropertyChanged(NameOf(IsAtrStandardSelected))
            NotifyPropertyChanged(NameOf(IsAtrWideSelected))
        End Sub

        ' ── Strategy activation ───────────────────────────────────────────────────

        ''' <summary>
        ''' Activates the EMA/RSI Combined strategy for all 5 assets.
        ''' DurationHours = 8 760 (one calendar year) so sessions never auto-expire
        ''' — satisfying the "runs 24/7" requirement.
        ''' Profile N-multiples, leverage and ADX gate come entirely from the backing fields
        ''' (set by ApplyRiskProfile); only strategy-specific dollar fallbacks are set here.
        ''' </summary>
        Private Sub ApplyEmaRsiCombined()
            TpDollarBracket = 20D        ' dollar fallback (used when ATR unavailable)
            SlDollarBracket = 10D        ' dollar fallback

            _currentStrategy = New StrategyDefinition With {
                .Name = "EMA/RSI Combined",
                .Indicator = StrategyIndicatorType.EmaRsiCombined,
                .Condition = StrategyConditionType.EmaRsiWeightedScore,
                .IndicatorPeriod = 50,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .SlMultipleOfN = _slMultipleOfN,
                .TpMultipleOfN = _tpMultipleOfN,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = _maxScaleIns,
                .ScaleInAmount = _capitalAtRisk,
                .ScaleInLeverage = _leverage,
                .MinConfidencePct = _minConfidencePct,
                .PersonaName = _selectedProfileName
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  EMA/RSI Combined  |  ADX≥{_adxThreshold:F0}  1ct  Conf:{_minConfidencePct}%"
            ActiveStrategyKind = "EmaRsi"
            StrategyDescription =
                "Price is checked every 5 minutes across all five markets at the same time. " &
                "Six things are scored: whether price is above its 21-bar and 50-bar moving averages, " &
                "whether RSI is in a tradeable zone, whether the faster average is above the slower one, " &
                "whether recent bars show momentum, and whether that momentum is accelerating. " &
                "Score adds up to 100. When it hits your confidence level, a trade fires — Long if the trend is up, Short if down." & vbLf & vbLf &
                "The stop follows price in your favour and never retreats. " &
                "If the score later drifts into the neutral 40–60% band, all positions close immediately — the trend has lost conviction and the engine gets flat. " &
                "Works best in markets with a clear directional bias. In flat, sideways sessions it will sit on its hands, which is exactly what you want."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · 1ct · ADX≥{_adxThreshold:F0} · Conf={_minConfidencePct}% · Max {_maxScaleIns} scale-ins")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  EMA/RSI Combined — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the Multi-Confluence Engine strategy for all 5 assets.
        ''' Uses Turtle bracket (TpDollarBracket = $20, SlDollarBracket = $10) as the
        ''' initial bracket; bracket advances on each TP hit using 0.5×N ATR steps.
        ''' DurationHours = 8 760 so sessions never auto-expire.
        ''' </summary>
        Private Sub ApplyMultiConfluenceEngine()
            TpDollarBracket = 20D        ' dollar fallback (used when ATR unavailable)
            SlDollarBracket = 10D        ' dollar fallback

            _currentStrategy = New StrategyDefinition With {
                .Name = "Multi-Confluence Engine",
                .Indicator = StrategyIndicatorType.MultiConfluence,
                .Condition = StrategyConditionType.MultiConfluence,
                .IndicatorPeriod = 80,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = _maxScaleIns,
                .ScaleInAmount = _capitalAtRisk,
                .ScaleInLeverage = _leverage,
                .MinConfidencePct = _minConfidencePct,
                .SlMultipleOfN = _slMultipleOfN,
                .TpMultipleOfN = _tpMultipleOfN,
                .ExtendTpOnClose = _extendTpOnClose,
                .PersonaName = _selectedProfileName,
                .MacdHistMinAtrFraction = _macdHistMinAtrFraction,
                .TradingWindowUtcStart = New TimeOnly(8, 0),
                .TradingWindowUtcEnd = New TimeOnly(17, 0)
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  Multi-Confluence  |  ADX≥{_adxThreshold:F0}  1ct  Conf:{_minConfidencePct}%  ExtTP:{If(_extendTpOnClose, "ON", "OFF")}"
            ActiveStrategyKind = "MultiConfluence"
            StrategyDescription =
                "Seven independent checks must all say yes before a single trade is placed. " &
                "Ichimoku cloud direction. Price position above or below the cloud. Fast and slow cloud lines crossing. " &
                "The lagging span confirming. ADX above 25 showing a genuine trend, not noise. " &
                "MACD histogram pointing the right way. StochRSI not already stretched into overbought territory. " &
                "Miss one — no trade. The bar is deliberately high." & vbLf & vbLf &
                "When all seven agree, entry fires on 5-minute bars. " &
                "The stop sits at the cloud edge or 1.5 times the ATR, whichever is closer to entry. " &
                "Target is 2:1 reward to risk. You will not trade often with this one, but every entry carries real conviction. " &
                "Backtest winner: Damian persona · Gold (GC=F) · 1-hr bars · ATR stops · 34 trades · 56% win rate · £17,746 P&L · Sharpe 7.71. " &
                "Confirmed across all three personas on the same instrument and timeframe (Joe #2, Lewis #3). Clear signal — Gold 1-hr is the primary deployment target."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine("• SL = min(1.5×ATR, cloud edge) · TP = 2:1 R:R (dynamic per-trade ATR)")
            LogLine("• Entry fires only when ALL 7 conditions align (Ichimoku + EMA21 + Tenkan/Kijun + Chikou + ADX + MACD + StochRSI)")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  Multi-Confluence Engine — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the LULT Divergence strategy for all 5 assets.
        ''' Uses WaveTrend (Market Cipher B) Anchor/Trigger divergence on 5-minute bars.
        ''' MaxScaleIns is forced to 0 in the StrategyDefinition regardless of profile
        ''' — the 6-step gate IS the entry criterion; scale-in is not applicable.
        ''' Time filter: 11:00–17:00 UTC (London + NY pre-market, 07:00–13:00 EST/EDT).
        ''' </summary>
        Private Sub ApplyLultDivergence()
            TpDollarBracket = 20D        ' dollar fallback (used when ATR unavailable)
            SlDollarBracket = 10D        ' dollar fallback

            _currentStrategy = New StrategyDefinition With {
                .Name = "LULT Divergence",
                .Indicator = StrategyIndicatorType.LultDivergence,
                .Condition = StrategyConditionType.LultDivergence,
                .IndicatorPeriod = 100,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = 0,
                .ScaleInAmount = 0D,
                .ScaleInLeverage = 1,
                .MinConfidencePct = _minConfidencePct,
                .PersonaName = _selectedProfileName
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  LULT Divergence  |  ${_capitalAtRisk:F0}×{_leverage}  5-min  07:00–13:00 EST"
            ActiveStrategyKind = "Lult"
            StrategyDescription =
                "Hunts for momentum exhaustion — the point where a strong move is running out of steam before reversing. " &
                "Uses a WaveTrend oscillator, similar to Market Cipher B, on 5-minute bars." & vbLf & vbLf &
                "Six steps must complete in order. First, a large anchor wave peaks or troughs at an extreme reading. " &
                "Second, a shallower second wave forms — the move is tiring. " &
                "Third, price makes a new high or low but the oscillator does not — that gap is the divergence. " &
                "Fourth, the two oscillator lines cross and print a signal dot. " &
                "Fifth, an engulfing candle confirms the reversal direction. Only then does the trade fire." & vbLf & vbLf &
                "Active from 7am to 1pm EST only — London open and the early New York session, where liquidity is at its best. " &
                "No scale-ins. The six-step gate is the entry and that is quite enough."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine("• SL = trigger wave extreme ± 3 ticks · TP = 2R · Partial TP at nearest swing (50 %)")
            LogLine("• 6-step gate: Anchor→Trigger (shallower)→Divergence→Dot→Engulfing candle")
            LogLine("• Time filter: 11:00–17:00 UTC (07:00–13:00 EST/EDT) — London + NY pre-market")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  LULT Divergence — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the BB Squeeze Scalper strategy for all 5 assets.
        ''' MaxScaleIns is forced to 0 regardless of profile (fast scalp, quick exit).
        ''' DurationHours = 8 760 so sessions never auto-expire.
        ''' </summary>
        Private Sub ApplyBbSqueezeScalper()
            TpDollarBracket = 8D         ' dollar fallback — ATR on 1-min bars is naturally smaller
            SlDollarBracket = 4D         ' dollar fallback

            _currentStrategy = New StrategyDefinition With {
                .Name = "BB Squeeze Scalper",
                .Indicator = StrategyIndicatorType.BbSqueezeScalper,
                .Condition = StrategyConditionType.BbSqueezeScalper,
                .IndicatorPeriod = 25,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 2.0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 1,
                .DurationHours = 8760,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = 0,       ' strategy override — fast scalp, quick exit
                .ScaleInAmount = 0D,
                .ScaleInLeverage = 1,
                .MinConfidencePct = _minConfidencePct,
                .PersonaName = _selectedProfileName
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  BB Squeeze Scalper  |  ADX≥{_adxThreshold:F0}  ${_capitalAtRisk:F0}×{_leverage}  1-min"
            ActiveStrategyKind = "BbSqueeze"
            StrategyDescription =
                "Two modes in one strategy, switching automatically depending on what the bands are doing." & vbLf & vbLf &
                "Mode A — Squeeze Breakout: the strategy watches how wide apart the Bollinger Bands are. " &
                "When they have been narrower than their 20-bar average for at least three bars in a row, volatility is coiling. " &
                "The moment price closes outside the band, with the 5-bar trend slope and RSI(7) both confirming the direction, " &
                "the breakout trade fires. In, target hit, out." & vbLf & vbLf &
                "Mode B — Band Bounce: when the bands are already wide and the market is volatile, the strategy flips to mean-reversion. " &
                "It waits for price to close outside the band, RSI(7) to reach an extreme below 25 or above 75, " &
                "and a rejection wick to cover at least 60% of the bar's range. The trade fades the move back toward the middle band." & vbLf & vbLf &
                "Runs on 1-minute bars, checking every 15 seconds. Fast in, fast out. No scale-ins."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 1-min bars · 1ct · ADX≥{_adxThreshold:F0} · 15s polling")
            LogLine("• Mode B (Band Bounce): %B < 0 or > 1 + RSI7 extreme + rejection wick ≥ 60%")
            LogLine("• Mode A (Squeeze Breakout): BBW < SMA(BBW,20) ≥3 bars + band break + EMA5 + RSI7")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  BB Squeeze Scalper — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the VIDYA Cross strategy for all 5 assets.
        ''' Variable Index Dynamic Average adapts its smoothing speed to CMO momentum —
        ''' fast in trending conditions, near-flat in chop. Entry fires on price crossover.
        ''' </summary>
        Private Sub ApplyVidya()
            TpDollarBracket = 20D
            SlDollarBracket = 10D

            _currentStrategy = New StrategyDefinition With {
                .Name = "VIDYA Cross",
                .Indicator = StrategyIndicatorType.Vidya,
                .Condition = StrategyConditionType.VidyaCross,
                .IndicatorPeriod = 14,
                .SecondaryPeriod = 9,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = _maxScaleIns,
                .ScaleInAmount = _capitalAtRisk,
                .ScaleInLeverage = _leverage,
                .MinConfidencePct = _minConfidencePct,
                .PersonaName = _selectedProfileName
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  VIDYA Cross  |  VIDYA(14) · CMO(9)  1ct  Conf:{_minConfidencePct}%"
            ActiveStrategyKind = "Vidya"
            StrategyDescription =
                "VIDYA is a moving average that changes its own speed depending on what the market is doing. " &
                "When momentum is strong, it moves fast and tracks price closely. " &
                "When the market is going sideways, it slows almost to a flat line — " &
                "giving far fewer false crossover signals than a standard moving average would." & vbLf & vbLf &
                "The rule is straightforward: when price closes above VIDYA, go Long. When price closes below it, go Short. " &
                "That adaptive quality is the whole edge. Most moving average crossovers fire constantly in choppy markets. " &
                "VIDYA's variable speed filters most of that noise automatically." & vbLf & vbLf &
                "Checked every 5 minutes. Works best on instruments that spend time in clear trends before consolidating, " &
                "then breaking out again — Oil, Gold, and index futures are natural fits."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · VIDYA(14) · CMO(9) · 1ct · Conf={_minConfidencePct}%")
            LogLine("• Long when close crosses above VIDYA · Short when close crosses below VIDYA")
            LogLine("• Dynamic alpha = EMA-alpha × |CMO| — fast in trends, flat in chop")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  VIDYA Cross — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the Naked Trader consensus strategy for all 5 assets.
        ''' 4-vote directional snapshot: EMA(9/21), MACD(8,17,9), DMI/ADX(14), VWAP.
        ''' High confidence fires with confidence 90; Medium fires at 60; Low is filtered.
        ''' </summary>
        Private Sub ApplyNakedTrader()
            TpDollarBracket = 20D
            SlDollarBracket = 10D

            _currentStrategy = New StrategyDefinition With {
                .Name = "Naked Trader",
                .Indicator = StrategyIndicatorType.NakedTrader,
                .Condition = StrategyConditionType.NakedTrader,
                .IndicatorPeriod = 14,
                .SecondaryPeriod = 9,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = _maxScaleIns,
                .ScaleInAmount = _capitalAtRisk,
                .ScaleInLeverage = _leverage,
                .MinConfidencePct = _minConfidencePct,
                .PersonaName = _selectedProfileName
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  Naked Trader  |  EMA·MACD·ADX·VWAP  1ct  Conf:{_minConfidencePct}%"
            ActiveStrategyKind = "NakedTrader"
            StrategyDescription =
                "Naked Trader strips away any single-indicator dependency and instead runs four independent gauges simultaneously. " &
                "Each bar, EMA(9/21), MACD(8,17,9), DMI/ADX(14), and VWAP each cast a directional vote — Long or Short. " &
                "The majority wins. If the vote is tied, it is resolved by the EMAs as a tiebreaker." & vbLf & vbLf &
                "Confidence is determined by two things: how strongly the votes agree, and whether the ADX reading confirms that a genuine trend exists. " &
                "If ADX is below 20 the result is Low confidence — the market is ranging and the engine stays flat. " &
                "With ADX between 20 and 25 and 3+ votes aligned the result is Medium. " &
                "With ADX at 25 or above and all votes aligned plus volume confirmation, the result is High." & vbLf & vbLf &
                "Only Medium and High confidence bars fire a trade. High signals carry confidence 90%; Medium signals carry 60%. " &
                "Low confidence is always filtered out regardless of direction. " &
                "Checked every 5 minutes."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · EMA(9/21) · MACD(8,17,9) · DMI/ADX(14) · VWAP · 1ct · Conf={_minConfidencePct}%")
            LogLine("• Fires on Medium (3/4 votes, ADX≥20) or High (all votes, ADX≥25) confidence")
            LogLine("• Low confidence / ADX < 20 → flat (no trade)")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  Naked Trader — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the Double Bubble Butt (Double Bollinger Band) strategy for all 5 assets.
        ''' Two concurrent BB sets over SMA(20): inner bands at ±1.0 SD, outer bands at ±2.0 SD.
        ''' Long when price closes into the Buy Zone (above upper 1.0 SD band).
        ''' Short when price closes into the Sell Zone (below lower 1.0 SD band).
        ''' Exit when price returns to the Neutral Zone (inside the 1.0 SD bands).
        ''' </summary>
        Private Sub ApplyDoubleBubbleButt()
            TpDollarBracket = 20D
            SlDollarBracket = 10D

            _currentStrategy = New StrategyDefinition With {
                .Name = "Double Bubble Butt",
                .Indicator = StrategyIndicatorType.DoubleBollingerBands,
                .Condition = StrategyConditionType.DoubleBubbleButt,
                .IndicatorPeriod = 20,
                .IndicatorMultiplier = 1.0,
                .SecondaryPeriod = 20,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .TradingStartHourUtc = 7,
                .TradingEndHourUtc = 20,
                .CapitalAtRisk = _capitalAtRisk,
                .Quantity = 1,
                .TpDollarBracket = _TpDollarBracket,
                .SlDollarBracket = _SlDollarBracket,
                .Leverage = _leverage,
                .AdxThreshold = _adxThreshold,
                .MaxScaleIns = _maxScaleIns,
                .ScaleInAmount = _capitalAtRisk,
                .ScaleInLeverage = _leverage,
                .MinConfidencePct = _minConfidencePct,
                .PersonaName = _selectedProfileName
            }

            HasParsedStrategy = True
            ActiveStrategyText = $"✔  Double Bubble Butt  |  BB(20,1SD)·BB(20,2SD)  EUR/USD  07–20 UTC  1ct  Conf:{_minConfidencePct}%"
            ActiveStrategyKind = "DoubleBubbleButt"
            StrategyDescription =
                "Double Bubble Butt uses two concurrent Bollinger Band sets plotted over the same SMA(20) baseline — " &
                "an inner set at ±1.0 standard deviation and an outer set at ±2.0 standard deviations — to divide " &
                "price action into three clearly defined trading zones." & vbLf & vbLf &
                "The Buy Zone sits between the upper 1.0 SD and upper 2.0 SD bands. When price closes into this area " &
                "it signals a strong upward trend with high momentum — the engine goes long and holds as long as price " &
                "stays above the upper 1.0 SD band." & vbLf & vbLf &
                "The Sell Zone sits between the lower 1.0 SD and lower 2.0 SD bands. A close into this zone signals " &
                "a powerful downward trend — the engine goes short and holds while price remains below the lower 1.0 SD band." & vbLf & vbLf &
                "The Neutral Zone occupies the space between the two inner bands. When price closes back inside this area " &
                "momentum has exhausted — any open position is exited. Developed by Kathy Lien. Checked every 5 minutes."

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · BB(20, 1.0 SD) inner · BB(20, 2.0 SD) outer · 1ct")
            LogLine("• Long: close > upper 1-SD band (Buy Zone) · Short: close < lower 1-SD band (Sell Zone)")
            LogLine("• Exit: close returns inside 1-SD bands (Neutral Zone)")
            LogLine($"• 5 independent sessions — {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}")
            LogLine("━━━  Double Bubble Butt — Hydra 5-Asset Monitor  ━━━")
        End Sub

        ' ── Command handlers ──────────────────────────────────────────────────────

        Private Sub ExecuteStart(param As Object)
            If _currentStrategy Is Nothing OrElse SelectedAccount Is Nothing Then Return

            IsRunning = True
            StatusText = $"● Running — {_currentStrategy.Name}"
            LogEntries.Clear()
            SyncLock _barCheckLock
                _barCheckCount = 0
            End SyncLock

            ' NOTE: CheckExistingPositionsAsync is NOT called here because it races with the
            ' engine's own startup position check (also in Task.Run), causing both to call
            ' OpenTrade for the same pre-existing position → displayed as ×2.
            ' The view-load call in LoadDataAsync (+ _wasPrePopulated) handles pre-existing
            ' positions correctly. The engine startup check fires within 3 seconds.

            ' Warn when running all 5 instruments simultaneously with scale-ins enabled.
            ' A correlated macro move can open N × (MaxScaleIns + 1) concurrent losing positions.
            If _currentStrategy.MaxScaleIns > 0 Then
                Dim maxPositions = Assets.Count * (_currentStrategy.MaxScaleIns + 1)
                LogLine($"⚠️  RISK: {Assets.Count} instruments × {_currentStrategy.MaxScaleIns + 1} max positions = up to {maxPositions} concurrent trades. " &
                        "A macro move against your bias can open all simultaneously. Consider disabling unused asset tiles.")
            End If

            For i = 0 To 4
                Dim assetVm = Assets(i)
                ' Deep-copy the shared template so each engine has its own independent state.
                ' All N-multiple, ADX, leverage and scale-in fields are copied from _currentStrategy
                ' (which was already stamped with the active profile's values by Apply*).
                Dim favContract = FavouriteContracts.TryGetBySymbol(assetVm.ContractId)
                Dim isGoldAsset = assetVm.Symbol.IndexOf("Gold", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                  assetVm.ContractId.IndexOf("MGC", StringComparison.OrdinalIgnoreCase) >= 0
                ' For MultiConfluence use the per-asset timeframe stored on FavouriteContract
                ' (Oil/MES=5m, Gold/M6E=10m, BTC=15m). All other strategies use the template value.
                Dim assetTimeframeMinutes As Integer = _currentStrategy.TimeframeMinutes
                If _currentStrategy.Condition = StrategyConditionType.MultiConfluence AndAlso
                   favContract IsNot Nothing AndAlso favContract.MultiConfluenceTimeframeMinutes > 0 Then
                    assetTimeframeMinutes = favContract.MultiConfluenceTimeframeMinutes
                End If
                Dim sd As New StrategyDefinition With {
                    .Name = _currentStrategy.Name,
                    .Indicator = _currentStrategy.Indicator,
                    .Condition = _currentStrategy.Condition,
                    .IndicatorPeriod = _currentStrategy.IndicatorPeriod,
                    .SecondaryPeriod = _currentStrategy.SecondaryPeriod,
                    .IndicatorMultiplier = _currentStrategy.IndicatorMultiplier,
                    .GoLongWhenBelowBands = _currentStrategy.GoLongWhenBelowBands,
                    .GoShortWhenAboveBands = _currentStrategy.GoShortWhenAboveBands,
                    .TimeframeMinutes = assetTimeframeMinutes,
                    .DurationHours = _currentStrategy.DurationHours,
                    .ContractId = assetVm.ContractId,
                    .AccountId = SelectedAccount.Id,
                    .CapitalAtRisk = _capitalAtRisk,
                    .Quantity = 1,
                    .TpDollarBracket = _currentStrategy.TpDollarBracket,
                    .SlDollarBracket = _currentStrategy.SlDollarBracket,
                    .Leverage = _currentStrategy.Leverage,
                    .AdxThreshold = _currentStrategy.AdxThreshold,
                    .MaxScaleIns = _currentStrategy.MaxScaleIns,
                    .ScaleInAmount = _currentStrategy.ScaleInAmount,
                    .ScaleInLeverage = _currentStrategy.ScaleInLeverage,
                    .MinConfidencePct = _minConfidencePct,
                    .SlMultipleOfN = _slMultipleOfN,
                    .TpMultipleOfN = _tpMultipleOfN,
                    .TickSize = If(favContract IsNot Nothing AndAlso favContract.PxTickSize > 0D, favContract.PxTickSize, 1D),
                    .TickValue = If(favContract IsNot Nothing AndAlso favContract.PxTickValue > 0D, favContract.PxTickValue, 1D),
                    .TradingWindowUtcStart = If(isGoldAsset, _currentStrategy.TradingWindowUtcStart, Nothing),
                    .TradingWindowUtcEnd = If(isGoldAsset, _currentStrategy.TradingWindowUtcEnd, Nothing),
                    .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                    .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                }
                _engines(i).Start(sd)
                LogLine($"[{assetVm.Symbol}] Session started")
            Next

            ' Start force-close monitor loop if enabled
            If ForceCloseEnabled Then
                _forceCloseCts = New CancellationTokenSource()
                _forceCloseTask = Task.Run(Function() ForceCloseMonitorLoopAsync(_forceCloseCts.Token))
            End If
        End Sub

        Private Sub ExecuteStop(param As Object)
            For i = 0 To 4
                _engines(i).[Stop]()
            Next
            ' Stop force-close monitor
            Try
                If _forceCloseCts IsNot Nothing Then
                    _forceCloseCts.Cancel()
                End If
            Catch
            End Try
            IsRunning = False
            StatusText = "● Not running"
        End Sub

        ' ── Helpers ───────────────────────────────────────────────────────────────

        Private Sub LogLine(message As String)
            LogEntries.Insert(0, $"[{DateTime.Now:HH:mm}] {message}")
            Do While LogEntries.Count > 500
                LogEntries.RemoveAt(LogEntries.Count - 1)
            Loop
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

        ''' <summary>
        ''' Background loop that periodically checks each asset for live positions via
        ''' the scoped IOrderService. If ForceClose is enabled and a live position's
        ''' unrealised P&L is ≥ ForceCloseAmount the position is flattened immediately
        ''' by calling FlattenContractAsync on that contract.
        ''' </summary>
        Private Async Function ForceCloseMonitorLoopAsync(ct As CancellationToken) As Task
            Try
                While Not ct.IsCancellationRequested
                    Try
                        If Not ForceCloseEnabled Then
                            Await Task.Delay(TimeSpan.FromSeconds(5), ct)
                            Continue While
                        End If

                        Dim accountId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
                        For i = 0 To 4
                            Dim index = i
                            If ct.IsCancellationRequested Then Exit For
                            Try
                                Dim assetVm = Assets(index)

                                ' ── Prefer the engine's real-time P&L (MarketHub sub-second quotes,
                                '    refreshed every ~5 s via PositionSynced).  Only fall back to the
                                '    REST 15-second bar when no engine P&L is available yet (e.g. the
                                '    position was opened externally before the engine started).
                                Dim pnl As Decimal
                                If assetVm.HasLivePnl Then
                                    pnl = assetVm.LastLivePnl
                                Else
                                    ' Fallback: independent REST snapshot (same as the old path).
                                    Dim scope = _assetScopes(index)
                                    If scope Is Nothing Then Continue For
                                    Dim orderService = scope.ServiceProvider.GetRequiredService(Of IOrderService)()
                                    Dim snapshot = Await orderService.GetLivePositionSnapshotAsync(accountId, assetVm.ContractId, Nothing, ct)
                                    If snapshot Is Nothing Then Continue For
                                    pnl = snapshot.UnrealizedPnlUsd
                                    If pnl = 0D Then
                                        Dim ingestionSvc = scope.ServiceProvider.GetRequiredService(Of IBarIngestionService)()
                                        Dim currentPrice = Await ingestionSvc.GetLatestPriceAsync(assetVm.ContractId, ct)
                                        If currentPrice > 0D Then
                                            Dim favContract = FavouriteContracts.TryGetBySymbol(assetVm.ContractId)
                                            Dim dpp As Decimal = If(favContract IsNot Nothing,
                                                                    CDec(favContract.GetPointValue(_session.ActiveBroker)), 1D)
                                            pnl = Math.Round((currentPrice - snapshot.OpenRate) * dpp * snapshot.Amount *
                                                             If(snapshot.IsBuy, 1D, -1D), 2)
                                        End If
                                    End If
                                End If

                                If pnl >= ForceCloseAmount Then
                                    ' Flatten the contract regardless of how it was opened
                                    Dim scope2 = _assetScopes(index)
                                    If scope2 Is Nothing Then Continue For
                                    Dim orderService2 = scope2.ServiceProvider.GetRequiredService(Of IOrderService)()
                                    Dim ok = Await orderService2.FlattenContractAsync(accountId, assetVm.ContractId, ct)
                                    Dispatch(Sub() LogLine($"[{assetVm.Symbol}] Force-close triggered — P&L=${pnl:F2} >= ${ForceCloseAmount:F2} — closed: {ok}"))
                                End If
                            Catch ex As OperationCanceledException
                                Exit For
                            Catch ex As Exception
                                ' Log and continue — non-fatal for other assets
                                Dispatch(Sub() LogLine($"ForceClose check failed for {Assets(index).Symbol}: {ex.Message}"))
                            End Try
                        Next
                    Catch ex As OperationCanceledException
                        Exit While
                    Catch ex As Exception
                        Dispatch(Sub() LogLine($"ForceClose monitor error: {ex.Message}"))
                    End Try
                    ' Poll every 15 seconds
                    Await Task.Delay(TimeSpan.FromSeconds(15), ct)
                End While
            Catch ex As OperationCanceledException
                ' expected on shutdown
            Catch ex As Exception
                Dispatch(Sub() LogLine($"ForceClose monitor fatal: {ex.Message}"))
            End Try
        End Function

        ' ── Per-asset bracket management ─────────────────────────────────────────

        ''' <summary>
        ''' Manually closes the live position on asset <paramref name="index"/> by calling
        ''' FlattenContractAsync on that asset's scoped IOrderService.
        ''' The asset tile is reset to "No position" whether or not the API call succeeds
        ''' — the engine will self-correct on the next broker sync if the close was silently refused.
        ''' </summary>
        Private Async Sub CloseAssetPositionAsync(index As Integer)
            Try
                Dim scope = _assetScopes(index)
                If scope Is Nothing Then Return
                Dim assetVm = Assets(index)
                Dim accountId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
                Dim orderService = scope.ServiceProvider.GetRequiredService(Of IOrderService)()
                Dim ok = Await orderService.FlattenContractAsync(accountId, assetVm.ContractId)
                Dispatch(Sub()
                             assetVm.CloseTrade("Manual close")
                             LogLine($"[{assetVm.Symbol}] ✕ Manual close — {If(ok, "OK", "FAILED")}")
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() LogLine($"[{Assets(index).Symbol}] ✕ Close error: {ex.Message}"))
            End Try
        End Sub

        ''' <summary>
        ''' Tightens the resting stop-loss on asset <paramref name="index"/> by 10% of its
        ''' distance to entry, guaranteeing at least 1 tick of movement per click.
        ''' Uses the numeric SL/TP prices tracked by ApplySl; falls back to 20-tick estimate
        ''' from the broker snapshot entry price when no bracket has been recorded yet.
        ''' </summary>
        Private Async Sub NudgeAssetBracketAsync(index As Integer)
            Try
                Dim scope = _assetScopes(index)
                If scope Is Nothing Then Return
                Dim assetVm = Assets(index)
                Dim accountId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
                Dim orderService = scope.ServiceProvider.GetRequiredService(Of IOrderService)()

                Dim snapshot = Await orderService.GetLivePositionSnapshotAsync(accountId, assetVm.ContractId)
                If snapshot Is Nothing Then
                    Dispatch(Sub() LogLine($"[{assetVm.Symbol}] ⟳ Nudge: no live position found"))
                    Return
                End If

                Dim fav = FavouriteContracts.TryGetBySymbol(assetVm.ContractId)
                Dim tickSize As Decimal = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0, fav.PxTickSize, 0.25D)
                Dim isBuy = snapshot.IsBuy
                Dim entryPrice = snapshot.OpenRate

                ' Resolve SL — use tracked numeric value; fall back to 20-tick estimate.
                Dim resolvedSl = assetVm.CurrentSlPrice
                If resolvedSl <= 0D Then
                    resolvedSl = If(isBuy, entryPrice - 20 * tickSize, entryPrice + 20 * tickSize)
                    Dispatch(Sub() LogLine($"[{assetVm.Symbol}] ⟳ Nudge: SL not tracked — using 20t fallback ({resolvedSl:F4})"))
                End If

                ' 10% closer to entry, minimum 1 tick.
                Dim slGap = If(isBuy, entryPrice - resolvedSl, resolvedSl - entryPrice)
                Dim slStep = Math.Max(Math.Round(slGap * 0.1D / tickSize) * tickSize, tickSize)
                Dim newSl = Math.Round(If(isBuy, resolvedSl + slStep, resolvedSl - slStep) / tickSize) * tickSize

                ' Nudge TP inward by same 10% if tracked.
                Dim newTp As Decimal? = Nothing
                Dim resolvedTp = assetVm.CurrentTpPrice
                If resolvedTp > 0D Then
                    Dim tpGap = If(isBuy, resolvedTp - entryPrice, entryPrice - resolvedTp)
                    Dim tpStep = Math.Max(Math.Round(tpGap * 0.1D / tickSize) * tickSize, tickSize)
                    newTp = Math.Round(If(isBuy, resolvedTp - tpStep, resolvedTp + tpStep) / tickSize) * tickSize
                End If

                Dim ok = Await orderService.EditPositionSlTpAsync(snapshot.PositionId, newSl, newTp)
                Dispatch(Sub()
                             If ok Then
                                 assetVm.ApplySl(newSl, If(newTp.HasValue, newTp.Value, resolvedTp), False, assetVm.IsFreeRide)
                                 LogLine($"[{assetVm.Symbol}] ⟳ Nudge SL → {newSl:F4}" &
                                         If(newTp.HasValue, $"  TP → {newTp.Value:F4}", ""))
                             Else
                                 LogLine($"[{assetVm.Symbol}] ⟳ Nudge failed — ensure a resting SL bracket exists on the broker")
                             End If
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() LogLine($"[{Assets(index).Symbol}] ⟳ Nudge error: {ex.Message}"))
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _session.AccountChanged, AddressOf OnSessionAccountChanged
                For i = 0 To 4
                    Try
                        _engines(i)?.Dispose()
                    Catch
                    End Try
                    Try
                        _assetScopes(i)?.Dispose()
                    Catch
                    End Try
                Next
                Try
                    If _forceCloseCts IsNot Nothing Then
                        _forceCloseCts.Cancel()
                    End If
                Catch
                End Try
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
