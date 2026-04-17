Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Responses

    Public Class ContractAvailableResponse
        <JsonPropertyName("success")>
        Public Property Success As Boolean = True
        <JsonPropertyName("errorCode")>
        Public Property ErrorCode As Integer
        <JsonPropertyName("contracts")>
        Public Property Contracts As List(Of ContractDto) = New List(Of ContractDto)()
    End Class

    Public Class ContractDto
        ''' <summary>ProjectX contract ID — API field name is "id" (confirmed via Swagger).</summary>
        <JsonPropertyName("id")>
        Public Property ContractId As String = String.Empty
        <JsonPropertyName("name")>
        Public Property Name As String = String.Empty
        <JsonPropertyName("description")>
        Public Property Description As String = String.Empty
        <JsonPropertyName("tickSize")>
        Public Property TickSize As Decimal
        <JsonPropertyName("tickValue")>
        Public Property TickValue As Decimal
        ''' <summary>
        ''' Minimum stop distance in ticks as returned by the ProjectX contract rules endpoint.
        ''' Zero/Nothing when the API does not provide this field.
        ''' </summary>
        <JsonPropertyName("minInitialMarginTicks")>
        Public Property MinInitialMarginTicks As Integer

        Public ReadOnly Property DisplayLabel As String
            Get
                Return $"{Name}  [{ContractId}]"
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return DisplayLabel
        End Function
    End Class

End Namespace
