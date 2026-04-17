Imports System.Windows.Media
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' One row in the AI Trade performance table.
    ''' Starts as "In Progress" with a pulsing amber dot; closes to TP/SL result.
    ''' </summary>
    Public Class TradeRowViewModel
        Inherits ViewModelBase

        ' ── Immutable fields set at construction ──────────────────────────────────
        Public ReadOnly Property EntryTime As DateTimeOffset
        Public ReadOnly Property ContractDisplay As String
        Public ReadOnly Property ConfidencePct As Integer
        Public ReadOnly Property SideDisplay As String   ' "BUY" or "SELL"

        ' Private computation fields (set in constructor, used by UpdatePnl)
        Private ReadOnly _entryPrice As Decimal
        Private ReadOnly _tradeSide As OrderSide
        Private _closedPnl As Decimal = 0D
        Private _apiPnlReceived As Boolean = False

        ' ── Formatted display of entry time (dd/MM HH:mm in local time) ──────────
        Public ReadOnly Property EntryTimeDisplay As String
            Get
                Return EntryTime.ToLocalTime().ToString("dd/MM HH:mm")
            End Get
        End Property

        ' ── Live / mutable: updated when position closes ───────────────────────────

        Private _result As String = "⏳ In Progress..."
        Public Property Result As String
            Get
                Return _result
            End Get
            Set(value As String)
                SetProperty(_result, value)
            End Set
        End Property

        Private _isInProgress As Boolean = True
        Public Property IsInProgress As Boolean
            Get
                Return _isInProgress
            End Get
            Set(value As Boolean)
                If SetProperty(_isInProgress, value) Then
                    NotifyPropertyChanged(NameOf(PnlDisplay))
                    NotifyPropertyChanged(NameOf(PnlForeground))
                End If
            End Set
        End Property

        Private _resultForeground As Brush = Brushes.White
        Public Property ResultForeground As Brush
            Get
                Return _resultForeground
            End Get
            Set(value As Brush)
                SetProperty(_resultForeground, value)
            End Set
        End Property

        ' ── Constructor ───────────────────────────────────────────────────────────

        Public Sub New(side As OrderSide, contractId As String,
                       confidencePct As Integer, entryTime As DateTimeOffset,
                       Optional externalOrderId As Long? = Nothing,
                       Optional etoroPositionId As Long? = Nothing,
                       Optional openedAtUtc As DateTimeOffset = Nothing,
                       Optional exposureUsd As Decimal = 0D,
                       Optional leverage As Integer = 1,
                       Optional entryPrice As Decimal = 0D)
            Me.EntryTime = entryTime
            Me.ContractDisplay = ToFriendlyName(contractId)
            Me.ConfidencePct = confidencePct
            Me.SideDisplay = If(side = OrderSide.Buy, "BUY", "SELL")
            Me.ExternalOrderId = externalOrderId
            _etoroPositionId = etoroPositionId
            _openedAtUtc = If(openedAtUtc = DateTimeOffset.MinValue, entryTime, openedAtUtc)
            Me.ExposureUsd = exposureUsd
            Me.LeverageMultiplier = If(leverage > 0, leverage, 1)
            _entryPrice = entryPrice
            _tradeSide = side
        End Sub

        ' ── Entry order ID ───────────────────────────────────────────────────────
        Public ReadOnly Property ExternalOrderId As Long?

        ''' <summary>Formatted entry order ID for display ("—" when not available).</summary>
        Public ReadOnly Property TradeIdDisplay As String
            Get
                Return If(ExternalOrderId.HasValue, ExternalOrderId.Value.ToString(), "—")
            End Get
        End Property

        ' ── eToro position / exposure fields ────────────────────────────────────
        Private _etoroPositionId As Long?
        Public Property EtoroPositionId As Long?
            Get
                Return _etoroPositionId
            End Get
            Set(value As Long?)
                If SetProperty(_etoroPositionId, value) Then
                    NotifyPropertyChanged(NameOf(EtoroPositionIdDisplay))
                End If
            End Set
        End Property

        Private _openedAtUtc As DateTimeOffset
        Public Property OpenedAtUtc As DateTimeOffset
            Get
                Return _openedAtUtc
            End Get
            Set(value As DateTimeOffset)
                If SetProperty(_openedAtUtc, value) Then
                    NotifyPropertyChanged(NameOf(OpenedAtDisplay))
                End If
            End Set
        End Property

        Public ReadOnly Property ExposureUsd As Decimal
        Public ReadOnly Property LeverageMultiplier As Integer

        Public ReadOnly Property EtoroPositionIdDisplay As String
            Get
                Return If(EtoroPositionId.HasValue, EtoroPositionId.Value.ToString(), "—")
            End Get
        End Property

        Public ReadOnly Property OpenedAtDisplay As String
            Get
                If OpenedAtUtc = DateTimeOffset.MinValue Then Return "—"
                Return OpenedAtUtc.ToLocalTime().ToString("dd/MM HH:mm")
            End Get
        End Property

        Public ReadOnly Property ExposureDisplay As String
            Get
                If ExposureUsd = 0D Then Return "—"
                Return If(LeverageMultiplier > 1,
                          $"${ExposureUsd:N0}×{LeverageMultiplier}",
                          $"${ExposureUsd:N0}")
            End Get
        End Property

        ' ── Live unrealised P&L (updated each bar-check cycle) ─────────────────
        Private _unrealizedPnlUsd As Decimal = 0D
        Public Property UnrealizedPnlUsd As Decimal
            Get
                Return _unrealizedPnlUsd
            End Get
            Private Set(value As Decimal)
                If SetProperty(_unrealizedPnlUsd, value) Then
                    NotifyPropertyChanged(NameOf(PnlDisplay))
                    NotifyPropertyChanged(NameOf(PnlForeground))
                End If
            End Set
        End Property

        Public ReadOnly Property PnlDisplay As String
            Get
                If IsInProgress Then
                    If Not _apiPnlReceived AndAlso _entryPrice = 0D Then Return "—"
                    Dim sign = If(_unrealizedPnlUsd >= 0, "+", "")
                    Return $"{sign}${_unrealizedPnlUsd:F2}"
                Else
                    If _closedPnl = 0D Then Return "—"
                    Dim sign = If(_closedPnl >= 0, "+", "")
                    Return $"{sign}${_closedPnl:F2}"
                End If
            End Get
        End Property

        Public ReadOnly Property PnlForeground As Brush
            Get
                If IsInProgress Then
                    If Not _apiPnlReceived AndAlso _entryPrice = 0D Then Return Brushes.Gray
                    If _unrealizedPnlUsd > 0 Then Return New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61))
                    If _unrealizedPnlUsd < 0 Then Return New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A))
                    Return Brushes.White
                Else
                    If _closedPnl > 0 Then Return New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61))
                    If _closedPnl < 0 Then Return New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A))
                    Return Brushes.Gray
                End If
            End Get
        End Property

        ' ── Close row with result ─────────────────────────────────────────────────

        ''' <summary>
        ''' Called when the position closes.
        ''' exitReason: "TP", "SL", or "Closed" (manual/unknown).
        ''' pnl: signed dollar amount (positive = profit, negative = loss).
        ''' </summary>
        Public Sub Close(exitReason As String, pnl As Decimal)
            _closedPnl = pnl
            IsInProgress = False
            Dim sign = If(pnl >= 0, "+", "")
            Dim pnlStr = $"{sign}${CInt(Math.Round(pnl)):N0}"
            Select Case exitReason
                Case "TP"
                    Result = $"TP  {pnlStr}  ✅"
                    ResultForeground = New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61))
                Case "SL"
                    Result = $"SL  {pnlStr}  ❌"
                    ResultForeground = New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A))
                Case "Reversal"
                    Dim pnlSuffix = If(pnl <> 0D, $"  {pnlStr}", String.Empty)
                    Result = $"Reversal ↔{pnlSuffix}"
                    ResultForeground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H00))
                Case Else
                    Result = "Closed"
                    ResultForeground = New SolidColorBrush(Color.FromRgb(&H80, &H80, &H80))
            End Select
        End Sub

        ' ── Live P&L update (called from bar-check cycle) ─────────────────────
        ''' <summary>
        ''' Recalculates unrealised P&amp;L from the latest bar close price.
        ''' No-op once the position is closed (IsInProgress = False).
        ''' Formula: priceDiff × dollarPerPoint  (futures point-value model)
        ''' dollarPerPoint = contracts × tickValue / tickSize
        ''' </summary>
        Public Sub UpdatePnl(currentPrice As Decimal, dollarPerPoint As Decimal)
            If Not IsInProgress Then Return
            If _entryPrice = 0D OrElse currentPrice = 0D OrElse dollarPerPoint = 0D Then Return
            Dim priceDiff = If(_tradeSide = OrderSide.Buy,
                               currentPrice - _entryPrice,
                               _entryPrice - currentPrice)
            UnrealizedPnlUsd = Math.Round(priceDiff * dollarPerPoint, 2)
        End Sub

        ''' <summary>
        ''' Updates the row with API-authoritative position data from the broker portfolio.
        ''' Called from the PositionSynced event on every 30-second monitoring cycle.
        ''' No-op once the position has been closed (IsInProgress = False).
        ''' </summary>
        Public Sub ApplyApiSnapshot(positionId As Long, unrealizedPnlUsd As Decimal, openedAtUtc As DateTimeOffset)
            If Not IsInProgress Then Return
            If Not EtoroPositionId.HasValue Then EtoroPositionId = positionId
            If openedAtUtc > DateTimeOffset.MinValue AndAlso _openedAtUtc = DateTimeOffset.MinValue Then
                OpenedAtUtc = openedAtUtc
            End If
            _apiPnlReceived = True
            UnrealizedPnlUsd = unrealizedPnlUsd
        End Sub

        ' ── Helpers ───────────────────────────────────────────────────────────────

        ''' <summary>
        ''' Maps a full contract ID to a short friendly display name.
        ''' TICKET-026 will replace this with an API-backed lookup.
        ''' </summary>
        Friend Shared Function ToFriendlyName(contractId As String) As String
            If contractId.Contains("MGC") Then Return "M.Gold"
            If contractId.Contains("MNQ") Then Return "M.Nasdaq"
            If contractId.Contains("MCL") Then Return "M.Oil"
            If contractId.Contains("MES") Then Return "M.S&P"
            Return contractId.Substring(0, Math.Min(6, contractId.Length))
        End Function

    End Class

End Namespace
