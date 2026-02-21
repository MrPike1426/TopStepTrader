Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Requests

    Public Class RetrieveBarsRequest
        <JsonPropertyName("contractId")>
        Public Property ContractId As String = String.Empty

        ''' <summary>1=1min, 2=5min, 3=15min, 4=30min, 5=1hr, 6=1day</summary>
        <JsonPropertyName("unit")>
        Public Property Unit As Integer = 2

        ''' <summary>Same as Unit — required by API as separate field</summary>
        <JsonPropertyName("unitNumber")>
        Public Property UnitNumber As Integer = 2

        ''' <summary>Maximum number of bars to return</summary>
        <JsonPropertyName("limit")>
        Public Property Limit As Integer = 500

        ''' <summary>false = simulated/paper account data</summary>
        <JsonPropertyName("live")>
        Public Property Live As Boolean = False

        <JsonPropertyName("startTime")>
        Public Property StartTime As String = String.Empty

        <JsonPropertyName("endTime")>
        Public Property EndTime As String = String.Empty
    End Class

End Namespace
