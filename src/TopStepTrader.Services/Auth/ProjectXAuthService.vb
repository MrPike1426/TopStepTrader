Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services.Auth

    ''' <summary>
    ''' Implements IAuthService for TopStepX / ProjectX.
    ''' Delegates to ProjectXTokenManager which handles JWT login and proactive refresh.
    ''' </summary>
    Public Class ProjectXAuthService
        Implements IAuthService

        Private ReadOnly _tokenManager As ProjectXTokenManager
        Private ReadOnly _logger As ILogger(Of ProjectXAuthService)

        Public Sub New(tokenManager As ProjectXTokenManager,
                       logger As ILogger(Of ProjectXAuthService))
            _tokenManager = tokenManager
            _logger = logger
        End Sub

        Public ReadOnly Property CurrentToken As String Implements IAuthService.CurrentToken
            Get
                Return _tokenManager.CurrentToken
            End Get
        End Property

        Public ReadOnly Property TokenExpiresAt As DateTimeOffset Implements IAuthService.TokenExpiresAt
            Get
                Return _tokenManager.TokenExpiresAt
            End Get
        End Property

        Public ReadOnly Property IsAuthenticated As Boolean Implements IAuthService.IsAuthenticated
            Get
                Return _tokenManager.IsAuthenticated
            End Get
        End Property

        Public Async Function LoginAsync(userName As String, apiKey As String) As Task(Of String) _
            Implements IAuthService.LoginAsync
            _logger.LogInformation("TopStepX login requested for {User}", userName)
            Await _tokenManager.ForceRefreshAsync()
            Return _tokenManager.CurrentToken
        End Function

        Public Async Function ValidateTokenAsync() As Task(Of Boolean) _
            Implements IAuthService.ValidateTokenAsync
            Try
                Dim token = Await _tokenManager.GetValidTokenAsync()
                Return Not String.IsNullOrEmpty(token)
            Catch ex As Exception
                _logger.LogWarning(ex, "TopStepX token validation failed")
                Return False
            End Try
        End Function

        Public Async Function RefreshTokenAsync() As Task(Of String) _
            Implements IAuthService.RefreshTokenAsync
            Await _tokenManager.ForceRefreshAsync()
            Return _tokenManager.CurrentToken
        End Function

    End Class

End Namespace
