Imports System.Text.Json.Serialization

Namespace TopStepTrader.API.Models.Requests

    ''' <summary>Body for POST /api/Auth/loginKey (ProjectX / TopStepX).</summary>
    Public Class LoginKeyRequest
        <JsonPropertyName("userName")>
        Public Property UserName As String = String.Empty

        <JsonPropertyName("apiKey")>
        Public Property ApiKey As String = String.Empty
    End Class

End Namespace
