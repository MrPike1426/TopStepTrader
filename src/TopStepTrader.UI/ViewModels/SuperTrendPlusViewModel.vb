Imports System.Collections.Concurrent
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
Imports TopStepTrader.API.Hubs
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
                NotifyPropertyChanged(NameOf(SignalDisplay))
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
                NotifyPropertyChanged(NameOf(SignalDisplay))
            End Set
        End Property

        Public ReadOnly Property SignalDisplay As String
            Get
                Return If(String.IsNullOrEmpty(_arrow) OrElse _arrow = "–",
                          _signal,
                          _arrow & "  " & _signal)
            End Get
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
                NotifyPropertyChanged(NameOf(StrengthColor))
            End Set
        End Property

        Public ReadOnly Property StrengthColor As Brush
            Get
                ' Extract ADX value from TrendStrength (e.g., "ADX:42 L3: Espresso")
                Dim adxMatch = System.Text.RegularExpressions.Regex.Match(_trendStrength, "ADX:(\d+)")
                If adxMatch.Success AndAlso Integer.TryParse(adxMatch.Groups(1).Value, Nothing) Then
                    Dim strength As Integer = Integer.Parse(adxMatch.Groups(1).Value)
                    ' Use secondary color (grey/blue) only for weak signals: Cat Piss (< 15) and Decaff (15-24)
                    If strength < 25 Then
                        Return CType(Application.Current?.Resources("TextSecondaryBrush"), Brush)
                    End If
                End If
                ' For values >= 25, use RowColor
                Return RowColor
            End Get
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
                NotifyPropertyChanged(NameOf(DiDominance))
                NotifyPropertyChanged(NameOf(DiSpreadColor))
            End Set
        End Property

        ''' <summary>Direction label + spread, e.g. "▲ BULL  Δ25" or "▼ BEAR  Δ6" or "— NEUT  Δ1"</summary>
        Public ReadOnly Property DiDominance As String
            Get
                Dim m = System.Text.RegularExpressions.Regex.Match(
                    _diDisplay, "\+DI:(\d+)\s+-DI:(\d+)")
                If Not m.Success Then Return "—"
                Dim plus As Integer = Integer.Parse(m.Groups(1).Value)
                Dim minus As Integer = Integer.Parse(m.Groups(2).Value)
                Dim delta As Integer = Math.Abs(plus - minus)
                Dim label As String
                If delta < 5 Then
                    label = "— NEUT"
                ElseIf plus > minus Then
                    label = "▲ BULL"
                Else
                    label = "▼ BEAR"
                End If
                Return String.Format("{0}   Δ {1}", label, delta)
            End Get
        End Property

        ''' <summary>Colour driven by DI spread strength and direction.</summary>
        Public ReadOnly Property DiSpreadColor As Brush
            Get
                Dim m = System.Text.RegularExpressions.Regex.Match(
                    _diDisplay, "\+DI:(\d+)\s+-DI:(\d+)")
                If Not m.Success Then Return Brushes.Gray
                Dim plus As Integer = Integer.Parse(m.Groups(1).Value)
                Dim minus As Integer = Integer.Parse(m.Groups(2).Value)
                Dim delta As Integer = Math.Abs(plus - minus)
                If delta < 5 Then Return Brushes.Gray
                Dim isBull As Boolean = plus > minus
                If delta >= 20 Then
                    Return If(isBull, New SolidColorBrush(Color.FromRgb(&H66, &HBB, &H6A)),  ' bright green
                                      New SolidColorBrush(Color.FromRgb(&HEF, &H53, &H50))) ' bright red
                ElseIf delta >= 10 Then
                    Return If(isBull, New SolidColorBrush(Color.FromRgb(&H43, &HA0, &H47)),  ' medium green
                                      New SolidColorBrush(Color.FromRgb(&HC6, &H28, &H28))) ' medium red
                Else
                    Return If(isBull, New SolidColorBrush(Color.FromRgb(&H2E, &H7D, &H32)),  ' dim green
                                      New SolidColorBrush(Color.FromRgb(&H8B, &H1A, &H1A))) ' dim red
                End If
            End Get
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

    ''' <summary>STRAT-40 F3: a candidate that survived the strategy-TF gates but is waiting
    ''' for a lower-TF SuperTrend to flip into agreement. Stored per-contract in
    ''' <c>SuperTrendPlusViewModel._deferredCandidates</c> and re-evaluated on each tick.</summary>
    Friend NotInheritable Class DeferredCandidate
        Public Property ContractId As String
        Public Property InstrumentIndex As Integer
        Public Property Side As String
        Public Property BarTimeOfSignal As DateTimeOffset
        Public Property StLineAtSignal As Decimal
        Public Property LastCloseAtSignal As Decimal
        Public Property FirstDeferredUtc As DateTimeOffset
        Public Property AdxAtSignal As Single
    End Class

    ''' <summary>
    ''' BUG-90 — Four independent channels exist to detect a broker-side close on an
    ''' occupied slot. They are intentionally redundant: any single channel can fail
    ''' silently (hub drop, REST returning a stale row, etc.), so a position is only
    ''' parked indefinitely when ALL four miss. Every release funnels through
    ''' <see cref="ReleaseSlotAsync"/> which emits a single structured log line via
    ''' <see cref="Core.Trading.ReleaseLogFormatter"/>.
    '''
    '''   1. <b>Hub event</b> (BUG-79) — <see cref="OnHubPositionUpdated"/> releases on
    '''      <c>NetPos=0</c> within the SignalR push latency. <c>trigger="hub"</c>.
    '''   2. <b>MissCount escalation</b> — <see cref="HandleOpenPositionAsync"/> delegates to
    '''      <see cref="IPositionManagementService"/>, which increments
    '''      <see cref="PositionSlot.MissCount"/> on every null/degenerate snapshot and
    '''      requests release at the SyncMissThreshold (3 ticks). <c>trigger="miss"</c>.
    '''   3. <b>SnapshotStalenessGuard</b> (BUG-79) — defensive 5-minute timeout when the
    '''      snapshot keeps failing but <c>MissCount</c> never reaches the threshold (e.g.
    '''      intermittent REST failures combined with the alternate-tick skip optimisation).
    '''      <c>trigger="staleness"</c>.
    '''   4. <b>Broker sweep</b> (BUG-90 F1) — <see cref="Services.Background.BrokerSlotSweepWorker"/>
    '''      queries the broker every 60 s. <b>This is the authoritative backstop</b>: it
    '''      cannot be fooled by the H2 case where REST returns a stale non-zero row to the
    '''      per-tick path. <c>trigger="sweep"</c>.
    '''
    ''' If the BUG-90 stuck-slot symptom recurs, the structured release log line names the
    ''' channel that finally caught it — <c>trigger=sweep</c> means channels 1–3 all failed
    ''' and the backstop ran.
    ''' </summary>
    Public Class SuperTrendPlusViewModel
        Inherits ViewModelBase
        Implements IDisposable, Core.Interfaces.IOpenSlotReleaseSink, Core.Interfaces.IOpenSlotInstrumentSource

        ' FEAT-72: root symbols and labels are derived per-instance — when the adaptive
        ' watchlist toggle is OFF (default) they mirror FavouriteContracts.GetDefaults
        ' exactly (existing behaviour); when ON they reflect the current adaptive selection
        ' captured at VM construction. Mid-flight refreshes are not applied to these
        ' arrays — re-navigating to the tab picks up a refreshed watchlist.
        Private ReadOnly _stDefaults As IReadOnlyList(Of Core.Trading.FavouriteContract)
        Private ReadOnly Instruments As String()
        Private ReadOnly InstrumentLabels As String()
        Private Const BarsToFetch As Integer = 60
        Private Const EntryStaggerMs As Integer = 5000
        Private Const SlotUpdateStaggerMs As Integer = 2000

        ' TopStepX session-close window: entries suppressed, scan skipped.
        Private Shared ReadOnly SessionCloseTime As TimeSpan = TimeSpan.FromHours(21).Add(TimeSpan.FromMinutes(10))
        Private Shared ReadOnly SessionResumeTime As TimeSpan = TimeSpan.FromHours(22)

        Public ReadOnly Property WatchlistItems

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
        ''' <summary>BUG-72: SignalR UserHub client. Subscribed to PositionUpdated for real-time
        ''' P&amp;L and VWAP entry-price sync, replacing the polling-only path that lagged 30+ minutes
        ''' behind the broker on practice accounts.</summary>
        Private ReadOnly _userHub As UserHubClient
        Private _userHubHandler As EventHandler(Of PXPositionUpdateEventArgs)

        ''' <summary>FEAT-52: SignalR MarketHub client. Subscribed to QuoteReceived for sub-second
        ''' last-trade prices that drive Live Price + local P&amp;L on each open slot card. Replaces
        ''' the up-to-15-second polling cadence of the 5-second bar fetch as the primary source.</summary>
        Private ReadOnly _marketHub As MarketHubClient
        Private _marketHubHandler As EventHandler(Of MarketQuoteEventArgs)

        ''' <summary>FEAT-54: Per-slot live price/P&amp;L push stream. Replaces the slot-path
        ''' <c>_lastQuotePrices</c> polling fallback for open positions — quotes and bar fallback
        ''' arrive on a single push channel, with diagnostics surfaced via <c>GetDiagnostics</c>.</summary>
        Private ReadOnly _livePnL As ILivePnLService

        ''' <summary>FEAT-52: per-instrument cache of the most recent last-trade price from
        ''' MarketHub.QuoteReceived. Keyed by resolved PX contract ID (e.g. "CON.F.US.MNQ.U26").
        ''' Written by the SignalR callback thread; read by the timer + hub threads.
        ''' FEAT-54: still drives the watchlist quote-cache (out-of-scope for this ticket);
        ''' open-slot cards no longer read from this dictionary.</summary>
        Private ReadOnly _lastQuotePrices As New ConcurrentDictionary(Of String, Double)(
            StringComparer.OrdinalIgnoreCase)

        ' ── Debug capture state (per-trade tracking, keyed by DebugTradeId) ────
        Private ReadOnly _debugMfe As New Dictionary(Of String, Decimal)()
        Private ReadOnly _debugMae As New Dictionary(Of String, Decimal)()
        Private ReadOnly _lastBarTimestampByTradeId As New Dictionary(Of String, DateTimeOffset)()

        Private _timer As Timer
        Private ReadOnly _timerLock As New Object()
        Private _disposed As Boolean = False

        ''' <summary>ARCH-19: cancellation token source bound to the monitoring lifecycle.
        ''' Fresh CTS on each Start; Cancel + Dispose on Stop. The token is handed to
        ''' <see cref="IEntryExecutionService.PlaceAsync"/> so an in-flight entry whose
        ''' AI veto await straddles a Stop click does not place an order after the user
        ''' has explicitly stopped monitoring (replaces the in-line _isMonitoring guard).</summary>
        Private _monitoringCts As CancellationTokenSource
        Private _isTicking As Integer = 0
        Private _allMarketsClosed As Boolean = False
        Private _lastScanUtc As DateTime = DateTime.MinValue

        Private ReadOnly _approachHistory As New Dictionary(Of String, ApproachState)
        Private ReadOnly _prevStDirByInstrument As New Dictionary(Of String, Single)()

        ''' <summary>STRAT-40 F3: per-contract deferred-entry queue. Populated when the
        ''' strategy-TF SuperTrend has flipped (or remains active) but the lower-TF
        ''' SuperTrend disagrees — the candidate is held until either (a) the lower TF
        ''' flips into agreement, (b) the strategy-TF signal evaporates, or (c) the
        ''' candidate ages out per <c>Config.MultiTfDeferMaxAgeMinutes</c>. Key is
        ''' the contract ID; a fresher same-contract candidate deliberately overwrites
        ''' an older one. Cleared on monitoring stop.</summary>
        Private ReadOnly _deferredCandidates As New ConcurrentDictionary(Of String, DeferredCandidate)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _exitEngine As ExitSignalEngine
        ''' <summary>FEAT-61: enriches the captured TradeSetupSnapshot with the indicator columns
        ''' that the live SuperTrend+ strategy does not itself compute (Ichimoku, EMA21/50, MACD,
        ''' StochRSI, VIDYA, CMO, ΔVol). Optional so existing test ViewModel construction sites
        ''' that don't care about ML feature persistence continue to compile.</summary>
        Private ReadOnly _snapshotEnricher As TradeSetupSnapshotEnricher

        ''' <summary>ARCH-19: strategy-agnostic entry pipeline. <c>FireEntryAsync</c> is now a
        ''' thin adapter that builds the request and delegates to this service.</summary>
        Private ReadOnly _entryExecution As IEntryExecutionService

        ''' <summary>ARCH-20: strategy-agnostic per-tick position management pipeline.
        ''' <c>HandleOpenPositionAsync</c> is now a thin coordinator that builds the tick
        ''' context and delegates to this service.</summary>
        Private ReadOnly _positionMgmt As IPositionManagementService

        ''' <summary>ARCH-20: strategy-agnostic exit execution pipeline. <c>ReleaseSlotAsync</c>
        ''' delegates DB / broker side-effects to this service and only retains the UI cleanup
        ''' (slot box reset, debug-capture EndTrade, MarketHub unsubscribe, SlotManager close).</summary>
        Private ReadOnly _exitExecution As IExitExecutionService

        ''' <summary>FEAT-71: account-wide daily-loss kill switch. Gates new entries.</summary>
        Private ReadOnly _dailyLossGuard As IDailyLossGuard
        Private _dailyLossPnlSource As IOpenSlotPnlSource

        ''' <summary>FEAT-72: adaptive watchlist service; supplies the per-VM Instruments
        ''' set at construction when the toggle is ON, and pins this VM's open slot
        ''' instruments so they cannot drop out of the global watchlist.</summary>
        Private ReadOnly _adaptiveWatchlist As AdaptiveWatchlistService
        ''' <summary>Instruments whose slot has been released at least once this monitoring session.
        ''' Cleared on Start. Used to enforce the 15s BB-middle re-entry sense-check (FEAT-47).</summary>
        Private ReadOnly _instrumentsReleasedThisSession As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private _useEarlyMode As Boolean = False

        ''' <summary>Set when any slot is released during a tick; cleared at tick start.
        ''' Prevents same-tick re-entry after a position is closed.</summary>
        Private _releasedThisTick As Boolean = False

        ' ARCH-20: the alternating-tick snapshot-skip set moved into the singleton
        ' IPositionManagementService so the per-strategy snapshot cadence is shared
        ' across ViewModels and a second strategy joining the same instrument inherits
        ' the in-flight cadence rather than burning a redundant REST call.

        ' ARCH-19: AI suppression state moved into the singleton IEntryExecutionService
        ' (the suppression dict outlives any single ViewModel and is shared across
        ' strategies). The VM only reads from it via IEntryExecutionService.IsAiSuppressed.

        ''' <summary>Last AI veto reason per contract, captured in <see cref="SetWatchlistAiStatus"/>.
        ''' The watchlist scan re-applies this whenever <see cref="IEntryExecutionService.IsAiSuppressed"/>
        ''' is still true, so the "What this means" cell does not snap back to the trend description
        ''' on the next 15s tick.</summary>
        Private ReadOnly _aiVetoReasons As New ConcurrentDictionary(Of String, String)(
            StringComparer.OrdinalIgnoreCase)

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

        ' ── Entry mode selector (combobox replacement of legacy radios) ─────────
        Public Const EntryModeBarClose As String = "Bar Close"
        Public Const EntryModePreemptive As String = "Pre-emptive Entry"

        Public ReadOnly Property EntryModes As String() = {EntryModeBarClose, EntryModePreemptive}

        Public Property SelectedEntryMode As String
            Get
                Return If(_useEarlyMode, EntryModePreemptive, EntryModeBarClose)
            End Get
            Set(value As String)
                Dim early = String.Equals(value, EntryModePreemptive, StringComparison.OrdinalIgnoreCase)
                If early <> _useEarlyMode Then
                    _useEarlyMode = early
                    NotifyPropertyChanged(NameOf(SelectedEntryMode))
                    NotifyPropertyChanged(NameOf(UseEarlyMode))
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

        Public ReadOnly Property Timeframes As String() = {"5min", "15min", "1hr"}

        ' ── Leverage multiplier (resets to 1 on every restart) ──────────────────
        Public ReadOnly Property Leverages As Integer() = {1, 2, 3}

        Public Property SelectedLeverage As Integer
            Get
                Return Math.Max(1, Config.LeverageMultiplier)
            End Get
            Set(value As Integer)
                Dim clamped As Integer = Math.Max(1, Math.Min(3, value))
                If Config.LeverageMultiplier <> clamped Then
                    Config.LeverageMultiplier = clamped
                    NotifyPropertyChanged(NameOf(SelectedLeverage))
                End If
            End Set
        End Property

        ' ── FEAT-63: $-denominated TP ladder ────────────────────────────────────
        ''' <summary>Global ladder TP increment in dollars (persists across restarts).
        ''' &gt; 0 enables ladder mode and suppresses E1–E9 force-close.</summary>
        Public Property LadderTpDollars As Decimal
            Get
                Return Config.LadderTpDollars
            End Get
            Set(value As Decimal)
                Dim clamped As Decimal = If(value < 0D, 0D, value)
                If Config.LadderTpDollars <> clamped Then
                    Config.LadderTpDollars = clamped
                    NotifyPropertyChanged(NameOf(LadderTpDollars))
                    SaveConfigFireAndForget()
                End If
            End Set
        End Property

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

        ''' <summary>BUG-90 F1: registry the VM joins on Start, leaves on Stop/Dispose.
        ''' The singleton <c>BrokerSlotSweepWorker</c> iterates registered sinks every 60 s.</summary>
        Private ReadOnly _sweepRegistry As Core.Trading.OpenSlotReleaseSinkRegistry

        Public Sub New(barService As IBarIngestionService,
                       orderService As IOrderService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService,
                       accountService As IAccountService,
                       contractResolver As Core.Interfaces.IContractResolutionService,
                       claudeService As IClaudeReviewService,
                       logger As ILogger(Of SuperTrendPlusViewModel),
                       tradeRecordService As Core.Interfaces.ITradeRecordService,
                       exitEngine As ExitSignalEngine,
                       Optional configRepo As SuperTrendPlusConfigRepository = Nothing,
                       Optional debugCapture As Core.Interfaces.IDebugTradeCaptureService = Nothing,
                       Optional userHub As UserHubClient = Nothing,
                       Optional marketHub As MarketHubClient = Nothing,
                       Optional livePnL As ILivePnLService = Nothing,
                       Optional snapshotEnricher As TradeSetupSnapshotEnricher = Nothing,
                       Optional sweepRegistry As Core.Trading.OpenSlotReleaseSinkRegistry = Nothing,
                       Optional entryExecution As IEntryExecutionService = Nothing,
                       Optional positionMgmt As IPositionManagementService = Nothing,
                       Optional exitExecution As IExitExecutionService = Nothing,
                       Optional dailyLossGuard As IDailyLossGuard = Nothing,
                       Optional adaptiveWatchlist As AdaptiveWatchlistService = Nothing)
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
            _userHub = userHub
            _marketHub = marketHub
            _livePnL = livePnL
            Config = New SuperTrendPlusConfig()
            _slotManager = New SlotManager(Config)
            _exitEngine = exitEngine
            _snapshotEnricher = snapshotEnricher
            _sweepRegistry = sweepRegistry
            _entryExecution = entryExecution
            _positionMgmt = positionMgmt
            _exitExecution = exitExecution
            _dailyLossGuard = dailyLossGuard
            If _dailyLossGuard IsNot Nothing Then
                _dailyLossPnlSource = New SlotManagerPnlSource(_slotManager)
                _dailyLossGuard.RegisterOpenSlotPnlSource(_dailyLossPnlSource)
            End If
            _adaptiveWatchlist = adaptiveWatchlist
            _stDefaults = ResolveInstrumentSet(_adaptiveWatchlist)
            Instruments = _stDefaults.Select(Function(f) f.PxRootSymbol).ToArray()
            InstrumentLabels = _stDefaults.Select(
                Function(f)
                    Dim root = If(f.PxRootSymbol = "MCLE", "MCL", f.PxRootSymbol).ToUpperInvariant()
                    Dim name = If(String.IsNullOrWhiteSpace(f.DisplayName), f.PxRootSymbol, f.DisplayName).ToUpperInvariant()
                    Return root & ": " & name
                End Function).ToArray()
            _adaptiveWatchlist?.RegisterOpenSlotSource(Me)
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

            ' BUG-90 F4: wire each slot card's Force-reconcile button to the VM entry point.
            Slot1.ForceReconcileCommand = New RelayCommand(Async Sub() Await ForceReconcileSlotAsync(0))
            Slot2.ForceReconcileCommand = New RelayCommand(Async Sub() Await ForceReconcileSlotAsync(1))
            Slot3.ForceReconcileCommand = New RelayCommand(Async Sub() Await ForceReconcileSlotAsync(2))

            If WatchlistItems Is Nothing Then
                WatchlistItems = New System.Collections.ObjectModel.ObservableCollection(Of WatchlistRowVm)()
            End If

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
            Config.WarningScoreThreshold = entity.WarningScoreThreshold
            Config.ExitingScoreThreshold = entity.ExitingScoreThreshold
            Config.EntryExitScoreBlockThreshold = entity.EntryExitScoreBlockThreshold
            Config.LadderTpDollars = entity.LadderTpDollars
            NotifyPropertyChanged(NameOf(LadderTpDollars))
        End Sub

        Private Function BuildConfigEntity() As Data.Entities.SuperTrendPlusConfigEntity
            Return New Data.Entities.SuperTrendPlusConfigEntity With {
                .ActivePersona = _activePersona,
                .SelectedTimeframe = _selectedTimeframe,
                .MaxSlots = Config.MaxSlots,
                .AdxWeakThreshold = Config.AdxWeakThreshold,
                .AdxModerateThreshold = Config.AdxModerateThreshold,
                .AdxStrongThreshold = Config.AdxStrongThreshold,
                .WarningScoreThreshold = Config.WarningScoreThreshold,
                .ExitingScoreThreshold = Config.ExitingScoreThreshold,
                .EntryExitScoreBlockThreshold = Config.EntryExitScoreBlockThreshold,
                .LadderTpDollars = Config.LadderTpDollars
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
            _instrumentsReleasedThisSession.Clear()
            ' ARCH-19: fresh monitoring CTS for this run.
            _monitoringCts?.Dispose()
            _monitoringCts = New CancellationTokenSource()
            ' BUG-72: subscribe to the SignalR UserHub real-time position stream so P&L
            ' and entry-price VWAP arrive immediately rather than waiting on the
            ' 15-second poll (which sourced stale paper-feed prices and could freeze
            ' the displayed P&L for tens of minutes on practice accounts).
            If _userHub IsNot Nothing AndAlso _userHubHandler Is Nothing Then
                _userHubHandler = AddressOf OnHubPositionUpdated
                AddHandler _userHub.PositionUpdated, _userHubHandler
            End If
            ' FEAT-52: subscribe to MarketHub.QuoteReceived for sub-second last-trade prices.
            ' Per-instrument SubscribeContractAsync calls happen on slot open (FireEntryAsync).
            If _marketHub IsNot Nothing AndAlso _marketHubHandler Is Nothing Then
                _marketHubHandler = AddressOf OnMarketQuoteReceived
                AddHandler _marketHub.QuoteReceived, _marketHubHandler
            End If
            _timer = New Timer(AddressOf TimerCallback, Nothing, 0, 15000)
            ' BUG-90 F1: join the broker-sweep registry so the singleton 60 s worker can
            ' iterate this VM's occupied slots. The registry is optional (Nothing in
            ' test construction paths) so the gate is required here.
            _sweepRegistry?.Register(Me)
            If _selectedAccount Is Nothing OrElse _selectedAccount.Id = 0 Then
                StatusText = "? No account selected — monitoring in read-only mode (orders will be blocked until account loads)"
                Application.Current?.Dispatcher?.Invoke(Sub()
                                                            StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H0))
                                                        End Sub)
            End If
        End Sub

        Friend Sub StopMonitoring()
            IsMonitoring = False
            ' ARCH-19: cancel the monitoring CTS so any in-flight entry that's awaiting
            ' the AI veto bails out instead of placing an order after Stop is clicked.
            Try
                _monitoringCts?.Cancel()
            Catch
            End Try
            ' BUG-90 F1: leave the broker-sweep registry so the worker stops querying
            ' this VM's slots once monitoring is off.
            _sweepRegistry?.Unregister(Me)
            SyncLock _timerLock
                If _timer IsNot Nothing Then
                    _timer.Dispose()
                    _timer = Nothing
                End If
            End SyncLock
            ' BUG-72: tear down the SignalR position subscription. Without this the
            ' VM would keep mutating slot state after Stop Monitoring (and after
            ' Dispose, if the singleton hub outlives the VM).
            If _userHub IsNot Nothing AndAlso _userHubHandler IsNot Nothing Then
                Try
                    RemoveHandler _userHub.PositionUpdated, _userHubHandler
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ failed to unsubscribe UserHub.PositionUpdated")
                End Try
                _userHubHandler = Nothing
            End If
            ' FEAT-52: tear down MarketHub quote subscription and drop cached prices.
            ' Per-contract MarketHub.UnsubscribeContractAsync is the responsibility of
            ' ReleaseSlotAsync on each individual slot close.
            If _marketHub IsNot Nothing AndAlso _marketHubHandler IsNot Nothing Then
                Try
                    RemoveHandler _marketHub.QuoteReceived, _marketHubHandler
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ failed to unsubscribe MarketHub.QuoteReceived")
                End Try
                _marketHubHandler = Nothing
            End If
            _lastQuotePrices.Clear()
            _prevStDirByInstrument.Clear()
            ' STRAT-40 F3: deferred-entry queue must not survive a strategy stop —
            ' a stale candidate carrying yesterday's signal would fire at startup.
            _deferredCandidates.Clear()
            ' FEAT-54: dispose every active slot live-price subscription before resetting
            ' slot state so we never leak a MarketHub ref-count past Stop Monitoring.
            For Each s In _slotManager.Slots
                _slotManager.EndLiveTracking(s)
            Next
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
                    Await HandleOpenPositionAsync(slot, tf, barCache)
                    isFirstSlotUpdate = False
                End If
            Next

            ' BUG-90 F4: refresh stuck-slot diagnostic display once per tick. Runs after
            ' HandleOpenPositionAsync (which advances LastSnapshotOkUtc on a confirmed
            ' snapshot) so the warning chip / red banner reflect the latest known age.
            RefreshStuckSlotDiagnostics()

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

                ' STRAT-42 F4: per-contract session-hours gate. Skip evaluation when the
                ' contract is outside its trading window. The skip does NOT count toward
                ' the anyFreshBar / _allMarketsClosed throttle — a single closed contract
                ' must not stop the timer for the rest of the watchlist.
                If Not ContractSessionHours.IsContractTradingNow(contractId, DateTime.UtcNow) Then
                    Dim opensAt = ContractSessionHours.NextOpenUtc(contractId, DateTime.UtcNow)
                    _logger.LogInformation(
                        "ST+ ScanWatchlist [{Contract}] contract closed — next session opens at {OpensAt:u}",
                        contractId, opensAt)
                    Application.Current?.Dispatcher?.Invoke(
                        Sub()
                            wRow.Signal = "closed"
                            wRow.Arrow = "–"
                            wRow.RowColor = Brushes.Gray
                            wRow.SignalReason = String.Format("Contract closed — next session opens at {0:u}", opensAt)
                        End Sub)
                    Continue For
                End If

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
                    ' STRAT-42 F3: replaced "Market closed" with a freshness-based message. The
                    ' previous wording implied an exchange closure even during legitimate
                    ' low-volume overnight sessions where bars can lag.
                    Application.Current?.Dispatcher?.Invoke(
                        Sub()
                            wRow.Signal = "stale"
                            wRow.Arrow = "–"
                            wRow.RowColor = Brushes.Gray
                            wRow.SignalReason = String.Format("Awaiting bar — last bar {0:F0} min ago", staleAgeMins)
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
                    Dim n3 As Integer = 3 * Math.Max(1, Config.LeverageMultiplier)
                    strength = String.Format("ADX:{0:D2} L3: Espresso", CInt(Math.Floor(adxVal)))
                    signalReason = If(signal = "BULL", $"Strong uptrend — bot will open {n3} positions.",
                                   If(signal = "BEAR", $"Strong downtrend — bot will open {n3} positions.",
                                      "Strong trend forming — waiting for direction alignment."))
                ElseIf adxVal >= Config.AdxModerateThreshold Then
                    Dim n2 As Integer = 2 * Math.Max(1, Config.LeverageMultiplier)
                    strength = String.Format("ADX:{0:D2} L2: Latte", CInt(Math.Floor(adxVal)))
                    signalReason = If(signal = "BULL", $"Moderate uptrend — bot will open {n2} positions.",
                                   If(signal = "BEAR", $"Moderate downtrend — bot will open {n2} positions.",
                                      "Trending — waiting for +DI/-DI to align with SuperTrend."))
                ElseIf adxVal >= Config.AdxWeakThreshold Then
                    Dim n1 As Integer = 1 * Math.Max(1, Config.LeverageMultiplier)
                    Dim positionWord As String = If(n1 = 1, "position", "positions")
                    strength = String.Format("ADX:{0:D2} L1: Latte", CInt(Math.Floor(adxVal)))
                    signalReason = If(signal = "BULL", $"Uptrend active — bot will open {n1} {positionWord}.",
                                   If(signal = "BEAR", $"Downtrend active — bot will open {n1} {positionWord}.",
                                      "Trending — waiting for +DI/-DI to align with SuperTrend."))
                    ' Persona-gate override: ADX may be in a tradeable band but below the
                    ' active persona's MinEntryAdx — be honest that no slot will open.
                    If (signal = "BULL" OrElse signal = "BEAR") AndAlso adxVal < Config.MinEntryAdx Then
                        signalReason = String.Format("Trending — waiting for ADX ≥ {0:D2} ({1} gate).",
                                                     CInt(Math.Ceiling(Config.MinEntryAdx)), Config.ActivePersona)
                    End If
                ElseIf adxVal >= 15 Then
                    strength = String.Format("ADX:{0:D2} L0: Decaff", CInt(Math.Floor(adxVal)))
                    signalReason = "Trend is weak — watching for momentum to build before entering."
                Else
                    strength = String.Format("ADX:{0:D2} Cat Piss", CInt(Math.Floor(adxVal)))
                    signalReason = "Market is choppy with no clear trend — standing aside to avoid false signals."
                End If

                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--",
                                           If(adxVal >= Config.AdxStrongThreshold, String.Format("ADX:{0:D2} L3: Espresso", CInt(Math.Floor(adxVal))),
                                           If(adxVal >= Config.AdxModerateThreshold, String.Format("ADX:{0:D2} L2: Cappuccino", CInt(Math.Floor(adxVal))),
                                           If(adxVal >= Config.AdxWeakThreshold, String.Format("ADX:{0:D2} L1: Latte", CInt(Math.Floor(adxVal))),
                                              String.Format("ADX:{0:D2}", CInt(Math.Floor(adxVal)))))))
                Dim diStr As String = If(Single.IsNaN(plusDi) OrElse Single.IsNaN(minusDi),
                                        "+DI:-- -DI:--",
                                        String.Format("+DI:{0:D2} -DI:{1:D2}", CInt(plusDi), CInt(minusDi)))

                ' BB median slope warning on watchlist
                Dim scanBbLookback As Integer = If(_selectedTimeframe = "5min", 12, If(_selectedTimeframe = "1hr", 2, 4))
                If closes.Count >= 20 + scanBbLookback AndAlso (isLongSignal OrElse isShortSignal) Then
                    Dim scanBbResult = TechnicalIndicators.BollingerBands(closes, period:=20, stdDevMultiplier:=2.0)
                    Dim scanBbMidNow = scanBbResult.Middle(n)
                    Dim scanBbMidPrev = scanBbResult.Middle(n - scanBbLookback)
                    If Not Single.IsNaN(scanBbMidNow) AndAlso Not Single.IsNaN(scanBbMidPrev) Then
                        Dim scanBbSlope = scanBbMidNow - scanBbMidPrev
                        Dim bbConflict As Boolean = (isLongSignal AndAlso scanBbSlope < 0F) OrElse
                                                    (isShortSignal AndAlso scanBbSlope > 0F)
                        If bbConflict Then
                            signalReason = "⚠ BB trend conflict — median slope opposes signal; entry blocked."
                        End If
                    End If
                End If

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

                ' If the AI vetoed this contract recently, keep the veto reason in the
                ' "What this means" cell for the duration of the suppression window —
                ' otherwise the scan would clobber it with the trend description on the
                ' next 15s tick.
                If _entryExecution IsNot Nothing AndAlso _entryExecution.IsAiSuppressed(contractId) Then
                    Dim cachedVeto As String = Nothing
                    If _aiVetoReasons.TryGetValue(contractId, cachedVeto) AndAlso
                       Not String.IsNullOrEmpty(cachedVeto) Then
                        signalReason = cachedVeto
                    Else
                        signalReason = "🤖 AI veto active — new entries suppressed."
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

            Next
            ' STRAT-42 F3/F2d: throttle reviewed. _allMarketsClosed flips back to False the moment
            ' any single contract returns a fresh bar on the next tick, so the 60-second back-off
            ' lifts immediately. Preserve.
            _allMarketsClosed = Not anyFreshBar

            Return cache
        End Function

        Private Async Sub FlashStatusAsync()
            StatusBackground = New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))
            Await Task.Delay(400)
            StatusBackground = Brushes.Transparent
        End Sub

        Private Function UpdateApproachHistory(contractId As String, stDir As Integer, distance As Double) As Boolean
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

                ' STRAT-40 F3: deferred-candidate queue check. If a candidate was held on a
                ' prior tick waiting for a lower-TF flip, re-evaluate here. Three outcomes:
                '   (a) direction evaporated → drop, fall through to fresh evaluation;
                '   (b) too old → drop, fall through to fresh evaluation;
                '   (c) lower TF now agrees → promote (skip fresh eval for this contract);
                '   (d) lower TF still disagrees → keep deferred (skip fresh eval).
                Dim deferred As DeferredCandidate = Nothing
                If Config.MultiTfConfirmationEnabled AndAlso
                   _deferredCandidates.TryGetValue(contractId, deferred) Then
                    Dim dirMatches As Boolean =
                        (String.Equals(deferred.Side, "Buy", StringComparison.OrdinalIgnoreCase) AndAlso isLong) OrElse
                        (String.Equals(deferred.Side, "Sell", StringComparison.OrdinalIgnoreCase) AndAlso isShort)
                    Dim ageMin As Double = (DateTimeOffset.UtcNow - deferred.FirstDeferredUtc).TotalMinutes
                    Dim maxAge As Double = CDbl(Config.MultiTfDeferMaxAgeMinutes)

                    If Not dirMatches Then
                        Dim dropped As DeferredCandidate = Nothing
                        _deferredCandidates.TryRemove(contractId, dropped)
                        _logger.LogInformation(
                            "ST+ [{Contract}] deferred candidate dropped — strategy-TF direction evaporated (was {Side})",
                            contractId, deferred.Side)
                    ElseIf ageMin > maxAge Then
                        Dim dropped As DeferredCandidate = Nothing
                        _deferredCandidates.TryRemove(contractId, dropped)
                        _logger.LogInformation(
                            "ST+ [{Contract}] deferred candidate dropped — age {Age:F1}m > {Max}m",
                            contractId, ageMin, Config.MultiTfDeferMaxAgeMinutes)
                    Else
                        ' Re-check lower TF; promote on agreement, keep on disagreement.
                        Dim lowerBars = Await GetLowerTfBarsAsync(contractId)
                        Dim isLongDeferred = String.Equals(deferred.Side, "Buy", StringComparison.OrdinalIgnoreCase)
                        Dim ltRes = EntryQualityGate.EvaluateLowerTfAgreement(
                            lowerBars, isLongDeferred, stPeriod:=10, stMultiplier:=_stMultiplier)
                        If Not ltRes.IsBlocked Then
                            _logger.LogInformation(
                                "ST+ [{Contract}] promoting deferred candidate — {Reason} (age {Age:F1}m)",
                                contractId, ltRes.Reason, ageMin)
                            candidates.Add((deferred.ContractId, deferred.InstrumentIndex, deferred.Side,
                                            deferred.AdxAtSignal, deferred.BarTimeOfSignal,
                                            deferred.StLineAtSignal, deferred.LastCloseAtSignal))
                            Dim dropped As DeferredCandidate = Nothing
                            _deferredCandidates.TryRemove(contractId, dropped)
                            Dim adxStrPromote = If(Single.IsNaN(adxVal), "ADX:--",
                                String.Format("ADX:{0:D2}", CInt(Math.Floor(adxVal))))
                            UpdateSlotSymbolRows(i, If(isLongDeferred, "UP", "DN"), adxStrPromote,
                                                 If(isLongDeferred, "LONG", "SHORT"),
                                                 If(isLongDeferred, CType(Brushes.LimeGreen, Brush), CType(Brushes.Red, Brush)))
                            Continue For
                        Else
                            _logger.LogInformation(
                                "ST+ [{Contract}] candidate deferred — waiting for lower TF ({Reason}, age {Age:F1}m)",
                                contractId, ltRes.Reason, ageMin)
                            Dim adxStrDefer = If(Single.IsNaN(adxVal), "ADX:--",
                                String.Format("ADX:{0:D2}", CInt(Math.Floor(adxVal))))
                            UpdateSlotSymbolRows(i, If(isLongDeferred, "UP", "DN"), adxStrDefer,
                                                 "deferred", Brushes.Gold)
                            Continue For
                        End If
                    End If
                End If

                ' STRAT-40 F1: BB-median slope filter with relax-on-flip. The legacy filter
                ' blocked every short flip during the multi-week rally (BB-mid up-sloping →
                ' 6:1 long bias from 2026-05-19). EvaluateBbMedianRelaxOnFlip bypasses the
                ' slope check when a fresh SuperTrend flip occurs in a strong-ADX regime;
                ' otherwise the standard slope-agrees rule still applies.
                Dim bbLookback As Integer = If(_selectedTimeframe = "5min", 12, If(_selectedTimeframe = "1hr", 2, 4))
                Dim bbMedianAgrees As Boolean = True
                If isLong OrElse isShort Then
                    Dim closesSingleBb As IList(Of Single) = closes.Select(Function(d) CSng(d)).ToList()
                    Dim bbRelax = EntryQualityGate.EvaluateBbMedianRelaxOnFlip(
                        closesSingleBb, isLong, isFlip, adxVal,
                        bbLookback, Config.BbMedianRelaxAdxThreshold)
                    If bbRelax.IsBlocked Then
                        bbMedianAgrees = False
                        _logger.LogInformation(
                            "ST+ [{Contract}] {Reason} — entry suppressed",
                            contractId, bbRelax.Reason)
                    Else
                        ' Surface the relax-path reason at Information so the AI/Entry log
                        ' explicitly shows "BB-median bypassed: fresh flip with ADX >= strong".
                        _logger.LogInformation(
                            "ST+ [{Contract}] {Reason}",
                            contractId, bbRelax.Reason)
                    End If
                End If

                ' UAT-03 F2/F3/F6: deterministic entry-quality gates layered on the existing
                ' BB-median-slope check. F2 (BB position) always evaluated; F3 (momentum-against)
                ' only when isFlip = False; F6 (confirmation candle) only when isFlip = True.
                Dim entryGateBlocks As Boolean = False
                If (isLong OrElse isShort) AndAlso bbMedianAgrees Then
                    Dim closesSingle As IList(Of Single) =
                        closes.Select(Function(d) CSng(d)).ToList()

                    If Config.BbPositionGateEnabled AndAlso closes.Count >= 20 Then
                        Dim bbForGate = TechnicalIndicators.BollingerBands(closes, period:=20, stdDevMultiplier:=2.0)
                        Dim medianAtEntry As Single = bbForGate.Middle(n)
                        Dim bbRes = EntryQualityGate.EvaluateBbPosition(
                            closesSingle, medianAtEntry, isLong, Config.BbPositionGateBars)
                        If bbRes.IsBlocked Then
                            _logger.LogInformation("ST+ [{Contract}] {Reason} — entry suppressed", contractId, bbRes.Reason)
                            entryGateBlocks = True
                        End If
                    End If

                    If Not entryGateBlocks AndAlso Config.MomentumAgainstGateEnabled Then
                        Dim momRes = EntryQualityGate.EvaluateMomentumAgainst(
                            closesSingle, isLong, isFlip, Config.MomentumAgainstGateBars)
                        If momRes.IsBlocked Then
                            _logger.LogInformation("ST+ [{Contract}] {Reason} — entry suppressed", contractId, momRes.Reason)
                            entryGateBlocks = True
                        End If
                    End If

                    If Not entryGateBlocks AndAlso Config.ConfirmationCandleGateEnabled Then
                        Dim confRes = EntryQualityGate.EvaluateConfirmationCandle(
                            bars, isLong, isFlip)
                        If confRes.IsBlocked Then
                            _logger.LogInformation("ST+ [{Contract}] {Reason} — entry suppressed", contractId, confRes.Reason)
                            entryGateBlocks = True
                        End If
                    End If
                End If

                Dim isFavourable As Boolean = (isLong OrElse isShort) AndAlso (isFlip OrElse isActive) AndAlso bbMedianAgrees AndAlso Not entryGateBlocks

                ' FEAT-47: 15s BB-middle re-entry sense check.
                ' For instruments released this session, require 15s BB middle (length 10, mult 2.0)
                ' to confirm direction on the just-closed 15s bar before re-entering.
                If isFavourable AndAlso _instrumentsReleasedThisSession.Contains(contractId) AndAlso
                   Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                       String.Equals(s.Instrument, contractId, StringComparison.OrdinalIgnoreCase)) Then
                    If Not Await Bb15sConfirmsDirectionAsync(contractId, isLong) Then
                        _logger.LogInformation(
                            "ST+ [{Contract}] 15s BB confirmation gate — re-entry suppressed", contractId)
                        isFavourable = False
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

                ' STRAT-40 F2/F3: lower-TF SuperTrend agreement. A favourable strategy-TF
                ' candidate is held back when the lower TF has not yet flipped — instead of
                ' dropping it, we enqueue it for re-check on subsequent ticks. Note: per-contract
                ' overwrite is deliberate (a fresher candidate replaces an older one).
                If isFavourable AndAlso Config.MultiTfConfirmationEnabled Then
                    Dim lowerBars = Await GetLowerTfBarsAsync(contractId)
                    Dim ltRes = EntryQualityGate.EvaluateLowerTfAgreement(
                        lowerBars, isLong, stPeriod:=10, stMultiplier:=_stMultiplier)
                    If ltRes.IsBlocked Then
                        _logger.LogInformation(
                            "ST+ [{Contract}] candidate deferred — waiting for lower TF ({Reason})",
                            contractId, ltRes.Reason)
                        Dim sideDefer As String = If(isLong, "Buy", "Sell")
                        _deferredCandidates(contractId) = New DeferredCandidate With {
                            .ContractId = contractId,
                            .InstrumentIndex = i,
                            .Side = sideDefer,
                            .BarTimeOfSignal = bars(n).Timestamp,
                            .StLineAtSignal = stLine,
                            .LastCloseAtSignal = CDec(closes(n)),
                            .FirstDeferredUtc = DateTimeOffset.UtcNow,
                            .AdxAtSignal = adxVal
                        }
                        isFavourable = False
                    Else
                        _logger.LogInformation(
                            "ST+ [{Contract}] {Reason}", contractId, ltRes.Reason)
                    End If
                End If

                _logger.LogInformation(
                    "ST+ [{Contract}] stDir={StDir} ADX={Adx:F1} +DI={PlusDI:F1} -DI={MinusDI:F1} " &
                    "isLong={IsLong} isShort={IsShort} isFlip={IsFlip} isActive={IsActive} isFavourable={IsFav}",
                    contractId, stDir, adxVal, plusDi, minusDi, isLong, isShort, isFlip, isActive, isFavourable)

                Dim adxStr = If(Single.IsNaN(adxVal), "ADX:--",
                               If(adxVal >= Config.AdxStrongThreshold, String.Format("ADX:{0:D2} L3: Espresso", CInt(Math.Floor(adxVal))),
                               If(adxVal >= Config.AdxModerateThreshold, String.Format("ADX:{0:D2} L2: Cappuccino", CInt(Math.Floor(adxVal))),
                               If(adxVal >= Config.AdxWeakThreshold, String.Format("ADX:{0:D2} L1: Latte", CInt(Math.Floor(adxVal))),
                                   String.Format("ADX:{0:D2}", CInt(Math.Floor(adxVal)))))))

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

                ' FEAT-46: Pre-entry exit-signal gate — block if exit conditions already present at signal time.
                ' Uses the same ExitSignalEngine (E1–E9) that previously ran post-entry; ATR is not available
                ' here so E2/E7 are disabled (NaN array). Threshold 0 disables the gate entirely.
                If Config.EntryExitScoreBlockThreshold > 0 Then
                    Dim preSlot = New PositionSlot() With {
                        .SlotIndex = -1,
                        .Instrument = contractId,
                        .Side = side,
                        .EntryAdx = adxVal,
                        .EntryAtr = 0D,
                        .EntryPrice = CDec(closes(n)),
                        .StopPrice = stLine
                    }
                    Dim nanAtr(n) As Single
                    For k = 0 To n : nanAtr(k) = Single.NaN : Next
                    Dim preEval = _exitEngine.Evaluate(preSlot, highs, lows, closes,
                                                       st.Line, st.Direction,
                                                       dmi.PlusDI, dmi.MinusDI, dmi.ADX,
                                                       nanAtr)
                    If preEval.Score >= Config.EntryExitScoreBlockThreshold Then
                        _logger.LogInformation(
                            "ST+ [{Contract}] pre-entry gate (FEAT-46) score={Score} [{Signals}] >= {Threshold} — entry blocked",
                            contractId, preEval.Score,
                            String.Join(",", preEval.ContributingSignals),
                            Config.EntryExitScoreBlockThreshold)
                        Continue For
                    End If
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

                ' FEAT-71: daily-loss guard hard stop. Suppress this contract and continue the
                ' loop so watchlist UI still updates; the cap applies to the account, not to
                ' an individual instrument, so it short-circuits all candidates equally.
                If _dailyLossGuard IsNot Nothing AndAlso Not _dailyLossGuard.CanEnterNewTrade() Then
                    Dim guardState = _dailyLossGuard.GetState()
                    _logger.LogInformation(
                        "ST+ Entry suppressed — DailyLossGuard halted: {Reason} (combined={Combined:F2}, limit={Limit:F2})",
                        guardState.Reason, guardState.CombinedDailyPnl, guardState.LimitDollars)
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
                If _entryExecution IsNot Nothing AndAlso _entryExecution.IsAiSuppressed(candidate.ContractId) Then
                    _logger.LogDebug("ST+ [{Contract}] AI suppression active — skipping", candidate.ContractId)
                    Continue For
                End If

                ' Guard: verify no live position already exists on the exchange for this instrument
                Dim guardAccId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0)
                If guardAccId <> 0 Then
                    Try
                        Dim liveCheck = Await _orderService.GetLivePositionSnapshotAsync(guardAccId, candidate.ContractId, bypassCache:=True)
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
                    ' STRAT-40 F3: real entry on this contract retires any deferred candidate.
                    Dim droppedOnOpen As DeferredCandidate = Nothing
                    _deferredCandidates.TryRemove(candidate.ContractId, droppedOnOpen)
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

        ''' <summary>STRAT-40 F4: resolves <see cref="SuperTrendPlusConfig.MultiTfLowerTimeframe"/>
        ''' to a <see cref="BarTimeframe"/> enum. Falls back to <c>ThreeMinute</c> on any
        ''' unrecognised string so a typo never silently disables the multi-TF check.</summary>
        Private Shared Function ResolveLowerTimeframe(label As String) As BarTimeframe
            If String.IsNullOrWhiteSpace(label) Then Return BarTimeframe.ThreeMinute
            Select Case label.Trim().ToLowerInvariant()
                Case "1min" : Return BarTimeframe.OneMinute
                Case "3min" : Return BarTimeframe.ThreeMinute
                Case "5min" : Return BarTimeframe.FiveMinute
                Case "15min" : Return BarTimeframe.FifteenMinute
                Case "30min" : Return BarTimeframe.ThirtyMinute
                Case "1hr", "1hour", "60min" : Return BarTimeframe.OneHour
                Case Else : Return BarTimeframe.ThreeMinute
            End Select
        End Function

        ''' <summary>STRAT-40 F4: fetches lower-TF bars for the multi-TF agreement check
        ''' and strips the forming bar (mirrors the BUG-101 pattern in
        ''' <see cref="Bb15sConfirmsDirectionAsync"/>). Returns Nothing on any fetch failure
        ''' so <see cref="EntryQualityGate.EvaluateLowerTfAgreement"/> fails open.</summary>
        Private Async Function GetLowerTfBarsAsync(contractId As String) As Task(Of IList(Of MarketBar))
            Try
                Dim tf = ResolveLowerTimeframe(Config.MultiTfLowerTimeframe)
                Dim tfMinutes = Math.Max(1, CInt(tf))
                Dim raw = Await _barService.GetLiveBarsAsync(contractId, tf, 30)
                If raw Is Nothing OrElse raw.Count = 0 Then Return Nothing
                ' Strip forming bar: last bar younger than its timeframe is incomplete.
                Dim ageSec As Double = (DateTime.UtcNow - raw(raw.Count - 1).Timestamp).TotalSeconds
                If ageSec < tfMinutes * 60.0 AndAlso raw.Count > 1 Then
                    Return raw.Take(raw.Count - 1).ToList()
                End If
                Return raw
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ lower-TF bar fetch failed for {Contract} — failing open", contractId)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' FEAT-47 re-entry sense check: confirm 15s BB middle (length 10, mult 2.0)
        ''' direction agrees with the proposed side on the just-closed 15s bar.
        ''' For LONG: close must be at or above the BB middle; for SHORT, at or below.
        ''' Returns True (allow) on any data shortfall to avoid blocking valid signals.
        ''' </summary>
        Private Async Function Bb15sConfirmsDirectionAsync(contractId As String, isLong As Boolean) As Task(Of Boolean)
            Try
                Dim raw = Await _barService.GetLiveBarsAsync(contractId, BarTimeframe.FifteenSecond, 30)
                If raw Is Nothing OrElse raw.Count < 11 Then Return True
                ' Strip forming bar.
                Dim closedBars As IList(Of MarketBar) =
                    If((DateTime.UtcNow - raw(raw.Count - 1).Timestamp).TotalSeconds < 15 AndAlso raw.Count > 1,
                       CType(raw.Take(raw.Count - 1).ToList(), IList(Of MarketBar)),
                       raw)
                If closedBars.Count < 10 Then Return True
                Dim closesDec = closedBars.Select(Function(b) CDec(b.Close)).ToList()
                Dim bb = TechnicalIndicators.BollingerBands(closesDec, 10, 2.0)
                Dim lastIdx = bb.Middle.Length - 1
                Dim mid As Single = bb.Middle(lastIdx)
                If Single.IsNaN(mid) Then Return True
                Dim lastClose As Decimal = closesDec(closesDec.Count - 1)
                Return If(isLong, lastClose >= CDec(mid), lastClose <= CDec(mid))
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ 15s BB confirmation failed for {Contract} — allowing entry", contractId)
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

                ' BB median direction filter (same 1-hour slope check as confirmed-mode)
                Dim earlyBbLookback As Integer = If(_selectedTimeframe = "5min", 12, If(_selectedTimeframe = "1hr", 2, 4))
                Dim earlyBbMedianAgrees As Boolean = True
                If closes15.Count >= 20 + earlyBbLookback Then
                    Dim earlyBbResult = TechnicalIndicators.BollingerBands(closes15, period:=20, stdDevMultiplier:=2.0)
                    Dim earlyBbMidNow = earlyBbResult.Middle(n15)
                    Dim earlyBbMidPrev = earlyBbResult.Middle(n15 - earlyBbLookback)
                    If Not Single.IsNaN(earlyBbMidNow) AndAlso Not Single.IsNaN(earlyBbMidPrev) Then
                        Dim earlyBbSlope = earlyBbMidNow - earlyBbMidPrev
                        If anticipatedLong AndAlso earlyBbSlope < 0F Then
                            earlyBbMedianAgrees = False
                        ElseIf Not anticipatedLong AndAlso earlyBbSlope > 0F Then
                            earlyBbMedianAgrees = False
                        End If
                    End If
                End If

                Dim earlySignal As Boolean = sig1 AndAlso sig2 AndAlso sig3 AndAlso sig4 AndAlso sig5 AndAlso earlyBbMedianAgrees

                ' FEAT-47: 15s BB-middle re-entry sense check (early-mode parity).
                If earlySignal AndAlso _instrumentsReleasedThisSession.Contains(contractId) AndAlso
                   Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = contractId) Then
                    If Not Await Bb15sConfirmsDirectionAsync(contractId, anticipatedLong) Then
                        earlySignal = False
                    End If
                End If

                Dim side As String = If(anticipatedLong, "Buy", "Sell")
                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--",
                                          If(adxVal >= Config.AdxStrongThreshold, String.Format("ADX:{0:D2} L3: Espresso", CInt(adxVal)),
                                          If(adxVal >= Config.AdxModerateThreshold, String.Format("ADX:{0:D2} L2: Cappuccino", CInt(adxVal)),
                                          If(adxVal >= Config.AdxWeakThreshold, String.Format("ADX:{0:D2} L1: Latte", CInt(adxVal)),
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

            ' FEAT-71: daily-loss guard hard stop applies before any per-instrument live-position
            ' lookups so we don't burn REST calls on entries that will be rejected anyway.
            If _dailyLossGuard IsNot Nothing AndAlso Not _dailyLossGuard.CanEnterNewTrade() Then
                Dim guardState = _dailyLossGuard.GetState()
                _logger.LogInformation(
                    "ST+ EvaluateEarlyEntry suppressed — DailyLossGuard halted: {Reason} (combined={Combined:F2}, limit={Limit:F2})",
                    guardState.Reason, guardState.CombinedDailyPnl, guardState.LimitDollars)
                Return
            End If

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
                        Dim liveCheck2 = Await _orderService.GetLivePositionSnapshotAsync(earlyGuardAccId, bestContractId, bypassCache:=True)
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
                s2.LastSnapshotOkUtc = DateTime.UtcNow  ' BUG-79: stamp on onboarding via reconcile
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

                ' ── Scale-in based on current ADX band (multiplied by leverage) ─────────
                Dim levRec As Integer = Math.Max(1, Config.LeverageMultiplier)
                Dim targetContracts As Integer
                If currentAdx >= Config.AdxStrongThreshold Then
                    targetContracts = 3 * levRec   ' L3: Espresso × leverage
                ElseIf currentAdx >= Config.AdxModerateThreshold Then
                    targetContracts = 2 * levRec   ' L2: Cappuccino × leverage
                Else
                    targetContracts = 1 * levRec   ' L1: Latte × leverage
                End If
                Dim extraContracts As Integer = Math.Max(0, targetContracts - baseContracts)

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
                        ' BUG-72: VWAP-correct EntryPrice on scale-in. Prefer broker fill price;
                        ' fall back to most recent live price. SignalR hub authoritatively
                        ' resyncs once the position update arrives.
                        Dim addFillPrice As Decimal = 0D
                        If placed.FillPrice.HasValue AndAlso placed.FillPrice.Value > 0D Then
                            addFillPrice = placed.FillPrice.Value
                        ElseIf s2.LivePrice > 0D Then
                            addFillPrice = s2.LivePrice
                        End If
                        _slotManager.ApplyScaleIn(s2, extraContracts, addFillPrice)
                        _logger.LogInformation(
                            "ST+ Reconcile [{Contract}] scale-in +{Extra} contracts (ADX {Band}). Total contracts now {Total} fillPx={Px}",
                            contractId, extraContracts,
                            If(currentAdx >= Config.AdxStrongThreshold, "L3: Espresso", "L2: Cappuccino"),
                            s2.Contracts,
                            If(addFillPrice > 0D, addFillPrice.ToString("F4"), "n/a"))
                        ' BUG-92: re-subscribe the live P&L stream so signedSize matches
                        ' the new contract count. Without this the slot card's local
                        ' P&L stays scaled for the prior contracts until a side flip or
                        ' broker-pushed contract change re-triggers a subscribe.
                        BeginSlotLiveTracking(s2)
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

        ' ARCH-19: FireEntryAsync is now a thin adapter — it builds an
        ' EntryExecutionRequest from VM-local state and delegates to the strategy-
        ' agnostic IEntryExecutionService. All eight steps of the entry pipeline
        ' (account check, live-price guard, stop-tick computation, AI veto, order
        ' placement, persistence, live tracking startup) live on the service so a
        ' second strategy tab (Break and Bounce, FEAT-62) can reuse them.
        Private Async Function FireEntryAsync(slot As PositionSlot,
                                               contractId As String,
                                               side As String,
                                               stLine As Decimal,
                                               lastClose As Decimal,
                                               barTime As DateTimeOffset) As Task
            If _entryExecution Is Nothing Then
                _logger.LogError("ST+ FireEntryAsync invoked with no IEntryExecutionService — releasing slot {Idx}",
                                 slot.SlotIndex)
                _slotManager.CloseSlot(slot.SlotIndex)
                Return
            End If

            Dim tfMins As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
            If _selectedTimeframe.EndsWith("hr") Then tfMins *= 60
            Dim configJson As String = String.Empty
            Try
                configJson = JsonSerializer.Serialize(Config)
            Catch
            End Try

            Dim candidate As New EntryCandidate With {
                .Side = If(side = "Buy", OrderSide.Buy, OrderSide.Sell),
                .StrategyName = "SuperTrendPlus",
                .EntryReason = $"ST flip {side} @ {barTime:HH:mm:ss}",
                .ReferencePrice = lastClose,
                .SuggestedInitialStopPrice = stLine
            }

            Dim request As New EntryExecutionRequest With {
                .Candidate = candidate,
                .Slot = slot,
                .AccountId = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0),
                .ContractSymbol = contractId,
                .LastClose = lastClose,
                .BarTime = barTime,
                .StopReferencePrice = stLine,
                .StrategyName = "SuperTrend+",
                .StrategyDisplayName = "SuperTrend+ Autopilot",
                .ModelVersion = "SuperTrendPlus.v1",
                .Persona = _activePersona,
                .PersonaMinAdx = PersonaMinAdx,
                .PersonaRrRatio = PersonaRrRatio,
                .TimeframeLabel = _selectedTimeframe,
                .TimeframeMinutes = tfMins,
                .TimeframeForBars = MapTimeframe(_selectedTimeframe),
                .IsAiEnabled = _isAiEnabled,
                .DebugCaptureEnabled = _isDebugCaptureEnabled,
                .StrategyConfigJson = configJson,
                .EntryModeLabel = If(_useEarlyMode, "Preemptive", "BarClose"),
                .OnAiLogEntry = AddressOf AddAiLogEntry,
                .OnWatchlistAiStatus = AddressOf SetWatchlistAiStatus,
                .OnReleaseSlot = Sub(idx) _slotManager.CloseSlot(idx),
                .OnSlotEntered = AddressOf BeginSlotLiveTracking,
                .BandForAdx = AddressOf _slotManager.BandForAdx
            }

            Dim ct As CancellationToken = If(_monitoringCts IsNot Nothing, _monitoringCts.Token, CancellationToken.None)
            Dim result = Await _entryExecution.PlaceAsync(request, ct)

            If Not result.Success Then
                ' Match the legacy "no other slots open → reset timer to 15s" reset
                ' that the pre-refactor code applied on the order-not-accepted path.
                If Not _slotManager.Slots.Any(Function(s) s.IsOpen) Then
                    SyncLock _timerLock
                        _timer?.Change(15000, 15000)
                    End SyncLock
                End If
                Return
            End If

            Dim box = BoxForSlot(slot)
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    If box IsNot Nothing Then
                        box.IdleMonitorText = String.Empty
                        box.HasPosition = True
                        UpdatePositionDisplay(box, slot, 0D)
                        Task.Run(Async Function() As Task
                                     Await box.FlashBorderAsync()
                                 End Function)
                    End If
                End Sub)
        End Function

        ''' <summary>ARCH-19: callback the EntryExecutionService invokes with the AI
        ''' check status (PASS / VETO) so the SuperTrend+ watchlist row's SignalReason
        ''' cell renders the appropriate badge. Marshals to the dispatcher because the
        ''' service may invoke this from a background task.</summary>
        Private Sub SetWatchlistAiStatus(contractId As String, statusText As String)
            ' Cache veto text so the next watchlist scan can re-apply it while AI suppression
            ' is still active; clear it when the same contract passes a fresh check.
            If Not String.IsNullOrEmpty(contractId) Then
                If statusText IsNot Nothing AndAlso statusText.IndexOf("Checked", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Dim removed As String = Nothing
                    _aiVetoReasons.TryRemove(contractId, removed)
                Else
                    _aiVetoReasons(contractId) = statusText
                End If
            End If

            Dim wIdx As Integer = Array.IndexOf(Instruments, contractId)
            If wIdx >= 0 Then
                Dim wRow = WatchlistItems(wIdx)
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.SignalReason = statusText
                    End Sub)
            End If
        End Sub

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
                    ' BUG-72: Recompute EntryPrice as the size-weighted average across the
                    ' prior fills and the new fill, instead of leaving it stuck on the first
                    ' fill (which made local P&L drift by ~$7+ on a 2-lot M6E scale-in).
                    ' Prefer the broker-reported fill price; if not available yet, fall back
                    ' to the slot's most recent live price. The SignalR hub will overwrite
                    ' with the authoritative broker VWAP shortly after the fill.
                    Dim addFillPrice As Decimal = 0D
                    If placed.FillPrice.HasValue AndAlso placed.FillPrice.Value > 0D Then
                        addFillPrice = placed.FillPrice.Value
                    ElseIf slot.LivePrice > 0D Then
                        addFillPrice = slot.LivePrice
                    End If
                    _slotManager.ApplyScaleIn(slot, addContracts, addFillPrice)
                    _logger.LogInformation("ST+ [Slot {Idx}] Scale-in accepted — contracts now {Total} fillPx={Px}",
                                           slot.SlotIndex, slot.Contracts,
                                           If(addFillPrice > 0D, addFillPrice.ToString("F4"), "n/a"))
                    ' BUG-92: re-subscribe the live P&L stream so signedSize matches
                    ' the new contract count. Without this the slot card's local
                    ' P&L stays scaled for the prior contracts until the broker hub
                    ' push arrives (and even then only if its delta survives the
                    ' SyncFromBrokerVwap → NeedsResubscribe sequence in OnHubPositionUpdated).
                    BeginSlotLiveTracking(slot)
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

        ''' <summary>
        ''' BUG-72: Real-time handler for SignalR GatewayUserPosition pushes.
        '''
        ''' The TopStepX REST snapshot returns OpenPnL = 0 for futures positions, so the
        ''' VM previously fell back to local P&L derived from a paper-feed 5-second bar
        ''' close — a price that could freeze for 30+ minutes on practice accounts. The
        ''' hub push carries the broker's authoritative OpenPnL and NetPrice (the
        ''' size-weighted average fill price) for every position update.
        '''
        ''' This handler:
        '''   1. Maps the hub ContractId (e.g. "CON.F.US.MNQ.U26") to the matching open slot
        '''      via the FavouriteContracts root-symbol lookup.
        '''   2. Syncs slot.EntryPrice / slot.Contracts to the broker VWAP and net position
        '''      so scale-ins no longer drift.
        '''   3. Stores OpenPnL on slot.UnrealizedPnl when the broker reports a non-zero
        '''      value, deriving the implied live market price from it; falls back to
        '''      slot.LivePrice from the most recent bar when the hub reports OpenPnL = 0
        '''      (some practice accounts).
        '''   4. Pushes the new values to the slot card on the UI dispatcher.
        ''' </summary>
        Private Sub OnHubPositionUpdated(sender As Object, e As PXPositionUpdateEventArgs)
            Try
                If Not _isMonitoring OrElse e Is Nothing OrElse e.PositionData Is Nothing Then Return
                Dim data = e.PositionData
                If String.IsNullOrEmpty(data.ContractId) Then Return

                ' Map full contract ID → open slot via root-symbol matcher.
                Dim slot As PositionSlot = Nothing
                For Each candidate In _slotManager.Slots
                    If Not candidate.IsOpen OrElse String.IsNullOrEmpty(candidate.Instrument) Then Continue For
                    If String.Equals(candidate.Instrument, data.ContractId, StringComparison.OrdinalIgnoreCase) Then
                        slot = candidate
                        Exit For
                    End If
                    Dim fc = FavouriteContracts.TryGetBySymbolResolved(candidate.Instrument, _contractResolver)
                    If fc IsNot Nothing AndAlso Not String.IsNullOrEmpty(fc.PxRootSymbol) Then
                        If data.ContractId.StartsWith($"CON.F.US.{fc.PxRootSymbol}.", StringComparison.OrdinalIgnoreCase) OrElse
                           String.Equals(data.ContractId, fc.PxContractId, StringComparison.OrdinalIgnoreCase) Then
                            slot = candidate
                            Exit For
                        End If
                    End If
                Next
                If slot Is Nothing Then Return

                ' BUG-79: broker reports the position is flat → release the slot immediately,
                ' independent of the 15-second polling tick + MissCount escalation. The hub
                ' push is the fastest authoritative signal that the position has actually
                ' closed (manual flatten, SL/TP fill, force-flat). Without this branch the UI
                ' slot can survive for tens of minutes if SearchOpenPositionsAsync keeps
                ' replaying a stale row, as observed on the MES UAT bug (2026-05-13).
                If data.NetPos = 0 Then
                    Dim closingSlot = slot
                    _logger.LogInformation(
                        "ST+ OnHubPositionUpdated [Slot {Idx}] {Contract} reports NetPos=0 — releasing slot via hub close path",
                        closingSlot.SlotIndex, closingSlot.Instrument)
#Disable Warning BC42358
                    Task.Run(Async Function() As Task
                                 Try
                                     Await ReleaseSlotAsync(closingSlot, "Closed by Broker (hub)", trigger:="hub")
                                 Catch ex As Exception
                                     _logger.LogWarning(ex, "ST+ OnHubPositionUpdated hub-release failed for [Slot {Idx}] {Contract}",
                                                        closingSlot.SlotIndex, closingSlot.Instrument)
                                 End Try
                             End Function)
#Enable Warning BC42358
                    Return
                End If

                Dim brokerVwap As Decimal = CDec(data.NetPrice)
                Dim brokerNetPos As Integer = Math.Abs(data.NetPos)
                Dim brokerSide As String = If(data.NetPos > 0, "Buy", "Sell")

                ' BUG-92: capture the slot's pre-sync side/contracts so the resubscribe
                ' check below sees the real delta. SyncFromBrokerVwap overwrites
                ' slot.Contracts to brokerNetPos, which would otherwise mask the change
                ' from NeedsResubscribe and leave the LivePnL subscription stuck at the
                ' old signedSize (causing half-scaled P&L after a broker-driven scale-in).
                Dim priorSide As String = slot.Side
                Dim priorContracts As Integer = slot.Contracts

                ' Authoritatively sync EntryPrice/Contracts from broker VWAP — fixes the
                ' scale-in drift bug where the original first-fill price was used for P&L.
                _slotManager.SyncFromBrokerVwap(slot, brokerVwap, brokerNetPos)

                ' FEAT-54: re-subscribe the live price stream only when the slot's signed size
                ' has materially changed (side flip OR contracts delta). Pure EntryPrice drift
                ' is absorbed by ILivePnLService.Subscribe's internal entry-price self-correction
                ' so we do not churn the MarketHub ref-count on every VWAP fill.
                If _slotManager.NeedsResubscribe(priorSide, priorContracts, brokerSide, brokerNetPos) Then
                    BeginSlotLiveTracking(slot)
                End If

                Dim openPnl As Decimal = CDec(data.OpenPnL)
                Dim derivedLivePrice As Decimal = slot.LivePrice
                Dim haveBrokerPnl As Boolean = (openPnl <> 0D)
                ' FEAT-54: removed the _lastQuotePrices fallback for the slot card —
                ' OnSlotLiveTick now drives slot.LivePrice from the push stream.
                If haveBrokerPnl Then
                    slot.UnrealizedPnl = openPnl
                    ' Derive implied market price from broker P&L so the slot card's
                    ' Live Price field tracks what TopStepX is computing P&L against.
                    Dim fc2 = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                    If fc2 IsNot Nothing AndAlso fc2.PxTickSize > 0D AndAlso fc2.PxTickValue > 0D AndAlso
                       slot.Contracts > 0 AndAlso slot.EntryPrice > 0D Then
                        Dim ticks As Decimal = openPnl / (fc2.PxTickValue * slot.Contracts)
                        Dim direction As Decimal = If(slot.Side = "Buy", 1D, -1D)
                        derivedLivePrice = Math.Round(slot.EntryPrice + direction * ticks * fc2.PxTickSize, 6)
                        slot.LivePrice = derivedLivePrice
                        slot.LivePriceUtc = DateTime.UtcNow
                        slot.PriceStaleCount = 0
                    End If
                End If

                Dim pnlForDisplay As Decimal = slot.UnrealizedPnl
                Dim priceForDisplay As Decimal = If(derivedLivePrice > 0D, derivedLivePrice, slot.LivePrice)
                Dim box = BoxForSlot(slot)
                Application.Current?.Dispatcher?.BeginInvoke(
                    Sub()
                        If box IsNot Nothing AndAlso slot.IsOpen Then
                            UpdatePositionDisplay(box, slot, pnlForDisplay, priceForDisplay)
                        End If
                    End Sub)

            Catch ex As Exception
                ' Never let a hub-thread exception crash the SignalR connection.
                _logger.LogWarning(ex, "ST+ OnHubPositionUpdated handler error")
            End Try
        End Sub

        ''' <summary>
        ''' FEAT-54: Per-slot push tick from <see cref="ILivePnLService"/>. Mirrors the semantics
        ''' of <c>PriceTrackerViewModel.OnPnLTick</c>:
        '''   • Always apply the broker-derived <c>UnrealisedPnL</c> — a metadata-only tick
        '''     (<c>Source = None</c>) is the broker correcting our entry estimate, so its
        '''     recomputed P&amp;L is authoritative.
        '''   • Only update <c>LivePrice</c> / <c>LivePriceUtc</c> / <c>LivePriceSource</c>
        '''     when the tick carries a real price (<c>Source &lt;&gt; None</c>).
        ''' Marshals the slot-card refresh onto the dispatcher.
        ''' </summary>
        Private Sub OnSlotLiveTick(slot As PositionSlot, t As LivePnLTick)
            If slot Is Nothing OrElse t Is Nothing OrElse Not slot.IsOpen Then Return
            Dim hasPrice As Boolean = (t.Source <> LivePriceSource.None)

            slot.UnrealizedPnl = t.UnrealisedPnL
            If hasPrice Then
                slot.LivePrice = t.Price
                slot.LivePriceUtc = t.TimestampUtc
                slot.LivePriceSource = t.Source
                slot.PriceStaleCount = 0
            End If

            Dim box = BoxForSlot(slot)
            Dim displayPrice As Decimal = slot.LivePrice
            Dim displayPnl As Decimal = t.UnrealisedPnL
            Dim sourceLabel As String = If(slot.LivePriceSource = LivePriceSource.None,
                                           String.Empty, slot.LivePriceSource.ToString())
            Application.Current?.Dispatcher?.BeginInvoke(
                Sub()
                    If box Is Nothing OrElse Not slot.IsOpen Then Return
                    box.LivePriceSourceLabel = sourceLabel
                    UpdatePositionDisplay(box, slot, displayPnl, displayPrice)
                End Sub)
        End Sub

        ''' <summary>
        ''' FEAT-54: Open or refresh the live-price subscription for a slot. Disposes any
        ''' prior subscription before assigning the new one (handled by <c>SlotManager.AssignSubscription</c>).
        ''' The callback captures <paramref name="slot"/> directly so we never have to re-walk
        ''' slots from the tick's contract id.
        ''' </summary>
        Private Sub BeginSlotLiveTracking(slot As PositionSlot)
            If _livePnL Is Nothing OrElse slot Is Nothing OrElse String.IsNullOrEmpty(slot.Instrument) Then Return
            Try
                Dim signed As Integer = If(String.Equals(slot.Side, "Buy", StringComparison.OrdinalIgnoreCase),
                                           slot.Contracts, -slot.Contracts)
                Dim accId As Long = slot.AccountId
                Dim capturedSlot = slot
                Dim handle = _livePnL.Subscribe(slot.Instrument, slot.EntryPrice, signed,
                                                Sub(t) OnSlotLiveTick(capturedSlot, t),
                                                accountId:=accId)
                _slotManager.AssignSubscription(slot, handle)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ [Slot {Idx}] BeginSlotLiveTracking failed for {Contract}",
                                   slot.SlotIndex, slot.Instrument)
            End Try
        End Sub

        ''' <summary>
        ''' FEAT-52: caches the most recent last-trade price from the MarketHub GatewayQuote
        ''' stream, keyed by the broker's full PX contract ID. Runs on the SignalR callback
        ''' thread, so it must be fast and non-blocking. Prefers LastPrice; falls back to the
        ''' bid/ask mid when no trade has printed yet on the subscribed contract.
        ''' </summary>
        Private Sub OnMarketQuoteReceived(sender As Object, e As MarketQuoteEventArgs)
            If Not _isMonitoring OrElse e Is Nothing OrElse e.Quote Is Nothing Then Return
            If String.IsNullOrEmpty(e.Quote.ContractId) Then Return
            Dim price As Double = CDbl(e.Quote.LastPrice)
            If price <= 0D AndAlso e.Quote.BidPrice > 0D AndAlso e.Quote.AskPrice > 0D Then
                price = (CDbl(e.Quote.BidPrice) + CDbl(e.Quote.AskPrice)) / 2.0
            End If
            If price > 0D Then _lastQuotePrices(e.Quote.ContractId) = price
        End Sub

        ''' <summary>
        ''' ARCH-20: per-tick position-management coordinator. Builds the tick context and
        ''' delegates to <see cref="IPositionManagementService.UpdateAsync"/>; observes the
        ''' result to dispatch a UI refresh and, when the service requests an exit, calls
        ''' <see cref="ReleaseSlotAsync"/> (which fans out to <c>IExitExecutionService</c>).
        ''' </summary>
        Private Async Function HandleOpenPositionAsync(slot As PositionSlot,
                                                       tf As BarTimeframe,
                                                       Optional barCache As Dictionary(Of Integer, IList(Of MarketBar)) = Nothing) As Task
            If slot.AccountId = 0 AndAlso _session.SelectedAccount IsNot Nothing Then
                slot.AccountId = _session.SelectedAccount.Id
            End If
            If _positionMgmt Is Nothing Then Return

            Dim mgmt As PositionManagementResult = Nothing
            Try
                mgmt = Await _positionMgmt.UpdateAsync(slot, BuildTickContext(slot, tf, barCache), CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ PositionManagementService.UpdateAsync threw for [Slot {Idx}] on {Contract}",
                                   slot.SlotIndex, slot.Instrument)
                Return
            End Try
            If mgmt Is Nothing Then Return

            DispatchSlotRefresh(slot, mgmt)

            If mgmt.Outcome = PositionManagementOutcome.ExitRequested Then
                Await ReleaseSlotAsync(slot, mgmt.ExitReason, trigger:=mgmt.ExitTrigger)
            End If
        End Function

        ''' <summary>Builds the per-tick context handed to the position-management service.
        ''' Lives next to <see cref="HandleOpenPositionAsync"/> so the coordinator stays short.</summary>
        Private Function BuildTickContext(slot As PositionSlot,
                                           tf As BarTimeframe,
                                           barCache As Dictionary(Of Integer, IList(Of MarketBar))) As PositionManagementTickContext
            ' STRAT-41: cap is min(config soft cap, 2 × leverage) so the post-pullback size
            ' never exceeds the per-persona leverage envelope. Default cap = 2 contracts.
            Dim leverage As Integer = Math.Max(1, Config.LeverageMultiplier)
            Dim maxAfterScaleIn As Integer = Math.Min(Config.MaxContractsAfterScaleIn, 2 * leverage)
            Return New PositionManagementTickContext With {
                .StrategyTimeframe = tf,
                .AsOfUtc = DateTime.UtcNow,
                .ReleasedThisTick = _releasedThisTick,
                .ForceSnapshot = _releasedThisTick,
                .StMultiplier = _stMultiplier,
                .ExitScoreThreshold = Config.ExitScoreThreshold,
                .EarlyModeMaxAgeMinutes = Config.EarlyModeMaxAgeMinutes,
                .IsPrimaryForBracketEdit = IsPrimaryForBracketEdit(slot),
                .IsDebugCaptureEnabled = _isDebugCaptureEnabled,
                .BandForAdx = AddressOf _slotManager.BandForAdx,
                .OnScaleInRequested = Function(s, addContracts) ScaleInSlotAsync(s, addContracts),
                .LeverageMultiplier = Config.LeverageMultiplier,
                .LadderTpDollars = Config.LadderTpDollars,
                .PullbackScaleInEnabled = Config.PullbackScaleInEnabled,
                .PullbackAtrFactor = Config.PullbackAtrFactor,
                .PullbackScaleInContracts = Config.PullbackScaleInContracts,
                .MaxContractsAfterScaleIn = maxAfterScaleIn,
                .Bars = TryGetCachedBars(slot, barCache)
            }
        End Function

        ''' <summary>Single-marshal slot card refresh — runs only when the service reached
        ''' the indicator stage (i.e. <see cref="PositionManagementResult.RanExitEngine"/>).</summary>
        Private Sub DispatchSlotRefresh(slot As PositionSlot, mgmt As PositionManagementResult)
            If Not mgmt.RanExitEngine Then Return
            Dim box = BoxForSlot(slot)
            Dim displayPnl = mgmt.LatestPnl
            Dim displayClose = mgmt.CurrentClose
            Dim adxSample = mgmt.AdxSample
            Dim plusDi = mgmt.PlusDiSample
            Dim minusDi = mgmt.MinusDiSample
            Dim priceToSt = mgmt.PriceToStSample
            Application.Current?.Dispatcher?.Invoke(Sub()
                                                        If box Is Nothing OrElse Not slot.IsOpen Then Return
                                                        UpdatePositionDisplay(box, slot, displayPnl, displayClose)
                                                        If Not Single.IsNaN(adxSample) Then
                                                            box.PushAdxSample(adxSample, plusDi, minusDi, priceToSt)
                                                        End If
                                                    End Sub)
        End Sub

        ''' <summary>Returns the strategy-TF bars already cached by <c>ScanWatchlistAsync</c>
        ''' for the slot's instrument, or Nothing when the cache has no usable entry. The
        ''' position-management service refetches when this returns Nothing.</summary>
        Private Function TryGetCachedBars(slot As PositionSlot,
                                           barCache As Dictionary(Of Integer, IList(Of MarketBar))) As IList(Of MarketBar)
            If barCache Is Nothing OrElse slot Is Nothing OrElse String.IsNullOrEmpty(slot.Instrument) Then Return Nothing
            For idx = 0 To Instruments.Length - 1
                If String.Equals(Instruments(idx), slot.Instrument, StringComparison.OrdinalIgnoreCase) Then
                    Dim cached As IList(Of MarketBar) = Nothing
                    If barCache.TryGetValue(idx, cached) AndAlso cached IsNot Nothing AndAlso cached.Count >= 14 Then
                        Return cached
                    End If
                    Exit For
                End If
            Next
            Return Nothing
        End Function

        ''' <summary>True when this slot is the lowest-indexed open slot on its instrument and
        ''' therefore owns the broker bracket SL edit. Scale-in slots defer their stop ratchet
        ''' to the primary slot's tick.</summary>
        Private Function IsPrimaryForBracketEdit(slot As PositionSlot) As Boolean
            If slot Is Nothing OrElse String.IsNullOrEmpty(slot.Instrument) Then Return True
            Return Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                                                          s.Instrument = slot.Instrument AndAlso
                                                          s.SlotIndex < slot.SlotIndex)
        End Function

        ''' <summary>ARCH-20: thin passthrough — bracket verification lives in
        ''' <see cref="IPositionManagementService.VerifyBracketStopAsync"/>. Retained on the
        ''' VM so reconcile paths that need an ad-hoc bracket check can keep working without
        ''' adopting the management-service contract directly.</summary>
        Private Async Function VerifyBracketStopAsync(slot As PositionSlot) As Task
            If _positionMgmt Is Nothing Then Return
            Dim result = Await _positionMgmt.VerifyBracketStopAsync(slot, CancellationToken.None)
            If result.Outcome = PositionManagementOutcome.ExitRequested Then
                Await ReleaseSlotAsync(slot, result.ExitReason, trigger:=result.ExitTrigger)
            End If
        End Function

        ''' <summary>
        ''' BUG-90 F4: refresh each slot card's stuck-slot diagnostic chip / red banner
        ''' based on time since the most recent broker-confirmed snapshot. Healthy slots
        ''' (&lt; 60 s) clear both indicators; 60 s–5 min shows the amber chip; ≥ 5 min
        ''' shows the red banner with the "Force reconcile this slot" button.
        ''' </summary>
        Private Sub RefreshStuckSlotDiagnostics()
            Dim now = DateTime.UtcNow
            For Each box In AllSlotBoxes()
                Dim slot = _slotManager.Slots(box.SlotIndex)
                If slot Is Nothing OrElse Not slot.IsOpen Then
                    box.SnapshotAgeText = String.Empty
                    box.ShowSnapshotWarn = Visibility.Collapsed
                    box.ShowSnapshotRed = Visibility.Collapsed
                    Continue For
                End If
                If slot.LastSnapshotOkUtc = DateTime.MinValue Then
                    ' First-tick window — no successful snapshot yet, mirror SnapshotStalenessGuard
                    ' policy and surface nothing rather than a false red banner.
                    box.SnapshotAgeText = String.Empty
                    box.ShowSnapshotWarn = Visibility.Collapsed
                    box.ShowSnapshotRed = Visibility.Collapsed
                    Continue For
                End If
                Dim ageSeconds As Double = (now - slot.LastSnapshotOkUtc).TotalSeconds
                box.SnapshotAgeText = FormatAge(ageSeconds)
                If ageSeconds >= 300.0 Then
                    box.ShowSnapshotWarn = Visibility.Collapsed
                    box.ShowSnapshotRed = Visibility.Visible
                ElseIf ageSeconds >= 60.0 Then
                    box.ShowSnapshotWarn = Visibility.Visible
                    box.ShowSnapshotRed = Visibility.Collapsed
                Else
                    box.ShowSnapshotWarn = Visibility.Collapsed
                    box.ShowSnapshotRed = Visibility.Collapsed
                End If
            Next
        End Sub

        Private Shared Function FormatAge(seconds As Double) As String
            If seconds < 0 Then seconds = 0
            If seconds < 60 Then Return CInt(seconds).ToString() & "s"
            Dim mins As Integer = CInt(Math.Floor(seconds / 60.0))
            Dim secs As Integer = CInt(seconds - mins * 60)
            Return mins.ToString() & "m " & secs.ToString() & "s"
        End Function

        ''' <summary>
        ''' BUG-90 F4: invoked by the slot card's "Force reconcile this slot" button.
        ''' Queries the broker directly for this slot's instrument and releases the slot
        ''' if (and only if) the broker reports flat. Manual safety lever for the case
        ''' where all four automated release channels failed.
        ''' </summary>
        Friend Async Function ForceReconcileSlotAsync(slotIndex As Integer) As Task
            If slotIndex < 0 OrElse slotIndex >= _slotManager.Slots.Count Then Return
            Dim slot = _slotManager.Slots(slotIndex)
            If slot Is Nothing OrElse Not slot.IsOpen Then Return
            Dim accountId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0L)
            If accountId = 0L Then
                _logger.LogWarning("ST+ ForceReconcile [Slot {Idx}] aborted — no account selected", slotIndex)
                Return
            End If

            Dim snapshot As LivePositionSnapshot = Nothing
            Try
                snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                    accountId, slot.Instrument, slot.PositionId, bypassCache:=True)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ ForceReconcile [Slot {Idx}] {Contract} broker query failed",
                                   slotIndex, slot.Instrument)
                Return
            End Try

            If Core.Trading.LivePositionSnapshotValidator.IsConfirmedOpen(snapshot) Then
                _logger.LogInformation(
                    "ST+ ForceReconcile [Slot {Idx}] {Contract} broker confirms position is open (Units={U}) — no release",
                    slotIndex, slot.Instrument, snapshot.Units)
                ' Stamp LastSnapshotOkUtc so the diagnostic chip clears immediately.
                slot.LastSnapshotOkUtc = DateTime.UtcNow
                slot.NetPosLastSeen = CInt(Math.Round(snapshot.Units))
                RefreshStuckSlotDiagnostics()
                Return
            End If

            Await ReleaseSlotAsync(slot, "Manual reconcile (broker flat)", trigger:="manual")
        End Function

        ''' <summary>ARCH-20: thin coordinator. <see cref="IExitExecutionService.CloseAsync"/>
        ''' owns the data-side (TradeRecord close, TradeOutcome resolve, TradeLifespan save,
        ''' broker flatten); this method handles the strategy-VM side-effects — release-tick
        ''' bookkeeping, slot box UI reset, debug-capture EndTrade, MarketHub unsubscribe,
        ''' SlotManager close, and timer cadence reset.</summary>
        Private Async Function ReleaseSlotAsync(slot As PositionSlot,
                                                  Optional exitReason As String = "Signal",
                                                  Optional trigger As String = "internal") As Task
            If slot Is Nothing OrElse Not slot.IsOpen Then Return

            ' Block same-tick re-entry and enforce the FEAT-47 15s BB-middle re-entry gate.
            _releasedThisTick = True
            If Not String.IsNullOrEmpty(slot.Instrument) Then
                _instrumentsReleasedThisSession.Add(slot.Instrument)
            End If

            Dim result As ExitExecutionResult = Nothing
            If _exitExecution IsNot Nothing Then
                Try
                    result = Await _exitExecution.CloseAsync(slot,
                                                              exitReason,
                                                              trigger,
                                                              TimeframeMinutesFor(_selectedTimeframe),
                                                              CancellationToken.None)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ ExitExecutionService.CloseAsync threw for [Slot {Idx}] on {Contract}",
                                       slot.SlotIndex, slot.Instrument)
                End Try
            End If

            Dim closingInstrument As String = If(result IsNot Nothing AndAlso Not String.IsNullOrEmpty(result.ClosingInstrument),
                                                  result.ClosingInstrument, slot.Instrument)
            Dim closingSlotIndex As Integer = If(result IsNot Nothing AndAlso result.ClosingSlotIndex >= 0,
                                                  result.ClosingSlotIndex, slot.SlotIndex)
            Dim closingPxContractId As String =
                If(result IsNot Nothing AndAlso Not String.IsNullOrEmpty(result.ClosingPxContractId),
                   result.ClosingPxContractId,
                   ResolvePxContractId(closingInstrument))
            Dim exitPxForDebug As Decimal? = If(result IsNot Nothing, result.ExitPrice, Nothing)

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

            ' ── Debug Capture: Exit snapshot + EndTrade (FEAT-39) ─────────────
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
                _debugCapture.RecordAction(New DebugTradeAction With {
                    .TradeId = slot.DebugTradeId,
                    .TimestampUtc = DateTime.UtcNow.ToString("O"),
                    .ActionType = "Closed",
                    .Price = exitPxForDebug,
                    .Quantity = slot.Contracts,
                    .NewValue = slot.UnrealizedPnl,
                    .Reason = If(exitPxForDebug.HasValue,
                                 exitReason & " (engine-derived from PnL)",
                                 exitReason & " (exit price unknown — engine derivation failed)"),
                    .Source = "Local"
                })
                _debugCapture.EndTrade(slot.DebugTradeId, DateTime.UtcNow, slot.UnrealizedPnl)
                _debugMfe.Remove(slot.DebugTradeId)
                _debugMae.Remove(slot.DebugTradeId)
                _lastBarTimestampByTradeId.Remove(slot.DebugTradeId)
            End If

            ' FEAT-52: unsubscribe the MarketHub quote stream unless another open slot still
            ' holds the same instrument. The closing slot's Instrument is wiped by CloseSlot,
            ' so SlotIndex match is also implicitly excluded after the close runs.
            If _marketHub IsNot Nothing AndAlso Not String.IsNullOrEmpty(closingInstrument) Then
                Dim stillNeeded As Boolean = _slotManager.Slots.Any(
                    Function(s) s.IsOpen AndAlso s.SlotIndex <> closingSlotIndex AndAlso
                                String.Equals(s.Instrument, closingInstrument, StringComparison.OrdinalIgnoreCase))
                If Not stillNeeded AndAlso Not String.IsNullOrEmpty(closingPxContractId) Then
                    _lastQuotePrices.TryRemove(closingPxContractId, Nothing)
#Disable Warning BC42358
                    Task.Run(Async Function() As Task
                                 Try
                                     Await _marketHub.UnsubscribeContractAsync(closingPxContractId)
                                 Catch ex As Exception
                                     _logger.LogDebug(ex, "ST+ MarketHub unsubscribe failed for {Id}", closingPxContractId)
                                 End Try
                             End Function)
#Enable Warning BC42358
                End If
            End If

            _slotManager.CloseSlot(slot.SlotIndex)
            If Not _slotManager.Slots.Any(Function(s) s.IsOpen) Then
                SyncLock _timerLock
                    _timer?.Change(15000, 15000)
                End SyncLock
            End If
        End Function

        Private Function ResolvePxContractId(instrument As String) As String
            If String.IsNullOrEmpty(instrument) Then Return Nothing
            Dim fc = FavouriteContracts.TryGetBySymbolResolved(instrument, _contractResolver)
            Return If(fc IsNot Nothing, fc.PxContractId, Nothing)
        End Function

        Private Sub UpdatePositionDisplay(box As SlotBoxVm, slot As PositionSlot, pnl As Decimal,
                                          Optional livePrice As Decimal = 0D)
            Dim sideLbl As String = If(slot.Side = "Buy", "LONG", "SHORT")
            Dim idx As Integer = Array.IndexOf(Instruments, slot.Instrument)
            Dim label As String = If(idx >= 0, InstrumentLabels(idx), slot.Instrument)
            Dim entryTimeStr As String = If(slot.EntryTime = DateTime.MinValue, "--", slot.EntryTime.ToString("HH:mm:ss"))

            ' ── Row 1 ─────────────────────────────────────────────────────────
            Dim spFmt As String = If(slot.EntryPrice = 0D, "--", slot.EntryPrice.ToString("F4"))
            box.SlotLabel = $"{label}  {slot.Instrument} — {sideLbl} @ {entryTimeStr} — SP: {spFmt}"

            ' ── Row 2 ─────────────────────────────────────────────────────────
            Dim priceFmt As String = If(livePrice = 0D, "--", livePrice.ToString("F4"))
            Dim slFmt As String = If(slot.StopPrice = 0D, "--", slot.StopPrice.ToString("F4"))
            Dim strength As String = AdxBandLabel(slot.CurrentAdx)
            box.LivePriceDisplay = priceFmt
            box.SlDisplay = slFmt
            box.EntrySpDisplay = spFmt

            ' SL dollar risk: current dollar distance from entry to current SL
            If slot.EntryPrice <> 0D AndAlso slot.StopPrice <> 0D AndAlso slot.InitialRisk > 0D AndAlso slot.InitialRiskDollars > 0D Then
                Dim priceDist As Decimal = Math.Abs(slot.EntryPrice - slot.StopPrice)
                Dim dollarRisk As Decimal = Math.Round(priceDist / slot.InitialRisk * slot.InitialRiskDollars, 2)
                box.SlDollarDisplay = $"(${dollarRisk:F2})"
            Else
                box.SlDollarDisplay = String.Empty
            End If

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
            box.StopPhaseLabel = PhaseLabel(slot.StopPhase, Config, _selectedTimeframe)
            box.SizeLabel = $"Size {slot.Contracts}x"

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
            If adx >= Config.AdxModerateThreshold Then Return "Cappuccino"
            If adx >= Config.AdxWeakThreshold Then Return "Latte"
            Return "Decaff"
        End Function

        ''' <summary>
        ''' Computes a one-line "Next phase" label for the ExitSignalEngine PDF ladder (ARCH-15).
        ''' e.g.  "Next: Breakeven in 1.0R = $57.50"
        ''' </summary>
        Private Shared Function NextPhaseDisplay(slot As PositionSlot, currentPnl As Decimal) As String
            If slot.InitialRiskDollars <= 0D Then Return String.Empty
            Dim ir = slot.InitialRiskDollars
            Const beR As Decimal = 1.0D    ' ExitSignalEngine.ComputePhasedStop Breakeven trigger
            Const ptR As Decimal = 1.5D    ' ExitSignalEngine.ComputePhasedStop ProfitTrail trigger
            Const hvR As Decimal = 2.0D    ' ExitSignalEngine.ComputePhasedStop Harvest trigger
            Const frR As Decimal = 3.0D    ' ExitSignalEngine.ComputePhasedStop FreeRide trigger
            Select Case slot.StopPhase
                Case StopPhase.Initial
                    Dim dollarTarget = Math.Round(ir * beR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: Breakeven in {beR:F1}R = ${remaining:F2}"
                Case StopPhase.Breakeven
                    Dim dollarTarget = Math.Round(ir * ptR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: ProfitTrail in {ptR:F1}R = ${remaining:F2}"
                Case StopPhase.ProfitTrail
                    Dim dollarTarget = Math.Round(ir * hvR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: Harvest in {hvR:F1}R = ${remaining:F2}"
                Case StopPhase.Harvest
                    Dim dollarTarget = Math.Round(ir * frR, 2)
                    Dim remaining = Math.Max(0D, dollarTarget - currentPnl)
                    Return $"Next: FreeRide in {frR:F1}R = ${remaining:F2}"
                Case StopPhase.FreeRide
                    Return "FreeRide — SL locked at entry + 2R"
                Case Else
                    Return String.Empty
            End Select
        End Function

        ''' <summary>Returns a human-readable phase label for the ExitSignalEngine PDF ladder (ARCH-15).</summary>
        ''' <remarks>
        ''' Initial-phase label includes the chart timeframe so users know which ST line
        ''' the stop is ratcheting against. STRAT-39 will replace the underlying engine
        ''' with an ATR(5m) Chandelier-based ladder per the HLD docs.
        ''' </remarks>
        Private Shared Function PhaseLabel(phase As StopPhase, cfg As SuperTrendPlusConfig, timeframe As String) As String
            Dim tfShort As String = If(String.IsNullOrEmpty(timeframe), "chart",
                                       timeframe.Replace("min", "m").Replace("hr", "h"))
            Select Case phase
                Case StopPhase.Initial
                    Return $"Initial: SL = {tfShort} SuperTrend"
                Case StopPhase.Breakeven
                    Return "Breakeven: SL = entry + 0.5R (1R reached)"
                Case StopPhase.ProfitTrail
                    Return "ProfitTrail: SL trails ATR×1 (1.5R reached)"
                Case StopPhase.Harvest
                    Return "Harvest: SL = entry + 1.5R (2R reached)"
                Case StopPhase.FreeRide
                    Return "FreeRide: SL = entry + 2R (3R reached)"
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

        ' FEAT-58: TF label → integer minutes for snapshot/lifespan persistence.
        ' Mirrors MapTimeframe but emits minutes for ML-friendly storage.
        Private Shared Function TimeframeMinutesFor(tf As String) As Integer
            Select Case tf
                Case "5min"  : Return 5
                Case "1hr"   : Return 60
                Case Else    : Return 15
            End Select
        End Function

        ' STRAT-42: ResolveSessionWindow unified into TopStepTrader.Core.Trading.SessionWindowResolver.
        ' Original local copy was dead code (no call sites in this VM); deleted as part of the 23-hour
        ' coverage audit. Use SessionWindowResolver.Resolve(utc) if a label is needed here in future.

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
                        PhaseLabel(slot.StopPhase, Config, _selectedTimeframe),
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

        ''' <summary>BUG-90 F1: <see cref="IOpenSlotReleaseSink.OccupiedSlots"/> — snapshot of
        ''' open slots for the broker-sweep worker. Returns a fresh list so the worker can
        ''' iterate without locking against the per-tick mutation path.</summary>
        Public ReadOnly Property OccupiedSlots As IReadOnlyList(Of PositionSlot) _
            Implements Core.Interfaces.IOpenSlotReleaseSink.OccupiedSlots
            Get
                Return _slotManager.Slots.Where(Function(s) s IsNot Nothing AndAlso s.IsOpen).ToList()
            End Get
        End Property

        ''' <summary>BUG-90 F1: <see cref="IOpenSlotReleaseSink.ForceReleaseAsync"/> — invoked
        ''' by the broker-sweep worker (or the F4 "Force reconcile" UI button) when an external
        ''' caller has determined the slot should be released. Marshals onto the UI dispatcher
        ''' so <see cref="ReleaseSlotAsync"/> mutates UI state on the right thread.</summary>
        Public Async Function ForceReleaseAsync(slotIndex As Integer,
                                                reason As String,
                                                trigger As String) As Task _
            Implements Core.Interfaces.IOpenSlotReleaseSink.ForceReleaseAsync
            If slotIndex < 0 OrElse slotIndex >= _slotManager.Slots.Count Then Return
            Dim slot = _slotManager.Slots(slotIndex)
            If slot Is Nothing OrElse Not slot.IsOpen Then Return

            Dim dispatcher = Application.Current?.Dispatcher
            If dispatcher IsNot Nothing AndAlso Not dispatcher.CheckAccess() Then
                Await dispatcher.InvokeAsync(
                    Async Function() As Task
                        If slot.IsOpen Then Await ReleaseSlotAsync(slot, reason, trigger)
                    End Function).Task.Unwrap()
            Else
                If slot.IsOpen Then Await ReleaseSlotAsync(slot, reason, trigger)
            End If
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                ' BUG-90 F1: ensure the VM is removed from the sweep registry even when
                ' Dispose is called without a prior StopMonitoring.
                _sweepRegistry?.Unregister(Me)
                ' FEAT-71: unhook the daily-loss guard PnL source registered at construction.
                If _dailyLossGuard IsNot Nothing AndAlso _dailyLossPnlSource IsNot Nothing Then
                    _dailyLossGuard.UnregisterOpenSlotPnlSource(_dailyLossPnlSource)
                End If
                ' FEAT-72: unhook the adaptive watchlist open-slot pin.
                _adaptiveWatchlist?.UnregisterOpenSlotSource(Me)
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
                ' FEAT-54: defensive — ensure no live-price subscription survives the VM,
                ' even if StopMonitoring was never invoked (e.g. tab replaced before start).
                For Each s In _slotManager.Slots
                    _slotManager.EndLiveTracking(s)
                Next
            End If
        End Sub

        ''' <summary>
        ''' FEAT-72: returns the FavouriteContract list that drives this VM's Instruments /
        ''' WatchlistItems. When the adaptive toggle is ON and the live watchlist has at
        ''' least one contract, that selection is used; otherwise <see cref="FavouriteContracts.GetDefaults"/>
        ''' is returned (existing behaviour preserved).
        ''' </summary>
        Private Shared Function ResolveInstrumentSet(adaptive As AdaptiveWatchlistService) As IReadOnlyList(Of FavouriteContract)
            If adaptive IsNot Nothing AndAlso adaptive.IsEnabled Then
                Dim live = adaptive.GetCurrentWatchlist()
                If live IsNot Nothing AndAlso live.Count > 0 Then
                    Dim filtered = live.Where(Function(c) c IsNot Nothing AndAlso Not String.IsNullOrEmpty(c.PxRootSymbol)).ToList()
                    If filtered.Count > 0 Then Return filtered
                End If
            End If
            Return FavouriteContracts.GetDefaults().
                Where(Function(f) Not String.IsNullOrEmpty(f.PxRootSymbol)).
                ToList()
        End Function

        ''' <summary>FEAT-72: pin root symbols with open slots so they cannot be dropped from
        ''' the adaptive watchlist under live exposure.</summary>
        Public Function GetOpenInstrumentRootSymbols() As IEnumerable(Of String) _
            Implements Core.Interfaces.IOpenSlotInstrumentSource.GetOpenInstrumentRootSymbols
            Dim result As New List(Of String)
            For Each slot In _slotManager.Slots
                If slot IsNot Nothing AndAlso slot.IsOpen AndAlso Not String.IsNullOrEmpty(slot.Instrument) Then
                    result.Add(slot.Instrument)
                End If
            Next
            Return result
        End Function

    End Class

End Namespace
