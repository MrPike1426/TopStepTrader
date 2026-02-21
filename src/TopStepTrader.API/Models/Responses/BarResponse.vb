Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Responses

    Public Class BarResponse
        <JsonPropertyName("bars")>
        Public Property Bars As List(Of BarDto) = New List(Of BarDto)()

        <JsonPropertyName("success")>
        Public Property Success As Boolean

        <JsonPropertyName("errorCode")>
        Public Property ErrorCode As Integer

        <JsonPropertyName("errorMessage")>
        Public Property ErrorMessage As String
    End Class

    Public Class BarDto
        <JsonPropertyName("t")>
        Public Property Timestamp As String = String.Empty  ' ISO 8601 datetime

        <JsonPropertyName("o")>
        Public Property Open As Double

        <JsonPropertyName("h")>
        Public Property High As Double

        <JsonPropertyName("l")>
        Public Property Low As Double

        <JsonPropertyName("c")>
        Public Property Close As Double

        <JsonPropertyName("v")>
        Public Property Volume As Long
    End Class

End Namespace
