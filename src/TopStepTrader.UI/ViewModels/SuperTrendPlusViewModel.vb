Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Threading
Imports System.Windows
Imports System.Windows.Media
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Market
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

    Public Class PersonaBoxVm
        Inherits ViewModelBase

        Public ReadOnly Symbols As New ObservableCollection(Of SymbolRowVm)
        Public Property PersonaName As String = String.Empty

        Private _isPaused As Boolean = False
        Public Property IsPaused As Boolean
            Get
                Return _isPaused
            End Get
            Set(value As Boolean)
                If SetProperty(_isPaused, value) Then
                    NotifyPropertyChanged(NameOf(BoxOpacity))
                    NotifyPropertyChanged(NameOf(PausedBadgeVisibility))
                End If
            End Set
        End Property

        Public ReadOnly Property BoxOpacity As Double
            Get
                Return If(_isPaused, 0.4, 1.0)
            End Get
        End Property

        Public ReadOnly Property PausedBadgeVisibility As Visibility
            Get
                Return If(_isPaused, Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        Private _hasPosition As Boolean = False
        Public Property HasPosition As Boolean
            Get
                Return _hasPosition
            End Get
            Set(value As Boolean)
                If SetProperty(_hasPosition, value) Then
                    NotifyPropertyChanged(NameOf(PositionVisibility))
                End If
            End Set
        End Property

        Public ReadOnly Property PositionVisibility As Visibility
            Get
                Return If(_hasPosition, Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        Private _positionDisplay As String = String.Empty
        Public Property PositionDisplay As String
            Get
                Return _positionDisplay
            End Get
            Set(value As String)
                SetProperty(_positionDisplay, value)
            End Set
        End Property

        Private _pnlBrush As Brush = Brushes.White
        Public Property PnlBrush As Brush
            Get
                Return _pnlBrush
            End Get
            Set(value As Brush)
                SetProperty(_pnlBrush, value)
            End Set
        End Property

        Friend Profile As PersonaProfile
        Friend ScaleInCount As Integer = 0
        Friend CurrentStLine As Decimal = 0D
        Friend EntryPrice As Decimal = 0D
        Friend EntryInstrument As String = String.Empty
        Friend EntrySide As String = String.Empty
        Friend EntryTime As DateTime = DateTime.MinValue
        Friend PositionId As Long? = Nothing
        Friend AccountId As Long = 0
        Friend MissCount As Integer = 0
        Friend TpPrice As Decimal = 0D
        Friend LastScaleInBarTime As DateTimeOffset = DateTimeOffset.MinValue

    End Class

    Friend Class ApproachState
        Friend LastStDir As Integer = 0
        Friend Distances As New Queue(Of Decimal)
    End Class

    Public Class SuperTrendPlusViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private Shared ReadOnly Instruments As String() = {"MCLE", "MGC", "MES", "MNQ", "M6E"}
        Private Shared ReadOnly InstrumentLabels As String() = {"Oil", "Gold", "S&P 500", "NQ", "EUR/USD"}
        Private Const BarsToFetch As Integer = 60

        Public ReadOnly Property WatchlistItems As New ObservableCollection(Of WatchlistRowVm)
        Private Const SyncMissThreshold As Integer = 3

        Private ReadOnly _barService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _logger As ILogger(Of SuperTrendPlusViewModel)

        Private _timer As Timer
        Private ReadOnly _timerLock As New Object()
        Private _lockOwner As String = Nothing
        Private _disposed As Boolean = False

        Private ReadOnly _approachHistory As New Dictionary(Of String, ApproachState)
        Private _useEarlyMode As Boolean = False
        Public Property UseEarlyMode As Boolean
            Get
                Return _useEarlyMode
            End Get
            Set(value As Boolean)
                SetProperty(_useEarlyMode, value)
            End Set
        End Property

        Public ReadOnly Property LewisBox As PersonaBoxVm = New PersonaBoxVm() With {.PersonaName = "Lewis (Averse)"}
        Public ReadOnly Property DamianBox As PersonaBoxVm = New PersonaBoxVm() With {.PersonaName = "Damian (Moderate)"}
        Public ReadOnly Property JoeBox As PersonaBoxVm = New PersonaBoxVm() With {.PersonaName = "Joe (Aggressive)"}

        ' -- Accounts --------------------------------------------------------------
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

        ' -- How-it-works panel expand/collapse ------------------------------------
        Private _isHowItWorksExpanded As Boolean = True
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
                       logger As ILogger(Of SuperTrendPlusViewModel))
            _barService     = barService
            _orderService   = orderService
            _session        = session
            _personaService = personaService
            _accountService = accountService
            _logger         = logger
            StartStopCommand = New RelayCommand(AddressOf OnStartStop)
            For i = 0 To Instruments.Length - 1
                WatchlistItems.Add(New WatchlistRowVm() With {
                    .Symbol = Instruments(i),
                    .Label  = InstrumentLabels(i)
                })
            Next
            For Each box In AllBoxes()
                For i = 0 To Instruments.Length - 1
                    box.Symbols.Add(New SymbolRowVm() With {.Symbol = InstrumentLabels(i)})
                Next
            Next
        End Sub

        Private Function AllBoxes() As PersonaBoxVm()
            Return {LewisBox, DamianBox, JoeBox}
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
            LewisBox.Profile  = _personaService.GetProfile("Lewis")
            DamianBox.Profile = _personaService.GetProfile("Damian")
            JoeBox.Profile    = _personaService.GetProfile("Joe")
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
            _lockOwner = Nothing
            For Each wRow In WatchlistItems
                wRow.Arrow         = "–"
                wRow.AdxDisplay    = "ADX:–"
                wRow.Signal        = "–"
                wRow.TrendStrength = ""
                wRow.RowColor      = Brushes.Gray
            Next
            For Each box In AllBoxes()
                box.IsPaused        = False
                box.HasPosition     = False
                box.PositionDisplay = String.Empty
                box.ScaleInCount    = 0
                box.CurrentStLine   = 0D
                box.PositionId      = Nothing
                box.MissCount       = 0
                box.TpPrice         = 0D
                box.LastScaleInBarTime = DateTimeOffset.MinValue
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
            Catch
            End Try
        End Sub

        Private Async Function DoTickAsync() As Task
            Dim tf = MapTimeframe(_selectedTimeframe)
            Await ScanWatchlistAsync(tf)
            If _lockOwner IsNot Nothing Then
                Dim ownerBox = AllBoxes().FirstOrDefault(Function(b) b.PersonaName.StartsWith(_lockOwner))
                If ownerBox IsNot Nothing Then
                    Await HandleOpenPositionAsync(ownerBox, tf)
                End If
            End If
            If _lockOwner Is Nothing Then
                If _useEarlyMode Then
                    Await EvaluateEarlyPersonasAsync(tf)
                Else
                    Await EvaluatePersonasAsync(tf)
                End If
            End If
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    StatusText = String.Format("SuperTrend+ monitoring - {0} TimeFrame - updated {1:HH:mm:ss}", _selectedTimeframe, DateTime.Now)
                    FlashStatusAsync()
                End Sub)
        End Function

        Private Async Function ScanWatchlistAsync(tf As BarTimeframe) As Task
            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim wRow = WatchlistItems(i)
                Dim bars As IList(Of MarketBar)
                Try
                    bars = Await _barService.GetLiveBarsAsync(contractId, tf, BarsToFetch)
                Catch
                    Continue For
                End Try
                If bars Is Nothing OrElse bars.Count < 15 Then Continue For

                Dim tfMinutesScan As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                If _selectedTimeframe.EndsWith("hr") Then tfMinutesScan *= 60
                If bars.Count > 1 Then
                    Dim lastBarAgeScan = (DateTime.UtcNow - bars.Last().Timestamp).TotalMinutes
                    If lastBarAgeScan < tfMinutesScan Then
                        bars = bars.Take(bars.Count - 1).ToList()
                    End If
                End If

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
                Dim isLongSignal  As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
                Dim isShortSignal As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso minusDi > plusDi
                If isLongSignal Then
                    arrow = "?" : signal = "BULL" : rowColor = Brushes.LimeGreen
                ElseIf isShortSignal Then
                    arrow = "?" : signal = "BEAR" : rowColor = Brushes.Red
                ElseIf stDir > 0 Then
                    arrow = "?" : signal = "WAIT" : rowColor = Brushes.DarkGoldenrod
                ElseIf stDir < 0 Then
                    arrow = "?" : signal = "WAIT" : rowColor = Brushes.DarkGoldenrod
                Else
                    arrow = "–" : signal = "flat" : rowColor = Brushes.Gray
                End If

                If Single.IsNaN(adxVal) Then
                    strength = "ADX: –"
                ElseIf adxVal >= 40 Then
                    strength = String.Format("ADX:{0:D2} ???", CInt(adxVal))
                ElseIf adxVal >= 25 Then
                    strength = String.Format("ADX:{0:D2} ???", CInt(adxVal))
                ElseIf adxVal >= 15 Then
                    strength = String.Format("ADX:{0:D2} ???", CInt(adxVal))
                Else
                    strength = String.Format("ADX:{0:D2} ???", CInt(adxVal))
                End If

                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))

                ' Early mode: overlay EARLY/WATCH signals
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
                        signal   = "EARLY"
                        rowColor = Brushes.Goldenrod
                    ElseIf sigsCount >= 3 Then
                        signal   = "WATCH"
                        rowColor = Brushes.DimGray
                    End If
                End If

                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        wRow.Arrow         = arrow
                        wRow.AdxDisplay    = adxStr
                        wRow.Signal        = signal
                        wRow.RowColor      = rowColor
                        wRow.TrendStrength = strength
                    End Sub)
            Next
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

        Private Async Function EvaluateEarlyPersonasAsync(tf As BarTimeframe) As Task
            For Each box In AllBoxes()
                If box.IsPaused Then Continue For
                Await EvaluateEarlyPersonaAsync(box, tf)
                If _lockOwner IsNot Nothing Then Exit For
            Next
        End Function

        Private Async Function EvaluateEarlyPersonaAsync(box As PersonaBoxVm, tf As BarTimeframe) As Task
            If box.Profile Is Nothing Then Return
            Dim minAdx As Single = CSng(box.Profile.AdxThreshold)
            If minAdx <= 0 Then minAdx = 20.0F

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim row = box.Symbols(i)
                Dim bars15 As IList(Of MarketBar)
                Dim bars5 As IList(Of MarketBar)
                Try
                    bars15 = Await _barService.GetLiveBarsAsync(contractId, tf, BarsToFetch)
                Catch
                    Continue For
                End Try
                If bars15 Is Nothing OrElse bars15.Count < 15 Then Continue For
                Try
                    bars5 = Await _barService.GetLiveBarsAsync(contractId, BarTimeframe.FiveMinute, BarsToFetch)
                Catch
                    Continue For
                End Try
                If bars5 Is Nothing OrElse bars5.Count < 15 Then Continue For

                ' Strip forming bar from 15-min series
                Dim tfMinutes As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                If _selectedTimeframe.EndsWith("hr") Then tfMinutes *= 60
                If bars15.Count > 1 Then
                    Dim age = (DateTime.UtcNow - bars15.Last().Timestamp).TotalMinutes
                    If age < tfMinutes Then bars15 = bars15.Take(bars15.Count - 1).ToList()
                End If
                ' Strip forming bar from 5-min series
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

                Dim lastClose = closes15(n15)
                Dim dist = Math.Abs(lastClose - stLine15)
                Dim atr14 = TechnicalIndicators.ATR(highs15, lows15, closes15, period:=14)
                Dim atrN  = If(atr14 IsNot Nothing AndAlso atr14.Length > n15, CDec(atr14(n15)), 0D)

                Dim sig1 As Boolean = atrN > 0D AndAlso dist <= 1.5D * atrN
                Dim sig2 As Boolean = UpdateApproachHistory(contractId, stDir15, dist)
                Dim spreadDI As Single = Math.Abs(plusDi - minusDi)
                Dim anticipatedLong As Boolean = stDir15 < 0
                Dim sig3 As Boolean = If(anticipatedLong, plusDi > minusDi, minusDi > plusDi) OrElse spreadDI < 5
                Dim sig4 As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= minAdx
                Dim sig5 As Boolean = If(anticipatedLong, stDir5 > 0, stDir5 < 0)

                Dim earlySignal As Boolean = sig1 AndAlso sig2 AndAlso sig3 AndAlso sig4 AndAlso sig5
                Dim side As String = If(anticipatedLong, "Buy", "Sell")
                Dim arrow As String = If(anticipatedLong, "UP", "DN")
                Dim signal As String = If(earlySignal, "EARLY", "flat")
                Dim rowColor As Brush = If(earlySignal, Brushes.Goldenrod, Brushes.White)
                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))

                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        row.Arrow      = arrow
                        row.AdxDisplay = adxStr
                        row.Signal     = signal
                        row.RowColor   = rowColor
                    End Sub)

                If earlySignal AndAlso _lockOwner Is Nothing Then
                    Await FireEntryAsync(box, contractId, side, stLine15)
                    Return
                End If
            Next
        End Function

        Private Async Function EvaluatePersonasAsync(tf As BarTimeframe) As Task
            Dim allBoxes = Me.AllBoxes()
            Dim candidates As New List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single))
            For Each box In allBoxes
                If box.IsPaused Then Continue For
                Dim boxSignals = Await EvaluatePersonaAsync(box, tf)
                candidates.AddRange(boxSignals)
            Next
            If candidates.Any() Then
                ' Pick highest ADX; Joe (index 2) > Damian (index 1) > Lewis (index 0) as tiebreaker
                Dim priorityMap As New Dictionary(Of PersonaBoxVm, Integer)
                For idx = 0 To allBoxes.Length - 1
                    priorityMap(allBoxes(idx)) = idx
                Next
                Dim best = candidates _
                    .OrderByDescending(Function(c) c.Item5) _
                    .ThenByDescending(Function(c) If(priorityMap.ContainsKey(c.Item1), priorityMap(c.Item1), 0)) _
                    .First()
                Await FireEntryAsync(best.Item1, best.Item2, best.Item3, best.Item4)
            End If
        End Function

        Private Async Function EvaluatePersonaAsync(box As PersonaBoxVm, tf As BarTimeframe) As Task(Of List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single)))
            Dim results As New List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single))
            If box.Profile Is Nothing Then Return results
            Dim minAdx As Single = CSng(box.Profile.AdxThreshold)
            If minAdx <= 0 Then minAdx = 20.0F

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim row = box.Symbols(i)
                Dim bars As IList(Of MarketBar)
                Try
                    bars = Await _barService.GetLiveBarsAsync(contractId, tf, BarsToFetch)
                Catch
                    Continue For
                End Try
                If bars Is Nothing OrElse bars.Count < 15 Then Continue For

                Dim tfMinutesEval As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
                If _selectedTimeframe.EndsWith("hr") Then tfMinutesEval *= 60
                If bars.Count > 1 Then
                    Dim lastBarAgeEval = (DateTime.UtcNow - bars.Last().Timestamp).TotalMinutes
                    If lastBarAgeEval < tfMinutesEval Then
                        bars = bars.Take(bars.Count - 1).ToList()
                    End If
                End If

                Dim highs   = bars.Select(Function(b) b.High).ToList()
                Dim lows    = bars.Select(Function(b) b.Low).ToList()
                Dim closes  = bars.Select(Function(b) b.Close).ToList()

                Dim st  = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
                Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim n       = bars.Count - 1
                Dim stDir   = st.Direction(n)
                Dim stLine  = st.Line(n)
                Dim adxVal  = dmi.ADX(n)
                Dim plusDi  = dmi.PlusDI(n)
                Dim minusDi = dmi.MinusDI(n)

                Dim isLong  As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso adxVal >= minAdx AndAlso plusDi > minusDi
                Dim isShort As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso adxVal >= minAdx AndAlso minusDi > plusDi

                Dim arrow    As String
                Dim signal   As String
                Dim rowColor As Brush
                If isLong Then
                    arrow = "UP" : signal = "LONG" : rowColor = Brushes.LimeGreen
                ElseIf isShort Then
                    arrow = "DN" : signal = "SHORT" : rowColor = Brushes.Red
                Else
                    arrow = "--" : signal = "flat" : rowColor = Brushes.White
                End If

                Dim adxStr As String = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        row.Arrow      = arrow
                        row.AdxDisplay = adxStr
                        row.Signal     = signal
                        row.RowColor   = rowColor
                    End Sub)

                If isLong OrElse isShort Then
                    results.Add(Tuple.Create(box, contractId, If(isLong, "Buy", "Sell"), CDec(stLine), adxVal))
                End If
            Next
            Return results
        End Function

        Private Async Function FireEntryAsync(box As PersonaBoxVm,
                                              contractId As String,
                                              side As String,
                                              stLine As Decimal) As Task
            SyncLock _timerLock
                If _lockOwner IsNot Nothing Then Return
                _lockOwner = box.PersonaName.Split(" "c)(0)
            End SyncLock

            Dim accountId As Long = If(_selectedAccount IsNot Nothing,
                                       _selectedAccount.Id, 0)
            If accountId = 0 Then
                SyncLock _timerLock
                    _lockOwner = Nothing
                    _timer?.Change(15000, 15000)
                End SyncLock
                Application.Current?.Dispatcher?.Invoke(Sub()
                    For Each b In AllBoxes()
                        b.IsPaused = False
                    Next
                End Sub)
                Return
            End If
            box.AccountId = accountId

            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    For Each b In AllBoxes()
                        b.IsPaused = (b IsNot box)
                    Next
                End Sub)
            SyncLock _timerLock
                _timer?.Change(2000, 2000)
            End SyncLock

            Dim qty As Integer    = If(box.Profile IsNot Nothing, box.Profile.PositionSize, 1)
            Dim oSide As OrderSide = If(side = "Buy", OrderSide.Buy, OrderSide.Sell)
            Dim order As New Order With {
                .AccountId  = box.AccountId,
                .ContractId = contractId,
                .Side       = oSide,
                .Quantity   = qty,
                .OrderType  = OrderType.Market
            }
            Dim placed As Order = Nothing
            Try
                placed = Await _orderService.PlaceOrderAsync(order)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ PlaceOrderAsync failed for {Contract}", contractId)
            End Try

            ' Only show a position tile
            ' If placement failed, release the lock immediately so the tile stays blank.
            If placed Is Nothing Then
                SyncLock _timerLock
                    _lockOwner = Nothing
                    _timer?.Change(15000, 15000)
                End SyncLock
                Application.Current?.Dispatcher?.Invoke(
                    Sub()
                        For Each b In AllBoxes()
                            b.IsPaused = False
                        Next
                    End Sub)
                Return
            End If

            box.EntryPrice      = 0D
            box.EntryInstrument = contractId
            box.EntrySide       = side
            box.EntryTime       = DateTime.Now
            box.CurrentStLine   = stLine
            box.ScaleInCount    = 1
            box.MissCount       = 0
            box.PositionId      = placed.ExternalPositionId

            ' TpPrice will be computed in HandleOpenPositionAsync once the fill price is known via snapshot.
            box.TpPrice = 0D
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    box.HasPosition = True
                    UpdatePositionDisplay(box, 0D)
                End Sub)
        End Function

        Private Async Function HandleOpenPositionAsync(box As PersonaBoxVm, tf As BarTimeframe) As Task
            If box.AccountId = 0 AndAlso _session.SelectedAccount IsNot Nothing Then
                box.AccountId = _session.SelectedAccount.Id
            End If
            Dim snapshot As LivePositionSnapshot = Nothing
            Try
                snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                    box.AccountId, box.EntryInstrument, box.PositionId)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ GetLivePositionSnapshotAsync failed for {Box} on {Contract}", box.PersonaName, box.EntryInstrument)
            End Try

            If snapshot Is Nothing Then
                box.MissCount += 1
                If box.MissCount >= SyncMissThreshold Then
                    Await ReleasePositionLockAsync(box)
                    Return
                End If
            Else
                box.MissCount = 0
                If Not box.PositionId.HasValue OrElse box.PositionId.Value = 0 Then
                    box.PositionId = snapshot.PositionId
                End If
                ' Resolve fill price and TP on the first snapshot tick
                If box.EntryPrice = 0D AndAlso snapshot.OpenRate <> 0D Then
                    box.EntryPrice = snapshot.OpenRate
                    Dim tpMult = ParseTpMultiple()
                    If tpMult > 0D AndAlso box.CurrentStLine <> 0D Then
                        Dim initialRisk = Math.Abs(box.EntryPrice - box.CurrentStLine)
                        box.TpPrice = If(box.EntrySide = "Buy",
                                         box.EntryPrice + initialRisk * tpMult,
                                         box.EntryPrice - initialRisk * tpMult)
                    End If
                End If
                Dim pnl = snapshot.UnrealizedPnlUsd
                Application.Current?.Dispatcher?.Invoke(Sub() UpdatePositionDisplay(box, pnl))
            End If

            Dim bars As IList(Of MarketBar)
            Try
                bars = Await _barService.GetLiveBarsAsync(box.EntryInstrument, tf, BarsToFetch)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ GetLiveBarsAsync failed for {Box} on {Contract}", box.PersonaName, box.EntryInstrument)
                Return
            End Try
            If bars Is Nothing OrElse bars.Count < 15 Then Return

            Dim highs   = bars.Select(Function(b) b.High).ToList()
            Dim lows    = bars.Select(Function(b) b.Low).ToList()
            Dim closes  = bars.Select(Function(b) b.Close).ToList()
            Dim st      = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=_stMultiplier)
            Dim n       = bars.Count - 1
            Dim stDir   = st.Direction(n)
            Dim stLine  = CDec(st.Line(n))

            Dim entryIsLong As Boolean = (box.EntrySide = "Buy")
            Dim flipped As Boolean = (entryIsLong AndAlso stDir < 0) OrElse (Not entryIsLong AndAlso stDir > 0)
            If flipped Then
                Try
                    Await _orderService.FlattenContractAsync(box.AccountId, box.EntryInstrument)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ST+ FlattenContractAsync failed for {Box} on {Contract}", box.PersonaName, box.EntryInstrument)
                End Try
                Await ReleasePositionLockAsync(box)
                Return
            End If

            If Not Single.IsNaN(CSng(stLine)) AndAlso stLine <> 0D Then
                Dim shouldUpdate As Boolean =
                    (entryIsLong AndAlso stLine > box.CurrentStLine) OrElse
                    (Not entryIsLong AndAlso stLine < box.CurrentStLine)
                If shouldUpdate AndAlso box.PositionId.HasValue Then
                    Dim tpArg As Decimal? = If(box.TpPrice <> 0D, CType(box.TpPrice, Decimal?), Nothing)
                    Try
                        Await _orderService.EditPositionSlTpAsync(box.PositionId.Value, stLine, tpArg)
                        _logger.LogInformation("ST+ SL trail ? {Price} (TP={Tp}) for {Box} on {Contract}", stLine, If(tpArg.HasValue, tpArg.Value.ToString("F2"), "none"), box.PersonaName, box.EntryInstrument)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ EditPositionSlTpAsync failed for {Box} on {Contract}", box.PersonaName, box.EntryInstrument)
                    End Try
                    box.CurrentStLine = stLine
                    Dim latestPnl As Decimal = If(snapshot IsNot Nothing, snapshot.UnrealizedPnlUsd, 0D)
                    Application.Current?.Dispatcher?.Invoke(Sub() UpdatePositionDisplay(box, latestPnl))
                End If
            End If

            Dim maxScaleIns As Integer = If(box.Profile IsNot Nothing, box.Profile.MaxScaleIns, 1)
            If box.ScaleInCount < maxScaleIns Then
                Dim latestBarTime = bars.Last().Timestamp
                If box.LastScaleInBarTime = latestBarTime Then
                    ' Same bar still forming — skip scale-in this tick
                Else
                Dim currentPnl As Decimal = If(snapshot IsNot Nothing, snapshot.UnrealizedPnlUsd, 0D)
                If currentPnl < 0D Then
                    ' Scale-in suppressed: position P&L is negative
                Else
                Dim dmi     = TechnicalIndicators.DMI(highs, lows, closes, period:=14)
                Dim adxVal  = dmi.ADX(n)
                Dim plusDi  = dmi.PlusDI(n)
                Dim minusDi = dmi.MinusDI(n)
                Dim minAdx  = CSng(If(box.Profile IsNot Nothing, box.Profile.AdxThreshold, 20.0F))
                Dim stillLong  As Boolean = stDir > 0 AndAlso adxVal >= minAdx AndAlso plusDi > minusDi
                Dim stillShort As Boolean = stDir < 0 AndAlso adxVal >= minAdx AndAlso minusDi > plusDi
                If (entryIsLong AndAlso stillLong) OrElse (Not entryIsLong AndAlso stillShort) Then
                    Dim qty   As Integer   = If(box.Profile IsNot Nothing, box.Profile.PositionSize, 1)
                    Dim oSide As OrderSide = If(entryIsLong, OrderSide.Buy, OrderSide.Sell)
                    Dim order As New Order With {
                        .AccountId  = box.AccountId,
                        .ContractId = box.EntryInstrument,
                        .Side       = oSide,
                        .Quantity   = qty,
                        .OrderType  = OrderType.Market
                    }
                    Try
                        Await _orderService.PlaceOrderAsync(order)
                        box.ScaleInCount += 1
                        box.LastScaleInBarTime = latestBarTime
                        _logger.LogInformation("ST+ scale-in placed for {Box} on {Contract} (count={Count})", box.PersonaName, box.EntryInstrument, box.ScaleInCount)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "ST+ scale-in PlaceOrderAsync failed for {Box} on {Contract}", box.PersonaName, box.EntryInstrument)
                    End Try
                End If
                End If
                End If
            End If
        End Function

        Private Async Function ReleasePositionLockAsync(box As PersonaBoxVm) As Task
            SyncLock _timerLock
                _lockOwner = Nothing
                _timer?.Change(15000, 15000)
            End SyncLock
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    box.HasPosition      = False
                    box.PositionDisplay  = String.Empty
                    box.ScaleInCount     = 0
                    box.CurrentStLine    = 0D
                    box.PositionId       = Nothing
                    box.MissCount        = 0
                    box.TpPrice          = 0D
                    box.LastScaleInBarTime = DateTimeOffset.MinValue
                    For Each b In AllBoxes()
                        b.IsPaused = False
                    Next
                End Sub)
            Await Task.CompletedTask
        End Function

        Private Sub UpdatePositionDisplay(box As PersonaBoxVm, pnl As Decimal)
            Dim side  As String  = If(box.EntrySide = "Buy", "LONG", "SHORT")
            Dim idx   As Integer = Array.IndexOf(Instruments, box.EntryInstrument)
            Dim label As String  = If(idx >= 0, InstrumentLabels(idx), box.EntryInstrument)
            Dim entry As String  = If(box.EntryPrice = 0D, "--", box.EntryPrice.ToString("F2"))
            Dim sl    As String  = If(box.CurrentStLine = 0D, "--", box.CurrentStLine.ToString("F2"))
            Dim tp    As String  = If(box.TpPrice = 0D, "flip", box.TpPrice.ToString("F2"))
            Dim maxSI As Integer = If(box.Profile IsNot Nothing, box.Profile.MaxScaleIns, 1)
            Dim sign  As String  = If(pnl >= 0, "+", "")
            box.PositionDisplay =
                String.Format("{0}  {1}  @ {2}", side, label, entry) & Environment.NewLine &
                String.Format("SL: {0}  TP: {1}", sl, tp) & Environment.NewLine &
                String.Format("Scale-ins: {0} / {1}", box.ScaleInCount, maxSI) & Environment.NewLine &
                String.Format("Entry: {0:HH:mm}  |  P&L: {1}{2:F2}$", box.EntryTime, sign, pnl)
            box.PnlBrush = If(pnl >= 0, Brushes.LimeGreen, Brushes.Red)
        End Sub

        Private Shared Function MapTimeframe(tf As String) As BarTimeframe
            Select Case tf
                Case "5min"  : Return BarTimeframe.FiveMinute
                Case "1hr"   : Return BarTimeframe.OneHour
                Case Else    : Return BarTimeframe.FifteenMinute
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
