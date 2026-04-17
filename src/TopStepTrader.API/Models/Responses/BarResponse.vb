Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Responses

    Public Class BarResponse
        Public Property Success As Boolean = True
        Public Property ErrorMessage As String = String.Empty
        Public Property Bars As List(Of BarDto) = New List(Of BarDto)()
    End Class

    Public Class BarDto
        <JsonPropertyName("t")>
        Public Property Timestamp As String = String.Empty

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
