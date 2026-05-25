Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data
Imports TopStepTrader.Services.BreakAndBounce
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-62: Backing VM for the Break and Bounce tab. Loads + persists
    ''' <see cref="BreakAndBounceConfig"/>, pre-populates the watchlist
    ''' (MES / MNQ / MGC), subscribes to <see cref="BreakAndBounceOrchestrator"/>
    ''' events, appends signal-detected events to the on-tab log, and binds the key
    ''' tunables (entry window, SL floor knobs, dir-invalidation toggles, AI veto).
    ''' </summary>
    Public Class BreakAndBounceViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private Const MaxSignalLogEntries As Integer = 50

        Private ReadOnly _configRepository As BreakAndBounceConfigRepository
        Private ReadOnly _orchestrator As BreakAndBounceOrchestrator
        Private ReadOnly _logger As ILogger(Of BreakAndBounceViewModel)
        Private ReadOnly _rowBySymbol As New Dictionary(Of String, BreakAndBounceWatchlistRowVm)(StringComparer.OrdinalIgnoreCase)
        Private _disposed As Boolean

        Public Sub New(configRepository As BreakAndBounceConfigRepository,
                       orchestrator As BreakAndBounceOrchestrator,
                       logger As ILogger(Of BreakAndBounceViewModel))
            _configRepository = configRepository
            _orchestrator = orchestrator
            _logger = logger

            WatchlistRows = New ObservableCollection(Of BreakAndBounceWatchlistRowVm) From {
                New BreakAndBounceWatchlistRowVm("MES", "S&P 500"),
                New BreakAndBounceWatchlistRowVm("MNQ", "Nasdaq"),
                New BreakAndBounceWatchlistRowVm("MGC", "Gold")
            }
            For Each row In WatchlistRows
                _rowBySymbol(row.Symbol) = row
            Next

            SignalLog = New ObservableCollection(Of BreakAndBounceSignalLogEntry)()
            LivePosition = New BreakAndBounceLivePositionVm()
            UpdateStatusFromOrchestrator()

            AddHandler _orchestrator.WatchlistTick, AddressOf OnWatchlistTick
            AddHandler _orchestrator.SignalDetected, AddressOf OnSignalDetected
            AddHandler _orchestrator.EnabledChanged, AddressOf OnOrchestratorEnabledChanged
            AddHandler _orchestrator.LivePositionChanged, AddressOf OnLivePositionChanged
            AddHandler _orchestrator.StopRatcheted, AddressOf OnStopRatcheted
            AddHandler _orchestrator.ScanCompleted, AddressOf OnScanCompleted

            Dim ignored = LoadConfigAsync()
        End Sub

        Public ReadOnly Property WatchlistRows As ObservableCollection(Of BreakAndBounceWatchlistRowVm)
        Public ReadOnly Property SignalLog As ObservableCollection(Of BreakAndBounceSignalLogEntry)
        Public ReadOnly Property LivePosition As BreakAndBounceLivePositionVm

        Private _config As BreakAndBounceConfig = New BreakAndBounceConfig()
        Public Property Config As BreakAndBounceConfig
            Get
                Return _config
            End Get
            Private Set(value As BreakAndBounceConfig)
                If SetProperty(_config, value) Then RaiseAllConfigBindings()
            End Set
        End Property

        Private _statusText As String = "Disabled"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Private Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _headerStatusText As String = String.Empty
        Public Property HeaderStatusText As String
            Get
                Return _headerStatusText
            End Get
            Private Set(value As String)
                SetProperty(_headerStatusText, value)
            End Set
        End Property

        Public Property IsEnabled As Boolean
            Get
                Return _orchestrator.IsEnabled
            End Get
            Set(value As Boolean)
                If value = _orchestrator.IsEnabled Then Return
                If value Then _orchestrator.Enable() Else _orchestrator.Disable()
                OnPropertyChanged(NameOf(IsEnabled))
            End Set
        End Property

        ' ── Flat config bindings ────────────────────────────────────────────

        Public Property EntryWindow As String
            Get
                Return _config.EntryWindow
            End Get
            Set(value As String)
                If _config.EntryWindow = value Then Return
                _config.EntryWindow = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property FlatWindow As String
            Get
                Return _config.FlatWindow
            End Get
            Set(value As String)
                If _config.FlatWindow = value Then Return
                _config.FlatWindow = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property MinimumStopDistanceTicks As Integer
            Get
                Return _config.MinimumStopDistanceTicks
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(1, value)
                If _config.MinimumStopDistanceTicks = clamped Then Return
                _config.MinimumStopDistanceTicks = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property MinimumStopAtrFraction As Double
            Get
                Return _config.MinimumStopAtrFraction
            End Get
            Set(value As Double)
                If _config.MinimumStopAtrFraction = value Then Return
                _config.MinimumStopAtrFraction = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property InvalidateDirOnCounterBreakout As Boolean
            Get
                Return _config.InvalidateDirOnCounterBreakout
            End Get
            Set(value As Boolean)
                If _config.InvalidateDirOnCounterBreakout = value Then Return
                _config.InvalidateDirOnCounterBreakout = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property InvalidateDirOnWindowExpiry As Boolean
            Get
                Return _config.InvalidateDirOnWindowExpiry
            End Get
            Set(value As Boolean)
                If _config.InvalidateDirOnWindowExpiry = value Then Return
                _config.InvalidateDirOnWindowExpiry = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property AiVetoEnabled As Boolean
            Get
                Return _config.AiVetoEnabled
            End Get
            Set(value As Boolean)
                If _config.AiVetoEnabled = value Then Return
                _config.AiVetoEnabled = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property EnableLong As Boolean
            Get
                Return _config.EnableLong
            End Get
            Set(value As Boolean)
                If _config.EnableLong = value Then Return
                _config.EnableLong = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property EnableShort As Boolean
            Get
                Return _config.EnableShort
            End Get
            Set(value As Boolean)
                If _config.EnableShort = value Then Return
                _config.EnableShort = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Private Sub RaiseAllConfigBindings()
            For Each name In New String() {
                NameOf(EntryWindow), NameOf(FlatWindow),
                NameOf(MinimumStopDistanceTicks), NameOf(MinimumStopAtrFraction),
                NameOf(InvalidateDirOnCounterBreakout), NameOf(InvalidateDirOnWindowExpiry),
                NameOf(AiVetoEnabled), NameOf(EnableLong), NameOf(EnableShort)
            }
                OnPropertyChanged(name)
            Next
        End Sub

        Private Sub FireAndForgetSave()
            Dim ignored = SaveConfigAsync()
        End Sub

        ' ── Persistence ─────────────────────────────────────────────────────

        Private Async Function LoadConfigAsync() As Task
            Try
                Config = Await _configRepository.LoadAsync()
            Catch ex As Exception
                _logger?.LogWarning(ex, "Failed to load BreakAndBounceConfig — defaults retained")
            End Try
        End Function

        Public Async Function SaveConfigAsync() As Task
            Try
                Await _configRepository.SaveAsync(_config)
            Catch ex As Exception
                _logger?.LogWarning(ex, "Failed to save BreakAndBounceConfig")
            End Try
        End Function

        ' ── Orchestrator event handlers ─────────────────────────────────────

        Private Sub OnWatchlistTick(sender As Object, eval As BreakAndBounceEvaluation)
            If eval Is Nothing Then Return
            Dim row As BreakAndBounceWatchlistRowVm = Nothing
            If Not _rowBySymbol.TryGetValue(eval.Symbol, row) Then Return
            DispatchAction(Sub()
                               row.PrevHigh = eval.PrevHigh
                               row.PrevLow = eval.PrevLow
                               row.LastClose = eval.LastFiveClose
                               row.Direction = eval.Direction
                               row.RetestTagged = eval.RetestTagged
                               row.PatternHit = eval.PatternHit
                               row.InEntryWindow = eval.InEntryWindow
                               row.SignalState = SignalStateLabel(eval.Signal)
                               row.RejectionReason = eval.RejectionReason
                               row.LastUpdatedUtc = DateTime.UtcNow
                           End Sub)
        End Sub

        Private Sub OnSignalDetected(sender As Object, eval As BreakAndBounceEvaluation)
            If eval Is Nothing Then Return
            DispatchAction(Sub()
                               Dim entry = New BreakAndBounceSignalLogEntry With {
                                   .TimestampUtc = DateTime.UtcNow,
                                   .Symbol = eval.Symbol,
                                   .Side = SignalStateLabel(eval.Signal),
                                   .Close = eval.LastFiveClose,
                                   .Pattern = eval.PatternHit,
                                   .Direction = eval.Direction,
                                   .StopPrice = eval.SuggestedInitialStopPrice,
                                   .StopFloorSource = eval.StopFloorSource
                               }
                               SignalLog.Insert(0, entry)
                               While SignalLog.Count > MaxSignalLogEntries
                                   SignalLog.RemoveAt(SignalLog.Count - 1)
                               End While
                           End Sub)
        End Sub

        Private Sub OnOrchestratorEnabledChanged(sender As Object, enabled As Boolean)
            DispatchAction(Sub()
                               OnPropertyChanged(NameOf(IsEnabled))
                               UpdateStatusFromOrchestrator()
                           End Sub)
        End Sub

        Private Sub OnLivePositionChanged(sender As Object, hasPosition As Boolean)
            DispatchAction(Sub()
                               LivePosition.HasPosition = hasPosition
                               If Not hasPosition Then LivePosition.Reset()
                               UpdateStatusFromOrchestrator()
                           End Sub)
        End Sub

        Private Sub OnStopRatcheted(sender As Object, snap As BreakAndBounceStopSnapshot)
            If snap Is Nothing Then Return
            DispatchAction(Sub() LivePosition.ApplySnapshot(snap))
        End Sub

        Private Sub OnScanCompleted(sender As Object, asOfUtc As DateTime)
            DispatchAction(Sub()
                               HeaderStatusText = "TopStepX checked @ " & asOfUtc.ToLocalTime().ToString("HH:mm:ss")
                           End Sub)
        End Sub

        Private Sub UpdateStatusFromOrchestrator()
            StatusText = If(Not _orchestrator.IsEnabled, "Disabled",
                            If(_orchestrator.IsInPosition, "In Position", "Scanning"))
        End Sub

        Private Shared Function SignalStateLabel(side As BreakAndBounceSignalSide) As String
            Select Case side
                Case BreakAndBounceSignalSide.Bullish
                    Return "Bullish"
                Case BreakAndBounceSignalSide.Bearish
                    Return "Bearish"
                Case Else
                    Return "—"
            End Select
        End Function

        Private Sub DispatchAction(action As Action)
            Dim app = Application.Current
            If app Is Nothing OrElse app.Dispatcher Is Nothing OrElse app.Dispatcher.CheckAccess() Then
                action()
            Else
                app.Dispatcher.BeginInvoke(action)
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            RemoveHandler _orchestrator.WatchlistTick, AddressOf OnWatchlistTick
            RemoveHandler _orchestrator.SignalDetected, AddressOf OnSignalDetected
            RemoveHandler _orchestrator.EnabledChanged, AddressOf OnOrchestratorEnabledChanged
            RemoveHandler _orchestrator.LivePositionChanged, AddressOf OnLivePositionChanged
            RemoveHandler _orchestrator.StopRatcheted, AddressOf OnStopRatcheted
            RemoveHandler _orchestrator.ScanCompleted, AddressOf OnScanCompleted
        End Sub

    End Class

    ''' <summary>FEAT-62: One watchlist row per scanned instrument.</summary>
    Public Class BreakAndBounceWatchlistRowVm
        Inherits ViewModelBase

        Public Sub New(symbol As String, displayName As String)
            _symbol = symbol
            _displayName = displayName
        End Sub

        Private _symbol As String
        Public ReadOnly Property Symbol As String
            Get
                Return _symbol
            End Get
        End Property

        Private _displayName As String
        Public ReadOnly Property DisplayName As String
            Get
                Return _displayName
            End Get
        End Property

        Private _prevHigh As Decimal
        Public Property PrevHigh As Decimal
            Get
                Return _prevHigh
            End Get
            Set(value As Decimal)
                SetProperty(_prevHigh, value)
            End Set
        End Property

        Private _prevLow As Decimal
        Public Property PrevLow As Decimal
            Get
                Return _prevLow
            End Get
            Set(value As Decimal)
                SetProperty(_prevLow, value)
            End Set
        End Property

        Private _lastClose As Decimal
        Public Property LastClose As Decimal
            Get
                Return _lastClose
            End Get
            Set(value As Decimal)
                SetProperty(_lastClose, value)
            End Set
        End Property

        Private _direction As Integer
        Public Property Direction As Integer
            Get
                Return _direction
            End Get
            Set(value As Integer)
                If SetProperty(_direction, value) Then OnPropertyChanged(NameOf(DirectionText))
            End Set
        End Property

        Public ReadOnly Property DirectionText As String
            Get
                Select Case _direction
                    Case 1 : Return "↑ LONG"
                    Case -1 : Return "↓ SHORT"
                    Case Else : Return "—"
                End Select
            End Get
        End Property

        Private _retestTagged As Boolean
        Public Property RetestTagged As Boolean
            Get
                Return _retestTagged
            End Get
            Set(value As Boolean)
                SetProperty(_retestTagged, value)
            End Set
        End Property

        Private _patternHit As String = "—"
        Public Property PatternHit As String
            Get
                Return _patternHit
            End Get
            Set(value As String)
                SetProperty(_patternHit, If(String.IsNullOrEmpty(value), "—", value))
            End Set
        End Property

        Private _inEntryWindow As Boolean
        Public Property InEntryWindow As Boolean
            Get
                Return _inEntryWindow
            End Get
            Set(value As Boolean)
                SetProperty(_inEntryWindow, value)
            End Set
        End Property

        Private _signalState As String = "—"
        Public Property SignalState As String
            Get
                Return _signalState
            End Get
            Set(value As String)
                SetProperty(_signalState, value)
            End Set
        End Property

        Private _rejectionReason As String = String.Empty
        Public Property RejectionReason As String
            Get
                Return _rejectionReason
            End Get
            Set(value As String)
                SetProperty(_rejectionReason, value)
            End Set
        End Property

        Private _lastUpdatedUtc As DateTime
        Public Property LastUpdatedUtc As DateTime
            Get
                Return _lastUpdatedUtc
            End Get
            Set(value As DateTime)
                SetProperty(_lastUpdatedUtc, value)
                OnPropertyChanged(NameOf(LastUpdatedText))
            End Set
        End Property

        Public ReadOnly Property LastUpdatedText As String
            Get
                If _lastUpdatedUtc = DateTime.MinValue Then Return "—"
                Return _lastUpdatedUtc.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
    End Class

    ''' <summary>FEAT-62: Row in the on-tab signal log. Last 50 detected signals are kept.</summary>
    Public Class BreakAndBounceSignalLogEntry
        Public Property TimestampUtc As DateTime
        Public Property Symbol As String = String.Empty
        Public Property Side As String = String.Empty
        Public Property Close As Decimal
        Public Property Pattern As String = String.Empty
        Public Property Direction As Integer
        Public Property StopPrice As Decimal
        Public Property StopFloorSource As String = String.Empty

        Public ReadOnly Property TimestampLocalText As String
            Get
                Return TimestampUtc.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
    End Class

    ''' <summary>FEAT-62: Live-position card VM. Updated by orchestrator's StopRatcheted events.</summary>
    Public Class BreakAndBounceLivePositionVm
        Inherits ViewModelBase

        Public Sub New()
            Reset()
        End Sub

        Private _hasPosition As Boolean
        Public Property HasPosition As Boolean
            Get
                Return _hasPosition
            End Get
            Set(value As Boolean)
                SetProperty(_hasPosition, value)
            End Set
        End Property

        Private _symbol As String = String.Empty
        Public Property Symbol As String
            Get
                Return _symbol
            End Get
            Set(value As String)
                SetProperty(_symbol, value)
            End Set
        End Property

        Private _sideText As String = "—"
        Public Property SideText As String
            Get
                Return _sideText
            End Get
            Set(value As String)
                SetProperty(_sideText, value)
            End Set
        End Property

        Private _entryPrice As Decimal
        Public Property EntryPrice As Decimal
            Get
                Return _entryPrice
            End Get
            Set(value As Decimal)
                SetProperty(_entryPrice, value)
            End Set
        End Property

        Private _currentStopPrice As Decimal
        Public Property CurrentStopPrice As Decimal
            Get
                Return _currentStopPrice
            End Get
            Set(value As Decimal)
                SetProperty(_currentStopPrice, value)
            End Set
        End Property

        Private _lastPrice As Decimal
        Public Property LastPrice As Decimal
            Get
                Return _lastPrice
            End Get
            Set(value As Decimal)
                SetProperty(_lastPrice, value)
            End Set
        End Property

        Friend Sub ApplySnapshot(snap As BreakAndBounceStopSnapshot)
            If snap Is Nothing Then Return
            Symbol = snap.Symbol
            SideText = If(snap.Side = OrderSide.Buy, "Long", "Short")
            EntryPrice = snap.EntryPrice
            CurrentStopPrice = snap.NewStopPrice
            LastPrice = snap.LastPrice
        End Sub

        Friend Sub Reset()
            Symbol = String.Empty
            SideText = "—"
            EntryPrice = 0D
            CurrentStopPrice = 0D
            LastPrice = 0D
        End Sub

    End Class

End Namespace
