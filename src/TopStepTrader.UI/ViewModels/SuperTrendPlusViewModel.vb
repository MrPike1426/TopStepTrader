Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Threading
Imports System.Windows
Imports System.Windows.Media
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Data
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports System.Text.Json
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>One entry in the AI check history log.</summary>
    Public Class AiLogEntryVm
        Public Property Timestamp As String
        Public Property Indicator As String
        Public Property CheckResult As String
        Public ReadOnly Property Display As String
            Get
                Return $"{Timestamp}  —  {Indicator}  —  {CheckResult}"
            End Get
        End Property
        Public ReadOnly Property EntryColour As String
            Get
                Dim v = CheckResult.ToUpperInvariant()
                If v.Contains("VETO") OrElse v.Contains("RED") OrElse v.Contains("BLOCK") Then Return "#EF9A9A"
                If v.Contains("YELLOW") OrElse v.Contains("CAUTION") OrElse v.Contains("WARN") Then Return "#FFF176"
                Return "#A5D6A7"
            End Get
        End Property
    End Class

    Public Class WatchlistRowVm
        Inherits ViewModelBase

        Public Property Symbol As String = String.Empty
        Public Property Label As String = String.Empty

        Private _arrow As String = "–"
        Public Property Arrow As String
            Get
                Return _arrow
            End Get
            Set(value As String)
                SetProperty(_arrow, value)
            End Set
        End Property

        Private _adxDisplay As String = "ADX: –"
        Public Property AdxDisplay As String
            Get
                Return _adxDisplay
            End Get
            Set(value As String)
                SetProperty(_adxDisplay, value)
            End Set
        End Property

        Private _signal As String = "flat"
        Public Property Signal As String
            Get
                Return _signal
            End Get
            Set(value As String)
                SetProperty(_signal, value)
            End Set
        End Property

        Private _rowColor As Brush = Brushes.White
        Public Property RowColor As Brush
            Get
                Return _rowColor
            End Get
            Set(value As Brush)
                SetProperty(_rowColor, value)
            End Set
        End Property

        Private _trendStrength As String = ""
        Public Property TrendStrength As String
            Get
                Return _trendStrength
            End Get
            Set(value As String)
                SetProperty(_trendStrength, value)
            End Set
        End Property

        Private _signalReason As String = ""
        Public Property SignalReason As String
            Get
                Return _signalReason
            End Get
            Set(value As String)
                SetProperty(_signalReason, value)
            End Set
        End Property

        Private _diDisplay As String = "+DI:-- -DI:--"
        Public Property DiDisplay As String
            Get
                Return _diDisplay
            End Get
            Set(value As String)
                SetProperty(_diDisplay, value)
            End Set
        End Property

        ' ── ORB properties (equity-index instruments only) ────────────────────
        Private _orbSignal As String = ""
        ''' <summary>"BULL" / "BEAR" / "WAIT" / "BUILDING" / "CLOSED" / "" (non-equity = empty)</summary>
        Public Property OrbSignal As String
            Get
                Return _orbSignal
            End Get
            Set(value As String)
                SetProperty(_orbSignal, value)
            End Set
        End Property

        Private _orbRangeDisplay As String = ""
        ''' <summary>E.g. "OR: 5012 / 4988" once the range is known, otherwise "".</summary>
        Public Property OrbRangeDisplay As String
            Get
                Return _orbRangeDisplay
            End Get
            Set(value As String)
                SetProperty(_orbRangeDisplay, value)
            End Set
        End Property

        Private _orbRowColor As Brush = Brushes.Transparent
        Public Property OrbRowColor As Brush
            Get
                Return _orbRowColor
            End Get
            Set(value As Brush)
                SetProperty(_orbRowColor, value)
            End Set
        End Property

    End Class

    Public Class SymbolRowVm
        Inherits ViewModelBase

        Public Property Symbol As String = String.Empty

        Private _arrow As String = "–"
        Public Property Arrow As String
            Get
                Return _arrow
            End Get
            Set(value As String)
                SetProperty(_arrow, value)
            End Set
        End Property

        Private _adxDisplay As String = "ADX:–"
        Public Property AdxDisplay As String
            Get
                Return _adxDisplay
            End Get
            Set(value As String)
                SetProperty(_adxDisplay, value)
            End Set
        End Property

        Private _signal As String = "flat"
        Public Property Signal As String
            Get
                Return _signal
            End Get
            Set(value As String)
                SetProperty(_signal, value)
            End Set
        End Property

        Private _rowColor As Brush = Brushes.White
        Public Property RowColor As Brush
            Get
                Return _rowColor
            End Get
            Set(value As Brush)
                SetProperty(_rowColor, value)
            End Set
        End Property

    End Class

    Friend Class ApproachState
        Friend LastStDir As Integer = 0
        Friend Distances As New Queue(Of Decimal)
    End Class

    Public Class SuperTrendPlusViewModel
        Inherits ViewModelBase
        Implements IDisposable

        ' Root symbols and friendly display names are driven directly from FavouriteContracts —
        ' no hardcoded parallel arrays, so a single change in GetDefaults() is enough.
        Private Shared ReadOnly _stDefaults As IReadOnlyList(Of Core.Trading.FavouriteContract) =
            Core.Trading.FavouriteContracts.GetDefaults().Where(Function(f) Not String.IsNullOrEmpty(f.PxRootSymbol)).ToList()
        Private Shared ReadOnly Instruments As String() = _stDefaults.Select(Function(f) f.PxRootSymbol).ToArray()
        Private Shared ReadOnly InstrumentLabels As String() = _stDefaults.Select(Function(f) If(String.IsNullOrWhiteSpace(f.DisplayName), f.PxRootSymbol, f.DisplayName)).ToArray()
        Private Const BarsToFetch As Integer = 60
        Private Const EntryStaggerMs As Integer = 5000
        Private Const SlotUpdateStaggerMs As Integer = 5000

        ' TopStepX session-close window: entries suppressed, scan skipped.
        Private Shared ReadOnly SessionCloseTime As TimeSpan = TimeSpan.FromHours(21).Add(TimeSpan.FromMinutes(10))
        Private Shared ReadOnly SessionResumeTime As TimeSpan = TimeSpan.FromHours(22)

        ' ── ORB time-of-day constants (US Eastern) ────────────────────────────
        ' Regular equity session: 09:30–16:00 ET.  OR window: 09:30–10:00.
        ' Entry window: 10:00–12:45 (first half of session).
        ' Banner shown: 09:00–17:00 ET only.
        Private Shared ReadOnly EasternTz As TimeZoneInfo =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
        Private Shared ReadOnly OrbSessionOpen   As TimeSpan = TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(30))
        Private Shared ReadOnly OrbRangeEnd      As TimeSpan = TimeSpan.FromHours(10)
        Private Shared ReadOnly OrbEntryClose    As TimeSpan = TimeSpan.FromHours(12).Add(TimeSpan.FromMinutes(45))
        Private Shared ReadOnly OrbSessionClose  As TimeSpan = TimeSpan.FromHours(16)
        Private Shared ReadOnly OrbBannerStart   As TimeSpan = TimeSpan.FromHours(9)
        Private Shared ReadOnly OrbBannerEnd     As TimeSpan = TimeSpan.FromHours(17)

        ''' <summary>PxRootSymbols that participate in ORB monitoring (equity index futures only).</summary>
        Private Shared ReadOnly OrbEquitySymbols As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) _
            From {"MES", "MNQ", "M2K", "MYM"}

        Public ReadOnly Property WatchlistItems As New ObservableCollection(Of WatchlistRowVm)
        Private Const SyncMissThreshold As Integer = 3

        Private ReadOnly _barService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _contractResolver As Core.Interfaces.IContractResolutionService
        Private ReadOnly _logger As ILogger(Of SuperTrendPlusViewModel)
        Private ReadOnly _claudeService As IClaudeReviewService
        Private ReadOnly _configRepo As SuperTrendPlusConfigRepository
        Private ReadOnly _tradeRecordService As Core.Interfaces.ITradeRecordService
        Private ReadOnly _debugCapture As Core.Interfaces.IDebugTradeCaptureService

        ' ── Debug capture state (per-trade tracking, keyed by DebugTradeId) ────
        Private ReadOnly _debugMfe As New Dictionary(Of String, Decimal)()
        Private ReadOnly _debugMae As New Dictionary(Of String, Decimal)()
        Private ReadOnly _lastBarTimestampByTradeId As New Dictionary(Of String, DateTimeOffset)()

        Private _timer As Timer
        Private ReadOnly _timerLock As New Object()
        Private _disposed As Boolean = False
        Private _isTicking As Integer = 0
        Private _allMarketsClosed As Boolean = False
        Private _lastScanUtc As DateTime = DateTime.MinValue

        Private ReadOnly _approachHistory As New Dictionary(Of String, ApproachState)
        Private ReadOnly _prevStDirByInstrument As New Dictionary(Of String, Single)()
        Private ReadOnly _exitEngine As ExitSignalEngine
        Private _useEarlyMode As Boolean = False

        ''' <summary>Set when any slot is released during a tick; cleared at tick start.
        ''' Prevents same-tick re-entry after a position is closed.</summary>
        Private _releasedThisTick As Boolean = False

        ''' <summary>Stores the DateTimeOffset when each instrument's slot was last released.
        ''' Re-entry is blocked until at least one full bar has elapsed.</summary>
        Private ReadOnly _reEntryCooldown As New Dictionary(Of String, DateTimeOffset)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>Instruments suppressed from AI pre-trade checks until the stored DateTimeOffset (15-min block on NO).</summary>
        Private ReadOnly _aiSuppression As New Dictionary(Of String, DateTimeOffset)(StringComparer.OrdinalIgnoreCase)

        ' ── AI toggle ───────────────────────────────────────────────────────────
        Private _isAiEnabled As Boolean = False
        ''' <summary>When True, a Claude Haiku pre-trade sense check gates every new order.
        ''' Resets to False on each app start.</summary>
        Public Property IsAiEnabled As Boolean
            Get
                Return _isAiEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isAiEnabled, value)
            End Set
        End Property

        ' ── Debug capture toggle (FEAT-39) ──────────────────────────────────────
        Private _isDebugCaptureEnabled As Boolean = False
        ''' <summary>When True, every ST+ trade is recorded to debug_trades.db.
        ''' Default False; NOT persisted across restarts.</summary>
        Public Property IsDebugCaptureEnabled As Boolean
            Get
                Return _isDebugCaptureEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isDebugCaptureEnabled, value) Then
                    If _debugCapture IsNot Nothing Then _debugCapture.IsEnabled = value
                End If
            End Set
        End Property
        Public Property UseEarlyMode As Boolean
            Get
                Return _useEarlyMode
            End Get
            Set(value As Boolean)
                SetProperty(_useEarlyMode, value)
            End Set
        End Property

        ' ── Take $100 Profit toggle ──────────────────────────────────────────
        ''' <summary>When True, once unrealised P&amp;L reaches $100 the stop snaps to the
        ''' BB mid (EMA 10, 1.5×) on the 15-second bar, ratchet rules still apply.</summary>
        Public Property IsTake100Enabled As Boolean
            Get
                Return Config.Take100ProfitEnabled
            End Get
            Set(value As Boolean)
                If Config.Take100ProfitEnabled <> value Then
                    Config.Take100ProfitEnabled = value
                    NotifyPropertyChanged(NameOf(IsTake100Enabled))
                End If
            End Set
        End Property

        ' ── Slot boxes (replaces persona boxes) ─────────────────────────────
        Public ReadOnly Property Slot1 As SlotBoxVm = New SlotBoxVm(0)
        Public ReadOnly Property Slot2 As SlotBoxVm = New SlotBoxVm(1)
        Public ReadOnly Property Slot3 As SlotBoxVm = New SlotBoxVm(2)

        Private ReadOnly _slotManager As SlotManager
        Friend ReadOnly Property Config As SuperTrendPlusConfig

        ' -- Accounts --------------------------------------------------------
        Public Property Accounts As New ObservableCollection(Of Account)

        Private _selectedAccount As Account
        Public Property SelectedAccount As Account
            Get
                Return _selectedAccount
            End Get
            Set(value As Account)
                SetProperty(_selectedAccount, value)
                If value IsNot Nothing Then _session.SelectAccount(value)
            End Set
        End Property

        ' -- How-it-works panel expand/collapse ------------------------------
        Private _isHowItWorksExpanded As Boolean = False
        Public Property IsHowItWorksExpanded As Boolean
            Get
                Return _isHowItWorksExpanded
            End Get
            Set(value As Boolean)
                SetProperty(_isHowItWorksExpanded, value)
            End Set
        End Property

        ' ── ORB phase banner ─────────────────────────────────────────────────
        Private _orbPhaseLabel As String = ""
        ''' <summary>Human-readable ORB phase shown in the banner strip, e.g. "📐 ORB  •  🟠 Building opening range  09:30–10:00 ET".</summary>
        Public Property OrbPhaseLabel As String
            Get
                Return _orbPhaseLabel
            End Get
            Set(value As String)
                SetProperty(_orbPhaseLabel, value)
            End Set
        End Property

        Private _isOrbBannerVisible As Boolean = False
        ''' <summary>True only during 09:00–17:00 ET on weekdays.</summary>
        Public Property IsOrbBannerVisible As Boolean
            Get
                Return _isOrbBannerVisible
            End Get
            Set(value As Boolean)
                SetProperty(_isOrbBannerVisible, value)
            End Set
        End Property

        Public ReadOnly Property Timeframes As String() = {"5min", "15min", "1hr"}

        ' ── Persona selection ───────────────────────────────────────────────────
        ' Lewis=risk-averse (MinADX 40, ST×3.5, RR 0.75)
        ' Damian=balanced  (MinADX 30, ST×3.0, RR 1.5)
        ' Joe=reward-seeking (MinADX 20, ST×2.5, RR 2.0)
        Private _activePersona As String = "Damian"
        Private _stMultiplier As Double = 3.0

        Public Property ActivePersona As String
            Get
                Return _activePersona
            End Get
            Set(value As String)
                If SetProperty(_activePersona, value) Then
                    ApplyPersonaConfig()
                    NotifyPropertyChanged(NameOf(IsLewisSelected))
                    NotifyPropertyChanged(NameOf(IsDamianSelected))
                    NotifyPropertyChanged(NameOf(IsJoeSelected))
                    SaveConfigFireAndForget()
                End If
            End Set
        End Property

        Public ReadOnly Property IsLewisSelected As Boolean
            Get
                Return _activePersona = "Lewis"
            End Get
        End Property

        Public ReadOnly Property IsDamianSelected As Boolean
            Get
                Return _activePersona = "Damian"
            End Get
        End Property

        Public ReadOnly Property IsJoeSelected As Boolean
            Get
                Return _activePersona = "Joe"
            End Get
        End Property

        ''' <summary>Minimum entry ADX for the active persona.</summary>
        Public ReadOnly Property PersonaMinAdx As Single
            Get
                Select Case _activePersona
                    Case "Lewis" : Return 40.0F
                    Case "Joe" : Return 20.0F
                    Case Else : Return 30.0F  ' Damian default
                End Select
            End Get
        End Property

        ''' <summary>R:R ratio target for the active persona (visual milestone).</summary>
        Public ReadOnly Property PersonaRrRatio As Decimal
            Get
                Select Case _activePersona
                    Case "Lewis" : Return 0.75D
                    Case "Joe" : Return 2D
                    Case Else : Return 1.5D  ' Damian default
                End Select
            End Get
        End Property

        Public ReadOnly Property SelectLewisCommand As RelayCommand
        Public ReadOnly Property SelectDamianCommand As RelayCommand
        Public ReadOnly Property SelectJoeCommand As RelayCommand

        Private Sub ApplyPersonaConfig()
            Select Case _activePersona
                Case "Lewis"
                    _stMultiplier = 3.5
                    Config.MinEntryAdx = 40.0F
                Case "Joe"
                    _stMultiplier = 2.5
                    Config.MinEntryAdx = 20.0F
                Case Else
                    _stMultiplier = 3.0
                    Config.MinEntryAdx = 30.0F
            End Select
            Config.StMultiplier = _stMultiplier
        End Sub

        Private _selectedTimeframe As String = "15min"
        Public Property SelectedTimeframe As String
            Get
                Return _selectedTimeframe
            End Get
            Set(value As String)
                If SetProperty(_selectedTimeframe, value) Then
                    NotifyPropertyChanged(NameOf(StatusText))
                    SaveConfigFireAndForget()
                End If
            End Set
        End Property

        Private _isMonitoring As Boolean = False
        Public Property IsMonitoring As Boolean
            Get
                Return _isMonitoring
            End Get
            Set(value As Boolean)
                If SetProperty(_isMonitoring, value) Then
                    NotifyPropertyChanged(NameOf(StartStopLabel))
                    NotifyPropertyChanged(NameOf(StatusVisibility))
                End If
            End Set
        End Property

        Public ReadOnly Property StartStopLabel As String
            Get
                Return If(_isMonitoring, "~Stop Monitoring", "~Start Monitoring")
            End Get
        End Property

        Private _statusText As String = String.Empty
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Public ReadOnly Property StatusVisibility As Visibility
            Get
                Return If(_isMonitoring, Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        Private _statusBackground As Brush = Brushes.Transparent
        Public Property StatusBackground As Brush
            Get
                Return _statusBackground
            End Get
            Set(value As Brush)
                SetProperty(_statusBackground, value)
            End Set
        End Property

        ''' <summary>Scrolling history of all AI checks, newest first.</summary>
        Public ReadOnly Property AiHistoryLog As New ObservableCollection(Of AiLogEntryVm)()

        Private Sub AddAiLogEntry(indicator As String, checkResult As String)
            Dim entry As New AiLogEntryVm With {
                .Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                .Indicator = indicator,
                .CheckResult = checkResult
            }
            Application.Current?.Dispatcher?.Invoke(Sub() AiHistoryLog.Insert(0, entry))
        End Sub

        Public ReadOnly Property StartStopCommand As RelayCommand

        Public ReadOnly Property AiCheckSlot1Command As RelayCommand
        Public ReadOnly Property AiCheckSlot2Command As RelayCommand
        Public ReadOnly Property AiCheckSlot3Command As RelayCommand

        Public Sub New(barService As IBarIngestionService,
                       orderService As IOrderService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService,
                       accountService As IAccountService,
                       contractResolver As Core.Interfaces.IContractResolutionService,
                       claudeService As IClaudeReviewService,
                       logger As ILogger(Of SuperTrendPlusViewModel),
                       tradeRecordService As Core.Interfaces.ITradeRecordService,
                       Optional configRepo As SuperTrendPlusConfigRepository = Nothing,
                       Optional debugCapture As Core.Interfaces.IDebugTradeCaptureService = Nothing)
            _barService = barService
            _orderService = orderService
            _session = session
            _personaService = personaService
            _accountService = accountService
            _contractResolver = contractResolver
            _claudeService = claudeService
            _logger = logger
            _tradeRecordService = tradeRecordService
            _configRepo = configRepo
            _debugCapture = debugCapture
            Config = New SuperTrendPlusConfig()
            _slotManager = New SlotManager(Config)
            _exitEngine = New ExitSignalEngine(
                Microsoft.Extensions.Logging.Abstractions.NullLogger(Of ExitSignalEngine).Instance)
            StartStopCommand = New RelayCommand(AddressOf OnStartStop)
            AiCheckSlot1Command = New RelayCommand(Async Sub() Await RunMidTradeCheckAsync(Slot1))
            AiCheckSlot2Command = New RelayCommand(Async Sub() Await RunMidTradeCheckAsync(Slot2))
            AiCheckSlot3Command = New RelayCommand(Async Sub() Await RunMidTradeCheckAsync(Slot3))
            SelectLewisCommand = New RelayCommand(Sub() ActivePersona = "Lewis")
            SelectDamianCommand = New RelayCommand(Sub() ActivePersona = "Damian")
            SelectJoeCommand = New RelayCommand(Sub() ActivePersona = "Joe")
            ApplyPersonaConfig()

            Slot1.Slot = _slotManager.Slots(0)
            Slot2.Slot = _slotManager.Slots(1)
            Slot3.Slot = _slotManager.Slots(2)

            For i = 0 To Instruments.Length - 1
                WatchlistItems.Add(New WatchlistRowVm() With {
                    .Symbol = Instruments(i),
                    .Label = InstrumentLabels(i)
                })
            Next
            For Each box In AllSlotBoxes()
                For i = 0 To Instruments.Length - 1
                    box.Symbols.Add(New SymbolRowVm() With {.Symbol = InstrumentLabels(i)})
                Next
            Next
            AddHandler _session.AutoExecutionChanged, AddressOf OnAutoExecutionChanged
        End Sub

        Private Function AllSlotBoxes() As SlotBoxVm()
            Return {Slot1, Slot2, Slot3}
        End Function

        Private Function BoxForSlot(slot As PositionSlot) As SlotBoxVm
            Return AllSlotBoxes().FirstOrDefault(Function(b) b.SlotIndex = slot.SlotIndex)
        End Function

        Private Sub OnStartStop()
            If _isMonitoring Then
                StopMonitoring()
            Else
                StartMonitoring()
            End If
        End Sub

        Public Async Sub LoadDataAsync()
            If _configRepo IsNot Nothing Then
                Try
                    Dim entity = Await _configRepo.LoadAsync()
                    Application.Current?.Dispatcher?.Invoke(Sub() ApplyConfigEntity(entity))
                Catch
                End Try
            End If

            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        Accounts.Clear()
                        For Each a In accountList
                            Accounts.Add(a)
                        Next
                        If Accounts.Count > 0 Then
                            Dim sessionAcc = _session.SelectedAccount
                            Dim preferred = If(
                                If(sessionAcc IsNot Nothing,
                                   Accounts.FirstOrDefault(Function(a) a.Id = sessionAcc.Id),
                                   Nothing),
                                Accounts.FirstOrDefault(
                                    Function(a) a.Name IsNot Nothing AndAlso
                                                a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase)))
                            SelectedAccount = If(preferred, Accounts(0))
                        End If
                    End Sub)
            Catch
            End Try
        End Sub

        Private Sub OnAutoExecutionChanged(sender As Object, e As EventArgs)
            Task.Run(AddressOf RefreshAccountsAsync)
        End Sub

        Private Async Function RefreshAccountsAsync() As Task
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
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

        Private Sub ApplyConfigEntity(entity As Data.Entities.SuperTrendPlusConfigEntity)
            ' Update backing fields directly to avoid triggering save during load
            _activePersona = If(String.IsNullOrWhiteSpace(entity.ActivePersona), "Damian", entity.ActivePersona)
            _selectedTimeframe = entity.SelectedTimeframe
            NotifyPropertyChanged(NameOf(ActivePersona))
            NotifyPropertyChanged(NameOf(IsLewisSelected))
            NotifyPropertyChanged(NameOf(IsDamianSelected))
            NotifyPropertyChanged(NameOf(IsJoeSelected))
            NotifyPropertyChanged(NameOf(PersonaMinAdx))
            NotifyPropertyChanged(NameOf(PersonaRrRatio))
            NotifyPropertyChanged(NameOf(SelectedTimeframe))
            ApplyPersonaConfig()

            ' Config POCO properties
            Config.MaxSlots = entity.MaxSlots
            Config.AdxWeakThreshold = entity.AdxWeakThreshold
            Config.AdxModerateThreshold = entity.AdxModerateThreshold
            Config.AdxStrongThreshold = entity.AdxStrongThreshold
            Config.BreakevenTriggerR = entity.BreakevenTriggerR
            Config.ProfitLockTriggerR = entity.ProfitLockTriggerR
            Config.ProfitLockOffsetR = entity.ProfitLockOffsetR
            Config.TrailAtrMultiple = entity.TrailAtrMultiple
            Config.ProfitTrailTriggerR = entity.ProfitTrailTriggerR
            Config.HarvestTriggerR = entity.HarvestTriggerR
            Config.HarvestLockR = entity.HarvestLockR
            Config.FreeRideTriggerR = entity.FreeRideTriggerR
            Config.FreeRideLockR = entity.FreeRideLockR
            Config.WarningScoreThreshold = entity.WarningScoreThreshold
            Config.ExitingScoreThreshold = entity.ExitingScoreThreshold
        End Sub

        Private Function BuildConfigEntity() As Data.Entities.SuperTrendPlusConfigEntity
            Return New Data.Entities.SuperTrendPlusConfigEntity With {
                .ActivePersona = _activePersona,
                .SelectedTimeframe = _selectedTimeframe,
                .MaxSlots = Config.MaxSlots,
                .AdxWeakThreshold = Config.AdxWeakThreshold,
                .AdxModerateThreshold = Config.AdxModerateThreshold,
                .AdxStrongThreshold = Config.AdxStrongThreshold,
                .BreakevenTriggerR = Config.BreakevenTriggerR,
                .ProfitLockTriggerR = Config.ProfitLockTriggerR,
                .ProfitLockOffsetR = Config.ProfitLockOffsetR,
                .TrailAtrMultiple = Config.TrailAtrMultiple,
                .ProfitTrailTriggerR = Config.ProfitTrailTriggerR,
                .HarvestTriggerR = Config.HarvestTriggerR,
                .HarvestLockR = Config.HarvestLockR,
                .FreeRideTriggerR = Config.FreeRideTriggerR,
                .FreeRideLockR = Config.FreeRideLockR,
                .WarningScoreThreshold = Config.WarningScoreThreshold,
                .ExitingScoreThreshold = Config.ExitingScoreThreshold
            }
        End Function

        Private Sub SaveConfigFireAndForget()
            If _configRepo Is Nothing Then Return
            Dim entity = BuildConfigEntity()
            Task.Run(Async Function()
                         Try
                             Await _configRepo.SaveAsync(entity)
                         Catch
                         End Try
                     End Function)
        End Sub

        Private Sub StartMonitoring()
            IsHowItWorksExpanded = False
            IsMonitoring = True
            _timer = New Timer(AddressOf TimerCallback, Nothing, 0, 15000)
            If _selectedAccount Is Nothing OrElse _selectedAccount.Id = 0 Then
                StatusText = "? No account selected — monitoring in read-only mode (orders will be blocked until account loads)"
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H0))
                                                        End Sub)
            End If
        End Sub

        Friend Sub StopMonitoring()
            IsMonitoring = False
            SyncLock _timerLock
                If _timer IsNot Nothing Then
                    _timer.Dispose()
                    _timer = Nothing
                End If
            End SyncLock
            _prevStDirByInstrument.Clear()
            For Each wRow In WatchlistItems
                wRow.Arrow = "–"
                wRow.AdxDisplay = "ADX:–"
                wRow.Signal = "–"
                wRow.TrendStrength = ""
                wRow.RowColor = Brushes.Gray
            Next
            _slotManager.ResetAll()
            For Each box In AllSlotBoxes()
                box.IsPaused = False
                box.HasPosition = False
                box.PositionDisplay = String.Empty
                box.PnlLine = String.Empty
                box.PnlTextBrush = Brushes.Gray
                box.StopPhaseLabel = String.Empty
                box.SlotLabel = String.Empty
                box.IdleMonitorText = String.Empty
                box.IsIdleFlashing = False
                box.PnlBorderBrush = Brushes.Gray
                For Each row In box.Symbols
                    row.Arrow = "–"
                    row.AdxDisplay = "ADX:–"
                    row.Signal = "flat"
                    row.RowColor = Brushes.White
                Next
            Next
        End Sub

        Private Sub TimerCallback(state As Object)
            If Interlocked.CompareExchange(_isTicking, 1, 0) <> 0 Then
                _logger.LogWarning("ST+ tick skipped — previous tick still running")
                Return
            End If
            Try
                Task.Run(Async Function() As Task
                             Await DoTickAsync()
                         End Function).GetAwaiter().GetResult()
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ DoTickAsync error on timer tick")
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        StatusText = String.Format("Error: {0}", ex.Message)
                    End Sub)
            Finally
                Interlocked.Exchange(_isTicking, 0)
            End Try
        End Sub

        Private Async Function DoTickAsync() As Task
            ' Guard: if Stop Monitoring was clicked while this tick was in-flight, abort immediately.
            If Not _isMonitoring Then
                _logger.LogInformation("ST+ DoTickAsync aborted — monitoring was stopped while tick was in-flight.")
                Return
            End If
            _releasedThisTick = False
            Dim tf = MapTimeframe(_selectedTimeframe)

            Dim nowUtcTod = DateTime.UtcNow.TimeOfDay
            Dim inCloseWindow = nowUtcTod >= SessionCloseTime AndAlso nowUtcTod < SessionResumeTime

            Dim barCache As Dictionary(Of Integer, IList(Of MarketBar))
            If inCloseWindow Then
                ' Skip watchlist scan during the session-close window — TopStepX handles position
                ' closure on the platform side. Reconciliation still runs to clear slot state.
                barCache = New Dictionary(Of Integer, IList(Of MarketBar))()
            ElseIf _allMarketsClosed AndAlso (DateTime.UtcNow - _lastScanUtc).TotalSeconds < 60 Then
                barCache = New Dictionary(Of Integer, IList(Of MarketBar))()
            Else
                _lastScanUtc = DateTime.UtcNow
                barCache = Await ScanWatchlistAsync(tf)
            End If

            Await ReconcileOpenPositionsAsync(barCache)

            Dim isFirstSlotUpdate As Boolean = True
            For Each slot In _slotManager.Slots
                If slot.IsOpen Then
                    If Not isFirstSlotUpdate Then
                        Await Task.Delay(SlotUpdateStaggerMs)
                    End If
                    Await HandleOpenPositionAsync(slot, tf)
                    isFirstSlotUpdate = False
                End If
            Next

            If inCloseWindow Then
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        StatusText = "Session closed (21:10 UTC)"
                        StatusBackground = New SolidColorBrush(Color.FromRgb(&H69, &H69, &H69))
                    End Sub)
                Return
            End If

            ' Do not attempt new entries in the same tick that a position was released.
            ' This prevents rapid-fire re-entry when SL fires and snapshot disappears.
            If _releasedThisTick Then
                _logger.LogInformation("ST+ DoTickAsync — slot released this tick, skipping entry evaluation.")
            ElseIf _useEarlyMode Then
                Await EvaluateEarlyEntrySequenceAsync(tf, barCache)
                Await EvaluateSlotEntriesAsync(barCache)
            Else
                Await EvaluateSlotEntriesAsync(barCache)
            End If

            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    StatusText = String.Format("In Progress: Updated {0:HH:mm:ss}", DateTime.Now)
                    FlashStatusAsync()
                    ' Pulse idle text on empty boxes and refresh their timestamp
                    Dim now = DateTime.Now
                    For Each box In AllSlotBoxes()
                        If Not box.HasPosition Then
                            box.IdleMonitorText = String.Format("Actively Monitoring: {0:HH:mm:ss}", now)
                            Task.Run(Async Function() As Task
                                         Await box.FlashIdleAsync()
                                     End Function)
                        End If
                    Next
                End Sub)
        End Function

        Private Async Function ScanWatchlistAsync(tf As BarTimeframe) As Task(Of Dictionary(Of Integer, IList(Of MarketBar)))
            Dim cache As New Dictionary(Of Integer, IList(Of MarketBar))
            Dim anyFreshBar As Boolean = False
            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim wRow = WatchlistItems(i)
                Dim bars As IList(Of MarketBar)
                Try
                    bars = Await _barService.GetLiveBarsAsync(contractId, tf, BarsToFetch)
                Catch
                    Continue For
                End Try
                If bars Is Nothing OrElse bars.Count < 15 Then
                    _logger.LogInformation("ST+ ScanWatchlist [{Contract}] SKIP — bars null or count < 15 (count={Count})",
                                           contractId, If(bars Is Nothing, 0, bars.Count))
                    Continue For
                End If

                Dim tfMinutesScan As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                If _selectedTimeframe.EndsWith("hr") Then tfMinutesScan *= 60
                If bars.Count > 1 Then
                    Dim lastBarAgeScan = (DateTime.UtcNow - bars.Last().Timestamp).TotalMinutes
                    If lastBarAgeScan < tfMinutesScan Then
                        _logger.LogInformation("ST+ ScanWatchlist [{Contract}] stripping forming bar (lastBarAge={Age:F1}min < tf={Tf}min). bars: {Before} → {After}",
                                               contractId, lastBarAgeScan, tfMinutesScan, bars.Count, bars.Count - 1)
                        bars = bars.Take(bars.Count - 1).ToList()
                    End If
                End If
                If bars.Count < 14 Then
                    _logger.LogInformation("ST+ ScanWatchlist [{Contract}] SKIP — fewer than 14 bars after forming-bar strip (count={Count})",
                                           contractId, bars.Count)
                    Continue For
                End If

                Dim staleAgeMins = (DateTime.UtcNow - bars.Last().Timestamp).TotalMinutes
                If staleAgeMins > tfMinutesScan * 3 Then
                    _logger.LogInformation("ST+ ScanWatchlist [{Contract}] STALE — last bar {Age:F0} min ago (threshold={Threshold}min)",
                                           contractId, staleAgeMins, tfMinutesScan * 3)
                    Application.Current?.Dispatcher?.Invoke(
                        Sub()
                            wRow.Signal = "closed"
                            wRow.Arrow = "–"
                            wRow.RowColor = Brushes.Gray
                            wRow.SignalReason = String.Format("Market closed — last bar {0:F0} min ago", staleAgeMins)
                        End Sub)
                    Continue For
                End If

                anyFreshBar = True
                cache(i) = bars
                _logger.LogInformation("ST+ ScanWatchlist [{Contract}] cached {Count} bars.", contractId, bars.Count)

                Dim highs = bars.Select(Function(b) b.High).ToList()
                Dim lows = bars.Select(Function(b) b.Low).ToList()
                Dim closes = bars.Select(Function(b) b.Close).ToList()

                Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim n = bars.Count - 1
                Dim stDir = st.Direction(n)
                Dim adxVal = dmi.ADX(n)
                Dim plusDi = dmi.PlusDI(n)
                Dim minusDi = dmi.MinusDI(n)

                Dim arrow As String
                Dim signal As String
                Dim rowColor As Brush
                Dim strength As String
                Dim signalReason As String
                Dim isLongSignal As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
                Dim isShortSignal As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso minusDi > plusDi
                If isLongSignal Then
                    arrow = ChrW(&H25B2) : signal = "BULL" : rowColor = Brushes.LimeGreen
                ElseIf isShortSignal Then
                    arrow = ChrW(&H25BC) : signal = "BEAR" : rowColor = Brushes.Red
                ElseIf stDir > 0 Then
                    arrow = ChrW(&H25B2) : signal = "WAIT" : rowColor = Brushes.DarkGoldenrod
                ElseIf stDir < 0 Then
                    arrow = ChrW(&H25BC) : signal = "WAIT" : rowColor = Brushes.DarkGoldenrod
                Else
                    arrow = ChrW(&H2013) : signal = "flat" : rowColor = Brushes.Gray
                End If

                If Single.IsNaN(adxVal) Then
                    strength = "ADX: --"
                    signalReason = "Waiting for data..."
                ElseIf adxVal >= Config.AdxStrongThreshold Then
                    strength = String.Format("ADX:{0:D2} L3: Espresso", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Strong uptrend — bot will open 3 positions.",
                                  If(signal = "BEAR", "Strong downtrend — bot will open 3 positions.",
                                     "Strong trend forming — waiting for direction alignment."))
                ElseIf adxVal >= Config.AdxModerateThreshold Then
                    strength = String.Format("ADX:{0:D2} L2: Latte", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Moderate uptrend — bot will open 2 positions.",
                                  If(signal = "BEAR", "Moderate downtrend — bot will open 2 positions.",
                                     "Trending — waiting for +DI/-DI to align with SuperTrend."))
                ElseIf adxVal >= Config.AdxWeakThreshold Then
                    strength = String.Format("ADX:{0:D2} L0: Decaff", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Uptrend active — bot will open 1 position.",
                                  If(signal = "BEAR", "Downtrend active — bot will open 1 position.",
                                     "Trending — waiting for +DI/-DI to align with SuperTrend."))
                ElseIf adxVal >= 15 Then
                    strength = String.Format("ADX:{0:D2} L1: Mellow Birds", CInt(adxVal))
                    signalReason = "Trend is weak — watching for momentum to build before entering."
                Else
                    strength = String.Format("ADX:{0:D2} Cat Piss", CInt(adxVal))
                    signalReason = "Market is choppy with no clear trend — standing aside to avoid false signals."
                End If

                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--",
                                          If(adxVal >= Config.AdxStrongThreshold, String.Format("ADX:{0:D2} L3: Espresso", CInt(adxVal)),
                                          If(adxVal >= Config.AdxModerateThreshold, String.Format("ADX:{0:D2} L2: Latte", CInt(adxVal)),
                                          If(adxVal >= Config.AdxWeakThreshold, String.Format("ADX:{0:D2} L0: Decaff", CInt(adxVal)),
                                             String.Format("ADX:{0:D2}", CInt(adxVal))))))
                Dim diStr As String = If(Single.IsNaN(plusDi) OrElse Single.IsNaN(minusDi),
                                        "+DI:-- -DI:--",
                                        String.Format("+DI:{0:D2} -DI:{1:D2}", CInt(plusDi), CInt(minusDi)))

                If _useEarlyMode Then
                    Dim atr14 = TechnicalIndicators.ATR(highs, lows, closes, period:=14)
                    Dim atrN = If(atr14 IsNot Nothing AndAlso atr14.Length > n, CDec(atr14(n)), 0D)
                    Dim lastClose = closes(n)
                    Dim stLine = CDec(st.Line(n))
                    Dim dist = Math.Abs(lastClose - stLine)
                    Dim sig1 As Boolean = atrN > 0D AndAlso dist <= 1.5D * atrN
                    Dim sig2 As Boolean = UpdateApproachHistory(contractId, stDir, dist)
                    Dim spreadDI As Single = Math.Abs(plusDi - minusDi)
                    Dim anticipatedLong As Boolean = stDir < 0
                    Dim sig3 As Boolean = If(anticipatedLong, plusDi > minusDi, minusDi > plusDi) OrElse spreadDI < 5
                    Dim sig4 As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= 20.0F
                    Dim sigsCount = (If(sig1, 1, 0)) + (If(sig2, 1, 0)) + (If(sig3, 1, 0)) + (If(sig4, 1, 0))
                    If sig1 AndAlso sig2 AndAlso sig3 AndAlso sig4 Then
                        signal = "EARLY"
                        rowColor = Brushes.Goldenrod
                        signalReason = "All early signals aligned — potential reversal imminent, preparing to enter."
                    ElseIf sigsCount >= 3 Then
                        signal = "WATCH"
                        rowColor = Brushes.DimGray
                        signalReason = String.Format("{0}/4 early signals met — watching for the final trigger.", sigsCount)
                    End If
                End If

                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.Arrow = arrow
                        wRow.AdxDisplay = adxStr
                        wRow.Signal = signal
                        wRow.RowColor = rowColor
                        wRow.TrendStrength = strength
                        wRow.SignalReason = signalReason
                        wRow.DiDisplay = diStr
                    End Sub)

                ' ── ORB evaluation (equity-index instruments only, always 5-min bars) ──
                If OrbEquitySymbols.Contains(contractId) Then
                    EvaluateOrbSignal(wRow, bars)
                End If
            Next
            _allMarketsClosed = Not anyFreshBar

            ' Update ORB phase banner once per scan
            Dim etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTz)
            Dim etTod = etNow.TimeOfDay
            Dim isBannerVisible = etNow.DayOfWeek <> DayOfWeek.Saturday AndAlso
                                  etNow.DayOfWeek <> DayOfWeek.Sunday AndAlso
                                  etTod >= OrbBannerStart AndAlso etTod < OrbBannerEnd
            Dim phaseLabel = If(isBannerVisible, GetOrbPhaseLabel(etTod), "")
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    OrbPhaseLabel = phaseLabel
                    IsOrbBannerVisible = isBannerVisible
                End Sub)

            Return cache
        End Function

        Private Async Sub FlashStatusAsync()
            StatusBackground = New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))
            Await Task.Delay(400)
            StatusBackground = Brushes.Transparent
        End Sub

        ''' <summary>
        ''' Returns the human-readable ORB phase label for the given ET time-of-day.
        ''' Called once per scan tick and bound to the banner strip.
        ''' </summary>
        Friend Shared Function GetOrbPhaseLabel(etTod As TimeSpan) As String
            If etTod < OrbSessionOpen Then
                Return "📐 ORB  •  ⚫ Pre-market — session opens at 09:30 ET"
            ElseIf etTod < OrbRangeEnd Then
                Return "📐 ORB  •  🟠 Building opening range  09:30 – 10:00 ET"
            ElseIf etTod < OrbEntryClose Then
                Return "📐 ORB  •  🟢 Entry window open  10:00 – 12:45 ET"
            ElseIf etTod < OrbSessionClose Then
                Return "📐 ORB  •  ⛔ Entry window closed — past session midpoint"
            Else
                Return "📐 ORB  •  ⚫ Session closed"
            End If
        End Function

        ''' <summary>
        ''' Evaluates the ORB signal for one equity-index watchlist row using the supplied bars.
        ''' Always uses a 5-minute opening range (6 bars) regardless of the selected timeframe.
        ''' Updates OrbSignal, OrbRangeDisplay, and OrbRowColor on <paramref name="wRow"/>.
        ''' Must be called on the UI thread (or wrapped in a Dispatcher.Invoke).
        ''' </summary>
        Friend Sub EvaluateOrbSignal(wRow As WatchlistRowVm, bars As IList(Of MarketBar))
            ' Determine ET phase
            Dim etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTz)
            Dim etTod = etNow.TimeOfDay

            ' Outside banner window: clear ORB columns
            If etNow.DayOfWeek = DayOfWeek.Saturday OrElse etNow.DayOfWeek = DayOfWeek.Sunday OrElse
               etTod < OrbBannerStart OrElse etTod >= OrbBannerEnd Then
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.OrbSignal = ""
                        wRow.OrbRangeDisplay = ""
                        wRow.OrbRowColor = Brushes.Transparent
                    End Sub)
                Return
            End If

            ' Pre-market: show waiting state
            If etTod < OrbSessionOpen Then
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.OrbSignal = "PRE"
                        wRow.OrbRangeDisplay = ""
                        wRow.OrbRowColor = Brushes.DimGray
                    End Sub)
                Return
            End If

            ' ── Identify today's session bars ────────────────────────────────
            ' Use the last bar's date as the session date anchor (avoids UTC vs ET edge cases).
            Dim sessionDate = bars.Last().Timestamp.Date
            Dim sessionBars = bars.Where(Function(b) b.Timestamp.Date = sessionDate).OrderBy(Function(b) b.Timestamp).ToList()

            ' ORB always uses 5-min bars → 6 bars = 30 minutes.
            Const OrbBarCount As Integer = 6
            If sessionBars.Count = 0 Then Return

            ' Still building the opening range
            If etTod < OrbRangeEnd Then
                Dim barsBuilt = Math.Min(sessionBars.Count, OrbBarCount)
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.OrbSignal = "BUILDING"
                        wRow.OrbRangeDisplay = String.Format("OR: building ({0}/{1})", barsBuilt, OrbBarCount)
                        wRow.OrbRowColor = Brushes.DarkGoldenrod
                    End Sub)
                Return
            End If

            ' Past entry window
            If etTod >= OrbEntryClose Then
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.OrbSignal = "CLOSED"
                        wRow.OrbRangeDisplay = ""
                        wRow.OrbRowColor = Brushes.DimGray
                    End Sub)
                Return
            End If

            ' ── Compute OR high / low from first 6 session bars ─────────────
            Dim orbBars = sessionBars.Take(OrbBarCount).ToList()
            If orbBars.Count < OrbBarCount Then
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.OrbSignal = "BUILDING"
                        wRow.OrbRangeDisplay = String.Format("OR: building ({0}/{1})", orbBars.Count, OrbBarCount)
                        wRow.OrbRowColor = Brushes.DarkGoldenrod
                    End Sub)
                Return
            End If

            Dim orHigh = orbBars.Max(Function(b) b.High)
            Dim orLow = orbBars.Min(Function(b) b.Low)
            Dim orWidth = orHigh - orLow
            If orWidth <= 0D Then Return

            Dim rangeDisplay = String.Format("OR: {0:F2} / {1:F2}", orHigh, orLow)

            ' ── ATR(14) no-trade filter: OR width > 2× ATR ──────────────────
            Dim allHighs = bars.Select(Function(b) b.High).ToList()
            Dim allLows = bars.Select(Function(b) b.Low).ToList()
            Dim allCloses = bars.Select(Function(b) b.Close).ToList()
            Dim atr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, period:=14)
            Dim n = bars.Count - 1
            Dim atrNow As Single = If(atr14 IsNot Nothing AndAlso n < atr14.Length, atr14(n), 0.0F)
            If Not Single.IsNaN(atrNow) AndAlso atrNow > 0F AndAlso orWidth > CDec(atrNow) * 2D Then
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.OrbSignal = "WIDE"
                        wRow.OrbRangeDisplay = rangeDisplay & "  ⚠ OR too wide"
                        wRow.OrbRowColor = Brushes.DarkOrange
                    End Sub)
                Return
            End If

            ' ── Volume gate ──────────────────────────────────────────────────
            Dim lastBar = bars.Last()
            Dim volSeries = bars.Select(Function(b) CDec(b.Volume)).ToList()
            Dim volMa20 = TechnicalIndicators.SMA(volSeries, period:=20)
            Dim volMaNow As Single = If(volMa20 IsNot Nothing AndAlso n < volMa20.Length AndAlso Not Single.IsNaN(volMa20(n)), volMa20(n), 0.0F)
            Dim volOk = volMaNow > 0.0F AndAlso lastBar.Volume >= CDec(volMaNow) * 1.2D

            ' ── Signal evaluation ────────────────────────────────────────────
            Dim close = lastBar.Close
            Dim orbSignal As String
            Dim orbColor As Brush

            If close > orHigh AndAlso volOk Then
                orbSignal = "BULL"
                orbColor = Brushes.LimeGreen
            ElseIf close < orLow AndAlso volOk Then
                orbSignal = "BEAR"
                orbColor = Brushes.Red
            ElseIf close > orHigh OrElse close < orLow Then
                ' Breakout without volume confirmation
                orbSignal = "WAIT"
                orbColor = Brushes.Goldenrod
            Else
                ' Price inside range
                orbSignal = "WAIT"
                orbColor = Brushes.DarkGoldenrod
            End If

            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    wRow.OrbSignal = orbSignal
                    wRow.OrbRangeDisplay = rangeDisplay
                    wRow.OrbRowColor = orbColor
                End Sub)
        End Sub

        Private Function UpdateApproachHistory(contractId As String, stDir As Integer, distance As Decimal) As Boolean
            Dim state As ApproachState = Nothing
            If Not _approachHistory.TryGetValue(contractId, state) Then
                state = New ApproachState()
                _approachHistory(contractId) = state
            End If
            If stDir <> state.LastStDir Then
                state.Distances.Clear()
                state.LastStDir = stDir
                Return False
            End If
            state.Distances.Enqueue(distance)
            If state.Distances.Count > 3 Then state.Distances.Dequeue()
            If state.Distances.Count < 3 Then Return False
            Dim arr = state.Distances.ToArray()
            Return arr(0) > arr(1) AndAlso arr(1) > arr(2)
        End Function

        ''' <summary>
        ''' Confirmed-mode entry: evaluates ALL instruments each tick and opens a slot for every
        ''' favourable signal, up to the 3-slot cap. One slot per instrument maximum.
        ''' SlotManager enforces all remaining rules (bar gate, same-instrument counter-trend,
        ''' Exiting health, total cap).
        ''' </summary>
        Private Async Function EvaluateSlotEntriesAsync(barCache As Dictionary(Of Integer, IList(Of MarketBar))) As Task
            _logger.LogInformation("ST+ EvaluateSlotEntries tick — barCache={Count} instruments, openSlots={Open}",
                                   barCache.Count, _slotManager.OpenSlotCount)

            ' Guard: do not attempt entries without a valid account — avoids open/close slot loop.
            If _selectedAccount Is Nothing OrElse _selectedAccount.Id = 0 Then
                _logger.LogWarning("ST+ EvaluateSlotEntries BLOCKED — no valid account (selectedAccount={Acct})",
                                   If(_selectedAccount Is Nothing, "null", $"{_selectedAccount.Name} id={_selectedAccount.Id}"))
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            StatusText = "⚠ No account loaded — waiting before entering trades"
                                                            StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H0))
                                                        End Sub)
                Return
            End If

            ' ── Pass 1: evaluate all instruments, update watchlist rows, collect favourable candidates ──
            Dim candidates As New List(Of (ContractId As String, InstrumentIndex As Integer,
                                           Side As String, AdxVal As Single, BarTime As DateTimeOffset,
                                           StLine As Decimal, LastClose As Decimal))

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim bars As IList(Of MarketBar) = Nothing
                If Not barCache.TryGetValue(i, bars) OrElse bars Is Nothing OrElse bars.Count < 14 Then
                    Continue For
                End If

                ' Skip instruments whose contract ID failed to resolve
                If _contractResolver.FailedSymbols.Contains(contractId, StringComparer.OrdinalIgnoreCase) Then
                    _logger.LogWarning("ST+ EvaluateSlotEntries [{Contract}] SKIP — contract resolution failed.", contractId)
                    Continue For
                End If

                Dim highs = bars.Select(Function(b) b.High).ToList()
                Dim lows = bars.Select(Function(b) b.Low).ToList()
                Dim closes = bars.Select(Function(b) b.Close).ToList()
                Dim n = bars.Count - 1

                Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim stDir = st.Direction(n)
                Dim stLine = CDec(st.Line(n))
                Dim adxVal = dmi.ADX(n)
                Dim plusDi = dmi.PlusDI(n)
                Dim minusDi = dmi.MinusDI(n)

                Dim prevDir As Single = 0F
                _prevStDirByInstrument.TryGetValue(contractId, prevDir)
                Dim isFlip As Boolean = prevDir <> 0F AndAlso stDir <> prevDir AndAlso stDir <> 0F
                _prevStDirByInstrument(contractId) = stDir

                Dim isLong As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
                Dim isShort As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso minusDi > plusDi
                Dim isActive As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= PersonaMinAdx
                Dim isFavourable As Boolean = (isLong OrElse isShort) AndAlso (isFlip OrElse isActive)

                ' Re-entry cooldown: skip if released within the last bar duration
                If isFavourable AndAlso Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                    String.Equals(s.Instrument, contractId, StringComparison.OrdinalIgnoreCase)) Then
                    Dim cooldownUntil As DateTimeOffset
                    If _reEntryCooldown.TryGetValue(contractId, cooldownUntil) Then
                        Dim barMinutes As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                        If _selectedTimeframe.EndsWith("hr") Then barMinutes *= 60
                        If DateTimeOffset.UtcNow < cooldownUntil.AddMinutes(barMinutes) Then
                            _logger.LogInformation("ST+ [{Contract}] re-entry cooldown active — skipping until {Until:HH:mm:ss}",
                                                   contractId, cooldownUntil.AddMinutes(barMinutes).UtcDateTime)
                            isFavourable = False
                        End If
                    End If
                End If

                ' Monday morning 1H SuperTrend gate (FEAT-37)
                If isFavourable AndAlso Config.MondayMorningHtfFilterEnabled AndAlso IsMonMorningGateActive() Then
                    Dim htfAligned = Await Is1HourSuperTrendAlignedAsync(contractId, isLong)
                    If Not htfAligned Then
                        _logger.LogInformation(
                            "ST+ [{Contract}] Monday morning HTF gate — 1H ST disagrees, blocking entry.", contractId)
                        isFavourable = False
                    End If
                End If

                _logger.LogInformation(
                    "ST+ [{Contract}] stDir={StDir} ADX={Adx:F1} +DI={PlusDI:F1} -DI={MinusDI:F1} " &
                    "isLong={IsLong} isShort={IsShort} isFlip={IsFlip} isActive={IsActive} isFavourable={IsFav}",
                    contractId, stDir, adxVal, plusDi, minusDi, isLong, isShort, isFlip, isActive, isFavourable)

                Dim adxStr = If(Single.IsNaN(adxVal), "ADX:--",
                               If(adxVal >= Config.AdxStrongThreshold, String.Format("ADX:{0:D2} L3: Espresso", CInt(adxVal)),
                               If(adxVal >= Config.AdxModerateThreshold, String.Format("ADX:{0:D2} L2: Latte", CInt(adxVal)),
                               If(adxVal >= Config.AdxWeakThreshold, String.Format("ADX:{0:D2} L0: Decaff", CInt(adxVal)),
                                  String.Format("ADX:{0:D2}", CInt(adxVal))))))

                If Not isFavourable Then
                    UpdateSlotSymbolRows(i, "--", adxStr, "flat", Brushes.White)
                    Continue For
                End If

                Dim side = If(isLong, "Buy", "Sell")
                Dim barTime = bars(n).Timestamp
                Dim sigLabel = If(isLong, "LONG", "SHORT")
                Dim sigColor As Brush = If(isLong, Brushes.LimeGreen, Brushes.Red)
                UpdateSlotSymbolRows(i, If(isLong, "UP", "DN"), adxStr, sigLabel, sigColor)

                ' Skip if this instrument already has an in-memory slot open
                If _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                    String.Equals(s.Instrument, contractId, StringComparison.OrdinalIgnoreCase)) Then
                    Continue For
                End If

                candidates.Add((contractId, i, side, adxVal, barTime, stLine, CDec(closes(n))))
            Next

            ' ── Pass 2: sort candidates strongest → weakest (ADX descending), then fill slots sequentially ──
            Dim ranked = candidates.OrderByDescending(Function(c) c.AdxVal).ToList()
            _logger.LogInformation("ST+ EvaluateSlotEntries — {Count} new candidate(s) ranked by ADX: {List}",
                                   ranked.Count,
                                   String.Join(", ", ranked.Select(Function(c) $"{c.ContractId}({c.AdxVal:F1})")))

            For Each candidate In ranked
                ' Stop once slot cap is reached
                If _slotManager.OpenSlotCount >= Config.MaxSlots Then
                    _logger.LogInformation("ST+ EvaluateSlotEntries — slot cap reached ({Open}/{Max}), stopping entry evaluation.",
                                           _slotManager.OpenSlotCount, Config.MaxSlots)
                    Exit For
                End If

                ' Guard: skip if a FireEntryAsync call for this instrument is already in-flight
                ' (prevents duplicate market orders on the next 15-second tick while PlaceOrder awaits)
                If _slotManager.Slots.Any(Function(s) s.IsEntryInFlight AndAlso
                    String.Equals(s.Instrument, candidate.ContractId, StringComparison.OrdinalIgnoreCase)) Then
                    _logger.LogInformation("ST+ [{Contract}] entry in-flight, skipping re-evaluation this tick.", candidate.ContractId)
                    Continue For
                End If

                ' Guard: skip if AI veto suppression is still active — avoids opening and immediately
                ' closing a slot every 15 s while the cooldown window is in effect.
                SyncLock _aiSuppression
                    Dim aiSu As DateTimeOffset
                    If _aiSuppression.TryGetValue(candidate.ContractId, aiSu) AndAlso DateTimeOffset.UtcNow < aiSu Then
                        _logger.LogDebug("ST+ [{Contract}] AI suppression until {Until:HH:mm:ss} UTC — skipping",
                                         candidate.ContractId, aiSu.UtcDateTime)
                        Continue For
                    End If
                End SyncLock

                ' Guard: verify no live position already exists on the exchange for this instrument
                Dim guardAccId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0)
                If guardAccId <> 0 Then
                    Try
                        Dim liveCheck = Await _orderService.GetLivePositionSnapshotAsync(guardAccId, candidate.ContractId)
                        If liveCheck IsNot Nothing Then
                            _logger.LogInformation("ST+ [{Contract}] live position still open on exchange (units={Units}), skipping re-entry.",
                                                   candidate.ContractId, liveCheck.Units)
                            Continue For
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ live-position guard check failed for {Contract} — proceeding with caution", candidate.ContractId)
                    End Try
                End If

                Dim opened = _slotManager.TryOpenSlot(candidate.ContractId, candidate.Side, candidate.AdxVal,
                                                      candidate.BarTime, candidate.StLine, candidate.LastClose)
                If opened IsNot Nothing Then
                    _logger.LogInformation("ST+ SlotManager opened slot {Idx} for {Contract} {Side} ADX={Adx:F1} (rank by ADX)",
                                           opened.SlotIndex, candidate.ContractId, candidate.Side, candidate.AdxVal)
                    Await FireEntryAsync(opened, candidate.ContractId, candidate.Side,
                                        candidate.StLine, candidate.LastClose, candidate.BarTime)
                    Await Task.Delay(EntryStaggerMs)
                Else
                    _logger.LogInformation("ST+ SlotManager blocked slot for {Contract} (openCount={Open}/{Max})",
                                           candidate.ContractId, _slotManager.OpenSlotCount, Config.MaxSlots)
                End If
            Next
        End Function

        ''' <summary>Returns True if the current UK local time is Monday before 08:00 (BST-aware).</summary>
        Private Shared Function IsMonMorningGateActive() As Boolean
            Dim ukTz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")
            Dim ukNow = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.UtcNow.UtcDateTime, ukTz)
            Return ukNow.DayOfWeek = DayOfWeek.Monday AndAlso ukNow.Hour < 8
        End Function

        ''' <summary>
        ''' Fetches 1H bars for <paramref name="contractId"/> and checks whether the last
        ''' completed 1H SuperTrend direction matches <paramref name="isLong"/>.
        ''' Returns True (allow entry) when data is insufficient or the direction agrees.
        ''' </summary>
        Private Async Function Is1HourSuperTrendAlignedAsync(contractId As String, isLong As Boolean) As Task(Of Boolean)
            Try
                Dim bars1H = Await _barService.GetLiveBarsAsync(contractId, BarTimeframe.OneHour, 50)
                If bars1H Is Nothing OrElse bars1H.Count < 10 Then Return True
                Dim highs = bars1H.Select(Function(b) b.High).ToList()
                Dim lows = bars1H.Select(Function(b) b.Low).ToList()
                Dim closes = bars1H.Select(Function(b) b.Close).ToList()
                Dim st1H = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dir = st1H.Direction(bars1H.Count - 1)
                If Single.IsNaN(dir) OrElse dir = 0.0F Then Return True
                Return If(isLong, dir > 0, dir < 0)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ Monday morning 1H check failed for {Contract} — allowing entry", contractId)
                Return True
            End Try
        End Function

        ''' <summary>
        ''' Early-mode entry: multi-signal early reversal trigger, then ADX-band slot count.
        ''' </summary>
        Private Async Function EvaluateEarlyEntrySequenceAsync(tf As BarTimeframe,
                                                                barCache As Dictionary(Of Integer, IList(Of MarketBar))) As Task
            ' Guard: do not attempt entries without a valid account.
            If _selectedAccount Is Nothing OrElse _selectedAccount.Id = 0 Then
                _logger.LogWarning("ST+ EvaluateEarlyEntry BLOCKED — no valid account")
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            StatusText = "⚠ No account loaded — waiting before entering trades"
                                                            StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H0))
                                                        End Sub)
                Return
            End If

            Dim bestContractId As String = Nothing
            Dim bestSide As String = Nothing
            Dim bestStLine As Decimal = 0D
            Dim bestLastClose As Decimal = 0D
            Dim bestAdxVal As Single = 0F
            Dim bestBarTime As DateTimeOffset = DateTimeOffset.MinValue

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim bars15 As IList(Of MarketBar) = Nothing
                barCache.TryGetValue(i, bars15)
                If bars15 Is Nothing OrElse bars15.Count < 15 Then Continue For

                If _contractResolver.FailedSymbols.Contains(contractId, StringComparer.OrdinalIgnoreCase) Then
                    _logger.LogWarning("ST+ EvaluateEarlyEntry [{Contract}] SKIP — contract resolution failed.", contractId)
                    Continue For
                End If
                Dim bars5 As IList(Of MarketBar)
                Try
                    bars5 = Await _barService.GetLiveBarsAsync(contractId, BarTimeframe.FiveMinute, BarsToFetch)
                Catch
                    Continue For
                End Try
                If bars5 Is Nothing OrElse bars5.Count < 15 Then Continue For
                If bars5.Count > 1 Then
                    Dim age5 = (DateTime.UtcNow - bars5.Last().Timestamp).TotalMinutes
                    If age5 < 5 Then bars5 = bars5.Take(bars5.Count - 1).ToList()
                End If

                Dim highs15 = bars15.Select(Function(b) b.High).ToList()
                Dim lows15 = bars15.Select(Function(b) b.Low).ToList()
                Dim closes15 = bars15.Select(Function(b) b.Close).ToList()
                Dim highs5 = bars5.Select(Function(b) b.High).ToList()
                Dim lows5 = bars5.Select(Function(b) b.Low).ToList()
                Dim closes5 = bars5.Select(Function(b) b.Close).ToList()

                Dim st15 = TechnicalIndicators.SuperTrend(highs15, lows15, closes15, period:=10, multiplier:=_stMultiplier)
                Dim dmi = TechnicalIndicators.DMI(highs15, lows15, closes15, period:=14)
                Dim st5 = TechnicalIndicators.SuperTrend(highs5, lows5, closes5, period:=10, multiplier:=_stMultiplier)
                Dim n15 = bars15.Count - 1
                Dim n5 = bars5.Count - 1

                Dim stDir15 = st15.Direction(n15)
                Dim stLine15 = CDec(st15.Line(n15))
                Dim adxVal = dmi.ADX(n15)
                Dim plusDi = dmi.PlusDI(n15)
                Dim minusDi = dmi.MinusDI(n15)
                Dim stDir5 = st5.Direction(n5)

                Dim lastClose15 = closes15(n15)
                Dim dist = Math.Abs(lastClose15 - stLine15)
                Dim atr14 = TechnicalIndicators.ATR(highs15, lows15, closes15, period:=14)
                Dim atrN = If(atr14 IsNot Nothing AndAlso atr14.Length > n15, CDec(atr14(n15)), 0D)

                Dim sig1 As Boolean = atrN > 0D AndAlso dist <= 1.5D * atrN
                Dim sig2 As Boolean = UpdateApproachHistory(contractId, stDir15, dist)
                Dim spreadDI As Single = Math.Abs(plusDi - minusDi)
                Dim anticipatedLong As Boolean = stDir15 < 0
                Dim sig3 As Boolean = If(anticipatedLong, plusDi > minusDi, minusDi > plusDi) OrElse spreadDI < 5
                Dim sig4 As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= 20.0F
                Dim sig5 As Boolean = If(anticipatedLong, stDir5 > 0, stDir5 < 0)
                Dim earlySignal As Boolean = sig1 AndAlso sig2 AndAlso sig3 AndAlso sig4 AndAlso sig5

                ' Re-entry cooldown for early-mode: skip instrument released within last bar
                If earlySignal AndAlso Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = contractId) Then
                    Dim cooldownUntilE As DateTimeOffset
                    If _reEntryCooldown.TryGetValue(contractId, cooldownUntilE) Then
                        Dim barMinsE As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                        If _selectedTimeframe.EndsWith("hr") Then barMinsE *= 60
                        If DateTimeOffset.UtcNow < cooldownUntilE.AddMinutes(barMinsE) Then
                            earlySignal = False
                        End If
                    End If
                End If

                Dim side As String = If(anticipatedLong, "Buy", "Sell")
                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--",
                                          If(adxVal >= Config.AdxStrongThreshold, String.Format("ADX:{0:D2} L3: Espresso", CInt(adxVal)),
                                          If(adxVal >= Config.AdxModerateThreshold, String.Format("ADX:{0:D2} L2: Latte", CInt(adxVal)),
                                          If(adxVal >= Config.AdxWeakThreshold, String.Format("ADX:{0:D2} L0: Decaff", CInt(adxVal)),
                                             String.Format("ADX:{0:D2}", CInt(adxVal))))))
                Dim signalLabel As String = If(earlySignal, "EARLY", "flat")
                Dim sigColor As Brush = If(earlySignal, Brushes.Goldenrod, Brushes.White)
                UpdateSlotSymbolRows(i, If(anticipatedLong, "UP", "DN"), adxStr, signalLabel, sigColor)

                If Not earlySignal Then Continue For

                Dim barTime = bars15(n15).Timestamp
                Dim hasOpenSlot = _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = contractId)
                If bestContractId Is Nothing OrElse
                   hasOpenSlot OrElse
                   (Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = bestContractId) AndAlso adxVal > bestAdxVal) Then
                    bestContractId = contractId
                    bestSide = side
                    bestStLine = stLine15
                    bestLastClose = CDec(lastClose15)
                    bestAdxVal = adxVal
                    bestBarTime = barTime
                End If
            Next

            If bestContractId Is Nothing Then Return

            ' Guard: skip if a FireEntryAsync call for this instrument is already in-flight
            If _slotManager.Slots.Any(Function(s) s.IsEntryInFlight AndAlso
                String.Equals(s.Instrument, bestContractId, StringComparison.OrdinalIgnoreCase)) Then
                _logger.LogInformation("ST+ EvaluateEarlyEntry — [{Contract}] entry in-flight, skipping this tick.", bestContractId)
                Return
            End If

            ' Guard: only block true re-entries — skip when an in-memory slot already tracks
            ' this instrument (scale-in path).
            Dim hasEarlyInMemorySlot As Boolean = _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = bestContractId)
            If Not hasEarlyInMemorySlot Then
                Dim earlyGuardAccId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0)
                If earlyGuardAccId <> 0 Then
                    Try
                        Dim liveCheck2 = Await _orderService.GetLivePositionSnapshotAsync(earlyGuardAccId, bestContractId)
                        If liveCheck2 IsNot Nothing Then
                            _logger.LogInformation("ST+ EvaluateEarlyEntry — live position still open for {Contract} (units={Units}), skipping re-entry.",
                                                   bestContractId, liveCheck2.Units)
                            Return
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ early live-position guard failed for {Contract} — proceeding", bestContractId)
                    End Try
                End If
            End If

            Dim opened = _slotManager.TryOpenSlot(bestContractId, bestSide, bestAdxVal, bestBarTime, bestStLine, bestLastClose)
            If opened IsNot Nothing Then
                opened.IsEarlyModeEntry = True   ' E1 suppressed until ST confirms (BUG-49)
                Await FireEntryAsync(opened, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)
            End If
        End Function

        Private Sub UpdateSlotSymbolRows(instrIdx As Integer,
                                          arrow As String,
                                          adxDisplay As String,
                                          signal As String,
                                          color As Brush)
            For Each box In AllSlotBoxes()
                Dim row = box.Symbols(instrIdx)
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        row.Arrow = arrow
                        row.AdxDisplay = adxDisplay
                        row.Signal = signal
                        row.RowColor = color
                    End Sub)
            Next
        End Sub

        ''' <summary>
        ''' On each tick, checks for live broker positions that are not yet tracked in any slot.
        ''' Orphaned positions are adopted into the next free slot so that SL ratcheting,
        ''' degradation scoring, P&amp;L display, and exit logic run as normal.
        ''' If the current ADX is L2 (40–59), 1 additional contract is scaled in.
        ''' If L3 (60+), 2 additional contracts are scaled in.
        ''' </summary>
        Private Async Function ReconcileOpenPositionsAsync(barCache As Dictionary(Of Integer, IList(Of MarketBar))) As Task
            Dim accountId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0)
            If accountId = 0 Then Return

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)

                ' Skip if a slot already tracks this instrument
                If _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                        String.Equals(s.Instrument, contractId, StringComparison.OrdinalIgnoreCase)) Then
                    Continue For
                End If

                ' Check for a live broker position
                Dim snapshot As Core.Models.LivePositionSnapshot = Nothing
                Try
                    snapshot = Await _orderService.GetLivePositionSnapshotAsync(accountId, contractId)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ Reconcile [{Contract}] snapshot check failed", contractId)
                    Continue For
                End Try
                If snapshot Is Nothing OrElse snapshot.Units <= 0 Then Continue For

                ' Find a free slot
                Dim freeSlot = _slotManager.Slots.FirstOrDefault(Function(s) Not s.IsOpen)
                If freeSlot Is Nothing Then
                    _logger.LogInformation("ST+ Reconcile [{Contract}] live position found but no free slot available", contractId)
                    Continue For
                End If

                ' Compute current SuperTrend line from barCache so we have a valid stop price
                Dim bars As IList(Of MarketBar) = Nothing
                barCache.TryGetValue(i, bars)
                Dim stLine As Decimal = snapshot.OpenRate  ' fallback to entry price if no bars
                Dim currentAdx As Single = 0F
                If bars IsNot Nothing AndAlso bars.Count >= 14 Then
                    Dim highs = bars.Select(Function(b) b.High).ToList()
                    Dim lows = bars.Select(Function(b) b.Low).ToList()
                    Dim cls = bars.Select(Function(b) b.Close).ToList()
                    Dim st = TechnicalIndicators.SuperTrend(highs, lows, cls, period:=10, multiplier:=_stMultiplier)
                    Dim dmi = TechnicalIndicators.DMI(highs, lows, cls, period:=14)
                    Dim n = bars.Count - 1
                    stLine = CDec(st.Line(n))
                    If Not Single.IsNaN(dmi.ADX(n)) Then currentAdx = dmi.ADX(n)
                End If

                ' Warn and optionally skip if ADX is below persona minimum (defence-in-depth: BUG-53)
                If currentAdx < PersonaMinAdx Then
                    _logger.LogWarning(
                        "ST+ Reconcile [{Contract}] ADX={Adx:F1} is below PersonaMinAdx={Min} — onboarding with warning (position not placed by this app)",
                        contractId, currentAdx, PersonaMinAdx)
                End If

                Dim side As String = If(snapshot.IsBuy, "Buy", "Sell")
                Dim baseContracts As Integer = CInt(Math.Max(1, Math.Round(snapshot.Units)))

                ' Populate the slot directly (bypasses bar-gate / counter-trend rules that
                ' don't apply to positions already live on the exchange)
                Dim s2 = _slotManager.Slots(freeSlot.SlotIndex)
                s2.Instrument = contractId
                s2.Side = side
                s2.EntryAdx = currentAdx
                s2.CurrentAdx = currentAdx
                s2.EntryBarTime = snapshot.OpenedAtUtc
                s2.EntryPrice = snapshot.OpenRate
                s2.StopPrice = stLine
                s2.Contracts = baseContracts
                s2.IsOpen = True
                s2.Health = Core.Enums.SlotHealth.Healthy
                s2.MissCount = 0
                s2.ConsecutiveExitBars = 0
                s2.UnrealizedPnl = 0D
                s2.EntryReason = $"Onboarded (ADX {CInt(currentAdx)})"
                s2.StopPhase = Core.Enums.StopPhase.Initial
                s2.AccountId = accountId
                s2.EntryTime = DateTime.Now
                s2.PositionId = snapshot.PositionId

                _logger.LogInformation(
                    "ST+ Reconcile [{Contract}] onboarded into Slot {Idx}: side={Side} entry={Entry} stop={Stop} contracts={Qty} ADX={Adx:F1}",
                    contractId, s2.SlotIndex, side, snapshot.OpenRate, stLine, baseContracts, currentAdx)

                ' ── Scale-in based on current ADX band ──────────────────────────────────
                Dim extraContracts As Integer = 0
                If currentAdx >= Config.AdxStrongThreshold Then
                    extraContracts = 2   ' L3: Espresso
                ElseIf currentAdx >= Config.AdxModerateThreshold Then
                    extraContracts = 1   ' L2: Latte
                End If

                If extraContracts > 0 Then
                    Dim scaleOrder As New Core.Models.Order With {
                        .AccountId = accountId,
                        .ContractId = contractId,
                        .Side = If(side = "Buy", Core.Enums.OrderSide.Buy, Core.Enums.OrderSide.Sell),
                        .Quantity = extraContracts,
                        .OrderType = Core.Enums.OrderType.Market,
                        .InitialStopTicks = Nothing,
                        .InitialTakeProfitTicks = Nothing
                    }
                    Dim placed As Core.Models.Order = Nothing
                    Try
                        placed = Await _orderService.PlaceOrderAsync(scaleOrder)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ Reconcile [{Contract}] scale-in order failed", contractId)
                    End Try

                    Dim accepted = placed IsNot Nothing AndAlso
                                   (placed.Status = Core.Enums.OrderStatus.Working OrElse
                                    placed.Status = Core.Enums.OrderStatus.Filled)
                    If accepted Then
                        s2.Contracts += extraContracts
                        _logger.LogInformation(
                            "ST+ Reconcile [{Contract}] scale-in +{Extra} contracts (ADX {Band}). Total contracts now {Total}",
                            contractId, extraContracts,
                            If(currentAdx >= Config.AdxStrongThreshold, "L3: Espresso", "L2: Latte"),
                            s2.Contracts)
                    Else
                        _logger.LogWarning(
                            "ST+ Reconcile [{Contract}] scale-in order not accepted (status={Status}), slot keeps base contracts={Base}",
                            contractId, placed?.Status, baseContracts)
                    End If
                End If

                ' Update box UI
                Dim box = BoxForSlot(s2)
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        If box IsNot Nothing Then
                            box.IdleMonitorText = String.Empty
                            box.HasPosition = True
                            UpdatePositionDisplay(box, s2, 0D)
                            Task.Run(Async Function() As Task
                                         Await box.FlashBorderAsync()
                                     End Function)
                        End If
                    End Sub)
            Next
        End Function

        Private Async Function FireEntryAsync(slot As PositionSlot,
                                               contractId As String,
                                               side As String,
                                               stLine As Decimal,
                                               lastClose As Decimal,
                                               barTime As DateTimeOffset) As Task
            _logger.LogInformation("ST+ FireEntry [Slot {Idx}] {Side} {Contract} — resolving account...",
                                   slot.SlotIndex, side, contractId)

            Dim accountId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0)
            If accountId = 0 Then
                _logger.LogWarning("ST+ FireEntry [Slot {Idx}] BLOCKED — accountId=0. SelectedAccount={Acct}",
                                   slot.SlotIndex,
                                   If(_selectedAccount Is Nothing, "null", $"{_selectedAccount.Name} id={_selectedAccount.Id} canTrade={_selectedAccount.CanTrade}"))
                _slotManager.CloseSlot(slot.SlotIndex)
                Return
            End If
            slot.AccountId = accountId

            ' Guard: abort if the live price has already crossed the ST line before the order fills.
            ' The signal bar may have closed with price on the correct side, but a gap open (e.g. 09:30 ET)
            ' can push the market through the ST line before the market order executes.
            Try
                Dim guardBars = Await _barService.GetLiveBarsAsync(contractId, BarTimeframe.FifteenSecond, 3)
                If guardBars IsNot Nothing AndAlso guardBars.Count > 0 Then
                    Dim livePrice = CDec(guardBars(guardBars.Count - 1).Close)
                    Dim isSell = String.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase)
                    If (isSell AndAlso livePrice > stLine) OrElse (Not isSell AndAlso livePrice < stLine) Then
                        _logger.LogWarning("ST+ [{Contract}] entry aborted — live price {Live:F2} crossed ST line {St:F2} before fill",
                                           contractId, livePrice, stLine)
                        _slotManager.CloseSlot(slot.SlotIndex)
                        Return
                    End If
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ [{Contract}] live-price guard fetch failed — proceeding with entry", contractId)
            End Try

            ' Keep the 15-second scan interval — do NOT accelerate to 2 s here.
            ' Accelerating caused rapid-fire re-entries when a bracket filled quickly.

            Dim oSide As OrderSide = If(side = "Buy", OrderSide.Buy, OrderSide.Sell)
            Dim fc As FavouriteContract = FavouriteContracts.TryGetBySymbolResolved(contractId, _contractResolver)
            Dim stopTicks As Integer? = Nothing
            If fc IsNot Nothing AndAlso fc.PxTickSize > 0D Then
                Dim rawDist As Decimal = Math.Abs(lastClose - stLine)
                Dim rawTicks As Integer = CInt(Math.Round(rawDist / fc.PxTickSize))
                Dim minTicks As Integer = 1
                If fc.PxMinStopDollars > 0D AndAlso fc.PxTickValue > 0D Then
                    minTicks = CInt(Math.Ceiling(fc.PxMinStopDollars / fc.PxTickValue))
                End If
                stopTicks = Math.Max(rawTicks, minTicks)
            End If
            _logger.LogInformation("ST+ bracket for {Contract}: SL={SL} ticks (flip-only; no hard TP) lastClose={Close}, stLine={St}",
                                   contractId, If(stopTicks.HasValue, stopTicks.Value.ToString(), "none"),
                                   lastClose, stLine)

            ' ── AI pre-trade sense check (gated by IsAiEnabled toggle) ─────────────
            Dim capturedAiResult As String = Nothing
            Dim capturedAiReason As String = Nothing
            If _isAiEnabled AndAlso _claudeService IsNot Nothing Then
                Dim isSuppressed As Boolean = False
                SyncLock _aiSuppression
                    Dim suppressedUntil As DateTimeOffset
                    If _aiSuppression.TryGetValue(contractId, suppressedUntil) AndAlso
                       DateTimeOffset.UtcNow < suppressedUntil Then
                        isSuppressed = True
                        _logger.LogInformation("ST+ AI pre-trade check suppressed for {Contract} until {Until:HH:mm:ss} UTC",
                                               contractId, suppressedUntil.UtcDateTime)
                    End If
                End SyncLock

                If isSuppressed Then
                    ' A prior VETO suppression window is still active — block this entry.
                    _slotManager.CloseSlot(slot.SlotIndex)
                    Return
                End If

                Try
                    Dim barsForAi As IList(Of MarketBar) = Nothing
                    Try
                        ' 4 hours worth of bars at the selected timeframe
                        Dim tf2 = MapTimeframe(_selectedTimeframe)
                        Dim tfMins As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                        If _selectedTimeframe.EndsWith("hr") Then tfMins *= 60
                        Dim barsNeeded As Integer = Math.Max(30, CInt(Math.Ceiling(240.0 / tfMins)))
                        barsForAi = Await _barService.GetLiveBarsAsync(contractId, tf2, barsNeeded)
                    Catch
                    End Try

                    Dim exitDesc As String = $"SuperTrend+ flip-only exit — no hard TP bracket. " &
                                             $"SL placed at SuperTrend line ({If(stopTicks.HasValue, $"{stopTicks.Value} ticks", "TBD")} from entry at {stLine:F2}). " &
                                             $"Persona: {_activePersona} (RR target {PersonaRrRatio:F2}R, MinADX {PersonaMinAdx:F0}). " &
                                             "Position managed via phased stop ratcheting and 9-signal degradation monitor."
                    Dim ctx As New PreTradeContext With {
                        .ContractId = contractId,
                        .ContractDescription = contractId,
                        .Side = side,
                        .Price = lastClose,
                        .AdxValue = 0F,
                        .TpMultiple = 0D,
                        .UtcNow = DateTimeOffset.UtcNow,
                        .StrategyName = "SuperTrend+ Autopilot",
                        .ExitStrategyDescription = exitDesc
                    }
                    Using cts = New System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8))
                        Dim aiResult = Await _claudeService.PreTradeCheckAsync(ctx, cts.Token)
                        If Not aiResult.Proceed Then
                            ' NO — block this signal, suppress for 15 minutes
                            Dim shortReason = If(aiResult.Reasoning.Length > 120,
                                                 aiResult.Reasoning.Substring(0, 117) & "…",
                                                 aiResult.Reasoning)
                            _logger.LogInformation("ST+ AI VETO [{Contract}]: {Reason}", contractId, shortReason)
                            SyncLock _aiSuppression
                                _aiSuppression(contractId) = DateTimeOffset.UtcNow.AddMinutes(15)
                            End SyncLock
                            AddAiLogEntry(contractId, $"VETO — {shortReason}")
                            ' Update watchlist row with the rejection reason
                            Dim wIdx As Integer = Array.IndexOf(Instruments, contractId)
                            If wIdx >= 0 Then
                                Dim wRow = WatchlistItems(wIdx)
                                Application.Current?.Dispatcher?.Invoke(
                                    Sub()
                                        wRow.SignalReason = $"🤖 AI: {shortReason}"
                                    End Sub)
                            End If
                            _slotManager.CloseSlot(slot.SlotIndex)
                            Return
                        End If
                        ' YES — update watchlist row with green confirmation
                        Dim wIdxOk As Integer = Array.IndexOf(Instruments, contractId)
                        If wIdxOk >= 0 Then
                            Application.Current?.Dispatcher?.Invoke(
                            Sub()
                                WatchlistItems(wIdxOk).SignalReason = "🤖 AI Checked ✓"
                            End Sub)
                        End If
                        AddAiLogEntry(contractId, "Pre-trade check PASSED ✓")
                        capturedAiResult = "PASSED"
                        capturedAiReason = If(aiResult.Reasoning.Length > 500,
                                         aiResult.Reasoning.Substring(0, 497) & "...",
                                         aiResult.Reasoning)
                    End Using
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ AI pre-trade check error for {Contract} — proceeding anyway", contractId)
                End Try
            End If

            ' Every instrument's first open slot is primary and gets brackets.
            ' Scale-ins for the *same* instrument at a lower slot index would suppress brackets,
            ' but TryOpenSlot already blocks a 2nd slot for the same instrument, so isPrimary is
            ' always True in practice.  The old check used slot index alone (ignoring instrument),
            ' which incorrectly marked M6E/M2K as scale-ins when MGC occupied slot 0 — BUG-40b.
            Dim isPrimary As Boolean =
                Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                                                        s.SlotIndex < slot.SlotIndex AndAlso
                                                        String.Equals(s.Instrument, contractId, StringComparison.OrdinalIgnoreCase))
            _logger.LogDebug("ST+ FireEntry {Contract} slot={Slot} isPrimary={IsPrimary} stopTicks={Stop} side={Side}",
                             contractId, slot.SlotIndex, isPrimary, stopTicks, oSide)
            Dim order As New Order With {
                .AccountId = slot.AccountId,
                .ContractId = contractId,
                .Side = oSide,
                .Quantity = slot.Contracts,
                .OrderType = OrderType.Market,
                .InitialStopTicks = If(isPrimary, stopTicks, Nothing),
                .InitialTakeProfitTicks = Nothing
            }
            ' Guard: abort order placement if monitoring was stopped while FireEntryAsync was awaiting.
            If Not _isMonitoring Then
                _logger.LogWarning("ST+ FireEntry [{Contract}] BLOCKED — monitoring stopped before order placement.", contractId)
                _slotManager.CloseSlot(slot.SlotIndex)
                Return
            End If

            Dim placed As Order = Nothing
            Try
                placed = Await _orderService.PlaceOrderAsync(order)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ PlaceOrderAsync failed for {Contract}", contractId)
            End Try

            Dim isAccepted = placed IsNot Nothing AndAlso
                             (placed.Status = OrderStatus.Working OrElse placed.Status = OrderStatus.Filled)
            If Not isAccepted Then
                _logger.LogWarning("ST+ order not accepted for {Contract}: status={Status}", contractId, placed?.Status)
                _slotManager.CloseSlot(slot.SlotIndex)   ' CloseSlot resets IsEntryInFlight
                If Not _slotManager.Slots.Any(Function(s) s.IsOpen) Then
                    SyncLock _timerLock

                        _timer?.Change(15000, 15000)
                    End SyncLock
                End If
                Return
            End If

            ' Order accepted — clear in-flight flag so normal monitoring takes over
            slot.IsEntryInFlight = False
            slot.StopPrice = stLine
            slot.EntryTime = DateTime.Now
            slot.EntryBarTime = barTime

            ' ── Debug Capture: begin trade record (FEAT-39) ──────────────────────
            If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing Then
                Dim newTradeId = Guid.NewGuid().ToString("D")
                slot.DebugTradeId = newTradeId
                Dim configJson As String = String.Empty
                Try
                    configJson = JsonSerializer.Serialize(Config)
                Catch
                End Try
                Dim header As New DebugTradeRecord With {
                    .TradeId = newTradeId,
                    .SlotIndex = slot.SlotIndex,
                    .Persona = _activePersona,
                    .Instrument = contractId,
                    .TimeFrame = _selectedTimeframe,
                    .EntryMode = If(_useEarlyMode, "Preemptive", "BarClose"),
                    .Direction = If(side = "Buy", "Long", "Short"),
                    .EntryPrice = CDec(lastClose),
                    .EntryTime = DateTime.UtcNow.ToString("O"),
                    .InitialSL = stLine,
                    .InitialTP = 0D,
                    .ContractCount = slot.Contracts,
                    .SuperTrendConfigJson = configJson,
                    .AiCheckResult = capturedAiResult,
                    .AiCheckReason = capturedAiReason,
                    .CreatedAt = DateTime.UtcNow.ToString("O")
                }
                _debugCapture.BeginTrade(header)
            End If
            slot.MissCount = 0
            slot.PositionId = placed.ExternalPositionId   ' may be Nothing until first monitoring tick resolves it
            slot.EntryOrderId = placed.ExternalOrderId
            slot.EntryPrice = 0D
            slot.TakeProfitPrice = 0D
            slot.StopPhase = StopPhase.Initial
            slot.InitialRisk = 0D  ' computed once EntryPrice is confirmed
            slot.EntryAtr = 0D  ' set from bar data below

            ' Persist opening trade record to TradeHistory.db
            Task.Run(Async Function()
                         Try
                             Dim fcRec = FavouriteContracts.TryGetBySymbolResolved(contractId, _contractResolver)
                             Dim persona = _personaService.GetProfile(_activePersona)
                             Dim commission = 0.5D * slot.Contracts
                             Dim fees = If(fcRec IsNot Nothing, fcRec.RoundTripFee * slot.Contracts, 0.8D * slot.Contracts)
                             Dim displaySymbol = If(fcRec IsNot Nothing, "/" & fcRec.Name, contractId)
                             Dim rec As New Core.Models.LiveTradeRecord With {
                                 .EntryOrderId = If(slot.EntryOrderId.HasValue, slot.EntryOrderId.Value, 0),
                                 .ContractId = contractId,
                                 .Symbol = displaySymbol,
                                 .Direction = If(side = "Buy", "Long", "Short"),
                                 .Sizes = slot.Contracts,
                                 .MaxScaleIns = If(persona IsNot Nothing, persona.MaxScaleIns, 1),
                                 .StrategyName = "SuperTrend+",
                                 .Persona = _activePersona,
                                 .Timeframe = _selectedTimeframe,
                                 .EntryTime = DateTimeOffset.UtcNow,
                                 .EntryPrice = 0D,
                                 .CommissionUsd = commission,
                                 .FeesUsd = fees,
                                 .IsOpen = True
                             }
                             slot.TradeRecordId = Await _tradeRecordService.OpenTradeAsync(rec)
                         Catch ex As Exception
                             _logger.LogWarning(ex, "ST+ [Slot {Idx}] failed to open trade record for {Contract}", slot.SlotIndex, contractId)
                         End Try
                     End Function)

            ' Capture entry ATR from the bars that were used to fire the entry
            Try
                Dim entryBars = Await _barService.GetLiveBarsAsync(contractId, MapTimeframe(_selectedTimeframe), BarsToFetch)
                If entryBars IsNot Nothing AndAlso entryBars.Count >= 14 Then
                    Dim eHighs = entryBars.Select(Function(b) b.High).ToList()
                    Dim eLows = entryBars.Select(Function(b) b.Low).ToList()
                    Dim eCloses = entryBars.Select(Function(b) b.Close).ToList()
                    Dim atr14 = TechnicalIndicators.ATR(eHighs, eLows, eCloses, period:=14)
                    Dim eN = entryBars.Count - 1
                    If atr14 IsNot Nothing AndAlso atr14.Length > eN AndAlso Not Single.IsNaN(atr14(eN)) Then
                        slot.EntryAtr = CDec(atr14(eN))
                    End If
                End If
            Catch
            End Try

            Dim box = BoxForSlot(slot)
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    If box IsNot Nothing Then
                        ' Set instrument label and flash border to signal new occupant
                        box.IdleMonitorText = String.Empty
                        box.HasPosition = True
                        UpdatePositionDisplay(box, slot, 0D)
                        Task.Run(Async Function() As Task
                                     Await box.FlashBorderAsync()
                                 End Function)
                    End If
                End Sub)
        End Function

        ''' <summary>
        ''' Places a naked market order to add contracts to an open slot when ADX rises to a higher band.
        ''' No bracket is sent — the primary slot's existing bracket covers the aggregate position.
        ''' </summary>
        Private Async Function ScaleInSlotAsync(slot As PositionSlot, addContracts As Integer) As Task
            _logger.LogInformation("ST+ [Slot {Idx}] Scale-in +{Add} for {Contract} (total will be {Total})",
                                   slot.SlotIndex, addContracts, slot.Instrument, slot.Contracts + addContracts)
            Dim order As New Order With {
                .AccountId = slot.AccountId,
                .ContractId = slot.Instrument,
                .Side = If(slot.Side = "Buy", OrderSide.Buy, OrderSide.Sell),
                .Quantity = addContracts,
                .OrderType = OrderType.Market,
                .InitialStopTicks = Nothing,
                .InitialTakeProfitTicks = Nothing
            }
            Try
                Dim placed = Await _orderService.PlaceOrderAsync(order)
                Dim ok = placed IsNot Nothing AndAlso
                         (placed.Status = OrderStatus.Working OrElse placed.Status = OrderStatus.Filled)
                If ok Then
                    slot.Contracts += addContracts
                    _logger.LogInformation("ST+ [Slot {Idx}] Scale-in accepted — contracts now {Total}",
                                           slot.SlotIndex, slot.Contracts)
                    If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                        Dim fillSnap As New DebugSnapshotRecord With {
                            .TradeId = slot.DebugTradeId,
                            .Timestamp = DateTime.UtcNow.ToString("O"),
                            .EventType = "PartialFill",
                            .CurrentSL = slot.StopPrice,
                            .Notes = $"+{addContracts} contracts (total {slot.Contracts})"
                        }
                        _debugCapture.RecordSnapshot(fillSnap)
                    End If
                    Dim latestPnl = slot.UnrealizedPnl
                    Dim box = BoxForSlot(slot)
                    Application.Current?.Dispatcher?.Invoke(Sub()
                                                                If box IsNot Nothing Then UpdatePositionDisplay(box, slot, latestPnl)
                                                            End Sub)
                Else
                    _logger.LogWarning("ST+ [Slot {Idx}] Scale-in rejected for {Contract}: status={Status}",
                                       slot.SlotIndex, slot.Instrument, placed?.Status)
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ [Slot {Idx}] ScaleInSlotAsync failed for {Contract}",
                                   slot.SlotIndex, slot.Instrument)
            End Try
        End Function

        Private Async Function HandleOpenPositionAsync(slot As PositionSlot, tf As BarTimeframe) As Task
            If slot.AccountId = 0 AndAlso _session.SelectedAccount IsNot Nothing Then
                slot.AccountId = _session.SelectedAccount.Id
            End If
            Dim snapshot As LivePositionSnapshot = Nothing
            Try
                snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                    slot.AccountId, slot.Instrument, slot.PositionId)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ GetLivePositionSnapshotAsync failed for [Slot {Idx}] on {Contract}", slot.SlotIndex, slot.Instrument)
            End Try

            If snapshot Is Nothing Then
                slot.MissCount += 1
                If slot.MissCount >= SyncMissThreshold Then
                    Await ReleaseSlotAsync(slot, "Closed by Broker")
                    Return
                End If
            Else
                slot.MissCount = 0
                ' BUG-38: PlaceOrderAsync only returns an orderId; the exchange position ID is
                ' not available until after fill. Backfill it from the first live snapshot so
                ' that ReleaseSlotAsync and EditPositionSlTpAsync can use the precise path.
                If Not slot.PositionId.HasValue AndAlso snapshot.PositionId <> 0 Then
                    slot.PositionId = snapshot.PositionId
                    _logger.LogInformation("ST+ [Slot {Idx}] {Contract} PositionId resolved from snapshot: {PosId}",
                                           slot.SlotIndex, slot.Instrument, slot.PositionId)
                End If
                If snapshot.OpenRate <> 0D AndAlso slot.EntryPrice = 0D Then
                    slot.EntryPrice = snapshot.OpenRate
                    ' One-time sync: the initial bracket SL is placed using lastClose-based ticks,
                    ' which diverges from the true fill price when a gap open occurs.  Drive the
                    ' broker SL to the correct ST-line value stored at entry time.
                    If slot.PositionId.HasValue AndAlso slot.StopPrice <> 0D Then
                        Try
                            Dim syncOk = Await _orderService.EditPositionSlTpAsync(slot.PositionId.Value, slot.StopPrice, Nothing)
                            _logger.LogInformation("ST+ [Slot {Idx}] initial SL sync → {Stop:F2} ok={Ok}",
                                                   slot.SlotIndex, slot.StopPrice, syncOk)
                        Catch ex As Exception
                            _logger.LogWarning(ex, "ST+ [Slot {Idx}] initial SL sync failed for {Contract}", slot.SlotIndex, slot.Instrument)
                        End Try
                    End If
                    If slot.TradeRecordId > 0 Then
                        Task.Run(Function() _tradeRecordService.UpdateEntryPriceAsync(slot.TradeRecordId, snapshot.OpenRate))
                    End If
                    If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                        _debugCapture.UpdateFill(slot.DebugTradeId, snapshot.OpenRate, DateTime.UtcNow)
                    End If
                End If

                ' Use snapshot P&L as a fallback; will be overridden below once bar close is available.
                Dim latestPnl = snapshot.UnrealizedPnlUsd
                slot.UnrealizedPnl = latestPnl

                Dim bars As IList(Of MarketBar)
                Try
                    bars = Await _barService.GetLiveBarsAsync(slot.Instrument, tf, BarsToFetch)
                Catch
                    Return
                End Try
                If bars Is Nothing OrElse bars.Count < 14 Then Return

                Dim highs = bars.Select(Function(b) b.High).ToList()
                Dim lows = bars.Select(Function(b) b.Low).ToList()
                Dim closes = bars.Select(Function(b) b.Close).ToList()
                Dim n = bars.Count - 1

                ' Fetch a small 15-second bar series to obtain the freshest available price.
                ' The strategy-timeframe bars above can be up to one full bar period stale
                ' (e.g. 15 minutes for the default 15-min TF). currentClose is used for both
                ' P&L display and the phased stop ratchet (BUG-51), so intra-bar price spikes
                ' that peak and retrace within one strategy bar can still advance the stop.
                ' Indicator calculations (ST line, DMI, ATR) still use the strategy-TF closes.
                ' tickBars is hoisted here so Take100 can reuse the same fetch without a second API call.
                Dim currentClose As Decimal = CDec(closes(n))  ' fallback to strategy-TF close
                Dim tickBars As IList(Of MarketBar) = Nothing
                Try
                    Dim rawTickBars = Await _barService.GetLiveBarsAsync(slot.Instrument, BarTimeframe.FifteenSecond, 20)
                    If rawTickBars IsNot Nothing AndAlso rawTickBars.Count > 0 Then
                        ' Strip the forming bar — act only on closed 15s bars.
                        Dim lastBarAge = (DateTime.UtcNow - rawTickBars(rawTickBars.Count - 1).Timestamp).TotalSeconds
                        tickBars = If(lastBarAge < 15 AndAlso rawTickBars.Count > 1,
                                      CType(rawTickBars.Take(rawTickBars.Count - 1).ToList(), IList(Of MarketBar)),
                                      rawTickBars)
                        currentClose = CDec(tickBars(tickBars.Count - 1).Close)
                        _logger.LogDebug("ST+ [Slot {Idx}] {Contract} currentClose={Price} (15s bar close)", slot.SlotIndex, slot.Instrument, currentClose)
                    End If
                Catch
                    ' Non-fatal: fall back to strategy-TF close already assigned above
                End Try

                ' Derive P&L locally from the freshest price (15s bar close).
                ' The TopStepX REST snapshot always returns openPnl=0, so local calculation
                ' is the sole source of truth for the display.
                If slot.EntryPrice <> 0D Then
                    Dim fc3 = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                    If fc3 IsNot Nothing AndAlso fc3.PxTickSize > 0D AndAlso fc3.PxTickValue > 0D Then
                        Dim priceDiff As Decimal = If(slot.Side = "Buy",
                            currentClose - slot.EntryPrice,
                            slot.EntryPrice - currentClose)
                        Dim ticks As Decimal = priceDiff / fc3.PxTickSize
                        latestPnl = Math.Round(ticks * fc3.PxTickValue * slot.Contracts, 2)
                        slot.UnrealizedPnl = latestPnl
                        If slot.InitialRiskDollars = 0D AndAlso slot.InitialRisk <> 0D Then
                            Dim riskTicks = slot.InitialRisk / fc3.PxTickSize
                            slot.InitialRiskDollars = Math.Round(riskTicks * fc3.PxTickValue * slot.Contracts, 2)
                        End If
                    End If
                End If

                Dim boxForDisplay = BoxForSlot(slot)
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            If boxForDisplay IsNot Nothing Then UpdatePositionDisplay(boxForDisplay, slot, latestPnl, currentClose)
                                                        End Sub)
                Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmiForExit = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim atr14Exit = TechnicalIndicators.ATR(highs, lows, closes, period:=14)
                Dim stLine = CDec(st.Line(n))

                ' BUG-49: Clear early-mode grace once ST direction confirms the slot's side.
                ' Check and clear BEFORE the E1 evaluation so that confirmation and E1 fire on the same bar.
                If slot.IsEarlyModeEntry Then
                    Dim stDirN = st.Direction(n)
                    If (slot.Side = "Buy" AndAlso stDirN > 0) OrElse (slot.Side = "Sell" AndAlso stDirN < 0) Then
                        slot.IsEarlyModeEntry = False
                        _logger.LogInformation("ST+ [Slot {Idx}] Early entry confirmed — E1 now active", slot.SlotIndex)
                    End If
                End If

                ' Compute VWAP from bar volumes (anchored at start of cached series)
                Dim volumes = bars.Select(Function(b) b.Volume).ToList()
                Dim vwapExit = TechnicalIndicators.VWAP(highs, lows, closes, volumes)

                ' Compute RSI-14 from bar closes
                Dim rsiExit = TechnicalIndicators.RSI(closes, 14)

                ' Set InitialRisk once EntryPrice is confirmed (after first snapshot)
                If slot.InitialRisk = 0D AndAlso slot.EntryPrice <> 0D AndAlso slot.StopPrice <> 0D Then
                    slot.InitialRisk = Math.Abs(slot.EntryPrice - slot.StopPrice)
                End If

                ' Always refresh current ADX for live display (independent of entry confirmation).
                Dim adxNow = dmiForExit.ADX(n)
                If Not Single.IsNaN(adxNow) Then slot.CurrentAdx = adxNow

                ' Push trend-weakening sample for Row 4 (8-bar / 2-minute composite).
                Dim diPlusNow = dmiForExit.PlusDI(n)
                Dim diMinusNow = dmiForExit.MinusDI(n)
                Dim priceToStNow = If(slot.EntryPrice <> 0D AndAlso stLine <> 0D,
                                      CSng(Math.Abs(currentClose - stLine)), 0F)
                Dim boxForTrend = BoxForSlot(slot)
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            If boxForTrend IsNot Nothing Then
                                                                boxForTrend.PushAdxSample(adxNow, diPlusNow, diMinusNow, priceToStNow)
                                                            End If
                                                        End Sub)

                ' STRAT-31: Scale in as ADX strengthens from a lower to a higher band.
                ' Skip during early-mode grace (position not yet ST-confirmed) and before entry fills.
                If Not slot.IsEarlyModeEntry AndAlso slot.EntryPrice <> 0D Then
                    If Not Single.IsNaN(adxNow) Then
                        Dim currentBand = _slotManager.BandForAdx(adxNow)
                        If currentBand > slot.LastAdxBand AndAlso slot.Contracts < 3 Then
                            Dim addContracts = Math.Min(currentBand - slot.LastAdxBand, 3 - slot.Contracts)
                            Await ScaleInSlotAsync(slot, addContracts)
                        End If
                        slot.LastAdxBand = Math.Max(slot.LastAdxBand, currentBand)
                    End If
                End If

                ' Composite exit signal evaluation
                Dim exitEval = _exitEngine.Evaluate(slot, highs, lows, closes,
                                                    st.Line, st.Direction,
                                                    dmiForExit.PlusDI, dmiForExit.MinusDI,
                                                    dmiForExit.ADX, atr14Exit,
                                                    vwapExit, rsiExit)

                slot.Health = exitEval.RecommendedHealth

                Dim boxForHealth = BoxForSlot(slot)
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            If boxForHealth IsNot Nothing Then
                                                                boxForHealth.HealthBrush = HealthBrushFor(slot.Health)
                                                            End If
                                                        End Sub)

                ' ── Debug Capture: Heartbeat (and BarClose when new bar detected) ─
                If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
                   Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                    Dim atrVal As Single = If(n < atr14Exit.Length AndAlso Not Single.IsNaN(atr14Exit(n)), atr14Exit(n), 0F)
                    RecordDebugSnapshot(slot, currentClose, latestPnl, bars(n), stLine, st.Direction(n), atrVal, "Heartbeat")
                    Dim lastBarTs As DateTimeOffset
                    Dim isNewBar = (Not _lastBarTimestampByTradeId.TryGetValue(slot.DebugTradeId, lastBarTs)) OrElse
                                   bars(n).Timestamp <> lastBarTs
                    _lastBarTimestampByTradeId(slot.DebugTradeId) = bars(n).Timestamp
                    If isNewBar Then
                        Dim barCloseNotes = $"Score={exitEval.Score} [{String.Join(",", exitEval.ContributingSignals)}] Health={exitEval.RecommendedHealth} ConsecExit={slot.ConsecutiveExitBars}"
                        RecordDebugSnapshot(slot, currentClose, latestPnl, bars(n), stLine, st.Direction(n), atrVal, "BarClose", barCloseNotes)
                    End If
                End If

                ' ── 2-bar confirmation guard ──────────────────────────────────────
                ' An immediate E1 SuperTrend flip exits on the current bar unconditionally.
                ' All other Exiting scores require 2 consecutive bars at Exiting health to
                ' prevent single-bar consolidation at trend highs from triggering a close.
                If exitEval.ImmediateExit Then
                    If slot.IsEarlyModeEntry Then
                        ' BUG-49: E1 suppressed — early-mode entry awaiting ST confirmation.
                        ' The SL bracket at the ST line handles downside protection during the grace period.
                        _logger.LogInformation("ST+ [Slot {Idx}] E1 suppressed (early-mode grace) for {Contract}",
                                               slot.SlotIndex, slot.Instrument)
                    Else
                        ' E1 flip — close without waiting for confirmation
                        slot.ConsecutiveExitBars = 0
                        Await ReleaseSlotAsync(slot, "ST Flip")
                        Return
                    End If
                End If

                If slot.Health = SlotHealth.Exiting Then
                    slot.ConsecutiveExitBars += 1
                    _logger.LogInformation("ST+ [Slot {Idx}] {Contract} exit score={Score} — consecutiveExitBars={Count}",
                                           slot.SlotIndex, slot.Instrument, exitEval.Score, slot.ConsecutiveExitBars)
                    If slot.ConsecutiveExitBars >= 2 Then
                        slot.ConsecutiveExitBars = 0
                        Await ReleaseSlotAsync(slot, "Exit Signal")
                        Return
                    End If
                Else
                    slot.ConsecutiveExitBars = 0
                End If

                ' BUG-50: Skip phased stop entirely during early-mode grace.
                ' ST has not yet confirmed direction, so stLine is on the wrong side of entry
                ' (e.g. bullish support below a short entry). ComputePhasedStop would produce a
                ' stop below entry that TopStepX rejects silently, freezing the SL.
                ' The bracket SL already protects downside during this period.
                If Not slot.IsEarlyModeEntry Then
                    ' Advance phased stop (ratchet-only).
                    ' Only the primary slot — the lowest-indexed open slot for this instrument —
                    ' is allowed to call EditPositionSlTpAsync.  Scale-in slots update their
                    ' local state and display but do NOT touch the exchange bracket, because
                    ' all slots share the same underlying TopStepX position and racing edits
                    ' would use different InitialRisk baselines and could widen the stop.
                    '
                    Dim stLineForPhase = If(Not Single.IsNaN(st.Line(n)), CDec(st.Line(n)), slot.StopPrice)
                    Dim atrForPhase = If(n < atr14Exit.Length AndAlso Not Single.IsNaN(atr14Exit(n)), CDec(atr14Exit(n)), 0D)
                    Dim oldStopPhase = slot.StopPhase
                    Dim newStop As Decimal

                    If Config.Take100ProfitEnabled Then
                          ' ── Take $100 mode: the R-based phase ladder is OFF ──────────────────
                         ' State 1 (PnL < $100):   SL trails the SuperTrend line (ratchet only).
                         ' State 2 (PnL ≥ $100):   SL uses two stacked ratchet floors:
                         '   Floor A — entry price (breakeven): SL may never drop below entry.
                         '   Floor B — BB lower band (LONG) or BB upper band (SHORT), computed
                         '             from EMA-10 of 15s bars with mult=1.5. Using the outer
                         '             directional band prevents the floor retracing when the
                         '             middle dips; ratchet ensures it never moves against trade.
                         Dim isLngSlot = slot.Side = "Buy"
                         If latestPnl >= 100D Then
                             ' Floor A: breakeven — ratchet preserves any higher floor already set.
                             Dim beStop = slot.EntryPrice
                             newStop = If(isLngSlot, Math.Max(slot.StopPrice, beStop), Math.Min(slot.StopPrice, beStop))
                             slot.StopPhase = StopPhase.Breakeven

                             ' Floor B: BB lower band (LONG) or BB upper band (SHORT) computed from
                                 ' 15-second closes using EMA-10 as the middle, mult = 1.5.
                                 ' Using the directional outer band instead of the middle band
                                 ' prevents the stop floor from retracing when the middle dips.
                                 ' Reuses tickBars fetched above — no second API call.
                                 If tickBars IsNot Nothing AndAlso tickBars.Count >= 10 Then
                                     Dim s15Closes = tickBars.Select(Function(b) CDec(b.Close)).ToList()
                                     Dim bbBands = TechnicalIndicators.BollingerBands(s15Closes, 10, 1.5)
                                     Dim lastIdx = bbBands.Upper.Length - 1
                                     Dim bbBand As Single = If(isLngSlot, bbBands.Lower(lastIdx), bbBands.Upper(lastIdx))
                                     If Not Single.IsNaN(bbBand) Then
                                         Dim bbFloor = CDec(bbBand)
                                         newStop = If(isLngSlot, Math.Max(newStop, bbFloor), Math.Min(newStop, bbFloor))
                                         _logger.LogInformation(
                                             "ST+ [Slot {Idx}] Take100 BB-{Band} trail — entry={E:F2} bbFloor={B:F2} newStop={S:F2} pnl={P:F2}",
                                             slot.SlotIndex, If(isLngSlot, "lower", "upper"), slot.EntryPrice, bbFloor, newStop, latestPnl)
                                     End If
                                 Else
                                     _logger.LogInformation(
                                         "ST+ [Slot {Idx}] Take100 breakeven (no 15s bars yet) — entry={Entry:F2} newStop={Stop:F2} pnl={Pnl:F2}",
                                         slot.SlotIndex, slot.EntryPrice, newStop, latestPnl)
                                 End If
                         Else
                             ' Pre-$100: trail SuperTrend line, ratchet only.
                             newStop = If(isLngSlot, Math.Max(slot.StopPrice, stLineForPhase), Math.Min(slot.StopPrice, stLineForPhase))
                             slot.StopPhase = StopPhase.Initial
                         End If
                    Else
                        ' ── Standard 6-phase configurable ladder ────────────────────────────
                        ' SuperTrend+ uses a configurable ladder via ComputePhasedStop overload.
                        ' This overwrites the generic phased-stop produced by ExitSignalEngine.Evaluate
                        ' and keeps the logic isolated to this strategy only.
                        Dim stPhasedResult = _exitEngine.ComputePhasedStop(slot, currentClose, stLineForPhase, atrForPhase, Config)
                        newStop = stPhasedResult.NewStop
                        slot.StopPhase = stPhasedResult.Phase
                    End If

                    Dim isPrimaryForEdit As Boolean =
                        Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                                                   s.Instrument = slot.Instrument AndAlso
                                                   s.SlotIndex < slot.SlotIndex)

                    Dim primaryEditSucceeded = False
                    If isPrimaryForEdit AndAlso slot.PositionId.HasValue AndAlso newStop <> slot.StopPrice Then
                        Dim tpArg As Decimal? = If(slot.TakeProfitPrice <> 0D, CType(slot.TakeProfitPrice, Decimal?), Nothing)
                        Try
                            Dim editOk = Await _orderService.EditPositionSlTpAsync(slot.PositionId.Value, newStop, tpArg)
                            If editOk Then
                                primaryEditSucceeded = True
                                If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
                                   Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                                    Dim slSnap As New DebugSnapshotRecord With {
                                        .TradeId = slot.DebugTradeId,
                                        .Timestamp = DateTime.UtcNow.ToString("O"),
                                        .EventType = "SlAdjust",
                                        .CurrentSL = newStop,
                                        .Notes = $"{oldStopPhase}->{slot.StopPhase}"
                                    }
                                    _debugCapture.RecordSnapshot(slSnap)
                                End If
                                _logger.LogInformation("ST+ SL phase={Phase} trail->{Price} (TP={Tp}) for [Slot {Idx}] on {Contract}",
                                                       slot.StopPhase, newStop,
                                                       If(tpArg.HasValue, tpArg.Value.ToString("F2"), "none"),
                                                       slot.SlotIndex, slot.Instrument)
                            Else
                                _logger.LogWarning("ST+ EditPositionSlTpAsync returned False for [Slot {Idx}] on {Contract} — will retry next tick",
                                                   slot.SlotIndex, slot.Instrument)
                            End If
                        Catch ex As Exception
                            _logger.LogWarning(ex, "ST+ EditPositionSlTpAsync failed for [Slot {Idx}] on {Contract}", slot.SlotIndex, slot.Instrument)
                        End Try
                    ElseIf Not isPrimaryForEdit Then
                        _logger.LogInformation("ST+ SL ratchet deferred for scale-in [Slot {Idx}] on {Contract} — primary slot owns bracket",
                                               slot.SlotIndex, slot.Instrument)
                    End If

                    ' Only advance the in-memory stop when the broker edit was confirmed (primary slot)
                    ' or when this is a scale-in slot whose display-only stop can freely track the phase.
                    ' Keeping slot.StopPrice at its old value on API failure causes the next tick to retry.
                    If newStop <> slot.StopPrice AndAlso (Not isPrimaryForEdit OrElse primaryEditSucceeded) Then
                        slot.StopPrice = newStop
                        Dim boxForTrail = BoxForSlot(slot)
                        Application.Current?.Dispatcher?.Invoke(Sub()
                                                                    If boxForTrail IsNot Nothing Then UpdatePositionDisplay(boxForTrail, slot, latestPnl)
                                                                End Sub)
                    End If
                End If
            End If
        End Function

        Private Async Function ReleaseSlotAsync(slot As PositionSlot,
                                                  Optional exitReason As String = "Signal") As Task
            ' ── Persist close record before flattening (while slot data is still valid) ──
            If slot.TradeRecordId > 0 Then
                Try
                    Dim exitPx As Decimal = 0D
                    If slot.EntryPrice <> 0D Then
                        Dim fcExit = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                        If fcExit IsNot Nothing AndAlso fcExit.PxTickSize > 0D Then
                            Dim ticks = slot.UnrealizedPnl / (fcExit.PxTickValue * slot.Contracts)
                            Dim direction = If(slot.Side = "Buy", 1D, -1D)
                            exitPx = Math.Round(slot.EntryPrice + direction * ticks * fcExit.PxTickSize, 6)
                        End If
                    End If
                    Await _tradeRecordService.CloseTradeAsync(slot.TradeRecordId,
                                                              DateTimeOffset.UtcNow,
                                                              exitPx,
                                                              slot.UnrealizedPnl,
                                                              exitReason)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ [Slot {Idx}] failed to close trade record {Id}", slot.SlotIndex, slot.TradeRecordId)
                End Try
            End If

            ' ── Close the live position on TopStepX before forgetting the slot ──
            ' Without this, the real position stays open on the exchange while the
            ' slot clears in-memory, causing EvaluateSlotEntriesAsync to re-enter the
            ' same instrument on the next tick (BUG-35).
            _releasedThisTick = True  ' block same-tick re-entry
            ' One-bar cooldown (see SuperTrendPlusConfig re-entry cooldown policy comment).
            If Not String.IsNullOrEmpty(slot.Instrument) Then
                _reEntryCooldown(slot.Instrument) = DateTimeOffset.UtcNow
            End If
            If slot.AccountId <> 0 AndAlso Not String.IsNullOrEmpty(slot.Instrument) Then
                Try
                    Await _orderService.FlattenContractAsync(slot.AccountId, slot.Instrument)
                    _logger.LogInformation("ST+ ReleaseSlot [Slot {Idx}] flatten {Contract}: brackets cancelled + position closed",
                                           slot.SlotIndex, slot.Instrument)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ ReleaseSlot [Slot {Idx}] flatten failed for {Contract} — slot cleared anyway",
                                       slot.SlotIndex, slot.Instrument)
                End Try
            End If

            Dim box = BoxForSlot(slot)
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    If box IsNot Nothing Then
                        box.HasPosition = False
                        box.PositionDisplay = String.Empty
                        box.LastPositionDisplay = String.Empty
                        box.PnlLine = String.Empty
                        box.PnlTextBrush = Brushes.Gray
                        box.StopPhaseLabel = String.Empty
                        box.SlotLabel = String.Empty
                        box.PnlBorderBrush = Brushes.Gray
                        box.IsRrAchieved = False
                        box.TargetPnlLine = String.Empty
                        box.ClearAiResult()
                        box.ClearTrendHistory()
                    End If
                End Sub)
            ' ── Debug Capture: Exit snapshot + EndTrade (FEAT-39) ───────────────
            If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                Dim exitSnap As New DebugSnapshotRecord With {
                    .TradeId = slot.DebugTradeId,
                    .Timestamp = DateTime.UtcNow.ToString("O"),
                    .EventType = "Exit",
                    .CurrentSL = slot.StopPrice,
                    .UnrealizedPnLDollars = slot.UnrealizedPnl,
                    .Notes = exitReason
                }
                _debugCapture.RecordSnapshot(exitSnap)
                _debugCapture.EndTrade(slot.DebugTradeId, DateTime.UtcNow, slot.UnrealizedPnl)
                _debugMfe.Remove(slot.DebugTradeId)
                _debugMae.Remove(slot.DebugTradeId)
                _lastBarTimestampByTradeId.Remove(slot.DebugTradeId)
            End If

            _slotManager.CloseSlot(slot.SlotIndex)
            If Not _slotManager.Slots.Any(Function(s) s.IsOpen) Then
                SyncLock _timerLock
                    _timer?.Change(15000, 15000)
                End SyncLock
            End If
        End Function

        Private Sub UpdatePositionDisplay(box As SlotBoxVm, slot As PositionSlot, pnl As Decimal,
                                          Optional livePrice As Decimal = 0D)
            Dim sideLbl As String = If(slot.Side = "Buy", "LONG", "SHORT")
            Dim idx As Integer = Array.IndexOf(Instruments, slot.Instrument)
            Dim label As String = If(idx >= 0, InstrumentLabels(idx), slot.Instrument)
            Dim entryTimeStr As String = If(slot.EntryTime = DateTime.MinValue, "--", slot.EntryTime.ToString("HH:mm:ss"))

            ' ── Row 1 ─────────────────────────────────────────────────────────
            box.SlotLabel = $"{label}  {slot.Instrument} — {sideLbl} @ {entryTimeStr}"

            ' ── Row 2 ─────────────────────────────────────────────────────────
            Dim priceFmt As String = If(livePrice = 0D, "--", livePrice.ToString("F2"))
            Dim slFmt As String = If(slot.StopPrice = 0D, "--", slot.StopPrice.ToString("F2"))
            Dim strength As String = AdxBandLabel(slot.CurrentAdx)
            box.LivePriceDisplay = priceFmt
            box.SlDisplay = slFmt
            box.EntrySpDisplay = If(slot.EntryPrice = 0D, "--", slot.EntryPrice.ToString("F2"))
            box.StrengthLabel = strength

            ' Legacy display (kept for debug / existing tooling)
            Dim newDisplay As String = $"Price: {priceFmt}  |  SL: {slFmt}  |  {strength}"
            Dim sign As String = If(pnl > 0D, "+", If(pnl < 0D, "-", ""))
            Dim absAmount As String = Math.Abs(pnl).ToString("F2")
            Dim newPnlLine As String = $"P&L: {sign}${absAmount}"
            Dim isFirstPopulation As Boolean = String.IsNullOrEmpty(box.LastPositionDisplay)
            Dim pnlChanged As Boolean = (newPnlLine <> box.PnlLine)
            Dim displayChanged As Boolean = (newDisplay <> box.LastPositionDisplay) OrElse pnlChanged
            box.PositionDisplay = newDisplay
            box.LastPositionDisplay = newDisplay
            box.PnlLine = newPnlLine
            box.PnlTextBrush = If(pnl > 0D, Brushes.LimeGreen, If(pnl < 0D, Brushes.Red, Brushes.Gray))
            box.PnlBrush = Brushes.White
            box.PnlBorderBrush = If(pnl > 0D, Brushes.LimeGreen, If(pnl < 0D, Brushes.Red, Brushes.Gray))
            box.StopPhaseLabel = PhaseLabel(slot.StopPhase, Config)

            ' ── Row 3: P&L, Target, and Next Phase ────────────────────────────
            Dim targetPnl As Decimal = 0D
            If slot.InitialRiskDollars > 0D Then
                targetPnl = Math.Round(slot.InitialRiskDollars * PersonaRrRatio, 2)
                box.TargetPnlLine = $"Target: ${targetPnl:F2}"
            Else
                box.TargetPnlLine = String.Empty
            End If
            box.IsRrAchieved = (targetPnl > 0D AndAlso pnl >= targetPnl)

            ' Next phase label
            box.NextPhaseLabel = NextPhaseDisplay(slot, pnl)

            ' ── Flashes ───────────────────────────────────────────────────────
            If Not isFirstPopulation Then
                Task.Run(Async Function() As Task
                             Await box.FlashRowAsync()
                         End Function)
                If pnlChanged Then
                    Task.Run(Async Function() As Task
                                 Await box.FlashPnlTextAsync()
                             End Function)
                End If
                If displayChanged Then
                    Task.Run(Async Function() As Task
                                 Await box.FlashPnlAsync()
                             End Function)
                End If
            End If
        End Sub

        ''' <summary>Returns the ADX band label for the given ADX reading.</summary>
        Private Function AdxBandLabel(adx As Single) As String
            If adx <= 0F Then Return String.Empty
            If adx >= Config.AdxStrongThreshold Then Return "Espresso"
            If adx >= Config.AdxModerateThreshold Then Return "Latte"
            If adx >= Config.AdxWeakThreshold Then Return "Decaff"
            Return String.Empty
        End Function

        ''' <summary>
        ''' Computes a one-line "Next phase" label showing the trigger in R and the dollar distance.
        ''' e.g.  "Next: Breakeven in 0.5R = $28.75"
        ''' </summary>
        Private Function NextPhaseDisplay(slot As PositionSlot, currentPnl As Decimal) As String
            If slot.InitialRiskDollars <= 0D Then Return String.Empty
            Dim ir = slot.InitialRiskDollars
            Select Case slot.StopPhase
                Case StopPhase.Initial
                    Dim triggerR = Config.BreakevenTriggerR
                    Dim dollarTarget = Math.Round(ir * triggerR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: Breakeven in {triggerR:F1}R = ${remaining:F2}"
                Case StopPhase.Breakeven
                    Dim triggerR = Config.ProfitLockTriggerR
                    Dim dollarTarget = Math.Round(ir * triggerR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: ProfitLock in {triggerR:F1}R = ${remaining:F2}"
                Case StopPhase.ProfitLock
                    Dim triggerR = Config.ProfitTrailTriggerR
                    Dim dollarTarget = Math.Round(ir * triggerR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: ProfitTrail in {triggerR:F1}R = ${remaining:F2}"
                Case StopPhase.ProfitTrail
                    Dim triggerR = Config.HarvestTriggerR
                    Dim dollarTarget = Math.Round(ir * triggerR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: Harvest in {triggerR:F1}R = ${remaining:F2}"
                Case StopPhase.Harvest
                    Dim triggerR = Config.FreeRideTriggerR
                    Dim dollarTarget = Math.Round(ir * triggerR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: FreeRide in {triggerR:F1}R = ${remaining:F2}"
                Case StopPhase.FreeRide
                    Return "🏆 FreeRide — letting it run"
                Case Else
                    Return String.Empty
            End Select
        End Function

        ''' <summary>Returns a human-readable phase label that explains both the phase name
        ''' and its meaning in terms of how the stop is managed at that point.</summary>
        Private Shared Function PhaseLabel(phase As StopPhase, cfg As SuperTrendPlusConfig) As String
            Select Case phase
                Case StopPhase.Initial
                    Return String.Format("Initial: SL on SuperTrend line - targeting {0:F1}R", cfg.BreakevenTriggerR)
                Case StopPhase.Breakeven
                    Return "Breakeven: Stops to entry."
                Case StopPhase.ProfitLock
                    Return String.Format("ProfitLock: Stop at entry + {0:F1}R.", cfg.ProfitLockOffsetR)
                Case StopPhase.ProfitTrail
                    Return "ProfitTrail: ATR trailing stop - riding the move."
                Case StopPhase.Harvest
                    Return String.Format("Harvest: Stop locked at entry + {0:F1}R.", cfg.HarvestLockR)
                Case StopPhase.FreeRide
                    Return "FreeRide: Stop at entry + 2R - letting it run."
                Case Else
                    Return phase.ToString()
            End Select
        End Function

        Private Shared Function MapTimeframe(tf As String) As BarTimeframe
            Select Case tf
                Case "5min"  : Return BarTimeframe.FiveMinute
                Case "1hr"   : Return BarTimeframe.OneHour
                Case Else    : Return BarTimeframe.FifteenMinute
            End Select
        End Function

        Private Shared Function HealthBrushFor(health As SlotHealth) As Brush
            Select Case health
                Case SlotHealth.Warning : Return New SolidColorBrush(Color.FromRgb(&HFF, &HAA, &H00))  ' amber
                Case SlotHealth.Exiting : Return Brushes.Red
                Case Else               : Return Brushes.LimeGreen
            End Select
        End Function

        ''' <summary>
        ''' Runs an ad-hoc Claude Haiku mid-trade sense check for the given slot box.
        ''' Updates <see cref="SlotBoxVm.AiVerdict"/>, <see cref="SlotBoxVm.AiExplanation"/>,
        ''' and <see cref="SlotBoxVm.AiSuggestedAction"/> on completion.
        ''' </summary>
        Public Async Function RunMidTradeCheckAsync(box As SlotBoxVm) As Task
            If _claudeService Is Nothing OrElse Not box.HasPosition OrElse box.Slot Is Nothing Then Return
            Dim slot = box.Slot
            If Not slot.IsOpen Then Return

            Application.Current?.Dispatcher?.Invoke(Sub() box.IsAiChecking = True)
            Try
                Dim tf = MapTimeframe(_selectedTimeframe)
                Dim bars As IList(Of MarketBar) = Nothing
                Try
                    bars = Await _barService.GetLiveBarsAsync(slot.Instrument, tf, 60)
                Catch
                End Try

                Dim adx As Single = 0F
                Dim pdi As Single = 0F
                Dim mdi As Single = 0F
                If bars IsNot Nothing AndAlso bars.Count >= 14 Then
                    Dim dmi = TechnicalIndicators.DMI(
                        bars.Select(Function(b) b.High).ToList(),
                        bars.Select(Function(b) b.Low).ToList(),
                        bars.Select(Function(b) b.Close).ToList(), period:=14)
                    Dim n = bars.Count - 1
                    If Not Single.IsNaN(dmi.ADX(n)) Then adx = dmi.ADX(n)
                    If Not Single.IsNaN(dmi.PlusDI(n)) Then pdi = dmi.PlusDI(n)
                    If Not Single.IsNaN(dmi.MinusDI(n)) Then mdi = dmi.MinusDI(n)
                End If

                Using cts = New System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10))
                    Dim result = Await _claudeService.MidTradeCheckAsync(
                        slot.Instrument, slot.Side, adx, pdi, mdi,
                        PhaseLabel(slot.StopPhase, Config),
                        slot.UnrealizedPnl,
                        If(bars IsNot Nothing, CType(bars, IReadOnlyList(Of MarketBar)), New List(Of MarketBar)()),
                        cts.Token)
                    Application.Current?.Dispatcher?.Invoke(
                        Sub()
                            box.AiVerdict = result.Verdict
                            box.AiExplanation = result.Explanation
                            box.AiSuggestedAction = result.SuggestedAction
                            AddAiLogEntry(slot.Instrument, $"Mid-trade: {result.Verdict} — {result.SuggestedAction}")
                        End Sub)
                    If _isDebugCaptureEnabled AndAlso _debugCapture IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(slot.DebugTradeId) Then
                        Dim aiSnap As New DebugSnapshotRecord With {
                            .TradeId = slot.DebugTradeId,
                            .Timestamp = DateTime.UtcNow.ToString("O"),
                            .EventType = "AiCheck",
                            .Notes = $"Mid-trade: {result.Verdict} — {result.SuggestedAction}"
                        }
                        _debugCapture.RecordSnapshot(aiSnap)
                    End If
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ mid-trade AI check error for {Contract}", slot.Instrument)
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        box.AiVerdict = "GREEN"
                        box.AiExplanation = $"Check failed: {ex.Message}"
                        box.AiSuggestedAction = "Continue monitoring."
                    End Sub)
            Finally
                Application.Current?.Dispatcher?.Invoke(Sub() box.IsAiChecking = False)
            End Try
        End Function

        ''' <summary>
        ''' Builds and queues a debug snapshot. Updates MFE/MAE running maxima.
        ''' No-op when debug capture is disabled or slot has no DebugTradeId.
        ''' </summary>
        Private Sub RecordDebugSnapshot(slot As PositionSlot,
                                         currentClose As Decimal,
                                         latestPnlUsd As Decimal,
                                         lastBar As MarketBar,
                                         stLine As Decimal,
                                         stDir As Single,
                                         atrVal As Single,
                                         eventType As String,
                                         Optional notes As String = Nothing)
            Dim tid = slot.DebugTradeId

            Dim mfe As Decimal
            Dim mae As Decimal
            If _debugMfe.TryGetValue(tid, mfe) Then
                If slot.Side = "Buy" Then
                    mfe = Math.Max(mfe, currentClose)
                    mae = Math.Min(_debugMae(tid), currentClose)
                Else
                    mfe = Math.Min(mfe, currentClose)
                    mae = Math.Max(_debugMae(tid), currentClose)
                End If
            Else
                mfe = currentClose
                mae = currentClose
            End If
            _debugMfe(tid) = mfe
            _debugMae(tid) = mae

            Dim pnlTicks As Nullable(Of Decimal) = Nothing
            If slot.EntryPrice <> 0D Then
                Dim fc = Core.Trading.FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                If fc IsNot Nothing AndAlso fc.PxTickSize > 0D Then
                    Dim priceDiff = If(slot.Side = "Buy", currentClose - slot.EntryPrice, slot.EntryPrice - currentClose)
                    pnlTicks = Math.Round(priceDiff / fc.PxTickSize, 2)
                End If
            End If

            Dim snap As New DebugSnapshotRecord With {
                .TradeId = tid,
                .Timestamp = DateTime.UtcNow.ToString("O"),
                .EventType = eventType,
                .LastPrice = currentClose,
                .CurrentSL = slot.StopPrice,
                .CurrentTP = If(slot.TakeProfitPrice <> 0D, CType(slot.TakeProfitPrice, Nullable(Of Decimal)), Nothing),
                .UnrealizedPnLTicks = pnlTicks,
                .UnrealizedPnLDollars = latestPnlUsd,
                .Mfe = mfe,
                .Mae = mae,
                .BarOpen = lastBar.Open,
                .BarHigh = lastBar.High,
                .BarLow = lastBar.Low,
                .BarClose = lastBar.Close,
                .SuperTrendValue = If(Not Single.IsNaN(CSng(stLine)), CType(stLine, Nullable(Of Decimal)), Nothing),
                .SuperTrendDirection = If(stDir > 0, "Up", If(stDir < 0, "Down", Nothing)),
                .Atr = If(Not Single.IsNaN(atrVal), CType(CDec(atrVal), Nullable(Of Decimal)), Nothing),
                .Adx = If(slot.CurrentAdx <> 0F, CType(slot.CurrentAdx, Nullable(Of Single)), Nothing),
                .StopPhase = slot.StopPhase.ToString(),
                .Notes = notes
            }
            _debugCapture.RecordSnapshot(snap)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                ' StopMonitoring disposes the timer and resets all in-memory slot state,
                ' preventing lingering entry desires (e.g. M2K) after app exit.
                If _isMonitoring Then
                    StopMonitoring()
                Else
                    SyncLock _timerLock
                        _timer?.Dispose()
                        _timer = Nothing
                    End SyncLock
                End If
            End If
        End Sub

    End Class

End Namespace
