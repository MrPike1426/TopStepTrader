Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.VwapMeanReversion
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-75: Backing VM for the VWAP Mean-Reversion tab.
    '''
    ''' Binds the shared singleton <see cref="VwapMeanReversionConfig"/> (edits apply live —
    ''' config persistence is not part of v1); pre-populates the watchlist rows; subscribes
    ''' to <see cref="VwapMeanReversionOrchestrator"/> events; appends detected signals to
    ''' the on-tab log; drives <see cref="IsEnabled"/> from the orchestrator master switch
    ''' (off by default — explicit opt-in); exposes the Lewis/Damian/Joe persona selector
    ''' which applies the persona's ADX-veto default.
    ''' </summary>
    Public Class VwapMeanReversionViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private Const MaxSignalLogEntries As Integer = 50

        Private ReadOnly _config As VwapMeanReversionConfig
        Private ReadOnly _orchestrator As VwapMeanReversionOrchestrator
        Private ReadOnly _logger As ILogger(Of VwapMeanReversionViewModel)
        Private ReadOnly _rowBySymbol As New Dictionary(Of String, VwapMrWatchlistRowVm)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _warmupBarCounts As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Private _disposed As Boolean

        Public Sub New(config As VwapMeanReversionConfig,
                       orchestrator As VwapMeanReversionOrchestrator,
                       logger As ILogger(Of VwapMeanReversionViewModel),
                       Optional adaptiveWatchlist As AdaptiveWatchlistService = Nothing)
            _config = config
            _orchestrator = orchestrator
            _logger = logger

            WatchlistRows = New ObservableCollection(Of VwapMrWatchlistRowVm)()
            For Each row In BuildInitialRows(adaptiveWatchlist)
                WatchlistRows.Add(row)
                _rowBySymbol(row.Symbol) = row
            Next

            SignalLog = New ObservableCollection(Of VwapMrSignalLogEntry)()
            LivePosition = New VwapMrLivePositionVm()
            UpdateStatusFromOrchestrator()

            SelectLewisCommand = New RelayCommand(Sub() ActivePersona = "Lewis")
            SelectDamianCommand = New RelayCommand(Sub() ActivePersona = "Damian")
            SelectJoeCommand = New RelayCommand(Sub() ActivePersona = "Joe")

            AddHandler _orchestrator.WatchlistTick, AddressOf OnWatchlistTick
            AddHandler _orchestrator.SignalDetected, AddressOf OnSignalDetected
            AddHandler _orchestrator.EnabledChanged, AddressOf OnOrchestratorEnabledChanged
            AddHandler _orchestrator.LivePositionChanged, AddressOf OnLivePositionChanged
            AddHandler _orchestrator.TrailUpdated, AddressOf OnTrailUpdated
            AddHandler _orchestrator.ScanCompleted, AddressOf OnScanCompleted
        End Sub

        Public ReadOnly Property WatchlistRows As ObservableCollection(Of VwapMrWatchlistRowVm)
        Public ReadOnly Property SignalLog As ObservableCollection(Of VwapMrSignalLogEntry)
        Public ReadOnly Property LivePosition As VwapMrLivePositionVm

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

        ' ─── Persona selection (Lewis 30 / Damian 25 / Joe 20 ADX veto) ────────

        Public ReadOnly Property SelectLewisCommand As RelayCommand
        Public ReadOnly Property SelectDamianCommand As RelayCommand
        Public ReadOnly Property SelectJoeCommand As RelayCommand

        Public Property ActivePersona As String
            Get
                Return _config.ActivePersona
            End Get
            Set(value As String)
                If _config.ActivePersona = value Then Return
                _config.ApplyPersona(value)
                OnPropertyChanged()
                OnPropertyChanged(NameOf(IsLewisSelected))
                OnPropertyChanged(NameOf(IsDamianSelected))
                OnPropertyChanged(NameOf(IsJoeSelected))
                OnPropertyChanged(NameOf(AdxVetoThreshold))
            End Set
        End Property

        Public ReadOnly Property IsLewisSelected As Boolean
            Get
                Return _config.ActivePersona = "Lewis"
            End Get
        End Property

        Public ReadOnly Property IsDamianSelected As Boolean
            Get
                Return _config.ActivePersona = "Damian"
            End Get
        End Property

        Public ReadOnly Property IsJoeSelected As Boolean
            Get
                Return _config.ActivePersona = "Joe"
            End Get
        End Property

        ' ─── Flat config bindings (live edits on the shared singleton) ─────────

        Public Property SdEntryThreshold As Double
            Get
                Return _config.SdEntryThreshold
            End Get
            Set(value As Double)
                If _config.SdEntryThreshold = value Then Return
                _config.SdEntryThreshold = value
                OnPropertyChanged()
            End Set
        End Property

        Public Property SdTooFarThreshold As Double
            Get
                Return _config.SdTooFarThreshold
            End Get
            Set(value As Double)
                If _config.SdTooFarThreshold = value Then Return
                _config.SdTooFarThreshold = value
                OnPropertyChanged()
            End Set
        End Property

        Public Property AdxVetoThreshold As Double
            Get
                Return _config.AdxVetoThreshold
            End Get
            Set(value As Double)
                If _config.AdxVetoThreshold = value Then Return
                _config.AdxVetoThreshold = value
                OnPropertyChanged()
            End Set
        End Property

        Public Property MinStopAtrMult As Double
            Get
                Return _config.MinStopAtrMult
            End Get
            Set(value As Double)
                If _config.MinStopAtrMult = value Then Return
                _config.MinStopAtrMult = value
                OnPropertyChanged()
            End Set
        End Property

        Public Property Tp2StopMultiple As Double
            Get
                Return _config.Tp2StopMultiple
            End Get
            Set(value As Double)
                If _config.Tp2StopMultiple = value Then Return
                _config.Tp2StopMultiple = value
                OnPropertyChanged()
            End Set
        End Property

        Public Property TrailAtrMult As Double
            Get
                Return _config.TrailAtrMult
            End Get
            Set(value As Double)
                If _config.TrailAtrMult = value Then Return
                _config.TrailAtrMult = value
                OnPropertyChanged()
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
            End Set
        End Property

        Public Property EntryCutoffMinutesBeforeClose As Integer
            Get
                Return _config.EntryCutoffMinutesBeforeClose
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                If _config.EntryCutoffMinutesBeforeClose = clamped Then Return
                _config.EntryCutoffMinutesBeforeClose = clamped
                OnPropertyChanged()
            End Set
        End Property

        Public Property CooldownBars As Integer
            Get
                Return _config.CooldownBars
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                If _config.CooldownBars = clamped Then Return
                _config.CooldownBars = clamped
                OnPropertyChanged()
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
            End Set
        End Property

        ' ─── Orchestrator event handlers ──────────────────────────────────────

        Private Sub OnWatchlistTick(sender As Object, eval As VwapMeanReversionEvaluation)
            If eval Is Nothing Then Return
            Dim row As VwapMrWatchlistRowVm = Nothing
            If Not _rowBySymbol.TryGetValue(eval.Symbol, row) Then Return

            DispatchAction(Sub()
                               row.LastClose = eval.LastClose
                               row.Vwap = eval.Vwap
                               row.DeviationSd = eval.DeviationSd
                               row.Adx = eval.Adx
                               row.BeyondEntryBand = eval.BeyondEntryBand
                               row.AdxVetoPassed = eval.AdxVetoPassed
                               row.ConfirmationCandle = eval.ConfirmationCandle
                               row.InEntryWindow = eval.InEntryWindow
                               row.SignalState = SignalStateLabel(eval.Signal)
                               row.RejectionReason = eval.RejectionReason
                               row.LastUpdatedUtc = DateTime.UtcNow
                           End Sub)
        End Sub

        Private Sub OnSignalDetected(sender As Object, eval As VwapMeanReversionEvaluation)
            If eval Is Nothing Then Return
            DispatchAction(Sub()
                               Dim entry = New VwapMrSignalLogEntry With {
                                   .TimestampUtc = DateTime.UtcNow,
                                   .Symbol = eval.Symbol,
                                   .Side = SignalStateLabel(eval.Signal),
                                   .Close = eval.LastClose,
                                   .DeviationSd = eval.DeviationSd,
                                   .Adx = eval.Adx,
                                   .Notes = If(String.IsNullOrEmpty(eval.ConfirmationPattern),
                                               eval.RejectionReason, eval.ConfirmationPattern)
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

        Private Sub OnTrailUpdated(sender As Object, snapshot As VwapMrTrailSnapshot)
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
                               ' Warmup: full ATR window + a buffer of session bars for the SD.
                               Dim threshold = _config.AtrLength * 2 + 10
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

        Private Shared Function SignalStateLabel(side As VwapMeanReversionSignalSide) As String
            Select Case side
                Case VwapMeanReversionSignalSide.Bullish
                    Return "Bullish"
                Case VwapMeanReversionSignalSide.Bearish
                    Return "Bearish"
                Case Else
                    Return "—"
            End Select
        End Function

        ''' <summary>FEAT-72: adaptive ON → the service's current selection; OFF → MES/MNQ/MGC.</summary>
        Private Shared Function BuildInitialRows(adaptive As AdaptiveWatchlistService) As IList(Of VwapMrWatchlistRowVm)
            Dim rows As New List(Of VwapMrWatchlistRowVm)
            If adaptive IsNot Nothing AndAlso adaptive.IsEnabled Then
                Dim live = adaptive.GetCurrentWatchlist()
                If live IsNot Nothing AndAlso live.Count > 0 Then
                    For Each c In live
                        If c Is Nothing OrElse String.IsNullOrEmpty(c.PxRootSymbol) Then Continue For
                        Dim display = If(String.IsNullOrWhiteSpace(c.DisplayName), c.PxRootSymbol, c.DisplayName)
                        rows.Add(New VwapMrWatchlistRowVm(c.PxRootSymbol, display))
                    Next
                    If rows.Count > 0 Then Return rows
                End If
            End If
            rows.Add(New VwapMrWatchlistRowVm("MES", "S&P 500"))
            rows.Add(New VwapMrWatchlistRowVm("MNQ", "Nasdaq"))
            rows.Add(New VwapMrWatchlistRowVm("MGC", "Gold"))
            Return rows
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

    ''' <summary>FEAT-75: One watchlist row per scanned instrument; receives WatchlistTick updates.</summary>
    Public Class VwapMrWatchlistRowVm
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

        Private _vwap As Decimal
        Public Property Vwap As Decimal
            Get
                Return _vwap
            End Get
            Set(value As Decimal)
                SetProperty(_vwap, value)
            End Set
        End Property

        Private _deviationSd As Double = Double.NaN
        Public Property DeviationSd As Double
            Get
                Return _deviationSd
            End Get
            Set(value As Double)
                SetProperty(_deviationSd, value)
                OnPropertyChanged(NameOf(DeviationSdText))
            End Set
        End Property

        Public ReadOnly Property DeviationSdText As String
            Get
                If Double.IsNaN(_deviationSd) Then Return "—"
                Return _deviationSd.ToString("+0.0;-0.0") & "σ"
            End Get
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

        Private _beyondEntryBand As Boolean
        Public Property BeyondEntryBand As Boolean
            Get
                Return _beyondEntryBand
            End Get
            Set(value As Boolean)
                SetProperty(_beyondEntryBand, value)
                OnPropertyChanged(NameOf(ConditionsText))
            End Set
        End Property

        Private _adxVetoPassed As Boolean
        Public Property AdxVetoPassed As Boolean
            Get
                Return _adxVetoPassed
            End Get
            Set(value As Boolean)
                SetProperty(_adxVetoPassed, value)
                OnPropertyChanged(NameOf(ConditionsText))
            End Set
        End Property

        Private _confirmationCandle As Boolean
        Public Property ConfirmationCandle As Boolean
            Get
                Return _confirmationCandle
            End Get
            Set(value As Boolean)
                SetProperty(_confirmationCandle, value)
                OnPropertyChanged(NameOf(ConditionsText))
            End Set
        End Property

        Private _inEntryWindow As Boolean
        Public Property InEntryWindow As Boolean
            Get
                Return _inEntryWindow
            End Get
            Set(value As Boolean)
                SetProperty(_inEntryWindow, value)
                OnPropertyChanged(NameOf(ConditionsText))
            End Set
        End Property

        ''' <summary>Compact per-condition status: 2σ / ADX / 1m-confirm / window.</summary>
        Public ReadOnly Property ConditionsText As String
            Get
                Return $"{Check(_beyondEntryBand)}2σ {Check(_adxVetoPassed)}ADX {Check(_confirmationCandle)}1m {Check(_inEntryWindow)}win"
            End Get
        End Property

        Private Shared Function Check(v As Boolean) As String
            Return If(v, "✓", "·")
        End Function

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

    ''' <summary>FEAT-75: Row in the on-tab signal log. Last 50 detected signals are kept.</summary>
    Public Class VwapMrSignalLogEntry
        Public Property TimestampUtc As DateTime
        Public Property Symbol As String = String.Empty
        Public Property Side As String = String.Empty
        Public Property Close As Decimal
        Public Property DeviationSd As Double
        Public Property Adx As Double
        Public Property Notes As String = String.Empty

        Public ReadOnly Property TimestampLocalText As String
            Get
                Return TimestampUtc.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
    End Class

    ''' <summary>FEAT-75: Live-position card VM. Updated by TrailUpdated snapshots.</summary>
    Public Class VwapMrLivePositionVm
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

        Private _t1Price As Decimal
        Public Property T1Price As Decimal
            Get
                Return _t1Price
            End Get
            Set(value As Decimal)
                SetProperty(_t1Price, value)
            End Set
        End Property

        Private _t2Price As Decimal
        Public Property T2Price As Decimal
            Get
                Return _t2Price
            End Get
            Set(value As Decimal)
                SetProperty(_t2Price, value)
            End Set
        End Property

        Private _t1Filled As Boolean
        Public Property T1Filled As Boolean
            Get
                Return _t1Filled
            End Get
            Set(value As Boolean)
                SetProperty(_t1Filled, value)
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

        Friend Sub ApplySnapshot(snap As VwapMrTrailSnapshot)
            If snap Is Nothing Then Return
            Symbol = snap.Symbol
            SideText = If(snap.Side = OrderSide.Buy, "Long", "Short")
            EntryPrice = snap.EntryPrice
            CurrentStopPrice = snap.CurrentStopPrice
            T1Price = snap.T1Price
            T2Price = snap.T2Price
            T1Filled = snap.T1Filled
            LastPrice = snap.LastPrice
            TrailArmed = snap.TrailArmed
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
            T1Price = 0D
            T2Price = 0D
            T1Filled = False
            LastPrice = 0D
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
