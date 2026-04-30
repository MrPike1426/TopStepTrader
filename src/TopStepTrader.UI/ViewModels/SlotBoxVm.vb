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

    End Class

End Namespace
