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
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base
Imports TopStepTrader.Services.Personas

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Single-asset, multi-strategy coordinator ViewModel.
    ''' Up to 9 independent StrategyExecutionEngine instances share one FavouriteContract.
    '''
    ''' Coordinator rules (answered in design Q&amp;A):
    '''   Q1. Danger Zone tightening: snap SL to breakeven (entry price).
    '''   Q2. Danger Zone persists until position closes, OR the triggering engine
    '''       fires a same-direction (reversal) confirming signal.
    '''   Q3. Asset selector: dropdown of FavouriteContracts for the active broker.
    '''   Q4. Strategy enable state resets to all-off on each launch.
    '''   Q5. Each strategy uses its own SlN/TpN defaults.
    ''' </summary>
    Public Class AssetBassettViewModel
        Inherits TradingViewModelBase
        Implements IDisposable

        ' ── Constants ─────────────────────────────────────────────────────────────
        Private Const MaxSlots As Integer = 7

        ' ── Dependencies ──────────────────────────────────────────────────────────
        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService

        ' ── Per-engine resources (allocated on Start, released on Stop/Dispose) ───
        Private ReadOnly _engineScopes(MaxSlots - 1) As IServiceScope
        Private ReadOnly _engines(MaxSlots - 1) As StrategyExecutionEngine

        ' ── Coordinator state ─────────────────────────────────────────────────────
        ' All fields below are guarded by _coordLock.
        Private ReadOnly _coordLock As New Object()
        Private _positionOwnerIdx As Integer = -1    ' slot index; -1 = no open position
        Private _positionSide As OrderSide
        Private _openTradeCount As Integer = 0
        Private _dangerZoneActive As Boolean = False
        Private _dangerZoneSourceIdx As Integer = -1  ' slot that triggered DangerZone

        ' ── Asset selection ────────────────────────────────────────────────────────
        Public ReadOnly Property AvailableContracts As New ObservableCollection(Of FavouriteContract)

        Private _selectedContract As FavouriteContract
        Public Property SelectedContract As FavouriteContract
            Get
                Return _selectedContract
            End Get
            Set(value As FavouriteContract)
                If SetProperty(_selectedContract, value) Then
                    NotifyPropertyChanged(NameOf(IsFormReady))
                    NotifyPropertyChanged(NameOf(SelectedContractName))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Public ReadOnly Property SelectedContractName As String
            Get
                Return If(_selectedContract?.Name, "—")
            End Get
        End Property

        ' ── Risk profile ──────────────────────────────────────────────────────────
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
        Private _selectedAtrTier As String = "Standard"
        Private _slMultipleOfN As Decimal = 1.0D   ' Damian persona default (matches backtest winner)
        Private _tpMultipleOfN As Decimal = 2.0D   ' Damian persona default (matches backtest winner)

        Public Property SlMultipleOfN As Decimal
            Get
                Return _slMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_slMultipleOfN, Math.Max(0.1D, value))
            End Set
        End Property

        Public Property TpMultipleOfN As Decimal
            Get
                Return _tpMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_tpMultipleOfN, Math.Max(0.1D, value))
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
                Case "Opening Range Breakout" : Return Core.Enums.StrategyConditionType.OpeningRangeBreakout
                Case Else                     : Return Nothing
            End Select
        End Function

        Private _minConfidencePct As Integer = 65
        Public Property MinConfidencePct As Integer
            Get
                Return _minConfidencePct
            End Get
            Set(value As Integer)
                SetProperty(_minConfidencePct, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        Private _adxThreshold As Single = 20.0F
        Public Property AdxThreshold As Single
            Get
                Return _adxThreshold
            End Get
            Set(value As Single)
                SetProperty(_adxThreshold, Math.Max(1.0F, value))
            End Set
        End Property

        Private _maxScaleIns As Integer = 2
        Public Property MaxScaleIns As Integer
            Get
                Return _maxScaleIns
            End Get
            Set(value As Integer)
                SetProperty(_maxScaleIns, Math.Max(0, value))
            End Set
        End Property

        ' ── Running state ─────────────────────────────────────────────────────────
        Private _isRunning As Boolean = False
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                If SetProperty(_isRunning, value) Then
                    NotifyPropertyChanged(NameOf(IsNotRunning))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Public ReadOnly Property IsNotRunning As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        ' ── Danger Zone indicator ─────────────────────────────────────────────────
        Private _isDangerZoneVisible As Boolean = False
        Public Property IsDangerZoneVisible As Boolean
            Get
                Return _isDangerZoneVisible
            End Get
            Set(value As Boolean)
                SetProperty(_isDangerZoneVisible, value)
            End Set
        End Property

        ' ── Status / Log ──────────────────────────────────────────────────────────
        Private _statusText As String = "● Select an asset and enable strategies"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Public Property LogEntries As New ObservableCollection(Of String)

        ' ── IsFormReady ───────────────────────────────────────────────────────────
        Public Overrides ReadOnly Property IsFormReady As Boolean
            Get
                Return SelectedAccount IsNot Nothing AndAlso _selectedContract IsNot Nothing
            End Get
        End Property

        ' ── Strategy enable toggles — 9 slots (reset to False on launch per Q4) ──

        Private _isEmaRsiEnabled As Boolean = False
        Public Property IsEmaRsiEnabled As Boolean
            Get
                Return _isEmaRsiEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isEmaRsiEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Private _isMultiConfluenceEnabled As Boolean = False
        Public Property IsMultiConfluenceEnabled As Boolean
            Get
                Return _isMultiConfluenceEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isMultiConfluenceEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Private _isLultEnabled As Boolean = False
        Public Property IsLultEnabled As Boolean
            Get
                Return _isLultEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isLultEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Private _isBbSqueezeEnabled As Boolean = False
        Public Property IsBbSqueezeEnabled As Boolean
            Get
                Return _isBbSqueezeEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isBbSqueezeEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Private _isVidyaEnabled As Boolean = False
        Public Property IsVidyaEnabled As Boolean
            Get
                Return _isVidyaEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isVidyaEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Private _isNakedTraderEnabled As Boolean = False
        Public Property IsNakedTraderEnabled As Boolean
            Get
                Return _isNakedTraderEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isNakedTraderEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Private _isDoubleBubbleButtEnabled As Boolean = False
        Public Property IsDoubleBubbleButtEnabled As Boolean
            Get
                Return _isDoubleBubbleButtEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isDoubleBubbleButtEnabled, value) Then NotifyStrategyChanged()
            End Set
        End Property

        Public ReadOnly Property HasAnyStrategyEnabled As Boolean
            Get
                Return _isEmaRsiEnabled OrElse _isMultiConfluenceEnabled OrElse
                       _isLultEnabled OrElse _isBbSqueezeEnabled OrElse
                       _isVidyaEnabled OrElse _isNakedTraderEnabled OrElse
                       _isDoubleBubbleButtEnabled
            End Get
        End Property

        Private Sub NotifyStrategyChanged()
            NotifyPropertyChanged(NameOf(HasAnyStrategyEnabled))
            RelayCommand.RaiseCanExecuteChanged()
        End Sub

        ' ── Commands ──────────────────────────────────────────────────────────────
        Public ReadOnly Property SelectProfileCommand As RelayCommand
        Public ReadOnly Property SelectAtrTierCommand As RelayCommand
        Public ReadOnly Property ToggleEmaRsiCommand As RelayCommand
        Public ReadOnly Property ToggleMultiConfluenceCommand As RelayCommand
        Public ReadOnly Property ToggleLultCommand As RelayCommand
        Public ReadOnly Property ToggleBbSqueezeCommand As RelayCommand
        Public ReadOnly Property ToggleVidyaCommand As RelayCommand
        Public ReadOnly Property ToggleNakedTraderCommand As RelayCommand
        Public ReadOnly Property ToggleDoubleBubbleButtCommand As RelayCommand
        Public ReadOnly Property StartCommand As RelayCommand
        Public ReadOnly Property StopCommand As RelayCommand

        Private _disposed As Boolean = False

        ' ── Constructor ───────────────────────────────────────────────────────────

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       accountService As IAccountService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService)
            _scopeFactory = scopeFactory
            _accountService = accountService
            _session = session
            _personaService = personaService

            ' Populate asset dropdown — all FavouriteContracts (Q3)
            For Each fc In FavouriteContracts.GetDefaults()
                AvailableContracts.Add(fc)
            Next
            If AvailableContracts.Count > 0 Then
                _selectedContract = AvailableContracts(0)
            End If

            AddHandler _session.AccountChanged, AddressOf OnSessionAccountChanged

            SelectProfileCommand = New RelayCommand(
                Sub(p) ApplyRiskProfile(CStr(p)),
                Function(p) IsNotRunning)

            SelectAtrTierCommand = New RelayCommand(
                Sub(p) ApplyAtrTier(CStr(p)),
                Function(p) IsNotRunning)

            ToggleEmaRsiCommand = New RelayCommand(
                Sub(p) IsEmaRsiEnabled = Not IsEmaRsiEnabled,
                Function(p) IsNotRunning)

            ToggleMultiConfluenceCommand = New RelayCommand(
                Sub(p) IsMultiConfluenceEnabled = Not IsMultiConfluenceEnabled,
                Function(p) IsNotRunning)

            ToggleLultCommand = New RelayCommand(
                Sub(p) IsLultEnabled = Not IsLultEnabled,
                Function(p) IsNotRunning)

            ToggleBbSqueezeCommand = New RelayCommand(
                Sub(p) IsBbSqueezeEnabled = Not IsBbSqueezeEnabled,
                Function(p) IsNotRunning)

            ToggleVidyaCommand = New RelayCommand(
                Sub(p) IsVidyaEnabled = Not IsVidyaEnabled,
                Function(p) IsNotRunning)

            ToggleNakedTraderCommand = New RelayCommand(
                Sub(p) IsNakedTraderEnabled = Not IsNakedTraderEnabled,
                Function(p) IsNotRunning)

            ToggleDoubleBubbleButtCommand = New RelayCommand(
                Sub(p) IsDoubleBubbleButtEnabled = Not IsDoubleBubbleButtEnabled,
                Function(p) IsNotRunning)

            StartCommand = New RelayCommand(
                AddressOf ExecuteStart,
                Function(p) HasAnyStrategyEnabled AndAlso IsNotRunning AndAlso IsFormReady)

            StopCommand = New RelayCommand(
                AddressOf ExecuteStop,
                Function(p) IsRunning)
            AddHandler _session.AutoExecutionChanged, AddressOf OnAutoExecutionChanged
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
                Dispatch(Sub() StatusText = $"⚠ Load error: {ex.Message}")
            End Try
        End Sub

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

        ' ── Session account sync ──────────────────────────────────────────────────

        Private Sub OnSessionAccountChanged(sender As Object, account As Account)
            Dispatch(Sub()
                         If account IsNot Nothing Then
                             Dim match = Accounts.FirstOrDefault(Function(a) a.Id = account.Id)
                             If match IsNot Nothing Then SelectedAccount = match
                         End If
                     End Sub)
        End Sub

        ' ── Risk profile ──────────────────────────────────────────────────────────

        Private Sub ApplyRiskProfile(profileName As String)
            Dim profile = _personaService.GetProfile(profileName)

            _selectedProfileName = profileName
            AdxThreshold = profile.AdxThreshold
            MaxScaleIns = profile.MaxScaleIns
            MinConfidencePct = profile.DefaultConfidencePct

            NotifyPropertyChanged(NameOf(IsLewisSelected))
            NotifyPropertyChanged(NameOf(IsDamianSelected))
            NotifyPropertyChanged(NameOf(IsJoeSelected))
        End Sub

        ''' <summary>
        ''' Applies the selected ATR risk tier to SlMultipleOfN / TpMultipleOfN.
        ''' All three tiers maintain a 1:2 R:R ratio. The active persona controls all other parameters.
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
                Case Else   ' "Standard"
                    _selectedAtrTier = "Standard"
                    SlMultipleOfN = 1.5D
                    TpMultipleOfN = 3.0D
            End Select
            NotifyPropertyChanged(NameOf(IsAtrTightSelected))
            NotifyPropertyChanged(NameOf(IsAtrStandardSelected))
            NotifyPropertyChanged(NameOf(IsAtrWideSelected))
        End Sub

        ' ── Strategy definitions (Q5: each uses own SlN/TpN defaults) ──

        Private Function BuildStrategyDefinition(slotIdx As Integer) As StrategyDefinition
            Dim fav = _selectedContract
            Dim contractId = fav.PxContractId
            Dim tickSz = If(fav.PxTickSize > 0D, fav.PxTickSize, 1D)
            Dim tickVal = If(fav.PxTickValue > 0D, fav.PxTickValue, 1D)
            Dim accountId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)

            Select Case slotIdx
                Case 0  ' EMA/RSI Combined — 5-min, SlN=1.0, TpN=2.0
                    Return New StrategyDefinition With {
                        .Name = "EMA/RSI Combined", .ContractId = contractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.EmaRsiCombined,
                        .Condition = StrategyConditionType.EmaRsiWeightedScore,
                        .IndicatorPeriod = 50, .SecondaryPeriod = 0, .IndicatorMultiplier = 0,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 5, .DurationHours = 8760,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = _maxScaleIns,
                        .MinConfidencePct = _minConfidencePct, .TickSize = tickSz, .TickValue = tickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case 1  ' Multi-Confluence — 15-min; uses ATR tier SL/TP multiples
                    Return New StrategyDefinition With {
                        .Name = "Multi-Confluence", .ContractId = contractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.MultiConfluence,
                        .Condition = StrategyConditionType.MultiConfluence,
                        .IndicatorPeriod = 80, .SecondaryPeriod = 0, .IndicatorMultiplier = 0,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 15, .DurationHours = 8760,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = _maxScaleIns,
                        .MinConfidencePct = _minConfidencePct, .TickSize = tickSz, .TickValue = tickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case 2  ' LULT Divergence — 5-min; uses ATR tier SL/TP multiples, no scale-in
                    Return New StrategyDefinition With {
                        .Name = "LULT Divergence", .ContractId = contractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.LultDivergence,
                        .Condition = StrategyConditionType.LultDivergence,
                        .IndicatorPeriod = 100, .SecondaryPeriod = 0, .IndicatorMultiplier = 0,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 5, .DurationHours = 8760,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = 0,
                        .MinConfidencePct = _minConfidencePct, .TickSize = tickSz, .TickValue = tickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case 3  ' BB Squeeze Scalper — 5-min, SlN=0.8, TpN=1.6, no scale-in
                    Return New StrategyDefinition With {
                        .Name = "BB Squeeze Scalper", .ContractId = contractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.BbSqueezeScalper,
                        .Condition = StrategyConditionType.BbSqueezeScalper,
                        .IndicatorPeriod = 20, .SecondaryPeriod = 0, .IndicatorMultiplier = 2.0,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 5, .DurationHours = 8760,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = 0,
                        .MinConfidencePct = _minConfidencePct, .TickSize = tickSz, .TickValue = tickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case 4  ' VIDYA Cross — 15-min; uses ATR tier SL/TP multiples
                    Return New StrategyDefinition With {
                        .Name = "VIDYA Cross", .ContractId = contractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.Vidya,
                        .Condition = StrategyConditionType.VidyaCross,
                        .IndicatorPeriod = 20, .SecondaryPeriod = 50, .IndicatorMultiplier = 0,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 15, .DurationHours = 8760,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = _maxScaleIns,
                        .MinConfidencePct = _minConfidencePct, .TickSize = tickSz, .TickValue = tickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case 5  ' Naked Trader — 5-min; uses ATR tier SL/TP multiples
                    Return New StrategyDefinition With {
                        .Name = "Naked Trader", .ContractId = contractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.NakedTrader,
                        .Condition = StrategyConditionType.NakedTrader,
                        .IndicatorPeriod = 14, .SecondaryPeriod = 9, .IndicatorMultiplier = 0,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 5, .DurationHours = 8760,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = _maxScaleIns,
                        .MinConfidencePct = _minConfidencePct, .TickSize = tickSz, .TickValue = tickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case 6  ' Double Bubble Butt — 5-min; inner BB 1.0 SD / outer BB 2.0 SD; FX micro (London+NY session)
                    ' DBB was designed by Kathy Lien for FX on daily/4H charts.
                    ' Pin this slot to M6J (Micro USD/JPY) — the remaining FX micro in the instrument universe.
                    ' Trading window 07:00–20:00 UTC covers London open through NY close.
                    Dim eurUsdFav = FavouriteContracts.TryGetBySymbolResolved("M6J")
                    Dim dbbContractId = If(eurUsdFav IsNot Nothing, eurUsdFav.PxContractId, contractId)
                    Dim dbbTickSz = If(eurUsdFav IsNot Nothing AndAlso eurUsdFav.PxTickSize > 0D, eurUsdFav.PxTickSize, tickSz)
                    Dim dbbTickVal = If(eurUsdFav IsNot Nothing AndAlso eurUsdFav.PxTickValue > 0D, eurUsdFav.PxTickValue, tickVal)
                    Return New StrategyDefinition With {
                        .Name = "Double Bubble Butt", .ContractId = dbbContractId, .AccountId = accountId,
                        .Indicator = StrategyIndicatorType.DoubleBollingerBands,
                        .Condition = StrategyConditionType.DoubleBubbleButt,
                        .IndicatorPeriod = 20, .IndicatorMultiplier = 1.0, .SecondaryPeriod = 20,
                        .GoLongWhenBelowBands = True, .GoShortWhenAboveBands = True,
                        .TimeframeMinutes = 5, .DurationHours = 8760,
                        .TradingStartHourUtc = 7, .TradingEndHourUtc = 20,
                        .Quantity = 1,
                        .SlMultipleOfN = _slMultipleOfN, .TpMultipleOfN = _tpMultipleOfN,
                        .AdxThreshold = _adxThreshold,
                        .MaxScaleIns = 0,
                        .MinConfidencePct = _minConfidencePct, .TickSize = dbbTickSz, .TickValue = dbbTickVal,
                        .ExtendTpOnClose = _extendTpOnClose, .PersonaName = _selectedProfileName,
                        .TrendingStrategyOverride = ParseRegimeOverride(_selectedTrendingOverrideName),
                        .RangingStrategyOverride = ParseRegimeOverride(_selectedRangingOverrideName)
                    }
                Case Else
                    Throw New ArgumentOutOfRangeException(NameOf(slotIdx), $"Slot {slotIdx} is out of range")
            End Select
        End Function

        Private Shared Function SlotLabel(slotIdx As Integer) As String
            Select Case slotIdx
                Case 0 : Return "EmaRsi"
                Case 1 : Return "MultiConf"
                Case 2 : Return "LULT"
                Case 3 : Return "BbSqueeze"
                Case 4 : Return "Vidya"
                Case 5 : Return "NakedTrader"
                Case 6 : Return "DoubleBubbleButt"
                Case Else : Return $"Engine{slotIdx}"
            End Select
        End Function

        Private Function IsSlotEnabled(slotIdx As Integer) As Boolean
            Select Case slotIdx
                Case 0 : Return _isEmaRsiEnabled
                Case 1 : Return _isMultiConfluenceEnabled
                Case 2 : Return _isLultEnabled
                Case 3 : Return _isBbSqueezeEnabled
                Case 4 : Return _isVidyaEnabled
                Case 5 : Return _isNakedTraderEnabled
                Case 6 : Return _isDoubleBubbleButtEnabled
                Case Else : Return False
            End Select
        End Function

        ' ── Command handlers ──────────────────────────────────────────────────────

        Private Sub ExecuteStart(param As Object)
            If _selectedContract Is Nothing OrElse SelectedAccount Is Nothing Then Return

            IsRunning = True
            StatusText = $"● Monitoring — {_selectedContract.Name}"
            LogEntries.Clear()

            SyncLock _coordLock
                _positionOwnerIdx = -1
                _openTradeCount = 0
                _dangerZoneActive = False
                _dangerZoneSourceIdx = -1
            End SyncLock
            IsDangerZoneVisible = False

            Dim enabledCount As Integer = 0
            For i = 0 To MaxSlots - 1
                If Not IsSlotEnabled(i) Then Continue For
                enabledCount += 1
                _engineScopes(i) = _scopeFactory.CreateScope()
                _engines(i) = _engineScopes(i).ServiceProvider.GetRequiredService(Of StrategyExecutionEngine)()

                Dim capturedIdx = i
                _engines(i).IsOrderingAllowed = Function()
                    SyncLock _coordLock
                        ' Allow entry when no position is open, or this is the owning engine (scale-in)
                        Return _positionOwnerIdx < 0 OrElse _positionOwnerIdx = capturedIdx
                    End SyncLock
                End Function

                WireEngineEvents(_engines(i), i)
                _engines(i).Start(BuildStrategyDefinition(i))
                LogLine($"[{SlotLabel(i)}] ▶ Session started")
            Next

            LogLine($"[Coordinator] {enabledCount} strategy engine(s) active on {_selectedContract.Name}")
        End Sub

        Private Sub ExecuteStop(param As Object)
            For i = 0 To MaxSlots - 1
                If _engines(i) IsNot Nothing Then
                    Try
                        _engines(i).[Stop]()
                    Catch
                    End Try
                End If
            Next
            IsRunning = False
            IsDangerZoneVisible = False
            StatusText = "● Not running"
        End Sub

        ' ── Engine event wiring ───────────────────────────────────────────────────

        Private Sub WireEngineEvents(engine As StrategyExecutionEngine, slotIdx As Integer)

            AddHandler engine.LogMessage,
                Sub(s As Object, msg As String)
                    Dispatch(Sub() LogLine($"[{SlotLabel(slotIdx)}] {msg}"))
                End Sub

            AddHandler engine.ExecutionStopped,
                Sub(s As Object, reason As String)
                    Dispatch(Sub() LogLine($"[{SlotLabel(slotIdx)}] ■ Stopped: {reason}"))
                End Sub

            AddHandler engine.TradeOpened,
                Sub(s As Object, e As TradeOpenedEventArgs)
                    Dim count As Integer
                    SyncLock _coordLock
                        If _positionOwnerIdx < 0 Then
                            ' First engine to fire — becomes the owner
                            _positionOwnerIdx = slotIdx
                            _positionSide = e.Side
                            _openTradeCount = 1
                        ElseIf _positionOwnerIdx = slotIdx Then
                            ' Scale-in from the owning engine itself
                            _openTradeCount += 1
                        End If
                        count = _openTradeCount
                    End SyncLock
                    Dispatch(Sub()
                                 LogLine($"[{SlotLabel(slotIdx)}] 🟢 Trade opened — {e.Side} @ {e.EntryPrice:F4} | {CInt(e.Amount)}ct")
                                 If count > 1 Then
                                     LogLine($"[Coordinator] ➕ Scale-in #{count} confirmed")
                                 End If
                                 Dim ownerIdx As Integer
                                 Dim side As OrderSide
                                 SyncLock _coordLock
                                     ownerIdx = _positionOwnerIdx
                                     side = _positionSide
                                 End SyncLock
                                 StatusText = $"● In Trade — {_selectedContract?.Name} {side} × {count}  [{SlotLabel(ownerIdx)}]"
                             End Sub)
                End Sub

            AddHandler engine.TradeClosed,
                Sub(s As Object, e As TradeClosedEventArgs)
                    Dim wasOwner As Boolean
                    Dim ownerLabel As String
                    SyncLock _coordLock
                        wasOwner = (_positionOwnerIdx = slotIdx)
                        ownerLabel = SlotLabel(slotIdx)
                        If wasOwner Then
                            _positionOwnerIdx = -1
                            _openTradeCount = 0
                            _dangerZoneActive = False
                            _dangerZoneSourceIdx = -1
                        End If
                    End SyncLock
                    Dispatch(Sub()
                                 LogLine($"[{ownerLabel}] 🔴 Closed — {e.ExitReason} | P&L={If(e.PnL >= 0D, "+", "")}${e.PnL:F2}")
                                 If wasOwner Then
                                     IsDangerZoneVisible = False
                                     StatusText = $"● Monitoring — {_selectedContract?.Name}"
                                 End If
                             End Sub)
                End Sub

            AddHandler engine.PositionSynced,
                Sub(s As Object, e As PositionSyncedEventArgs)
                    Dim isOwner As Boolean
                    Dim side As OrderSide
                    SyncLock _coordLock
                        isOwner = (_positionOwnerIdx = slotIdx)
                        side = _positionSide
                    End SyncLock
                    If Not isOwner Then Return
                    Dispatch(Sub()
                                 If Not _isDangerZoneVisible Then
                                     StatusText = $"● In Trade — {_selectedContract?.Name} {side}  P&L: {If(e.UnrealizedPnlUsd >= 0D, "+", "")}${e.UnrealizedPnlUsd:F2}"
                                 End If
                             End Sub)
                End Sub

            ' ── Coordinator: inspect non-owner ConfidenceUpdated events ──────────
            AddHandler engine.ConfidenceUpdated,
                Sub(s As Object, e As ConfidenceUpdatedEventArgs)
                    Dim ownerIdx As Integer
                    Dim side As OrderSide
                    Dim dzActive As Boolean
                    Dim dzSourceIdx As Integer
                    Dim scaleIns As Integer
                    SyncLock _coordLock
                        ownerIdx = _positionOwnerIdx
                        side = _positionSide
                        dzActive = _dangerZoneActive
                        dzSourceIdx = _dangerZoneSourceIdx
                        scaleIns = _openTradeCount
                    End SyncLock

                    ' Only act when a position is open and this is NOT the owning engine
                    If ownerIdx < 0 OrElse ownerIdx = slotIdx Then Return

                    Dim hasBull = e.UpPct >= _minConfidencePct AndAlso e.AdxGatePassed
                    Dim hasBear = e.DownPct >= _minConfidencePct

                    If Not hasBull AndAlso Not hasBear Then
                        ' Signal dropped — if this was the DangerZone source, clear it (Q2: signal reversal)
                        If dzActive AndAlso dzSourceIdx = slotIdx Then
                            SyncLock _coordLock
                                _dangerZoneActive = False
                                _dangerZoneSourceIdx = -1
                                If _engines(ownerIdx) IsNot Nothing Then
                                    _engines(ownerIdx).DangerZoneActive = False
                                End If
                            End SyncLock
                            Dispatch(Sub()
                                         IsDangerZoneVisible = False
                                         LogLine($"[Coordinator] ✅ Danger Zone cleared — {SlotLabel(slotIdx)} signal faded")
                                         StatusText = $"● In Trade — {_selectedContract?.Name} {side}  [{SlotLabel(ownerIdx)}]"
                                     End Sub)
                        End If
                        Return
                    End If

                    Dim sigSide = If(hasBull, OrderSide.Buy, OrderSide.Sell)

                    If sigSide = side Then
                        ' Same direction — confirming signal (scale-in coordination: log only)
                        ' Actual scale-in is placed by the owning engine's own logic.
                        If scaleIns < _maxScaleIns Then
                            Dispatch(Sub() LogLine($"[Coordinator] ➕ {SlotLabel(slotIdx)} confirms {sigSide} — scale-in candidate"))
                        Else
                            Dispatch(Sub() LogLine($"[Coordinator] ℹ️  {SlotLabel(slotIdx)} confirms {sigSide} — scale-in limit reached"))
                        End If
                        ' If this engine was the DangerZone source and now confirms same direction, clear it (Q2)
                        If dzActive AndAlso dzSourceIdx = slotIdx Then
                            SyncLock _coordLock
                                _dangerZoneActive = False
                                _dangerZoneSourceIdx = -1
                                If _engines(ownerIdx) IsNot Nothing Then
                                    _engines(ownerIdx).DangerZoneActive = False
                                End If
                            End SyncLock
                            Dispatch(Sub()
                                         IsDangerZoneVisible = False
                                         LogLine($"[Coordinator] ✅ Danger Zone reversed — {SlotLabel(slotIdx)} now confirms {sigSide}")
                                         StatusText = $"● In Trade — {_selectedContract?.Name} {side}  [{SlotLabel(ownerIdx)}]"
                                     End Sub)
                        End If
                    Else
                        ' Opposing direction — activate Danger Zone (Q1: snap SL to breakeven)
                        If Not dzActive Then
                            SyncLock _coordLock
                                _dangerZoneActive = True
                                _dangerZoneSourceIdx = slotIdx
                                If _engines(ownerIdx) IsNot Nothing Then
                                    _engines(ownerIdx).DangerZoneActive = True
                                End If
                            End SyncLock
                            Dispatch(Sub()
                                         IsDangerZoneVisible = True
                                         LogLine($"[Coordinator] ⚠️  DANGER ZONE — {SlotLabel(slotIdx)} signals {sigSide} against open {side}. Snapping SL to breakeven.")
                                         StatusText = $"⚠️ DANGER ZONE — {_selectedContract?.Name} {side}  [{SlotLabel(ownerIdx)}]"
                                     End Sub)
                        End If
                    End If
                End Sub
        End Sub

        ' ── Helpers ───────────────────────────────────────────────────────────────

        Private Sub LogLine(message As String)
            Dim ts = DateTime.Now.ToString("HH:mm:ss")
            LogEntries.Insert(0, $"[{ts}]  {message}")
            Do While LogEntries.Count > 500
                LogEntries.RemoveAt(LogEntries.Count - 1)
            Loop
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

        ' ── Dispose ───────────────────────────────────────────────────────────────

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _session.AccountChanged, AddressOf OnSessionAccountChanged
                For i = 0 To MaxSlots - 1
                    Try
                        If _engines(i) IsNot Nothing Then _engines(i).[Stop]()
                    Catch
                    End Try
                    Try
                        If _engineScopes(i) IsNot Nothing Then _engineScopes(i).Dispose()
                    Catch
                    End Try
                Next
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
