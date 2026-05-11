Imports System.Collections.ObjectModel
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Media
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' UI view-model for a single position slot card.
    ''' Identity-free — header shows the instrument when occupied, blank when empty.
    ''' </summary>
    Public Class SlotBoxVm
        Inherits ViewModelBase

        Public ReadOnly Symbols As New ObservableCollection(Of SymbolRowVm)

        Public ReadOnly Property SlotIndex As Integer

        Public Sub New(slotIndex As Integer)
            Me.SlotIndex = slotIndex
        End Sub

        ' ── Slot label: instrument name when open, blank when empty ─────────
        Private _slotLabel As String = String.Empty
        ''' <summary>e.g. "GOLD  MGC" when occupied; blank when empty.</summary>
        Public Property SlotLabel As String
            Get
                Return _slotLabel
            End Get
            Set(value As String)
                SetProperty(_slotLabel, value)
            End Set
        End Property

        ' ── Idle monitoring pulse ────────────────────────────────────────────
        Private _idleMonitorText As String = String.Empty
        ''' <summary>"Actively Monitoring: HH:mm:ss" shown in empty boxes while running.</summary>
        Public Property IdleMonitorText As String
            Get
                Return _idleMonitorText
            End Get
            Set(value As String)
                If SetProperty(_idleMonitorText, value) Then
                    NotifyPropertyChanged(NameOf(IdleVisibility))
                End If
            End Set
        End Property

        Private _isIdleFlashing As Boolean = False
        ''' <summary>True for 400 ms after each scan tick — drives XAML flash on idle text.</summary>
        Public Property IsIdleFlashing As Boolean
            Get
                Return _isIdleFlashing
            End Get
            Set(value As Boolean)
                SetProperty(_isIdleFlashing, value)
            End Set
        End Property

        Public ReadOnly Property IdleVisibility As Visibility
            Get
                Return If(Not _hasPosition AndAlso Not String.IsNullOrEmpty(_idleMonitorText),
                          Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        ''' <summary>Pulse the idle text for 400 ms on each scan tick.</summary>
        Public Async Function FlashIdleAsync() As Task
            IsIdleFlashing = True
            Await Task.Delay(400)
            IsIdleFlashing = False
        End Function

        ' ── Border flash when a new instrument claims this box ───────────────
        Private _isBorderFlashing As Boolean = False
        Public Property IsBorderFlashing As Boolean
            Get
                Return _isBorderFlashing
            End Get
            Set(value As Boolean)
                SetProperty(_isBorderFlashing, value)
            End Set
        End Property

        ''' <summary>400 ms border accent pulse when a new instrument fills this slot.</summary>
        Public Async Function FlashBorderAsync() As Task
            IsBorderFlashing = True
            Await Task.Delay(400)
            IsBorderFlashing = False
        End Function

        ' ── P&L / SL / TP change pulse ───────────────────────────────────────
        Private _isPnlFlashing As Boolean = False
        ''' <summary>True for 400 ms when the P&amp;L value or SL/TP price changes — drives XAML white border pulse.</summary>
        Public Property IsPnlFlashing As Boolean
            Get
                Return _isPnlFlashing
            End Get
            Set(value As Boolean)
                SetProperty(_isPnlFlashing, value)
            End Set
        End Property

        ''' <summary>Last rendered PositionDisplay string — used to detect P&amp;L/SL/TP changes.</summary>
        Friend LastPositionDisplay As String = String.Empty

        ''' <summary>400 ms white border pulse when P&amp;L or SL/TP changes.</summary>
        Public Async Function FlashPnlAsync() As Task
            IsPnlFlashing = True
            Await Task.Delay(400)
            IsPnlFlashing = False
        End Function

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
                    NotifyPropertyChanged(NameOf(IdleVisibility))
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

        Private _pnlLine As String = String.Empty
        ''' <summary>Formatted P&amp;L line, e.g. "P&amp;L: -$5.00".</summary>
        Public Property PnlLine As String
            Get
                Return _pnlLine
            End Get
            Set(value As String)
                SetProperty(_pnlLine, value)
            End Set
        End Property

        Private _pnlTextBrush As Brush = Brushes.Gray
        ''' <summary>Text colour for the P&amp;L line: green = positive, red = negative, grey = zero.</summary>
        Public Property PnlTextBrush As Brush
            Get
                Return _pnlTextBrush
            End Get
            Set(value As Brush)
                SetProperty(_pnlTextBrush, value)
            End Set
        End Property

        Private _isPnlTextFlashing As Boolean = False
        ''' <summary>True for 400 ms after a P&amp;L value change — drives yellow bold flash in XAML.</summary>
        Public Property IsPnlTextFlashing As Boolean
            Get
                Return _isPnlTextFlashing
            End Get
            Set(value As Boolean)
                SetProperty(_isPnlTextFlashing, value)
            End Set
        End Property

        ''' <summary>Flash the P&amp;L text bold yellow for 400 ms.</summary>
        Public Async Function FlashPnlTextAsync() As Task
            IsPnlTextFlashing = True
            Await Task.Delay(400)
            IsPnlTextFlashing = False
        End Function

        Private _pnlBrush As Brush = Brushes.White
        Public Property PnlBrush As Brush
            Get
                Return _pnlBrush
            End Get
            Set(value As Brush)
                SetProperty(_pnlBrush, value)
            End Set
        End Property

        Private _pnlBorderBrush As Brush = Brushes.Gray
        ''' <summary>Thick border colour for the slot card: green = positive P&amp;L, red = negative, grey = zero / no position.</summary>
        Public Property PnlBorderBrush As Brush
            Get
                Return _pnlBorderBrush
            End Get
            Set(value As Brush)
                SetProperty(_pnlBorderBrush, value)
            End Set
        End Property

        ''' <summary>Reference to the underlying slot state (set by ViewModel).</summary>
        Friend Slot As PositionSlot

        Private _stopPhaseLabel As String = String.Empty
        ''' <summary>Human-readable current stop phase, e.g. "Breakeven" or "ProfitLock".</summary>
        Public Property StopPhaseLabel As String
            Get
                Return _stopPhaseLabel
            End Get
            Set(value As String)
                SetProperty(_stopPhaseLabel, value)
            End Set
        End Property

        Private _healthBrush As Brush = Brushes.LimeGreen
        ''' <summary>Slot card border colour: green=Healthy, amber=Warning, red=Exiting.</summary>
        Public Property HealthBrush As Brush
            Get
                Return _healthBrush
            End Get
            Set(value As Brush)
                SetProperty(_healthBrush, value)
            End Set
        End Property

        ' ── AI mid-trade sense check ─────────────────────────────────────────

        Private _aiVerdict As String = String.Empty
        ''' <summary>"GREEN", "AMBER", or "RED" — set after a mid-trade Claude check.</summary>
        Public Property AiVerdict As String
            Get
                Return _aiVerdict
            End Get
            Set(value As String)
                If SetProperty(_aiVerdict, value) Then
                    NotifyPropertyChanged(NameOf(AiVerdictText))
                    NotifyPropertyChanged(NameOf(AiVerdictBrush))
                    NotifyPropertyChanged(NameOf(AiResultVisibility))
                End If
            End Set
        End Property

        Private _aiExplanation As String = String.Empty
        ''' <summary>Short explanation returned by Claude for the mid-trade check.</summary>
        Public Property AiExplanation As String
            Get
                Return _aiExplanation
            End Get
            Set(value As String)
                SetProperty(_aiExplanation, value)
            End Set
        End Property

        Private _aiSuggestedAction As String = String.Empty
        ''' <summary>Suggested action returned by Claude for the mid-trade check.</summary>
        Public Property AiSuggestedAction As String
            Get
                Return _aiSuggestedAction
            End Get
            Set(value As String)
                SetProperty(_aiSuggestedAction, value)
            End Set
        End Property

        Private _isAiChecking As Boolean = False
        ''' <summary>True while the mid-trade AI check is in progress (drives spinner/busy state).</summary>
        Public Property IsAiChecking As Boolean
            Get
                Return _isAiChecking
            End Get
            Set(value As Boolean)
                SetProperty(_isAiChecking, value)
            End Set
        End Property

        Public ReadOnly Property AiVerdictText As String
            Get
                Select Case _aiVerdict
                    Case "GREEN" : Return "🟢 AI: All clear"
                    Case "AMBER" : Return "🟡 AI: Caution"
                    Case "RED"   : Return "🔴 AI: Exit advised"
                    Case Else    : Return String.Empty
                End Select
            End Get
        End Property

        Public ReadOnly Property AiVerdictBrush As Brush
            Get
                Select Case _aiVerdict
                    Case "GREEN" : Return Brushes.LimeGreen
                    Case "AMBER" : Return New SolidColorBrush(Color.FromRgb(&HFF, &HCC, &H00))
                    Case "RED"   : Return Brushes.Red
                    Case Else    : Return Brushes.DimGray
                End Select
            End Get
        End Property

        Public ReadOnly Property AiResultVisibility As Visibility
            Get
                Return If(Not String.IsNullOrEmpty(_aiVerdict), Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        ' ── RR Achieved milestone ────────────────────────────────────────────────
        Private _isRrAchieved As Boolean = False
        ''' <summary>True when position P&amp;L has reached the persona's RR target.</summary>
        Public Property IsRrAchieved As Boolean
            Get
                Return _isRrAchieved
            End Get
            Set(value As Boolean)
                If SetProperty(_isRrAchieved, value) Then
                    NotifyPropertyChanged(NameOf(TitleBarBackground))
                    NotifyPropertyChanged(NameOf(RrAchievedVisibility))
                End If
            End Set
        End Property

        Public ReadOnly Property TitleBarBackground As Brush
            Get
                Return If(_isRrAchieved,
                          New SolidColorBrush(Color.FromRgb(&HB8, &H86, &H0B)),
                          Brushes.Transparent)
            End Get
        End Property

        Public ReadOnly Property RrAchievedVisibility As Visibility
            Get
                Return If(_isRrAchieved, Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        Private _sizeLabel As String = String.Empty
        ''' <summary>Position size label, e.g. "Size 2x".</summary>
        Public Property SizeLabel As String
            Get
                Return _sizeLabel
            End Get
            Set(value As String)
                SetProperty(_sizeLabel, value)
            End Set
        End Property

        Private _targetPnlLine As String = String.Empty
        ''' <summary>Formatted target line, e.g. "Target: $56.25" — shown from position open.</summary>
        Public Property TargetPnlLine As String
            Get
                Return _targetPnlLine
            End Get
            Set(value As String)
                If SetProperty(_targetPnlLine, value) Then
                    NotifyPropertyChanged(NameOf(TargetPnlVisibility))
                End If
            End Set
        End Property

        Public ReadOnly Property TargetPnlVisibility As Visibility
            Get
                Return If(Not String.IsNullOrEmpty(_targetPnlLine), Visibility.Visible, Visibility.Collapsed)
            End Get
        End Property

        ''' <summary>Clears the AI result (called when slot is released or monitoring stops).</summary>
        Public Sub ClearAiResult()
            AiVerdict = String.Empty
            AiExplanation = String.Empty
            AiSuggestedAction = String.Empty
            IsAiChecking = False
        End Sub

        ' ── Per-tick display properties (Row 1 / Row 2 / Row 3 / Row 4) ─────

        Private _entrySpDisplay As String = String.Empty
        ''' <summary>"SP: 1234.56" — start (entry) price, set once on open.</summary>
        Public Property EntrySpDisplay As String
            Get
                Return _entrySpDisplay
            End Get
            Set(value As String)
                SetProperty(_entrySpDisplay, value)
            End Set
        End Property

        Private _livePriceDisplay As String = String.Empty
        ''' <summary>Current live price updated every 15 s, e.g. "1238.50".</summary>
        Public Property LivePriceDisplay As String
            Get
                Return _livePriceDisplay
            End Get
            Set(value As String)
                SetProperty(_livePriceDisplay, value)
            End Set
        End Property

        Private _livePriceSourceLabel As String = String.Empty
        ''' <summary>FEAT-54: badge showing where the live price came from — "Quote" (sub-second
        ''' MarketHub push), "Bar" (history-bar fallback), or empty (no live tick yet).</summary>
        Public Property LivePriceSourceLabel As String
            Get
                Return _livePriceSourceLabel
            End Get
            Set(value As String)
                SetProperty(_livePriceSourceLabel, value)
            End Set
        End Property

        Private _slDisplay As String = String.Empty
        ''' <summary>"SL: 1233.00" — current stop loss price.</summary>
        Public Property SlDisplay As String
            Get
                Return _slDisplay
            End Get
            Set(value As String)
                SetProperty(_slDisplay, value)
            End Set
        End Property

        Private _slDollarDisplay As String = String.Empty
        ''' <summary>Dollar risk from entry to current SL, e.g. "($28.75)".</summary>
        Public Property SlDollarDisplay As String
            Get
                Return _slDollarDisplay
            End Get
            Set(value As String)
                SetProperty(_slDollarDisplay, value)
            End Set
        End Property

        Private _strengthLabel As String = String.Empty
        ''' <summary>ADX band name at entry: "Decaff", "Latte" or "Espresso".</summary>
        Public Property StrengthLabel As String
            Get
                Return _strengthLabel
            End Get
            Set(value As String)
                SetProperty(_strengthLabel, value)
            End Set
        End Property

        Private _nextPhaseLabel As String = String.Empty
        ''' <summary>"Next: Breakeven in 0.5R = $28.75" — distance to next stop phase in $.</summary>
        Public Property NextPhaseLabel As String
            Get
                Return _nextPhaseLabel
            End Get
            Set(value As String)
                SetProperty(_nextPhaseLabel, value)
            End Set
        End Property

        Private _trendHealthLabel As String = String.Empty
        ''' <summary>One-line composite trend health summary, e.g. "⚠ Softening (2m)".</summary>
        Public Property TrendHealthLabel As String
            Get
                Return _trendHealthLabel
            End Get
            Set(value As String)
                SetProperty(_trendHealthLabel, value)
            End Set
        End Property

        Private _trendHealthBrush As Brush = Brushes.DimGray
        ''' <summary>Colour for TrendHealthLabel: LimeGreen / Gold / OrangeRed.</summary>
        Public Property TrendHealthBrush As Brush
            Get
                Return _trendHealthBrush
            End Get
            Set(value As Brush)
                SetProperty(_trendHealthBrush, value)
            End Set
        End Property

        ' ── Row-2 flash (entire row flashes on each 15 s update) ─────────────
        Private _isRowFlashing As Boolean = False
        ''' <summary>True for 400 ms after each 15-second tick — drives Row 2 flash in XAML.</summary>
        Public Property IsRowFlashing As Boolean
            Get
                Return _isRowFlashing
            End Get
            Set(value As Boolean)
                SetProperty(_isRowFlashing, value)
            End Set
        End Property

        ''' <summary>400 ms flash on Row 2 to signal a live data update.</summary>
        Public Async Function FlashRowAsync() As Task
            IsRowFlashing = True
            Await Task.Delay(400)
            IsRowFlashing = False
        End Function

        ' ── 8-bar (2-minute) trend-weakening composite ───────────────────────
        Private ReadOnly _adxHistory As New Queue(Of Single)
        Private ReadOnly _diSpreadHistory As New Queue(Of Single)
        Private ReadOnly _priceToStHistory As New Queue(Of Single)
        Private Const TrendHistoryDepth As Integer = 8

        ''' <summary>
        ''' Push one 15-second sample into the rolling 8-bar (2-minute) history and
        ''' recompute TrendHealthLabel / TrendHealthBrush.
        ''' Three signals are composited:
        '''   1. ADX slope declining over the window
        '''   2. DI spread (|+DI − −DI|) narrowing
        '''   3. Price converging toward the SuperTrend line
        ''' Score 0 = holding, 1 = softening, 2–3 = weakening.
        ''' </summary>
        Public Sub PushAdxSample(adx As Single, diPlus As Single, diMinus As Single,
                                  priceToSt As Single)
            If Not Single.IsNaN(adx) Then
                _adxHistory.Enqueue(adx)
                If _adxHistory.Count > TrendHistoryDepth Then _adxHistory.Dequeue()
            End If

            If Not (Single.IsNaN(diPlus) OrElse Single.IsNaN(diMinus)) Then
                _diSpreadHistory.Enqueue(Math.Abs(diPlus - diMinus))
                If _diSpreadHistory.Count > TrendHistoryDepth Then _diSpreadHistory.Dequeue()
            End If

            If priceToSt > 0F Then
                _priceToStHistory.Enqueue(priceToSt)
                If _priceToStHistory.Count > TrendHistoryDepth Then _priceToStHistory.Dequeue()
            End If

            If _adxHistory.Count < 4 Then Return  ' not enough data yet

            Dim score As Integer = 0
            Dim split As Integer = Math.Max(1, _adxHistory.Count \ 2)

            ' Signal 1: ADX falling
            Dim adxArr = _adxHistory.ToArray()
            If adxArr.Skip(split).Average() < adxArr.Take(split).Average() Then score += 1

            ' Signal 2: DI spread narrowing
            If _diSpreadHistory.Count >= 4 Then
                Dim diArr = _diSpreadHistory.ToArray()
                Dim diSplit = Math.Max(1, diArr.Length \ 2)
                If diArr.Skip(diSplit).Average() < diArr.Take(diSplit).Average() Then score += 1
            End If

            ' Signal 3: Price converging toward ST line
            If _priceToStHistory.Count >= 4 Then
                Dim stArr = _priceToStHistory.ToArray()
                Dim stSplit = Math.Max(1, stArr.Length \ 2)
                If stArr.Skip(stSplit).Average() < stArr.Take(stSplit).Average() Then score += 1
            End If

            If score = 0 Then
                TrendHealthLabel = "✅ Trend holding (2m)"
                TrendHealthBrush = Brushes.LimeGreen
            ElseIf score = 1 Then
                TrendHealthLabel = "⚠ Softening (2m)"
                TrendHealthBrush = New SolidColorBrush(Color.FromRgb(&HFF, &HCC, &H00))
            Else
                TrendHealthLabel = "🔴 Weakening (2m)"
                TrendHealthBrush = Brushes.OrangeRed
            End If
        End Sub

        ''' <summary>Reset all rolling history (called when slot is released).</summary>
        Public Sub ClearTrendHistory()
            _adxHistory.Clear()
            _diSpreadHistory.Clear()
            _priceToStHistory.Clear()
            TrendHealthLabel = String.Empty
            TrendHealthBrush = Brushes.DimGray
            LivePriceDisplay = String.Empty
            EntrySpDisplay = String.Empty
            SlDisplay = String.Empty
            StrengthLabel = String.Empty
            NextPhaseLabel = String.Empty
        End Sub

    End Class

End Namespace
