Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Requests

    ' ── ProjectX / TopStepX request models ──────────────────────────────────────

    Public Class ContractSearchByIdRequest
        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty
    End Class

    Public Class PXAccountIdRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long
    End Class

    Public Class PXCancelOrderRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("orderId")>
        Public Property OrderId As Long
    End Class

    Public Class PXModifyOrderRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("orderId")>
        Public Property OrderId As Long

        ''' <summary>
        ''' Order type: 1=Limit (TP), 4=Stop (SL). Must match the resting bracket type.
        ''' Gemini/Tradovate: required in the modify payload or the API may reject silently.
        ''' </summary>
        <JsonPropertyName("type")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property OrderType As Integer?

        <JsonPropertyName("size")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Size As Integer?

        <JsonPropertyName("limitPrice")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property LimitPrice As Double?

        <JsonPropertyName("stopPrice")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property StopPrice As Double?

        <JsonPropertyName("trailPrice")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property TrailPrice As Double?
    End Class

    ''' <summary>
    ''' POST /api/Order/place for TopStepX.
    ''' type: 1=Limit, 2=Market, 4=Stop, 5=TrailingStop, 6=JoinBid, 7=JoinAsk
    ''' side: 0=Buy(Bid), 1=Sell(Ask)
    ''' </summary>
    Public Class PXPlaceOrderRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        <JsonPropertyName("type")>
        Public Property OrderType As Integer = 2    ' Market by default

        <JsonPropertyName("side")>
        Public Property Side As Integer             ' 0=Buy, 1=Sell

        <JsonPropertyName("size")>
        Public Property Size As Integer = 1

        <JsonPropertyName("limitPrice")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property LimitPrice As Double?

        <JsonPropertyName("stopPrice")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property StopPrice As Double?

        <JsonPropertyName("trailPrice")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property TrailPrice As Double?

        <JsonPropertyName("customTag")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property CustomTag As String

        <JsonPropertyName("stopLossBracket")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property StopLossBracket As PXBracketOrder

        <JsonPropertyName("takeProfitBracket")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property TakeProfitBracket As PXBracketOrder
    End Class

    Public Class PXBracketOrder
        <JsonPropertyName("ticks")>
        Public Property Ticks As Integer

        <JsonPropertyName("type")>
        Public Property OrderType As Integer = 1
    End Class

    Public Class PXSearchOrderRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("startTimestamp")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property StartTimestamp As Long?

        <JsonPropertyName("endTimestamp")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property EndTimestamp As Long?
    End Class

    Public Class PXCloseContractRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty
    End Class

    Public Class PXPartialCloseRequest
        <JsonPropertyName("accountId")>
        Public Property AccountId As Long

        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        <JsonPropertyName("size")>
        Public Property Size As Integer
    End Class

    ''' <summary>POST /api/Account/search — returns active TopStepX accounts.</summary>
    Public Class PXAccountSearchRequest
        <JsonPropertyName("onlyActiveAccounts")>
        Public Property OnlyActiveAccounts As Boolean = True
    End Class

    ''' <summary>POST /api/Contract/available and /api/Contract/search.</summary>
    Public Class PXContractAvailableRequest
        <JsonPropertyName("live")>
        Public Property Live As Boolean = False

        <JsonPropertyName("searchText")>
        Public Property SearchText As String = String.Empty
    End Class

    ''' <summary>POST /api/History/retrieveBars.</summary>
    Public Class PXRetrieveBarsRequest
        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        <JsonPropertyName("unit")>
        Public Property Unit As Integer

        <JsonPropertyName("unitNumber")>
        Public Property UnitNumber As Integer

        <JsonPropertyName("limit")>
        Public Property Limit As Integer

        <JsonPropertyName("live")>
        Public Property Live As Boolean = False

        <JsonPropertyName("startTime")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property StartTime As String

        <JsonPropertyName("endTime")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property EndTime As String

        ''' <summary>Required by the API. False = exclude the still-forming current bar.</summary>
        <JsonPropertyName("includePartialBar")>
        Public Property IncludePartialBar As Boolean = False
    End Class

End Namespace
