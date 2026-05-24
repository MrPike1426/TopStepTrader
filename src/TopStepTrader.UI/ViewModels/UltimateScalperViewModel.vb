Imports System.Collections.ObjectModel
Imports System.Runtime.CompilerServices
Imports System.Windows
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data
Imports TopStepTrader.Services.Scalper
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-64: Backing VM for the Ultimate Scalper tab.
    '''
    ''' F1: load + persist <see cref="UltimateScalperConfig"/> via the repository; pre-populate
    ''' three watchlist rows (MES / MNQ / MGC).
    ''' F3: subscribe to <see cref="UltimateScalperOrchestrator"/> events; route
    ''' <c>WatchlistTick</c> updates to the per-symbol row VM; append <c>SignalDetected</c>
    ''' entries to the on-tab signal log.
    ''' F4: <see cref="IsEnabled"/> drives the orchestrator's master switch (off by default —
    ''' explicit opt-in). Live position changes update <see cref="StatusText"/>.
    ''' F5: flat per-instrument trail settings two-way-bound to <see cref="Config"/>; live
    ''' position card driven by <c>TrailUpdated</c> snapshots.
    ''' </summary>
    Public Class UltimateScalperViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private Const MaxSignalLogEntries As Integer = 50

        Private ReadOnly _configRepository As UltimateScalperConfigRepository
        Private ReadOnly _orchestrator As UltimateScalperOrchestrator
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _logger As ILogger(Of UltimateScalperViewModel)
        Private ReadOnly _rowBySymbol As New Dictionary(Of String, ScalperWatchlistRowVm)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _warmupBarCounts As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Private _disposed As Boolean

        Public Sub New(configRepository As UltimateScalperConfigRepository,
                       orchestrator As UltimateScalperOrchestrator,
                       orderService As IOrderService,
                       logger As ILogger(Of UltimateScalperViewModel))
            _configRepository = configRepository
            _orchestrator = orchestrator
            _orderService = orderService
            _logger = logger

            WatchlistRows = New ObservableCollection(Of ScalperWatchlistRowVm) From {
                New ScalperWatchlistRowVm("MES", "S&P 500"),
                New ScalperWatchlistRowVm("MNQ", "Nasdaq"),
                New ScalperWatchlistRowVm("MGC", "Gold")
            }
            For Each row In WatchlistRows
                _rowBySymbol(row.Symbol) = row
            Next

            SignalLog = New ObservableCollection(Of ScalperSignalLogEntry)()
            LivePosition = New ScalperLivePositionVm()
            UpdateStatusFromOrchestrator()

            AddHandler _orchestrator.WatchlistTick, AddressOf OnWatchlistTick
            AddHandler _orchestrator.SignalDetected, AddressOf OnSignalDetected
            AddHandler _orchestrator.EnabledChanged, AddressOf OnOrchestratorEnabledChanged
            AddHandler _orchestrator.LivePositionChanged, AddressOf OnLivePositionChanged
            AddHandler _orchestrator.TrailUpdated, AddressOf OnTrailUpdated
            AddHandler _orchestrator.ScanCompleted, AddressOf OnScanCompleted
            AddHandler _orchestrator.WatchlistArmedChanged, AddressOf OnWatchlistArmedChanged
            AddHandler _orderService.OrderRejected, AddressOf OnOrderRejected

            ' Fire-and-forget initial load; UI shows defaults until it returns.
            Dim ignored = LoadConfigAsync()
        End Sub

        Public ReadOnly Property WatchlistRows As ObservableCollection(Of ScalperWatchlistRowVm)
        Public ReadOnly Property SignalLog As ObservableCollection(Of ScalperSignalLogEntry)
        Public ReadOnly Property LivePosition As ScalperLivePositionVm

        Private _config As UltimateScalperConfig = New UltimateScalperConfig()
        ''' <summary>Currently-loaded strategy config. Per-instrument settings bind to flat properties below.</summary>
        Public Property Config As UltimateScalperConfig
            Get
                Return _config
            End Get
            Private Set(value As UltimateScalperConfig)
                If SetProperty(_config, value) Then RaiseAllConfigBindings()
            End Set
        End Property

        Private _statusText As String = "Disabled"
        ''' <summary>Top-of-tab status badge: "Disabled" / "Scanning" / "In Position" / "Closing".</summary>
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Private Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _headerStatusText As String = String.Empty
        ''' <summary>FEAT-65: Header status line — "Loading 5 m bars… X/N" during warmup, "TopStepX checked @ HH:mm:ss" once warm.</summary>
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

        ' ─── Flat settings bindings ────────────────────────────────────────────
        ' Per-instrument trail values + global tunables proxied through Config so settings UI
        ' two-way-binds without needing INotifyPropertyChanged on the config model itself.

        Public Property MesInitialStopDollars As Decimal
            Get
                Return ProfileGet("MES", Function(p) p.InitialStopDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MES", Sub(p) p.InitialStopDollars = value)
            End Set
        End Property
        Public Property MesBreakevenSnapDollars As Decimal
            Get
                Return ProfileGet("MES", Function(p) p.BreakevenSnapDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MES", Sub(p) p.BreakevenSnapDollars = value)
            End Set
        End Property
        Public Property MesTrailDistanceDollars As Decimal
            Get
                Return ProfileGet("MES", Function(p) p.TrailDistanceDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MES", Sub(p) p.TrailDistanceDollars = value)
            End Set
        End Property
        Public Property MnqInitialStopDollars As Decimal
            Get
                Return ProfileGet("MNQ", Function(p) p.InitialStopDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MNQ", Sub(p) p.InitialStopDollars = value)
            End Set
        End Property
        Public Property MnqBreakevenSnapDollars As Decimal
            Get
                Return ProfileGet("MNQ", Function(p) p.BreakevenSnapDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MNQ", Sub(p) p.BreakevenSnapDollars = value)
            End Set
        End Property
        Public Property MnqTrailDistanceDollars As Decimal
            Get
                Return ProfileGet("MNQ", Function(p) p.TrailDistanceDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MNQ", Sub(p) p.TrailDistanceDollars = value)
            End Set
        End Property
        Public Property MgcInitialStopDollars As Decimal
            Get
                Return ProfileGet("MGC", Function(p) p.InitialStopDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MGC", Sub(p) p.InitialStopDollars = value)
            End Set
        End Property
        Public Property MgcBreakevenSnapDollars As Decimal
            Get
                Return ProfileGet("MGC", Function(p) p.BreakevenSnapDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MGC", Sub(p) p.BreakevenSnapDollars = value)
            End Set
        End Property
        Public Property MgcTrailDistanceDollars As Decimal
            Get
                Return ProfileGet("MGC", Function(p) p.TrailDistanceDollars)
            End Get
            Set(value As Decimal)
                ProfileSet("MGC", Sub(p) p.TrailDistanceDollars = value)
            End Set
        End Property

        Public Property RsiOversold As Double
            Get
                Return _config.RsiOversold
            End Get
            Set(value As Double)
                If _config.RsiOversold = value Then Return
                _config.RsiOversold = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property RsiOverbought As Double
            Get
                Return _config.RsiOverbought
            End Get
            Set(value As Double)
                If _config.RsiOverbought = value Then Return
                _config.RsiOverbought = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property MaxBarsSinceMidlineCross As Integer
            Get
                Return _config.MaxBarsSinceMidlineCross
            End Get
            Set(value As Integer)
                If _config.MaxBarsSinceMidlineCross = value Then Return
                _config.MaxBarsSinceMidlineCross = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property SafetyCeilingTpDollars As Decimal
            Get
                Return _config.SafetyCeilingTpDollars
            End Get
            Set(value As Decimal)
                If _config.SafetyCeilingTpDollars = value Then Return
                _config.SafetyCeilingTpDollars = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property Leverage As Integer
            Get
                Return _config.Leverage
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(1, value)
                If _config.Leverage = clamped Then Return
                _config.Leverage = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property

        ' ─── FEAT-69: pre-staged stop-entry knobs ──────────────────────────────

        Public Property PreStagedEntriesEnabled As Boolean
            Get
                Return _config.PreStagedEntriesEnabled
            End Get
            Set(value As Boolean)
                If _config.PreStagedEntriesEnabled = value Then Return
                _config.PreStagedEntriesEnabled = value
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property EntryTriggerOffsetTicks As Integer
            Get
                Return _config.EntryTriggerOffsetTicks
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(1, value)
                If _config.EntryTriggerOffsetTicks = clamped Then Return
                _config.EntryTriggerOffsetTicks = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property RepriceThresholdTicks As Integer
            Get
                Return _config.RepriceThresholdTicks
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(1, value)
                If _config.RepriceThresholdTicks = clamped Then Return
                _config.RepriceThresholdTicks = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property ArmStaleMinutes As Integer
            Get
                Return _config.ArmStaleMinutes
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(1, value)
                If _config.ArmStaleMinutes = clamped Then Return
                _config.ArmStaleMinutes = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        Public Property ReArmDebounceSeconds As Integer
            Get
                Return _config.ReArmDebounceSeconds
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                If _config.ReArmDebounceSeconds = clamped Then Return
                _config.ReArmDebounceSeconds = clamped
                OnPropertyChanged()
                FireAndForgetSave()
            End Set
        End Property
        ''' <summary>FEAT-69: read-only display in v1 (DB-only, not user-editable).</summary>
        Public ReadOnly Property MaxBrokerCallsPerMinute As Integer
            Get
                Return _config.MaxBrokerCallsPerMinute
            End Get
        End Property
        ''' <summary>FEAT-69: read-only display in v1 (forward-compat seam, clamped to 1).</summary>
        Public ReadOnly Property MaxConcurrentPositions As Integer
            Get
                Return _config.MaxConcurrentPositions
            End Get
        End Property

        Private Function ProfileGet(symbol As String, picker As Func(Of UltimateScalperInstrumentRiskProfile, Decimal)) As Decimal
            Dim p = _config.GetProfile(symbol)
            Return If(p Is Nothing, 0D, picker(p))
        End Function

        Private Sub ProfileSet(symbol As String, mutator As Action(Of UltimateScalperInstrumentRiskProfile),
                                <CallerMemberName> Optional propertyName As String = Nothing)
            Dim p = _config.GetProfile(symbol)
            If p Is Nothing Then Return
            mutator(p)
            OnPropertyChanged(propertyName)
            FireAndForgetSave()
        End Sub

        Private Sub RaiseAllConfigBindings()
            For Each name In New String() {
                NameOf(MesInitialStopDollars), NameOf(MesBreakevenSnapDollars), NameOf(MesTrailDistanceDollars),
                NameOf(MnqInitialStopDollars), NameOf(MnqBreakevenSnapDollars), NameOf(MnqTrailDistanceDollars),
                NameOf(MgcInitialStopDollars), NameOf(MgcBreakevenSnapDollars), NameOf(MgcTrailDistanceDollars),
                NameOf(RsiOversold), NameOf(RsiOverbought), NameOf(MaxBarsSinceMidlineCross),
                NameOf(SafetyCeilingTpDollars), NameOf(Leverage),
                NameOf(PreStagedEntriesEnabled), NameOf(EntryTriggerOffsetTicks), NameOf(RepriceThresholdTicks),
                NameOf(ArmStaleMinutes), NameOf(ReArmDebounceSeconds),
                NameOf(MaxBrokerCallsPerMinute), NameOf(MaxConcurrentPositions)
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
                _logger?.LogWarning(ex, "Failed to load UltimateScalperConfig — defaults retained")
            End Try
        End Function

        ''' <summary>Persists the current <see cref="Config"/> back to SQLite.</summary>
        Public Async Function SaveConfigAsync() As Task
            Try
                Await _configRepository.SaveAsync(_config)
            Catch ex As Exception
                _logger?.LogWarning(ex, "Failed to save UltimateScalperConfig")
            End Try
        End Function

        ' ─── Orchestrator event handlers ──────────────────────────────────────

        Private Sub OnWatchlistTick(sender As Object, eval As UltimateScalperEvaluation)
            If eval Is Nothing Then Return
            Dim row As ScalperWatchlistRowVm = Nothing
            If Not _rowBySymbol.TryGetValue(eval.Symbol, row) Then Return

            DispatchAction(Sub()
                               row.LastClose = eval.LastClose
                               row.Ma200 = eval.Ma200
                               row.Vwap = eval.Vwap
                               row.Rsi = eval.Rsi
                               row.BarsSinceMidlineCross = If(eval.Signal = UltimateScalperSignalSide.Bullish,
                                                              eval.BarsSinceCrossAbove,
                                                              eval.BarsSinceCrossBelow)
                               row.SignalState = SignalStateLabel(eval.Signal)
                               row.RejectionReason = eval.RejectionReason
                               row.LastUpdatedUtc = DateTime.UtcNow
                               ' FEAT-67 F2: trigger the per-row update flash via NotifyOnTargetUpdated.
                               row.LastUpdatedTick = row.LastUpdatedTick + 1L
                           End Sub)
        End Sub

        Private Sub OnSignalDetected(sender As Object, eval As UltimateScalperEvaluation)
            If eval Is Nothing Then Return
            DispatchAction(Sub()
                               Dim entry = New ScalperSignalLogEntry With {
                                   .TimestampUtc = DateTime.UtcNow,
                                   .Symbol = eval.Symbol,
                                   .Side = SignalStateLabel(eval.Signal),
                                   .Close = eval.LastClose,
                                   .Rsi = eval.Rsi,
                                   .Notes = eval.RejectionReason
                               }
                               SignalLog.Insert(0, entry)
                               While SignalLog.Count > MaxSignalLogEntries
                                   SignalLog.RemoveAt(SignalLog.Count - 1)
                               End While
                           End Sub)
        End Sub

        ' BUG-96: broker order rejection → signal log Notes column.
        Private Sub OnOrderRejected(sender As Object, e As OrderRejectedEventArgs)
            If e Is Nothing OrElse e.Order Is Nothing Then Return
            Dim symbol = ResolveWatchlistSymbol(e.Order.ContractId)
            If String.IsNullOrEmpty(symbol) Then Return
            DispatchAction(Sub()
                               Dim entry = New ScalperSignalLogEntry With {
                                   .TimestampUtc = DateTime.UtcNow,
                                   .Symbol = symbol,
                                   .Side = If(e.Order.Side = OrderSide.Buy, "Buy", "Sell"),
                                   .Close = If(e.Order.StopPrice.HasValue, e.Order.StopPrice.Value, 0D),
                                   .Rsi = Double.NaN,
                                   .Notes = "Rejected: " & If(String.IsNullOrEmpty(e.Reason), "(no reason)", e.Reason)
                               }
                               SignalLog.Insert(0, entry)
                               While SignalLog.Count > MaxSignalLogEntries
                                   SignalLog.RemoveAt(SignalLog.Count - 1)
                               End While
                           End Sub)
        End Sub

        ''' <summary>
        ''' Maps a PX contract id (e.g. "CON.F.US.MES.M26") back to the scalper watchlist
        ''' root symbol. Returns String.Empty when the contract is outside the watchlist so
        ''' rejections from other strategies (e.g. SuperTrendPlus on M2K) don't pollute the
        ''' scalper signal log.
        ''' </summary>
        Private Shared Function ResolveWatchlistSymbol(contractId As String) As String
            If String.IsNullOrEmpty(contractId) Then Return String.Empty
            Dim parts = contractId.Split("."c)
            For Each sym In UltimateScalperOrchestrator.WatchlistSymbols
                For Each p In parts
                    If String.Equals(p, sym, StringComparison.OrdinalIgnoreCase) Then Return sym
                Next
            Next
            Return String.Empty
        End Function

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

        Private Sub OnTrailUpdated(sender As Object, snapshot As ScalperTrailSnapshot)
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
                               Dim threshold = _config.MaLength + 50
                               Dim warm = WatchlistRows.All(Function(r) GetWarmupCount(r.Symbol) >= threshold)
                               Dim baseText As String
                               If warm Then
                                   baseText = "TopStepX checked @ " & e.AsOfUtc.ToLocalTime().ToString("HH:mm:ss")
                               Else
                                   baseText = "Loading 5 m bars… " & BuildPerSymbolCounts(threshold)
                               End If
                               HeaderStatusText = baseText & BuildArmedSummary()
                           End Sub)
        End Sub

        ''' <summary>FEAT-69: " · Armed: MES MNQ" suffix for header status; empty when nothing armed.</summary>
        Private Function BuildArmedSummary() As String
            Dim armedNow = _orchestrator.ArmedSymbols
            If armedNow Is Nothing OrElse armedNow.Count = 0 Then Return String.Empty
            Return " · Armed: " & String.Join(" ", armedNow)
        End Function

        Private Sub OnWatchlistArmedChanged(sender As Object, e As ScalperWatchlistArmedChangedArgs)
            If e Is Nothing Then Return
            Dim row As ScalperWatchlistRowVm = Nothing
            If Not _rowBySymbol.TryGetValue(e.Symbol, row) Then Return
            DispatchAction(Sub()
                               If e.IsArmed Then
                                   row.ArmedSide = e.Side
                                   row.ArmedTriggerPrice = e.TriggerPrice
                               Else
                                   row.ArmedSide = UltimateScalperSignalSide.None
                                   row.ArmedTriggerPrice = 0D
                               End If
                               ' Refresh header armed summary immediately so users see arms light up
                               ' between scan ticks.
                               Dim suffix = BuildArmedSummary()
                               Dim baseText = HeaderStatusText
                               Dim sepIdx = baseText.IndexOf(" · Armed:", StringComparison.Ordinal)
                               If sepIdx >= 0 Then baseText = baseText.Substring(0, sepIdx)
                               HeaderStatusText = baseText & suffix
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

        Private Shared Function SignalStateLabel(side As UltimateScalperSignalSide) As String
            Select Case side
                Case UltimateScalperSignalSide.Bullish : Return "Bullish"
                Case UltimateScalperSignalSide.Bearish : Return "Bearish"
                Case Else : Return "—"
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
            RemoveHandler _orchestrator.WatchlistArmedChanged, AddressOf OnWatchlistArmedChanged
            RemoveHandler _orderService.OrderRejected, AddressOf OnOrderRejected
        End Sub

    End Class

    ''' <summary>FEAT-64: Row in the on-tab signal log. Last 50 detected signals are kept.</summary>
    Public Class ScalperSignalLogEntry
        Public Property TimestampUtc As DateTime
        Public Property Symbol As String = String.Empty
        Public Property Side As String = String.Empty
        Public Property Close As Decimal
        Public Property Rsi As Double
        Public Property Notes As String = String.Empty

        Public ReadOnly Property TimestampLocalText As String
            Get
                Return TimestampUtc.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
    End Class

    ''' <summary>
    ''' FEAT-64 F5: Live-position card VM. Holds the projected display fields derived from
    ''' the orchestrator's <c>ScalperTrailSnapshot</c> stream. Updates marshal through the
    ''' WPF dispatcher in the parent VM.
    ''' </summary>
    Public Class ScalperLivePositionVm
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

        Private _distanceToStopDollars As Decimal
        Public Property DistanceToStopDollars As Decimal
            Get
                Return _distanceToStopDollars
            End Get
            Set(value As Decimal)
                SetProperty(_distanceToStopDollars, value)
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

        Private _livePnlDollars As Decimal
        Public Property LivePnlDollars As Decimal
            Get
                Return _livePnlDollars
            End Get
            Set(value As Decimal)
                SetProperty(_livePnlDollars, value)
            End Set
        End Property

        Private _hasBreakevenSnapped As Boolean
        Public Property HasBreakevenSnapped As Boolean
            Get
                Return _hasBreakevenSnapped
            End Get
            Set(value As Boolean)
                SetProperty(_hasBreakevenSnapped, value)
            End Set
        End Property

        Private _editsThisSecond As Integer
        Public Property EditsThisSecond As Integer
            Get
                Return _editsThisSecond
            End Get
            Set(value As Integer)
                SetProperty(_editsThisSecond, value)
            End Set
        End Property

        Friend Sub ApplySnapshot(snap As ScalperTrailSnapshot)
            If snap Is Nothing Then Return
            Symbol = snap.Symbol
            SideText = If(snap.Side = OrderSide.Buy, "Long", "Short")
            EntryPrice = snap.EntryPrice
            CurrentStopPrice = snap.CurrentStopPrice
            LastPrice = snap.LastPrice
            HasBreakevenSnapped = snap.HasBreakevenSnapped
            EditsThisSecond = snap.EditsThisSecond
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
            LastPrice = 0D
            DistanceToStopDollars = 0D
            PeakFavorableDollars = 0D
            LivePnlDollars = 0D
            HasBreakevenSnapped = False
            EditsThisSecond = 0
        End Sub

        Private Shared Function TicksToDollars(priceMove As Decimal, tickSize As Decimal, dollarsPerTick As Decimal) As Decimal
            If tickSize <= 0D Then Return 0D
            Dim ticks = priceMove / tickSize
            Return ticks * dollarsPerTick
        End Function

    End Class

End Namespace
