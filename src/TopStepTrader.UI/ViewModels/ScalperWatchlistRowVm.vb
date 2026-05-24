Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-64: One row per scanned instrument in the Ultimate Scalper watchlist.
    ''' Holds the live confluence-indicator readout (MA200(5m) side, VWAP side,
    ''' RSI value + recency, signal-ready pill). The orchestrator pushes updates
    ''' on each closed 5m bar via the parent VM's <c>WatchlistTick</c> handler.
    ''' All values are dispatcher-marshalled before <c>SetProperty</c> is called.
    ''' </summary>
    Public Class ScalperWatchlistRowVm
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

        Private _ma200 As Decimal
        Public Property Ma200 As Decimal
            Get
                Return _ma200
            End Get
            Set(value As Decimal)
                SetProperty(_ma200, value)
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

        Private _barsSinceMidlineCross As Integer = -1
        ''' <summary>
        ''' For the *current* RSI position: bars since the last cross from the opposite side
        ''' of the midline. -1 = no cross observed yet (warmup).
        ''' </summary>
        Public Property BarsSinceMidlineCross As Integer
            Get
                Return _barsSinceMidlineCross
            End Get
            Set(value As Integer)
                SetProperty(_barsSinceMidlineCross, value)
                OnPropertyChanged(NameOf(RecencyText))
            End Set
        End Property

        Public ReadOnly Property RecencyText As String
            Get
                If _barsSinceMidlineCross < 0 Then Return "—"
                Return _barsSinceMidlineCross.ToString()
            End Get
        End Property

        Private _signalState As String = "—"
        ''' <summary>"—" / "Bullish" / "Bearish". Drives the pill colour in the view.</summary>
        Public Property SignalState As String
            Get
                Return _signalState
            End Get
            Set(value As String)
                If SetProperty(_signalState, value) Then
                    OnPropertyChanged(NameOf(DirectionIconKey))
                    OnPropertyChanged(NameOf(DirectionText))
                End If
            End Set
        End Property

        ''' <summary>
        ''' FEAT-67 F3: signal-driven direction-icon key resolved by
        ''' <see cref="Infrastructure.StringKeyToImageSourceConverter"/>. Empty key → no icon.
        ''' </summary>
        Public ReadOnly Property DirectionIconKey As String
            Get
                Select Case _signalState
                    Case "Bullish" : Return "BullIconGreen"
                    Case "Bearish" : Return "BearIconRed"
                    Case Else : Return String.Empty
                End Select
            End Get
        End Property

        ''' <summary>
        ''' BUG-96 follow-on: text glyph that fills the role the icon column was meant to play
        ''' (the icon resources weren't resolving in UAT). Coloured by a DataTrigger on
        ''' <see cref="SignalState"/> in the view.
        ''' </summary>
        Public ReadOnly Property DirectionText As String
            Get
                Select Case _signalState
                    Case "Bullish" : Return "▲"
                    Case "Bearish" : Return "▼"
                    Case Else : Return String.Empty
                End Select
            End Get
        End Property

        Private _lastUpdatedTick As Long
        ''' <summary>
        ''' FEAT-67 F2: monotonically increasing counter bumped on every orchestrator tick
        ''' that touches the row. Bound (with NotifyOnTargetUpdated) to fire the row-flash
        ''' storyboard. The numeric value itself is not displayed.
        ''' </summary>
        Public Property LastUpdatedTick As Long
            Get
                Return _lastUpdatedTick
            End Get
            Set(value As Long)
                SetProperty(_lastUpdatedTick, value)
            End Set
        End Property

        Private _lastUpdatedUtc As DateTime
        Public Property LastUpdatedUtc As DateTime
            Get
                Return _lastUpdatedUtc
            End Get
            Set(value As DateTime)
                SetProperty(_lastUpdatedUtc, value)
            End Set
        End Property

        Private _rejectionReason As String = String.Empty
        ''' <summary>Populated when SignalState = "—" but the bar evaluated — explains the near-miss.</summary>
        Public Property RejectionReason As String
            Get
                Return _rejectionReason
            End Get
            Set(value As String)
                SetProperty(_rejectionReason, value)
            End Set
        End Property

        ' ─── FEAT-69: pre-staged stop-entry armed state ────────────────────────

        Private _armedSide As UltimateScalperSignalSide = UltimateScalperSignalSide.None
        ''' <summary>Direction of the resting stop-entry order. None = not armed.</summary>
        Public Property ArmedSide As UltimateScalperSignalSide
            Get
                Return _armedSide
            End Get
            Set(value As UltimateScalperSignalSide)
                If SetProperty(_armedSide, value) Then OnPropertyChanged(NameOf(ArmedText))
            End Set
        End Property

        Private _armedTriggerPrice As Decimal
        ''' <summary>Broker trigger price of the resting stop-entry. 0 when not armed.</summary>
        Public Property ArmedTriggerPrice As Decimal
            Get
                Return _armedTriggerPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_armedTriggerPrice, value) Then OnPropertyChanged(NameOf(ArmedText))
            End Set
        End Property

        ''' <summary>Display label for the Armed column: "↑ 4523.50", "↓ 4527.00", or empty.</summary>
        Public ReadOnly Property ArmedText As String
            Get
                Select Case _armedSide
                    Case UltimateScalperSignalSide.Bullish : Return "↑ " & _armedTriggerPrice.ToString("N2")
                    Case UltimateScalperSignalSide.Bearish : Return "↓ " & _armedTriggerPrice.ToString("N2")
                    Case Else : Return String.Empty
                End Select
            End Get
        End Property

    End Class

End Namespace
