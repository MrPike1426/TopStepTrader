Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Threading
Imports System.Windows
Imports System.Windows.Media
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
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
        Friend EntryBarTime As DateTimeOffset = DateTimeOffset.MinValue

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
        Private _disposed As Boolean = False

        Private ReadOnly _approachHistory As New Dictionary(Of String, ApproachState)
        Private ReadOnly _prevStDirByInstrument As New Dictionary(Of String, Single)()
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
            _prevStDirByInstrument.Clear()
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
                box.EntryBarTime    = DateTimeOffset.MinValue
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
            Dim tf = MapTimeframe(_selectedTimeframe)
            Dim barCache = Await ScanWatchlistAsync(tf)

            ' Manage open positions for every persona independently
            For Each box In AllBoxes()
                If box.HasPosition Then
                    Await HandleOpenPositionAsync(box, tf)
                End If
            Next

            ' Evaluate entry sequencing: Joe first, Damian next bar, Lewis the bar after
            If _useEarlyMode Then
                Await EvaluateEarlyEntrySequenceAsync(tf, barCache)
            Else
                Await EvaluateEntrySequenceAsync(barCache)
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
                        _logger.LogInformation("ST+ ScanWatchlist [{Contract}] stripping forming bar (lastBarAge={Age:F1}min < tf={Tf}min). bars: {Before} ? {After}",
                                               contractId, lastBarAgeScan, tfMinutesScan, bars.Count, bars.Count - 1)
                        bars = bars.Take(bars.Count - 1).ToList()
                    End If
                End If
                ' Re-check after forming-bar strip — need at least 14 bars for DMI(14) warmup
                If bars.Count < 14 Then
                    _logger.LogInformation("ST+ ScanWatchlist [{Contract}] SKIP — fewer than 14 bars after forming-bar strip (count={Count})",
                                           contractId, bars.Count)
                    Continue For
                End If
                cache(i) = bars
                _logger.LogInformation("ST+ ScanWatchlist [{Contract}] cached {Count} bars.",
                                       contractId, bars.Count)

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
                ElseIf adxVal >= 40 Then
                    strength = String.Format("ADX:{0:D2} Strong", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Strong uptrend — bot may enter long.",
                                  If(signal = "BEAR", "Strong downtrend — bot may enter short.",
                                     "Strong trend forming — waiting for direction alignment."))
                ElseIf adxVal >= 25 Then
                    strength = String.Format("ADX:{0:D2} Active", CInt(adxVal))
                    signalReason = If(signal = "BULL", "Uptrend active — good conditions for a long entry.",
                                  If(signal = "BEAR", "Downtrend active — good conditions for a short entry.",
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
        ''' Confirmed-mode entry sequencing: Joe enters first on any favourable signal
        ''' (direction flip or ADX?25 Active).  Damian scales in on the next closed
        ''' 5-min bar if the signal is still Active.  Lewis scales in on the bar after that.
        ''' </summary>
        Private Async Function EvaluateEntrySequenceAsync(barCache As Dictionary(Of Integer, IList(Of MarketBar))) As Task
            Dim joe    = JoeBox
            Dim damian = DamianBox
            Dim lewis  = LewisBox

            _logger.LogInformation("ST+ EvaluateEntry tick — barCache has {Count} instrument(s). Joe.HasPosition={JoePos} Damian.HasPosition={DamPos} Lewis.HasPosition={LewPos}",
                                   barCache.Count, joe.HasPosition, damian.HasPosition, lewis.HasPosition)

            ' Candidate: prefer instrument Joe is already in; otherwise highest ADX.
            Dim bestContractId As String          = Nothing
            Dim bestSide As String                = Nothing
            Dim bestStLine As Decimal             = 0D
            Dim bestLastClose As Decimal          = 0D
            Dim bestAdxVal As Single              = 0F
            Dim bestBarTime As DateTimeOffset     = DateTimeOffset.MinValue
            Dim bestIsActive As Boolean           = False   ' ADX >= 25 (for scale-in gate)

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim bars As IList(Of MarketBar) = Nothing
                If Not barCache.TryGetValue(i, bars) OrElse bars Is Nothing OrElse bars.Count < 14 Then
                    _logger.LogInformation("ST+ [{Contract}] SKIP — not in barCache or bar count < 14 (count={Count})",
                                           contractId, If(bars Is Nothing, 0, bars.Count))
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

                ' Flip detection
                Dim prevDir As Single = 0F
                _prevStDirByInstrument.TryGetValue(contractId, prevDir)
                Dim isFlip As Boolean = prevDir <> 0F AndAlso stDir <> prevDir AndAlso stDir <> 0F
                _prevStDirByInstrument(contractId) = stDir

                Dim isLong  As Boolean = stDir > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso plusDi > minusDi
                Dim isShort As Boolean = stDir < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso minusDi > plusDi
                Dim isActive As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= 25.0F

                ' Favourable = direction aligned AND (just flipped OR ADX Active)
                Dim isFavourable As Boolean = (isLong OrElse isShort) AndAlso (isFlip OrElse isActive)

                _logger.LogInformation(
                    "ST+ [{Contract}] stDir={StDir} prevDir={PrevDir} ADX={Adx:F1} +DI={PlusDI:F1} -DI={MinusDI:F1} " &
                    "isLong={IsLong} isShort={IsShort} isFlip={IsFlip} isActive={IsActive} isFavourable={IsFav}",
                    contractId, stDir, prevDir, adxVal, plusDi, minusDi, isLong, isShort, isFlip, isActive, isFavourable)

                If Not isFavourable Then
                    ' Update per-persona symbol row display
                    For Each box In AllBoxes()
                        Dim row = box.Symbols(i)
                        Dim adxStr2 = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                        Application.Current?.Dispatcher?.Invoke(
                            Sub()
                                row.Arrow      = "--"
                                row.AdxDisplay = adxStr2
                                row.Signal     = "flat"
                                row.RowColor   = Brushes.White
                            End Sub)
                    Next
                    Continue For
                End If

                Dim side = If(isLong, "Buy", "Sell")
                Dim barTime = bars(n).Timestamp
                Dim adxStr = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                Dim sigLabel = If(isLong, "LONG", "SHORT")
                Dim sigColor As Brush = If(isLong, Brushes.LimeGreen, Brushes.Red)
                For Each box In AllBoxes()
                    Dim row = box.Symbols(i)
                    Application.Current?.Dispatcher?.Invoke(
                        Sub()
                            row.Arrow      = If(isLong, "UP", "DN")
                            row.AdxDisplay = adxStr
                            row.Signal     = sigLabel
                            row.RowColor   = sigColor
                        End Sub)
                Next

                ' Prefer the instrument Joe is already positioned in; else highest ADX
                Dim isJoeInstrument = joe.HasPosition AndAlso joe.EntryInstrument = contractId
                If bestContractId Is Nothing OrElse
                   isJoeInstrument OrElse
                   (Not (joe.HasPosition AndAlso joe.EntryInstrument = bestContractId) AndAlso adxVal > bestAdxVal) Then
                    bestContractId = contractId
                    bestSide       = side
                    bestStLine     = stLine
                    bestLastClose  = CDec(closes(n))
                    bestAdxVal     = adxVal
                    bestBarTime    = barTime
                    bestIsActive   = isActive
                End If
            Next

            If bestContractId Is Nothing Then
                _logger.LogInformation("ST+ EvaluateEntry — no favourable candidate found this tick.")
                Return
            End If

            _logger.LogInformation(
                "ST+ Best candidate: {Contract} side={Side} ADX={Adx:F1} barTime={BarTime} isActive={IsActive}",
                bestContractId, bestSide, bestAdxVal, bestBarTime, bestIsActive)

            ' Joe enters first on any favourable signal
            If Not joe.HasPosition Then
                _logger.LogInformation("ST+ Joe has no position — calling FireEntryAsync for {Contract} {Side}", bestContractId, bestSide)
                Await FireEntryAsync(joe, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)

            ' Damian scales in on the next closed bar if signal still Active
            ElseIf joe.EntryInstrument = bestContractId AndAlso
                   Not damian.HasPosition AndAlso
                   bestIsActive AndAlso
                   joe.EntryBarTime < bestBarTime Then
                _logger.LogInformation("ST+ Damian scale-in — calling FireEntryAsync for {Contract} {Side}", bestContractId, bestSide)
                Await FireEntryAsync(damian, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)

            ' Lewis scales in on the bar after Damian
            ElseIf joe.EntryInstrument = bestContractId AndAlso
                   damian.HasPosition AndAlso damian.EntryInstrument = bestContractId AndAlso
                   Not lewis.HasPosition AndAlso
                   bestIsActive AndAlso
                   damian.EntryBarTime < bestBarTime Then
                _logger.LogInformation("ST+ Lewis scale-in — calling FireEntryAsync for {Contract} {Side}", bestContractId, bestSide)
                Await FireEntryAsync(lewis, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)
            Else
                _logger.LogInformation(
                    "ST+ No entry fired — Joe.HasPos={JoePos} Joe.Instrument={JoeInstr} Damian.HasPos={DamPos} Damian.Instrument={DamInstr} bestIsActive={IsActive} joe.EntryBarTime={JoeBarTime} bestBarTime={BestBarTime}",
                    joe.HasPosition, joe.EntryInstrument, damian.HasPosition, damian.EntryInstrument,
                    bestIsActive, joe.EntryBarTime, bestBarTime)
            End If
        End Function

        ''' <summary>
        ''' Early-mode entry sequencing: same Joe?Damian?Lewis order but uses the
        ''' multi-signal early reversal trigger for Joe's entry.  Damian and Lewis
        ''' scale in when the early signal or confirmed signal is still present.
        ''' </summary>
        Private Async Function EvaluateEarlyEntrySequenceAsync(tf As BarTimeframe,
                                                                barCache As Dictionary(Of Integer, IList(Of MarketBar))) As Task
            Dim joe    = JoeBox
            Dim damian = DamianBox
            Dim lewis  = LewisBox
            Dim minAdx As Single = If(joe.Profile IsNot Nothing, CSng(joe.Profile.AdxThreshold), 20.0F)
            If minAdx <= 0 Then minAdx = 20.0F

            Dim bestContractId As String          = Nothing
            Dim bestSide As String                = Nothing
            Dim bestStLine As Decimal             = 0D
            Dim bestLastClose As Decimal          = 0D
            Dim bestAdxVal As Single              = 0F
            Dim bestBarTime As DateTimeOffset     = DateTimeOffset.MinValue
            Dim bestIsScaleInOk As Boolean        = False

            Dim tfMinutes As Integer = CInt(_selectedTimeframe.Replace("min", "").Replace("hr", ""))
            If _selectedTimeframe.EndsWith("hr") Then tfMinutes *= 60

            For i = 0 To Instruments.Length - 1
                Dim contractId = Instruments(i)
                Dim bars15 As IList(Of MarketBar) = Nothing
                barCache.TryGetValue(i, bars15)
                If bars15 Is Nothing OrElse bars15.Count < 15 Then Continue For
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
                Dim sig4 As Boolean = Not Single.IsNaN(adxVal) AndAlso adxVal >= minAdx
                Dim sig5 As Boolean = If(anticipatedLong, stDir5 > 0, stDir5 < 0)
                Dim earlySignal As Boolean = sig1 AndAlso sig2 AndAlso sig3 AndAlso sig4 AndAlso sig5

                ' Scale-in is allowed when the early signal persists OR the signal is now confirmed
                Dim confirmedLong  As Boolean = stDir15 > 0 AndAlso Not Single.IsNaN(adxVal) AndAlso adxVal >= 25 AndAlso plusDi > minusDi
                Dim confirmedShort As Boolean = stDir15 < 0 AndAlso Not Single.IsNaN(adxVal) AndAlso adxVal >= 25 AndAlso minusDi > plusDi
                Dim isScaleInOk As Boolean = earlySignal OrElse confirmedLong OrElse confirmedShort

                Dim side As String    = If(anticipatedLong, "Buy", "Sell")
                Dim signal As String  = If(earlySignal, "EARLY", "flat")
                Dim rowColor As Brush = If(earlySignal, Brushes.Goldenrod, Brushes.White)
                Dim adxStr As String  = If(Single.IsNaN(adxVal), "ADX:--", String.Format("ADX:{0:D2}", CInt(adxVal)))
                Dim arrowStr As String = If(anticipatedLong, "UP", "DN")

                For Each box In AllBoxes()
                    Dim row = box.Symbols(i)
                    Application.Current?.Dispatcher?.Invoke(
                        Sub()
                            row.Arrow      = arrowStr
                            row.AdxDisplay = adxStr
                            row.Signal     = signal
                            row.RowColor   = rowColor
                        End Sub)
                Next

                If Not earlySignal Then Continue For

                Dim barTime = bars15(n15).Timestamp
                Dim isJoeInstrument = joe.HasPosition AndAlso joe.EntryInstrument = contractId
                If bestContractId Is Nothing OrElse
                   isJoeInstrument OrElse
                   (Not (joe.HasPosition AndAlso joe.EntryInstrument = bestContractId) AndAlso adxVal > bestAdxVal) Then
                    bestContractId  = contractId
                    bestSide        = side
                    bestStLine      = stLine15
                    bestLastClose   = CDec(lastClose15)
                    bestAdxVal      = adxVal
                    bestBarTime     = barTime
                    bestIsScaleInOk = isScaleInOk
                End If
            Next

            If bestContractId Is Nothing Then Return

            If Not joe.HasPosition Then
                Await FireEntryAsync(joe, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)
            ElseIf joe.EntryInstrument = bestContractId AndAlso
                   Not damian.HasPosition AndAlso
                   bestIsScaleInOk AndAlso
                   joe.EntryBarTime < bestBarTime Then
                Await FireEntryAsync(damian, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)
            ElseIf joe.EntryInstrument = bestContractId AndAlso
                   damian.HasPosition AndAlso damian.EntryInstrument = bestContractId AndAlso
                   Not lewis.HasPosition AndAlso
                   bestIsScaleInOk AndAlso
                   damian.EntryBarTime < bestBarTime Then
                Await FireEntryAsync(lewis, bestContractId, bestSide, bestStLine, bestLastClose, bestBarTime)
            End If
        End Function

        Private Async Function FireEntryAsync(box As PersonaBoxVm,
                                               contractId As String,
                                               side As String,
                                               stLine As Decimal,
                                               lastClose As Decimal,
                                               barTime As DateTimeOffset) As Task
            ' Guard: if this persona already has a position, skip
            If box.HasPosition Then
                _logger.LogInformation("ST+ FireEntry [{Persona}] SKIP — already has position on {Contract}",
                                       box.PersonaName, box.EntryInstrument)
                Return
            End If

            _logger.LogInformation("ST+ FireEntry [{Persona}] {Side} {Contract} — resolving account...",
                                   box.PersonaName, side, contractId)

            Dim accountId As Long = If(_selectedAccount IsNot Nothing,
                                       _selectedAccount.Id, 0)
            If accountId = 0 Then
                _logger.LogWarning("ST+ FireEntry [{Persona}] BLOCKED — accountId=0. SelectedAccount={Acct}",
                                   box.PersonaName,
                                   If(_selectedAccount Is Nothing, "null", $"{_selectedAccount.Name} id={_selectedAccount.Id} canTrade={_selectedAccount.CanTrade}"))
                Return
            End If
            box.AccountId = accountId

            SyncLock _timerLock
                _timer?.Change(2000, 2000)
            End SyncLock

            Dim qty As Integer    = If(box.Profile IsNot Nothing, box.Profile.PositionSize, 1)
            Dim oSide As OrderSide = If(side = "Buy", OrderSide.Buy, OrderSide.Sell)

            ' Compute SL ticks from the SuperTrend line distance, clamped to instrument minimums.
            Dim fc As FavouriteContract = FavouriteContracts.TryGetBySymbol(contractId)
            Dim stopTicks As Integer? = Nothing
            Dim tpTicks As Integer? = Nothing
            If fc IsNot Nothing AndAlso fc.PxTickSize > 0D Then
                Dim rawDist As Decimal = Math.Abs(lastClose - stLine)
                Dim rawTicks As Integer = CInt(Math.Round(rawDist / fc.PxTickSize))
                ' Clamp up to the instrument dollar floor.
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

            Dim order As New Order With {
                .AccountId              = box.AccountId,
                .ContractId            = contractId,
                .Side                  = oSide,
                .Quantity              = qty,
                .OrderType             = OrderType.Market,
                .InitialStopTicks      = stopTicks,
                .InitialTakeProfitTicks = tpTicks
            }
            Dim placed As Order = Nothing
            Try
                placed = Await _orderService.PlaceOrderAsync(order)
            Catch ex As Exception
                _logger.LogWarning(ex, "ST+ PlaceOrderAsync failed for {Contract}", contractId)
            End Try

            ' Only show a position tile
            ' Accept Working (bracket/limit) OR Filled (immediate market fill).
            ' Reject only if Nothing, Rejected, or Cancelled.
            Dim isAccepted = placed IsNot Nothing AndAlso
                             (placed.Status = OrderStatus.Working OrElse placed.Status = OrderStatus.Filled)
            If Not isAccepted Then
                _logger.LogWarning("ST+ order not accepted for {Contract}: status={Status}", contractId, placed?.Status)
                ' Slow the timer back if no persona has a position
                If Not AllBoxes().Any(Function(b) b.HasPosition) Then
                    SyncLock _timerLock
                        _timer?.Change(15000, 15000)
                    End SyncLock
                End If
                Return
            End If

            box.EntryPrice      = 0D
            box.EntryInstrument = contractId
            box.EntrySide       = side
            box.EntryTime       = DateTime.Now
            box.EntryBarTime    = barTime
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

            ' Scale-in within a single persona is no longer used.
            ' Cross-persona sequencing (Joe ? Damian ? Lewis) is handled by EvaluateEntrySequenceAsync.
        End Function

        Private Async Function ReleasePositionLockAsync(box As PersonaBoxVm) As Task
            Application.Current?.Dispatcher?.Invoke(
                Sub()
                    box.HasPosition        = False
                    box.PositionDisplay    = String.Empty
                    box.ScaleInCount       = 0
                    box.CurrentStLine      = 0D
                    box.PositionId         = Nothing
                    box.MissCount          = 0
                    box.TpPrice            = 0D
                    box.LastScaleInBarTime = DateTimeOffset.MinValue
                    box.EntryBarTime       = DateTimeOffset.MinValue
                End Sub)
            ' Slow the polling interval back to 15s only when no persona has an open position
            If Not AllBoxes().Any(Function(b) b.HasPosition) Then
                SyncLock _timerLock
                    _timer?.Change(15000, 15000)
                End SyncLock
            End If
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
