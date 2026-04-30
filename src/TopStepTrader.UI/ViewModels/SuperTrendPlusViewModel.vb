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
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

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

        Private Shared ReadOnly Instruments As String() = {"MES", "MNQ", "M2K", "MYM", "MGC", "M6J", "MCLE"}
        Private Shared ReadOnly InstrumentLabels As String() = {"MES", "MNQ", "M2K", "MYM", "MGC", "M6J", "OIL"}
        Private Const BarsToFetch As Integer = 60

        Public ReadOnly Property WatchlistItems As New ObservableCollection(Of WatchlistRowVm)
        Private Const SyncMissThreshold As Integer = 3

        Private ReadOnly _barService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _contractResolver As Core.Interfaces.IContractResolutionService
        Private ReadOnly _logger As ILogger(Of SuperTrendPlusViewModel)

        Private _timer As Timer
        Private ReadOnly _timerLock As New Object()
        Private _disposed As Boolean = False

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
        Public Property UseEarlyMode As Boolean
            Get
                Return _useEarlyMode
            End Get
            Set(value As Boolean)
                SetProperty(_useEarlyMode, value)
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

        Public ReadOnly Property TpMultiples As String() = {"None / flip only", "1.5×", "2×", "2.5×", "3×"}

        Private _selectedTpMultiple As String = "2×"
        Public Property SelectedTpMultiple As String
            Get
                Return _selectedTpMultiple
            End Get
            Set(value As String)
                SetProperty(_selectedTpMultiple, value)
            End Set
        End Property

        Public ReadOnly Property StMultipliers As Double() = {2.0, 2.5, 3.0}

        Private _stMultiplier As Double = 3.0
        Public Property StMultiplier As Double
            Get
                Return _stMultiplier
            End Get
            Set(value As Double)
                SetProperty(_stMultiplier, value)
            End Set
        End Property

        Private Function ParseTpMultiple() As Decimal
            Select Case _selectedTpMultiple
                Case "1.5×" : Return 1.5D
                Case "2×"   : Return 2.0D
                Case "2.5×" : Return 2.5D
                Case "3×"   : Return 3.0D
                Case Else   : Return 0D
            End Select
        End Function

        Private _selectedTimeframe As String = "15min"
        Public Property SelectedTimeframe As String
            Get
                Return _selectedTimeframe
            End Get
            Set(value As String)
                If SetProperty(_selectedTimeframe, value) Then
                    NotifyPropertyChanged(NameOf(StatusText))
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

        Public ReadOnly Property StartStopCommand As RelayCommand

        Public Sub New(barService As IBarIngestionService,
                       orderService As IOrderService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService,
                       accountService As IAccountService,
                       contractResolver As Core.Interfaces.IContractResolutionService,
                       logger As ILogger(Of SuperTrendPlusViewModel))
            _barService       = barService
            _orderService     = orderService
            _session          = session
            _personaService   = personaService
            _accountService   = accountService
            _contractResolver = contractResolver
            _logger           = logger
            Config          = New SuperTrendPlusConfig()
            _slotManager    = New SlotManager(Config)
            _exitEngine     = New ExitSignalEngine(
                Microsoft.Extensions.Logging.Abstractions.NullLogger(Of ExitSignalEngine).Instance)
            StartStopCommand = New RelayCommand(AddressOf OnStartStop)

            Slot1.Slot = _slotManager.Slots(0)
            Slot2.Slot = _slotManager.Slots(1)
            Slot3.Slot = _slotManager.Slots(2)

            For i = 0 To Instruments.Length - 1
                WatchlistItems.Add(New WatchlistRowVm() With {
                    .Symbol = Instruments(i),
                    .Label  = InstrumentLabels(i)
                })
            Next
            For Each box In AllSlotBoxes()
                For i = 0 To Instruments.Length - 1
                    box.Symbols.Add(New SymbolRowVm() With {.Symbol = InstrumentLabels(i)})
                Next
            Next
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

        Private Sub StartMonitoring()
            IsHowItWorksExpanded = False
            IsMonitoring = True
            _timer = New Timer(AddressOf TimerCallback, Nothing, 0, 15000)
            If _selectedAccount Is Nothing OrElse _selectedAccount.Id = 0 Then
                StatusText = "? No account selected — monitoring in read-only mode (orders will be blocked until account loads)"
                Application.Current?.Dispatcher?.Invoke(Sub()
                    StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H00))
                End Sub)
            End If
        End Sub

        Private Sub StopMonitoring()
            IsMonitoring = False
            SyncLock _timerLock
                If _timer IsNot Nothing Then
                    _timer.Dispose()
                    _timer = Nothing
                End If
            End SyncLock
            _prevStDirByInstrument.Clear()
            For Each wRow In WatchlistItems
                wRow.Arrow         = "–"
                wRow.AdxDisplay    = "ADX:–"
                wRow.Signal        = "–"
                wRow.TrendStrength = ""
                wRow.RowColor      = Brushes.Gray
            Next
            _slotManager.ResetAll()
            For Each box In AllSlotBoxes()
                box.IsPaused        = False
                box.HasPosition     = False
                box.PositionDisplay = String.Empty
                box.StopPhaseLabel  = String.Empty
                For Each row In box.Symbols
                    row.Arrow      = "–"
                    row.AdxDisplay = "ADX:–"
                    row.Signal     = "flat"
                    row.RowColor   = Brushes.White
                Next
            Next
        End Sub

        Private Sub TimerCallback(state As Object)
            Try
                Task.Run(Async Function() As Task
                             Await DoTickAsync()
                         End Function).Wait()
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ DoTickAsync error on timer tick")
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        StatusText = String.Format("Error: {0}", ex.Message)
                    End Sub)
            End Try
        End Sub

        Private Async Function DoTickAsync() As Task
            _releasedThisTick = False
            Dim tf = MapTimeframe(_selectedTimeframe)
            Dim barCache = Await ScanWatchlistAsync(tf)

            For Each slot In _slotManager.Slots
                If slot.IsOpen Then
                    Await HandleOpenPositionAsync(slot, tf)
                End If
            Next

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
                End Sub)
        End Function

        Private Async Function ScanWatchlistAsync(tf As BarTimeframe) As Task(Of Dictionary(Of Integer, IList(Of MarketBar)))
            Dim cache As New Dictionary(Of Integer, IList(Of MarketBar))
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
                cache(i) = bars
                _logger.LogInformation("ST+ ScanWatchlist [{Contract}] cached {Count} bars.", contractId, bars.Count)

                Dim highs   = bars.Select(Function(b) b.High).ToList()
                Dim lows    = bars.Select(Function(b) b.Low).ToList()
                Dim closes  = bars.Select(Function(b) b.Close).ToList()

                Dim st      = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmi     = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim n       = bars.Count - 1
                Dim stDir   = st.Direction(n)
                Dim adxVal  = dmi.ADX(n)
                Dim plusDi  = dmi.PlusDI(n)
                Dim minusDi = dmi.MinusDI(n)

                Dim arrow As String
                Dim signal As String
                Dim rowColor As Brush
                Dim strength As String
                Dim signalReason As String
                Dim isLongSignal  As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
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
                    strength = String.Format("ADX:{0:D2} Strong", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Strong uptrend — bot may open up to 3 slots.",
                                  If(signal = "BEAR", "Strong downtrend — bot may open up to 3 slots.",
                                     "Strong trend forming — waiting for direction alignment."))
                ElseIf adxVal >= Config.AdxModerateThreshold Then
                    strength = String.Format("ADX:{0:D2} Moderate", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Moderate uptrend — bot may open up to 2 slots.",
                                  If(signal = "BEAR", "Moderate downtrend — bot may open up to 2 slots.",
                                     "Trending — waiting for +DI/-DI to align with SuperTrend."))
                ElseIf adxVal >= Config.AdxWeakThreshold Then
                    strength = String.Format("ADX:{0:D2} Active", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Uptrend active — bot may open 1 slot.",
                                  If(signal = "BEAR", "Downtrend active — bot may open 1 slot.",
                                     "Trending — waiting for +DI/-DI to align with SuperTrend."))
                ElseIf adxVal >= 15 Then
                    strength = String.Format("ADX:{0:D2} Weak", CInt(adxVal))
                    signalReason = "Trend is weak — watching for momentum to build before entering."
                Else
                    strength = String.Format("ADX:{0:D2} Chop", CInt(adxVal))
                    signalReason = "Market is choppy with no clear trend — standing aside to avoid false signals."
                End If

                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                Dim diStr As String = If(Single.IsNaN(plusDi) OrElse Single.IsNaN(minusDi),
                                        "+DI:-- -DI:--",
                                        String.Format("+DI:{0:D2} -DI:{1:D2}", CInt(plusDi), CInt(minusDi)))

                If _useEarlyMode Then
                    Dim atr14 = TechnicalIndicators.ATR(highs, lows, closes, period:=14)
                    Dim atrN  = If(atr14 IsNot Nothing AndAlso atr14.Length > n, CDec(atr14(n)), 0D)
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
                        signal       = "EARLY"
                        rowColor     = Brushes.Goldenrod
                        signalReason = "All early signals aligned — potential reversal imminent, preparing to enter."
                    ElseIf sigsCount >= 3 Then
                        signal       = "WATCH"
                        rowColor     = Brushes.DimGray
                        signalReason = String.Format("{0}/4 early signals met — watching for the final trigger.", sigsCount)
                    End If
                End If

                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.Arrow         = arrow
                        wRow.AdxDisplay    = adxStr
                        wRow.Signal        = signal
                        wRow.RowColor      = rowColor
                        wRow.TrendStrength = strength
                        wRow.SignalReason  = signalReason
                        wRow.DiDisplay     = diStr
                    End Sub)
            Next
            Return cache
        End Function

        Private Async Sub FlashStatusAsync()
            StatusBackground = New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))
            Await Task.Delay(400)
            StatusBackground = Brushes.Transparent
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
        ''' Confirmed-mode entry: ADX band determines target slot count.
        ''' ADX 25-39 → 1 slot, 40-59 → 2 slots, 60+ → 3 slots.
        ''' SlotManager enforces all rules (bar gate, counter-trend, Exiting health).
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
                    StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H00))
                End Sub)
                Return
            End If

            Dim bestContractId As String      = Nothing
            Dim bestSide As String            = Nothing
            Dim bestStLine As Decimal         = 0D
            Dim bestLastClose As Decimal      = 0D
            Dim bestAdxVal As Single          = 0F
            Dim bestBarTime As DateTimeOffset = DateTimeOffset.MinValue

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim bars As IList(Of MarketBar) = Nothing
                If Not barCache.TryGetValue(i, bars) OrElse bars Is Nothing OrElse bars.Count < 14 Then
                    Continue For
                End If

                ' Skip instruments whose contract ID failed to resolve — trading them risks
                ' using a stale/wrong contract ID (e.g. wrong expiry month).
                If _contractResolver.FailedSymbols.Contains(contractId, StringComparer.OrdinalIgnoreCase) Then
                    _logger.LogWarning("ST+ EvaluateSlotEntries [{Contract}] SKIP — contract resolution failed.", contractId)
                    Continue For
                End If

                Dim highs   = bars.Select(Function(b) b.High).ToList()
                Dim lows    = bars.Select(Function(b) b.Low).ToList()
                Dim closes  = bars.Select(Function(b) b.Close).ToList()
                Dim n = bars.Count - 1

                Dim st  = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim stDir   = st.Direction(n)
                Dim stLine  = CDec(st.Line(n))
                Dim adxVal  = dmi.ADX(n)
                Dim plusDi  = dmi.PlusDI(n)
                Dim minusDi = dmi.MinusDI(n)

                Dim prevDir As Single = 0F
                _prevStDirByInstrument.TryGetValue(contractId, prevDir)
                Dim isFlip As Boolean = prevDir <> 0F AndAlso stDir <> prevDir AndAlso stDir <> 0F
                _prevStDirByInstrument(contractId) = stDir

                Dim isLong  As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
                Dim isShort As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso minusDi > plusDi
                Dim isActive As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= Config.AdxWeakThreshold
                Dim isFavourable As Boolean = (isLong OrElse isShort) AndAlso (isFlip OrElse isActive)

                ' Re-entry cooldown: skip this instrument if it was released within the last bar duration.
                If isFavourable AndAlso Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = contractId) Then
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

                _logger.LogInformation(
                    "ST+ [{Contract}] stDir={StDir} ADX={Adx:F1} +DI={PlusDI:F1} -DI={MinusDI:F1} " &
                    "isLong={IsLong} isShort={IsShort} isFlip={IsFlip} isActive={IsActive} isFavourable={IsFav}",
                    contractId, stDir, adxVal, plusDi, minusDi, isLong, isShort, isFlip, isActive, isFavourable)

                If Not isFavourable Then
                    Dim adxStr2 = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                    UpdateSlotSymbolRows(i, "--", adxStr2, "flat", Brushes.White)
                    Continue For
                End If

                Dim side = If(isLong, "Buy", "Sell")
                Dim barTime = bars(n).Timestamp
                Dim adxStr = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                Dim sigLabel = If(isLong, "LONG", "SHORT")
                Dim sigColor As Brush = If(isLong, Brushes.LimeGreen, Brushes.Red)
                UpdateSlotSymbolRows(i, If(isLong, "UP", "DN"), adxStr, sigLabel, sigColor)

                Dim hasOpenSlot = _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = contractId)
                If bestContractId Is Nothing OrElse
                   hasOpenSlot OrElse
                   (Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = bestContractId) AndAlso adxVal > bestAdxVal) Then
                    bestContractId = contractId
                    bestSide       = side
                    bestStLine     = stLine
                    bestLastClose  = CDec(closes(n))
                    bestAdxVal     = adxVal
                    bestBarTime    = barTime
                End If
            Next

            If bestContractId Is Nothing Then
                _logger.LogInformation("ST+ EvaluateSlotEntries — no favourable candidate found this tick.")
                Return
            End If

            _logger.LogInformation("ST+ Best candidate: {Contract} side={Side} ADX={Adx:F1} barTime={BarTime}",
                                   bestContractId, bestSide, bestAdxVal, bestBarTime)

            ' Guard: verify no live position already exists on the exchange for this instrument
            ' UNLESS we already have an in-memory slot open for it (scale-in path).
            ' This prevents re-entry after ReleaseSlotAsync clears the slot while the real
            ' position is still open, but does not block legitimate scale-ins.
            Dim hasInMemorySlot As Boolean = _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.Instrument = bestContractId)
            If Not hasInMemorySlot Then
                Dim guardAccId As Long = If(_selectedAccount IsNot Nothing, _selectedAccount.Id, 0)
                If guardAccId <> 0 Then
                    Try
                        Dim liveCheck = Await _orderService.GetLivePositionSnapshotAsync(guardAccId, bestContractId)
                        If liveCheck IsNot Nothing Then
                            _logger.LogInformation("ST+ EvaluateSlotEntries — live position still open on exchange for {Contract} (units={Units}), skipping re-entry.",
                                                   bestContractId, liveCheck.Units)
                            Return
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ live-position guard check failed for {Contract} — proceeding with caution", bestContractId)
                    End Try
                End If
            End If

            Dim opened = _slotManager.TryOpenSlot(bestContractId, bestSide, bestAdxVal, bestBarTime, bestStLine, bestLastClose)
            If opened IsNot Nothing Then
                _logger.LogInformation("ST+ SlotManager opened slot {Idx} for {Contract} {Side} ADX={Adx:F1}",
                                       opened.SlotIndex, bestContractId, bestSide, bestAdxVal)
                Await FireEntryAsync(opened, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)
            Else
                _logger.LogInformation("ST+ SlotManager blocked new slot (openCount={Open}, target={Target})",
                                       _slotManager.OpenSlotCount, _slotManager.TargetSlotCount(bestAdxVal))
            End If
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
                    StatusBackground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H00))
                End Sub)
                Return
            End If

            Dim bestContractId As String      = Nothing
            Dim bestSide As String            = Nothing
            Dim bestStLine As Decimal         = 0D
            Dim bestLastClose As Decimal      = 0D
            Dim bestAdxVal As Single          = 0F
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

                Dim highs15  = bars15.Select(Function(b) b.High).ToList()
                Dim lows15   = bars15.Select(Function(b) b.Low).ToList()
                Dim closes15 = bars15.Select(Function(b) b.Close).ToList()
                Dim highs5   = bars5.Select(Function(b) b.High).ToList()
                Dim lows5    = bars5.Select(Function(b) b.Low).ToList()
                Dim closes5  = bars5.Select(Function(b) b.Close).ToList()

                Dim st15  = TechnicalIndicators.SuperTrend(highs15, lows15, closes15, period:=10, multiplier:=_stMultiplier)
                Dim dmi   = TechnicalIndicators.DMI(highs15, lows15, closes15, period:=14)
                Dim st5   = TechnicalIndicators.SuperTrend(highs5, lows5, closes5, period:=10, multiplier:=_stMultiplier)
                Dim n15   = bars15.Count - 1
                Dim n5    = bars5.Count - 1

                Dim stDir15  = st15.Direction(n15)
                Dim stLine15 = CDec(st15.Line(n15))
                Dim adxVal   = dmi.ADX(n15)
                Dim plusDi   = dmi.PlusDI(n15)
                Dim minusDi  = dmi.MinusDI(n15)
                Dim stDir5   = st5.Direction(n5)

                Dim lastClose15 = closes15(n15)
                Dim dist = Math.Abs(lastClose15 - stLine15)
                Dim atr14 = TechnicalIndicators.ATR(highs15, lows15, closes15, period:=14)
                Dim atrN  = If(atr14 IsNot Nothing AndAlso atr14.Length > n15, CDec(atr14(n15)), 0D)

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

                Dim side As String    = If(anticipatedLong, "Buy", "Sell")
                Dim adxStr As String  = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
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
                    bestSide       = side
                    bestStLine     = stLine15
                    bestLastClose  = CDec(lastClose15)
                    bestAdxVal     = adxVal
                    bestBarTime    = barTime
                End If
            Next

            If bestContractId Is Nothing Then Return

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
                        row.Arrow      = arrow
                        row.AdxDisplay = adxDisplay
                        row.Signal     = signal
                        row.RowColor   = color
                    End Sub)
            Next
        End Sub

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

            ' Keep the 15-second scan interval — do NOT accelerate to 2 s here.
            ' Accelerating caused rapid-fire re-entries when a bracket filled quickly.

            Dim oSide As OrderSide = If(side = "Buy", OrderSide.Buy, OrderSide.Sell)
            Dim fc As FavouriteContract = FavouriteContracts.TryGetBySymbolResolved(contractId, _contractResolver)
            Dim stopTicks As Integer? = Nothing
            Dim tpTicks As Integer? = Nothing
            If fc IsNot Nothing AndAlso fc.PxTickSize > 0D Then
                Dim rawDist As Decimal = Math.Abs(lastClose - stLine)
                Dim rawTicks As Integer = CInt(Math.Round(rawDist / fc.PxTickSize))
                Dim minTicks As Integer = 1
                If fc.PxMinStopDollars > 0D AndAlso fc.PxTickValue > 0D Then
                    minTicks = CInt(Math.Ceiling(fc.PxMinStopDollars / fc.PxTickValue))
                End If
                stopTicks = Math.Max(rawTicks, minTicks)
                Dim tpMult As Decimal = ParseTpMultiple()
                If tpMult > 0D Then
                    tpTicks = CInt(Math.Round(stopTicks.Value * tpMult))
                End If
            End If
            _logger.LogInformation("ST+ bracket for {Contract}: SL={SL} ticks, TP={TP} ticks (lastClose={Close}, stLine={St})",
                                   contractId, If(stopTicks.HasValue, stopTicks.Value.ToString(), "none"),
                                   If(tpTicks.HasValue, tpTicks.Value.ToString(), "none"),
                                   lastClose, stLine)

            ' Only the primary slot (SlotIndex = 0) places a bracketed order.
            ' Scale-in slots add contracts without a bracket so TopStepX does not
            ' create independent SL/TP orders that conflict with the primary bracket.
            Dim isPrimary As Boolean = slot.SlotIndex = 0 OrElse
                Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso s.SlotIndex < slot.SlotIndex)
            Dim order As New Order With {
                .AccountId               = slot.AccountId,
                .ContractId             = contractId,
                .Side                   = oSide,
                .Quantity               = slot.Contracts,
                .OrderType              = OrderType.Market,
                .InitialStopTicks       = If(isPrimary, stopTicks, Nothing),
                .InitialTakeProfitTicks = If(isPrimary, tpTicks, Nothing)
            }
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
                _slotManager.CloseSlot(slot.SlotIndex)
                If Not _slotManager.Slots.Any(Function(s) s.IsOpen) Then
                    SyncLock _timerLock
                        _timer?.Change(15000, 15000)
                    End SyncLock
                End If
                Return
            End If

            slot.StopPrice    = stLine
            slot.EntryTime    = DateTime.Now
            slot.EntryBarTime = barTime
            slot.MissCount    = 0
            slot.PositionId   = placed.ExternalPositionId
            slot.EntryPrice   = 0D
            slot.TakeProfitPrice = 0D
            slot.StopPhase    = StopPhase.Initial
            slot.InitialRisk  = 0D  ' computed once EntryPrice is confirmed
            slot.EntryAtr     = 0D  ' set from bar data below

            ' Capture entry ATR from the bars that were used to fire the entry
            Try
                Dim entryBars = Await _barService.GetLiveBarsAsync(contractId, MapTimeframe(_selectedTimeframe), BarsToFetch)
                If entryBars IsNot Nothing AndAlso entryBars.Count >= 14 Then
                    Dim eHighs  = entryBars.Select(Function(b) b.High).ToList()
                    Dim eLows   = entryBars.Select(Function(b) b.Low).ToList()
                    Dim eCloses = entryBars.Select(Function(b) b.Close).ToList()
                    Dim atr14   = TechnicalIndicators.ATR(eHighs, eLows, eCloses, period:=14)
                    Dim eN      = entryBars.Count - 1
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
                        box.HasPosition = True
                        UpdatePositionDisplay(box, slot, 0D)
                    End If
                End Sub)
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
                    Await ReleaseSlotAsync(slot)
                    Return
                End If
            Else
                slot.MissCount = 0
                If snapshot.OpenRate <> 0D AndAlso slot.EntryPrice = 0D Then
                    slot.EntryPrice = snapshot.OpenRate
                    If slot.TakeProfitPrice = 0D Then
                        Dim fc2 = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
                        If fc2 IsNot Nothing AndAlso fc2.PxTickSize > 0D Then
                            Dim rawDist = Math.Abs(slot.EntryPrice - slot.StopPrice)
                            Dim stopTicks2 = CInt(Math.Round(rawDist / fc2.PxTickSize))
                            Dim tpMult2 = ParseTpMultiple()
                            If tpMult2 > 0D Then
                                Dim tpTicks2 = CInt(Math.Round(CDec(stopTicks2) * tpMult2))
                                slot.TakeProfitPrice = If(slot.Side = "Buy",
                                    slot.EntryPrice + tpTicks2 * fc2.PxTickSize,
                                    slot.EntryPrice - tpTicks2 * fc2.PxTickSize)
                            End If
                        End If
                    End If
                End If

                Dim latestPnl = snapshot.UnrealizedPnlUsd
                slot.UnrealizedPnl = latestPnl
                Dim boxForDisplay = BoxForSlot(slot)
                Application.Current?.Dispatcher?.Invoke(Sub()
                    If boxForDisplay IsNot Nothing Then UpdatePositionDisplay(boxForDisplay, slot, latestPnl)
                End Sub)

                Dim bars As IList(Of MarketBar)
                Try
                    bars = Await _barService.GetLiveBarsAsync(slot.Instrument, tf, BarsToFetch)
                Catch
                    Return
                End Try
                If bars Is Nothing OrElse bars.Count < 14 Then Return

                Dim highs  = bars.Select(Function(b) b.High).ToList()
                Dim lows   = bars.Select(Function(b) b.Low).ToList()
                Dim closes = bars.Select(Function(b) b.Close).ToList()
                Dim n = bars.Count - 1
                Dim st         = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmiForExit = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim atr14Exit  = TechnicalIndicators.ATR(highs, lows, closes, period:=14)
                Dim stLine     = CDec(st.Line(n))

                ' Compute VWAP from bar volumes (anchored at start of cached series)
                Dim volumes    = bars.Select(Function(b) b.Volume).ToList()
                Dim vwapExit   = TechnicalIndicators.VWAP(highs, lows, closes, volumes)

                ' Compute RSI-14 from bar closes
                Dim rsiExit    = TechnicalIndicators.RSI(closes, 14)

                ' Set InitialRisk once EntryPrice is confirmed (after first snapshot)
                If slot.InitialRisk = 0D AndAlso slot.EntryPrice <> 0D AndAlso slot.StopPrice <> 0D Then
                    slot.InitialRisk = Math.Abs(slot.EntryPrice - slot.StopPrice)
                End If

                ' ── Profit-milestone partial exits ────────────────────────────────
                ' At 2R: reduce Slot 2 (index 1) to 0 contracts via partial close.
                ' At 3R: reduce Slot 1 (index 0) to 0 contracts via partial close.
                ' Slot 0 continues as a free-ride until E1 or phase exit.
                If slot.InitialRisk > 0D AndAlso slot.EntryPrice <> 0D Then
                    Dim profit = If(slot.Side = "Buy",
                                    CDec(closes(n)) - slot.EntryPrice,
                                    slot.EntryPrice - CDec(closes(n)))
                    Dim R = slot.InitialRisk
                    ' 2R milestone: partial-close Slot 2 (index 1)
                    If profit >= 2D * R AndAlso slot.SlotIndex = 1 AndAlso Not slot.MilestoneFlag Then
                        slot.MilestoneFlag = True
                        Dim ok = Await _orderService.PartialCloseContractAsync(
                            slot.AccountId, slot.Instrument, slot.Contracts)
                        If ok Then
                            _logger.LogInformation(
                                "ST+ Milestone 2R: partial-closed Slot {Idx} ({Size} contracts) on {Contract}",
                                slot.SlotIndex, slot.Contracts, slot.Instrument)
                        Else
                            _logger.LogWarning(
                                "ST+ Milestone 2R: partial close failed for Slot {Idx} on {Contract} — falling back to full close",
                                slot.SlotIndex, slot.Instrument)
                            Await ReleaseSlotAsync(slot)
                            Return
                        End If
                        Await ReleaseSlotAsync(slot)
                        Return
                    End If
                    ' 3R milestone: partial-close Slot 1 (index 0)
                    If profit >= 3D * R AndAlso slot.SlotIndex = 0 AndAlso Not slot.MilestoneFlag Then
                        slot.MilestoneFlag = True
                        Dim ok = Await _orderService.PartialCloseContractAsync(
                            slot.AccountId, slot.Instrument, slot.Contracts)
                        If ok Then
                            _logger.LogInformation(
                                "ST+ Milestone 3R: partial-closed Slot {Idx} ({Size} contracts) on {Contract}",
                                slot.SlotIndex, slot.Contracts, slot.Instrument)
                        Else
                            _logger.LogWarning(
                                "ST+ Milestone 3R: partial close failed for Slot {Idx} on {Contract} — falling back to full close",
                                slot.SlotIndex, slot.Instrument)
                            Await ReleaseSlotAsync(slot)
                            Return
                        End If
                        Await ReleaseSlotAsync(slot)
                        Return
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

                If exitEval.ImmediateExit OrElse slot.Health = SlotHealth.Exiting Then
                    Await ReleaseSlotAsync(slot)
                    Return
                End If

                ' Advance phased stop (ratchet-only).
                ' Only the primary slot — the lowest-indexed open slot for this instrument —
                ' is allowed to call EditPositionSlTpAsync.  Scale-in slots update their
                ' local state and display but do NOT touch the exchange bracket, because
                ' all slots share the same underlying TopStepX position and racing edits
                ' would use different InitialRisk baselines and could widen the stop.
                '
                ' SuperTrend+ uses a 6-phase configurable ladder via ComputePhasedStop overload.
                ' This overwrites the generic phased-stop produced by ExitSignalEngine.Evaluate
                ' and keeps the logic isolated to this strategy only.
                Dim stLineForPhase = If(Not Single.IsNaN(st.Line(n)), CDec(st.Line(n)), slot.StopPrice)
                Dim atrForPhase    = If(n < atr14Exit.Length AndAlso Not Single.IsNaN(atr14Exit(n)), CDec(atr14Exit(n)), 0D)
                Dim stPhasedResult = _exitEngine.ComputePhasedStop(slot, CDec(closes(n)), stLineForPhase, atrForPhase, Config)
                Dim newStop = stPhasedResult.NewStop
                slot.StopPhase = stPhasedResult.Phase

                Dim isPrimaryForEdit As Boolean =
                    Not _slotManager.Slots.Any(Function(s) s.IsOpen AndAlso
                                               s.Instrument = slot.Instrument AndAlso
                                               s.SlotIndex < slot.SlotIndex)

                If isPrimaryForEdit AndAlso slot.PositionId.HasValue AndAlso newStop <> slot.StopPrice Then
                    Dim tpArg As Decimal? = If(slot.TakeProfitPrice <> 0D, CType(slot.TakeProfitPrice, Decimal?), Nothing)
                    Try
                        Await _orderService.EditPositionSlTpAsync(slot.PositionId.Value, newStop, tpArg)
                        _logger.LogInformation("ST+ SL phase={Phase} trail->{Price} (TP={Tp}) for [Slot {Idx}] on {Contract}",
                                               slot.StopPhase, newStop,
                                               If(tpArg.HasValue, tpArg.Value.ToString("F2"), "none"),
                                               slot.SlotIndex, slot.Instrument)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ EditPositionSlTpAsync failed for [Slot {Idx}] on {Contract}", slot.SlotIndex, slot.Instrument)
                    End Try
                ElseIf Not isPrimaryForEdit Then
                    _logger.LogInformation("ST+ SL ratchet deferred for scale-in [Slot {Idx}] on {Contract} — primary slot owns bracket",
                                           slot.SlotIndex, slot.Instrument)
                End If

                If newStop <> slot.StopPrice Then
                    slot.StopPrice = newStop
                    Dim boxForTrail = BoxForSlot(slot)
                    Application.Current?.Dispatcher?.Invoke(Sub()
                        If boxForTrail IsNot Nothing Then UpdatePositionDisplay(boxForTrail, slot, latestPnl)
                    End Sub)
                End If
            End If
        End Function

        Private Async Function ReleaseSlotAsync(slot As PositionSlot) As Task
            ' ── Close the live position on TopStepX before forgetting the slot ──
            ' Without this, the real position stays open on the exchange while the
            ' slot clears in-memory, causing EvaluateSlotEntriesAsync to re-enter the
            ' same instrument on the next tick (BUG-35).
            _releasedThisTick = True  ' block same-tick re-entry
            ' Record cooldown: re-entry on this instrument is blocked for at least one full bar
            If Not String.IsNullOrEmpty(slot.Instrument) Then
                _reEntryCooldown(slot.Instrument) = DateTimeOffset.UtcNow
            End If
            If slot.AccountId <> 0 AndAlso Not String.IsNullOrEmpty(slot.Instrument) Then
                Try
                    If slot.PositionId.HasValue Then
                        Dim ok = Await _orderService.CancelOrderAsync(slot.PositionId.Value)
                        _logger.LogInformation("ST+ ReleaseSlot [Slot {Idx}] close {Contract} posId={PosId}: ok={Ok}",
                                               slot.SlotIndex, slot.Instrument, slot.PositionId.Value, ok)
                    Else
                        Await _orderService.FlattenContractAsync(slot.AccountId, slot.Instrument)
                        _logger.LogInformation("ST+ ReleaseSlot [Slot {Idx}] flatten {Contract} (no positionId)",
                                               slot.SlotIndex, slot.Instrument)
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ ReleaseSlot [Slot {Idx}] close order failed for {Contract} — slot cleared anyway",
                                       slot.SlotIndex, slot.Instrument)
                End Try
            End If

            Dim box = BoxForSlot(slot)
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    If box IsNot Nothing Then
                        box.HasPosition     = False
                        box.PositionDisplay = String.Empty
                        box.StopPhaseLabel  = String.Empty
                    End If
                End Sub)
            _slotManager.CloseSlot(slot.SlotIndex)
            If Not _slotManager.Slots.Any(Function(s) s.IsOpen) Then
                SyncLock _timerLock
                    _timer?.Change(15000, 15000)
                End SyncLock
            End If
        End Function

        Private Sub UpdatePositionDisplay(box As SlotBoxVm, slot As PositionSlot, pnl As Decimal)
            Dim sideLbl As String = If(slot.Side = "Buy", "LONG", "SHORT")
            Dim idx   As Integer  = Array.IndexOf(Instruments, slot.Instrument)
            Dim label As String   = If(idx >= 0, InstrumentLabels(idx), slot.Instrument)
            Dim entry As String   = If(slot.EntryPrice = 0D, "--", slot.EntryPrice.ToString("F2"))
            Dim sl    As String   = If(slot.StopPrice = 0D, "--", slot.StopPrice.ToString("F2"))
            Dim tp    As String   = If(slot.TakeProfitPrice = 0D, "flip", slot.TakeProfitPrice.ToString("F2"))
            Dim sign  As String   = If(pnl >= 0, "+", "")
            box.PositionDisplay =
                String.Format("{0}  {1}  @ {2}", sideLbl, label, entry) & Environment.NewLine &
                String.Format("SL: {0}  TP: {1}", sl, tp) & Environment.NewLine &
                String.Format("Entry: {0:HH:mm}  |  P&L: {1}{2:F2}$", slot.EntryTime, sign, pnl) & Environment.NewLine &
                slot.EntryReason
            box.PnlBrush = If(pnl >= 0, Brushes.LimeGreen, Brushes.Red)
            box.StopPhaseLabel = slot.StopPhase.ToString()
        End Sub

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

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                SyncLock _timerLock
                    _timer?.Dispose()
                    _timer = Nothing
                End SyncLock
            End If
        End Sub

    End Class

End Namespace
