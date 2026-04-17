Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Responses

    ' ── Shared base ─────────────────────────────────────────────────────────────

    Public Class PXBaseResponse
        <JsonPropertyName("success")>
        Public Property Success As Boolean

        <JsonPropertyName("errorCode")>
        Public Property ErrorCode As Integer

        <JsonPropertyName("errorMessage")>
        Public Property ErrorMessage As String
    End Class

    ' ── Account ─────────────────────────────────────────────────────────────────

    Public Class PXAccountSearchResponse
        Inherits PXBaseResponse

        <JsonPropertyName("accounts")>
        Public Property Accounts As List(Of PXAccountDto) = New List(Of PXAccountDto)()
    End Class

    Public Class PXAccountDto
        <JsonPropertyName("id")>
        Public Property Id As Long

        <JsonPropertyName("name")>
        Public Property Name As String = String.Empty

        <JsonPropertyName("balance")>
        Public Property Balance As Decimal

        <JsonPropertyName("canTrade")>
        Public Property CanTrade As Boolean

        <JsonPropertyName("isVisible")>
        Public Property IsVisible As Boolean

        <JsonPropertyName("simulated")>
        Public Property Simulated As Boolean
    End Class

    ' ── Orders ──────────────────────────────────────────────────────────────────

    Public Class PXPlaceOrderResponse
        Inherits PXBaseResponse

        ''' <summary>Null when the order is rejected (success=False).</summary>
        <JsonPropertyName("orderId")>
        Public Property OrderId As Long?
    End Class

    Public Class PXOrderSearchResponse
        Inherits PXBaseResponse

        <JsonPropertyName("orders")>
        Public Property Orders As List(Of PXOrderDto) = New List(Of PXOrderDto)()
    End Class

    Public Class PXOrderDto
        <JsonPropertyName("id")>
        Public Property Id As Long

        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        <JsonPropertyName("creationTimestamp")>
        Public Property CreationTimestamp As String = String.Empty

        ''' <summary>1=Limit, 2=Market, 4=Stop, 5=TrailingStop</summary>
        <JsonPropertyName("type")>
        Public Property OrderType As Integer

        ''' <summary>0=Buy, 1=Sell</summary>
        <JsonPropertyName("side")>
        Public Property Side As Integer

        <JsonPropertyName("size")>
        Public Property Size As Integer

        <JsonPropertyName("limitPrice")>
        Public Property LimitPrice As Double?

        <JsonPropertyName("stopPrice")>
        Public Property StopPrice As Double?

        <JsonPropertyName("status")>
        Public Property Status As Integer

        <JsonPropertyName("avgFillPrice")>
        Public Property AvgFillPrice As Double?
    End Class

    ' ── Positions ───────────────────────────────────────────────────────────────

    Public Class PXPositionSearchResponse
        Inherits PXBaseResponse

        <JsonPropertyName("positions")>
        Public Property Positions As List(Of PXPositionDto) = New List(Of PXPositionDto)()
    End Class

    Public Class PXPositionDto
        <JsonPropertyName("id")>
        Public Property Id As Long

        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        ''' <summary>
        ''' Raw position size (unsigned).  Combine with PositionType to get signed NetPos.
        ''' /api/Position/searchOpen returns "size" not "netPos".
        ''' </summary>
        <JsonPropertyName("size")>
        Public Property Size As Integer

        ''' <summary>
        ''' Position direction from the API: 1 = long (bought), 2 = short (sold).
        ''' /api/Position/searchOpen returns "type" not a signed netPos.
        ''' </summary>
        <JsonPropertyName("type")>
        Public Property PositionType As Integer

        ''' <summary>Average fill price returned as "averagePrice" by the search endpoint.</summary>
        <JsonPropertyName("averagePrice")>
        Public Property AveragePrice As Double

        ''' <summary>Open P&amp;L from the broker (always 0 for TopStepX futures — compute locally).</summary>
        <JsonPropertyName("openPnl")>
        Public Property OpenPnL As Double

        ''' <summary>
        ''' Computed signed position: positive = long, negative = short.
        ''' Derived from PositionType (1=long, 2=short) × Size.
        ''' </summary>
        Public ReadOnly Property NetPos As Integer
            Get
                Return If(PositionType = 1, Size, -Size)
            End Get
        End Property

        ''' <summary>Alias for AveragePrice, matching legacy field name used in downstream logic.</summary>
        Public ReadOnly Property NetPrice As Double
            Get
                Return AveragePrice
            End Get
        End Property
    End Class

    ' ── Trades ──────────────────────────────────────────────────────────────────

    Public Class PXTradeSearchResponse
        Inherits PXBaseResponse

        <JsonPropertyName("trades")>
        Public Property Trades As List(Of PXTradeDto) = New List(Of PXTradeDto)()
    End Class

    Public Class PXTradeDto
        <JsonPropertyName("id")>
        Public Property Id As Long

        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        <JsonPropertyName("creationTimestamp")>
        Public Property CreationTimestamp As String = String.Empty

        <JsonPropertyName("side")>
        Public Property Side As Integer

        <JsonPropertyName("price")>
        Public Property Price As Double

        <JsonPropertyName("size")>
        Public Property Size As Integer

        <JsonPropertyName("orderId")>
        Public Property OrderId As Long
    End Class

End Namespace
