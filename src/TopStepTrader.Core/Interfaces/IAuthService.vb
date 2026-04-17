Namespace TopStepTrader.Core.Interfaces

    Public Interface IAuthService
        Function LoginAsync(userName As String, apiKey As String) As Task(Of String)
        Function ValidateTokenAsync() As Task(Of Boolean)
        Function RefreshTokenAsync() As Task(Of String)
        ReadOnly Property CurrentToken As String
        ReadOnly Property TokenExpiresAt As DateTimeOffset
        ReadOnly Property IsAuthenticated As Boolean
    End Interface

End Namespace
