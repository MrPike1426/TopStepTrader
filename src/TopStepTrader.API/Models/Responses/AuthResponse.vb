Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Responses

    ''' <summary>Response from POST /api/Auth/loginKey and /api/Auth/validate (ProjectX / TopStepX).</summary>
    Public Class AuthResponse
        <JsonPropertyName("token")>
        Public Property Token As String = String.Empty

        <JsonPropertyName("newToken")>
        Public Property NewToken As String = String.Empty

        <JsonPropertyName("success")>
        Public Property Success As Boolean

        <JsonPropertyName("errorCode")>
        Public Property ErrorCode As Integer

        <JsonPropertyName("errorMessage")>
        Public Property ErrorMessage As String
    End Class

End Namespace
