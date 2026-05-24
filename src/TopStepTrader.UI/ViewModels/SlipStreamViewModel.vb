Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data
Imports TopStepTrader.Services.SlipStream
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-70: Backing VM for the SlipStream tab.
    '''
    ''' Loads + persists <see cref="SlipStreamConfig"/> via the repository; pre-populates
    ''' three watchlist rows (MES / MNQ / MGC); subscribes to
    ''' <see cref="SlipStreamOrchestrator"/> events; appends signal-detected events to the
    ''' on-tab log; drives <see cref="IsEnabled"/> from the orchestrator master switch
    ''' (off by default — explicit opt-in); two-way-binds the key tunables (ATR multiples,
    ''' ADX min, risk %, session window) to <see cref="Config"/>.
    ''' </summary>
    Public Class SlipStreamViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private Const MaxSignalLogEntries As Integer = 50

        Private ReadOnly _configRepository As SlipStreamConfigRepository
        Private ReadOnly _orchestrator As SlipStreamOrchestrator
        Private ReadOnly _logger As ILogger(Of SlipStreamViewModel)
        Private ReadOnly _rowBySymbol As New Dictionary(Of String, SlipStreamWatchlistRowVm)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _warmupBarCounts As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Private _disposed As Boolean

        Public Sub New(configRepository As SlipStreamConfigRepository,
                       orchestrator As SlipStreamOrchestrator,
                       logger As ILogger(Of SlipStreamViewModel))
            _configRepository = configRepository
            _orchestrator = orchestrator
            _logger = logger

            WatchlistRows = New ObservableCollection(Of SlipStreamWatchlistRowVm) From {
                New SlipStreamWatchlistRowVm("MES", "S&P 500"),
                New SlipStreamWatchlistRowVm("MNQ", "Nasdaq"),
                New SlipStreamWatchlistRowVm("MGC", "Gold")
            }
            For Each row In WatchlistRows
                _rowBySymbol(row.Symbol) = row
            Next

            SignalLog = New ObservableCollection(Of SlipStreamSignalLogEntry)()
            LivePosition = New SlipStreamLivePositionVm()
            UpdateStatusFromOrchestrator()

            AddHandler _orchestrator.WatchlistTick, AddressOf OnWatchlistTick
            AddHandler _orchestrator.SignalDetected, AddressOf OnSignalDetected
            AddHandler _orchestrator.EnabledChanged, AddressOf OnOrchestratorEnabledChanged
            AddHandler _orchestrator.LivePositionChanged, AddressOf OnLivePositionChanged
            AddHandler _orchestrator.TrailUpdated, AddressOf OnTrailUpdated
            AddHandler _orchestrator.ScanCompleted, AddressOf OnScanCompleted

            Dim ignored = LoadConfigAsync()
        End Sub

        Public ReadOnly Property WatchlistRows As ObservableCollection(Of SlipStreamWatchlistRowVm)
        Public ReadOnly Property SignalLog As ObservableCollection(Of SlipStreamSignalLogEntry)
        Public ReadOnly Property LivePosition As SlipStreamLivePositionVm

        Private _config As SlipStreamConfig = New SlipStreamConfig()
        Public Property Config As SlipStreamConfig
            Get
                Return _config
            End Get
            Private Set(value As SlipStreamConfig)
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

        ''' <summary>Master enable toggle. Two-way binds to a ToggleButton in the view.</summary>
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

        ' ─── Flat config bindings ──────────────────────────────────────────────

        Public Property AdxMin As Double
            Get
                Return _config.AdxMin
            End Get
            Set(value As Double)
                If _config.AdxMin = value Then Return
                _config.AdxMin = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property RsiLongMin As Double
            Get
                Return _config.RsiLongMin
            End Get
            Set(value As Double)
                If _config.RsiLongMin = value Then Return
                _config.RsiLongMin = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property RsiShortMax As Double
            Get
                Return _config.RsiShortMax
            End Get
            Set(value As Double)
                If _config.RsiShortMax = value Then Return
                _config.RsiShortMax = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property AtrPercentMin As Double
            Get
                Return _config.AtrPercentMin
            End Get
            Set(value As Double)
                If _config.AtrPercentMin = value Then Return
                _config.AtrPercentMin = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property ExtendAtrMult As Double
            Get
                Return _config.ExtendAtrMult
            End Get
            Set(value As Double)
                If _config.ExtendAtrMult = value Then Return
                _config.ExtendAtrMult = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property RiskPct As Double
            Get
                Return _config.RiskPct
            End Get
            Set(value As Double)
                If _config.RiskPct = value Then Return
                _config.RiskPct = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property AtrSLmult As Double
            Get
                Return _config.AtrSLmult
            End Get
            Set(value As Double)
                If _config.AtrSLmult = value Then Return
                _config.AtrSLmult = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property AtrTP1mult As Double
            Get
                Return _config.AtrTP1mult
            End Get
            Set(value As Double)
                If _config.AtrTP1mult = value Then Return
                _config.AtrTP1mult = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property Tp1Pct As Double
            Get
                Return _config.Tp1Pct
            End Get
            Set(value As Double)
                If _config.Tp1Pct = value Then Return
                _config.Tp1Pct = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property TrailMult As Double
            Get
                Return _config.TrailMult
            End Get
            Set(value As Double)
                If _config.TrailMult = value Then Return
                _config.TrailMult = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property TrailOffsetMult As Double
            Get
                Return _config.TrailOffsetMult
            End Get
            Set(value As Double)
                If _config.TrailOffsetMult = value Then Return
                _config.TrailOffsetMult = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property MaxBarsInTrade As Integer
            Get
                Return _config.MaxBarsInTrade
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                If _config.MaxBarsInTrade = clamped Then Return
                _config.MaxBarsInTrade = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property UseSession As Boolean
            Get
                Return _config.UseSession
            End Get
            Set(value As Boolean)
                If _config.UseSession = value Then Return
                _config.UseSession = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property SessionWindow As String
            Get
                Return _config.SessionWindow
            End Get
            Set(value As String)
                If _config.SessionWindow = value Then Return
                _config.SessionWindow = value
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

        Public Property UseHtfFilter As Boolean
            Get
                Return _config.UseHtfFilter
            End Get
            Set(value As Boolean)
                If _config.UseHtfFilter = value Then Return
                _config.UseHtfFilter = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        Public Property HtfTimeframe As String
            Get
                Return _config.HtfTimeframe
            End Get
            Set(value As String)
                If _config.HtfTimeframe = value Then Return
                _config.HtfTimeframe = value
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
                NameOf(AdxMin), NameOf(RsiLongMin), NameOf(RsiShortMax), NameOf(AtrPercentMin),
                NameOf(ExtendAtrMult), NameOf(RiskPct), NameOf(AtrSLmult), NameOf(AtrTP1mult),
                NameOf(Tp1Pct), NameOf(TrailMult), NameOf(TrailOffsetMult), NameOf(MaxBarsInTrade),
                NameOf(UseSession), NameOf(SessionWindow), NameOf(FlatWindow),
                NameOf(UseHtfFilter), NameOf(HtfTimeframe),
                NameOf(EnableLong), NameOf(EnableShort)
            }
                OnPropertyChanged(name)
            Next
        End Sub

        Private Sub FireAndForgetSave()
            Dim ignored = SaveConfigAsync()
        End Sub

        ' ─── Persistence ───────────────────────────────────────────────────────

        Private Async Function LoadConfigAsync() As Task
            Try
                Config = Await _configRepository.LoadAsync()
            Catch ex As Exception
                _logger?.LogWarning(ex, "Failed to load SlipStreamConfig — defaults retained")
            End Try
        End Function

        Public Async Function SaveConfigAsync() As Task
            Try
                Await _configRepository.SaveAsync(_config)
            Catch ex As Exception
                _logger?.LogWarning(ex, "Failed to save SlipStreamConfig")
            End Try
        End Function

        ' ─── Orchestrator event handlers ──────────────────────────────────────

        Private Sub OnWatchlistTick(sender As Object, eval As SlipStreamEvaluation)
            If eval Is Nothing Then Return
            Dim row As SlipStreamWatchlistRowVm = Nothing
            If Not _rowBySymbol.TryGetValue(eval.Symbol, row) Then Return

            DispatchAction(Sub()
                               row.LastClose = eval.LastClose
                               row.EmaFast = eval.EmaFast
                               row.EmaSlow = eval.EmaSlow
                               row.Adx = eval.Adx
                               row.Rsi = eval.Rsi
                               row.Atr = eval.Atr
                               row.AtrPercentRank = eval.AtrPercentRank
                               row.SignalState = SignalStateLabel(eval.Signal)
                               row.RejectionReason = eval.RejectionReason
                               row.LastUpdatedUtc = DateTime.UtcNow
                           End Sub)
        End Sub

        Private Sub OnSignalDetected(sender As Object, eval As SlipStreamEvaluation)
            If eval Is Nothing Then Return
            DispatchAction(Sub()
                               Dim entry = New SlipStreamSignalLogEntry With {
                                   .TimestampUtc = DateTime.UtcNow,
                                   .Symbol = eval.Symbol,
                                   .Side = SignalStateLabel(eval.Signal),
                                   .Close = eval.LastClose,
                                   .Rsi = eval.Rsi,
                                   .Adx = eval.Adx,
                                   .Notes = eval.RejectionReason
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

        Private Sub OnTrailUpdated(sender As Object, snapshot As SlipStreamTrailSnapshot)
            If snapshot Is Nothing Then Return
            DispatchAction(Sub() LivePosition.ApplySnapshot(snapshot))
        End Sub

        Private Sub OnScanCompleted(sender As Object, e As ScannerScanCompletedEventArgs)
            If e Is Nothing Then Return
            DispatchAction(Sub()
                               If e.BarsAvailable IsNot Nothing Then
                                   For Each kv In e.BarsAvailable
                                       _warmupBarCounts(kv.Key) = kv.Value
                                   Next
                               End If
                               Dim threshold = Math.Max(_config.EmaSlowLength, _config.AtrPercentLookback) + 50
                               Dim warm = WatchlistRows.All(Function(r) GetWarmupCount(r.Symbol) >= threshold)
                               If warm Then
                                   HeaderStatusText = "TopStepX checked @ " & e.AsOfUtc.ToLocalTime().ToString("HH:mm:ss")
                               Else
                                   HeaderStatusText = "Loading 5 m bars… " & BuildPerSymbolCounts(threshold)
                               End If
                           End Sub)
        End Sub

        Private Function GetWarmupCount(symbol As String) As Integer
            Dim count As Integer
            _warmupBarCounts.TryGetValue(symbol, count)
            Return count
        End Function

        Private Function BuildPerSymbolCounts(threshold As Integer) As String
            Return String.Join(" · ",
                WatchlistRows.Select(Function(r) $"{r.Symbol} {GetWarmupCount(r.Symbol)}/{threshold}"))
        End Function

        Private Sub UpdateStatusFromOrchestrator()
            StatusText = If(Not _orchestrator.IsEnabled, "Disabled",
                            If(_orchestrator.IsInPosition, "In Position", "Scanning"))
        End Sub

        Private Shared Function SignalStateLabel(side As SlipStreamSignalSide) As String
            Select Case side
                Case SlipStreamSignalSide.Bullish
                    Return "Bullish"
                Case SlipStreamSignalSide.Bearish
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
            RemoveHandler _orchestrator.TrailUpdated, AddressOf OnTrailUpdated
            RemoveHandler _orchestrator.ScanCompleted, AddressOf OnScanCompleted
        End Sub

    End Class

    ''' <summary>FEAT-70: One watchlist row per scanned instrument; receives WatchlistTick updates.</summary>
    Public Class SlipStreamWatchlistRowVm
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

        Private _lastClose As Decimal
        Public Property LastClose As Decimal
            Get
                Return _lastClose
            End Get
            Set(value As Decimal)
                SetProperty(_lastClose, value)
            End Set
        End Property

        Private _emaFast As Decimal
        Public Property EmaFast As Decimal
            Get
                Return _emaFast
            End Get
            Set(value As Decimal)
                SetProperty(_emaFast, value)
            End Set
        End Property

        Private _emaSlow As Decimal
        Public Property EmaSlow As Decimal
            Get
                Return _emaSlow
            End Get
            Set(value As Decimal)
                SetProperty(_emaSlow, value)
            End Set
        End Property

        Private _adx As Double = Double.NaN
        Public Property Adx As Double
            Get
                Return _adx
            End Get
            Set(value As Double)
                SetProperty(_adx, value)
                OnPropertyChanged(NameOf(AdxText))
            End Set
        End Property

        Public ReadOnly Property AdxText As String
            Get
                If Double.IsNaN(_adx) Then Return "—"
                Return _adx.ToString("F1")
            End Get
        End Property

        Private _rsi As Double = Double.NaN
        Public Property Rsi As Double
            Get
                Return _rsi
            End Get
            Set(value As Double)
                SetProperty(_rsi, value)
                OnPropertyChanged(NameOf(RsiText))
            End Set
        End Property

        Public ReadOnly Property RsiText As String
            Get
                If Double.IsNaN(_rsi) Then Return "—"
                Return _rsi.ToString("F1")
            End Get
        End Property

        Private _atr As Decimal
        Public Property Atr As Decimal
            Get
                Return _atr
            End Get
            Set(value As Decimal)
                SetProperty(_atr, value)
            End Set
        End Property

        Private _atrPercentRank As Double = -1.0
        Public Property AtrPercentRank As Double
            Get
                Return _atrPercentRank
            End Get
            Set(value As Double)
                SetProperty(_atrPercentRank, value)
                OnPropertyChanged(NameOf(AtrPercentRankText))
            End Set
        End Property

        Public ReadOnly Property AtrPercentRankText As String
            Get
                If _atrPercentRank < 0 Then Return "—"
                Return _atrPercentRank.ToString("F0") & "%"
            End Get
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

    ''' <summary>FEAT-70: Row in the on-tab signal log. Last 50 detected signals are kept.</summary>
    Public Class SlipStreamSignalLogEntry
        Public Property TimestampUtc As DateTime
        Public Property Symbol As String = String.Empty
        Public Property Side As String = String.Empty
        Public Property Close As Decimal
        Public Property Rsi As Double
        Public Property Adx As Double
        Public Property Notes As String = String.Empty

        Public ReadOnly Property TimestampLocalText As String
            Get
                Return TimestampUtc.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
    End Class

    ''' <summary>FEAT-70: Live-position card VM. Updated by TrailUpdated snapshots.</summary>
    Public Class SlipStreamLivePositionVm
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

        Private _tp1Price As Decimal
        Public Property Tp1Price As Decimal
            Get
                Return _tp1Price
            End Get
            Set(value As Decimal)
                SetProperty(_tp1Price, value)
            End Set
        End Property

        Private _tp1Filled As Boolean
        Public Property Tp1Filled As Boolean
            Get
                Return _tp1Filled
            End Get
            Set(value As Boolean)
                SetProperty(_tp1Filled, value)
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

        Private _peakFavorableDollars As Decimal
        Public Property PeakFavorableDollars As Decimal
            Get
                Return _peakFavorableDollars
            End Get
            Set(value As Decimal)
                SetProperty(_peakFavorableDollars, value)
            End Set
        End Property

        Private _distanceToStopDollars As Decimal
        Public Property DistanceToStopDollars As Decimal
            Get
                Return _distanceToStopDollars
            End Get
            Set(value As Decimal)
                SetProperty(_distanceToStopDollars, value)
            End Set
        End Property

        Private _livePnlDollars As Decimal
        Public Property LivePnlDollars As Decimal
            Get
                Return _livePnlDollars
            End Get
            Set(value As Decimal)
                SetProperty(_livePnlDollars, value)
            End Set
        End Property

        Private _trailArmed As Boolean
        Public Property TrailArmed As Boolean
            Get
                Return _trailArmed
            End Get
            Set(value As Boolean)
                SetProperty(_trailArmed, value)
            End Set
        End Property

        Friend Sub ApplySnapshot(snap As SlipStreamTrailSnapshot)
            If snap Is Nothing Then Return
            Symbol = snap.Symbol
            SideText = If(snap.Side = OrderSide.Buy, "Long", "Short")
            EntryPrice = snap.EntryPrice
            CurrentStopPrice = snap.CurrentStopPrice
            Tp1Price = snap.Tp1Price
            Tp1Filled = snap.Tp1Filled
            LastPrice = snap.LastPrice
            TrailArmed = snap.TrailArmed
            DistanceToStopDollars = TicksToDollars(
                Math.Abs(snap.LastPrice - snap.CurrentStopPrice), snap.TickSize, snap.DollarsPerTick)
            PeakFavorableDollars = TicksToDollars(
                Math.Abs(snap.PeakFavorablePrice - snap.EntryPrice), snap.TickSize, snap.DollarsPerTick)
            Dim signedMove = If(snap.Side = OrderSide.Buy,
                                snap.LastPrice - snap.EntryPrice,
                                snap.EntryPrice - snap.LastPrice)
            LivePnlDollars = TicksToDollars(signedMove, snap.TickSize, snap.DollarsPerTick)
        End Sub

        Friend Sub Reset()
            Symbol = String.Empty
            SideText = "—"
            EntryPrice = 0D
            CurrentStopPrice = 0D
            Tp1Price = 0D
            Tp1Filled = False
            LastPrice = 0D
            DistanceToStopDollars = 0D
            PeakFavorableDollars = 0D
            LivePnlDollars = 0D
            TrailArmed = False
        End Sub

        Private Shared Function TicksToDollars(priceMove As Decimal, tickSize As Decimal, dollarsPerTick As Decimal) As Decimal
            If tickSize <= 0D Then Return 0D
            Dim ticks = priceMove / tickSize
            Return ticks * dollarsPerTick
        End Function

    End Class

End Namespace
