Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Media
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' UI view-model for a single position slot card (Slot 1 / Slot 2 / Slot 3).
    ''' Replaces PersonaBoxVm — slots are identity-free.
    ''' </summary>
    Public Class SlotBoxVm
        Inherits ViewModelBase

        Public ReadOnly Symbols As New ObservableCollection(Of SymbolRowVm)

        ''' <summary>Display label, e.g. "Slot 1".</summary>
        Public ReadOnly Property SlotLabel As String
        Public ReadOnly Property SlotIndex As Integer

        Public Sub New(slotIndex As Integer)
            Me.SlotIndex = slotIndex
            SlotLabel = $"Slot {slotIndex + 1}"
        End Sub

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

        ''' <summary>Reference to the underlying slot state (set by ViewModel).</summary>
        Friend Slot As PositionSlot

    End Class

End Namespace
