Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.API.Models.Requests
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.API

    ''' <summary>
    ''' Manages the TopStepX / ProjectX JWT session token.
    ''' Credentials are read from IApiKeyStore (TopStepXUsername + TopStepXApiKey).
    ''' Thread-safe: SemaphoreSlim prevents concurrent refresh races.
    ''' Token is proactively refreshed <see cref="ProjectXSettings.TokenRefreshMinutesBeforeExpiry"/>
    ''' minutes before the 24-hour expiry.
    ''' </summary>
    Public Class ProjectXTokenManager

        Private ReadOnly _settings As ProjectXSettings
        Private ReadOnly _keyStore As IApiKeyStore
        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _logger As ILogger(Of ProjectXTokenManager)
        Private ReadOnly _semaphore As New SemaphoreSlim(1, 1)

        Private Shared ReadOnly JsonOpts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}

        Private _token As String = String.Empty
        Private _expiresAt As DateTimeOffset = DateTimeOffset.MinValue

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       keyStore As IApiKeyStore,
                       httpClientFactory As IHttpClientFactory,
                       logger As ILogger(Of ProjectXTokenManager))
            _settings = options.Value
            _keyStore = keyStore
            _httpClient = httpClientFactory.CreateClient("ProjectX")
            _logger = logger
        End Sub

        ''' <summary>
        ''' Returns a valid JWT, refreshing automatically when within
        ''' <see cref="ProjectXSettings.TokenRefreshMinutesBeforeExpiry"/> of expiry.
        ''' </summary>
        ''' <summary>
        ''' Returns a valid JWT, refreshing automatically when within
        ''' <see cref="ProjectXSettings.TokenRefreshMinutesBeforeExpiry"/> of expiry.
        ''' Returns an empty string (rather than throwing) when credentials are not
        ''' configured or the auth call fails — this allows SignalR hubs to receive
        ''' a 401 and disconnect gracefully instead of crashing the app.
        ''' </summary>
        Public Async Function GetValidTokenAsync(Optional cancel As CancellationToken = Nothing) As Task(Of String)
            Dim buffer = _settings.TokenRefreshMinutesBeforeExpiry
            If Not String.IsNullOrEmpty(_token) AndAlso
               DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-buffer) Then
                Return _token
            End If

            Await _semaphore.WaitAsync(cancel)
            Try
                If Not String.IsNullOrEmpty(_token) AndAlso
                   DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-buffer) Then
                    Return _token
                End If

                Await RefreshTokenInternalAsync(cancel)
                Return _token
            Catch ex As Exception
                _logger.LogWarning(ex, "TopStepX GetValidTokenAsync failed — returning empty token")
                Return String.Empty   ' Hub/HTTP callers will receive 401; app stays alive
            Finally
                _semaphore.Release()
            End Try
        End Function

        Public ReadOnly Property CurrentToken As String
            Get
                Return _token
            End Get
        End Property

        Public ReadOnly Property TokenExpiresAt As DateTimeOffset
            Get
                Return _expiresAt
            End Get
        End Property

        Public ReadOnly Property IsAuthenticated As Boolean
            Get
                Return Not String.IsNullOrEmpty(_token) AndAlso DateTimeOffset.UtcNow < _expiresAt
            End Get
        End Property

        Public ReadOnly Property IsConfigured As Boolean
            Get
                Dim keys = _keyStore.Load()
                Return Not String.IsNullOrEmpty(keys.TopStepXUsername) AndAlso
                       Not String.IsNullOrEmpty(keys.TopStepXApiKey)
            End Get
        End Property

        ''' <summary>Force a token refresh (called by TokenRefreshWorker background service).</summary>
        Public Async Function ForceRefreshAsync(Optional cancel As CancellationToken = Nothing) As Task
            Await _semaphore.WaitAsync(cancel)
            Try
                Await RefreshTokenInternalAsync(cancel)
            Finally
                _semaphore.Release()
            End Try
        End Function

        Private Async Function RefreshTokenInternalAsync(cancel As CancellationToken) As Task
            Dim keys = _keyStore.Load()

            If String.IsNullOrEmpty(keys.TopStepXUsername) OrElse String.IsNullOrEmpty(keys.TopStepXApiKey) Then
                Throw New InvalidOperationException(
                    "TopStepX credentials are not configured. Enter them in the API Keys view.")
            End If

            _logger.LogInformation("Refreshing TopStepX session token for user {User}", keys.TopStepXUsername)

            Dim request = New LoginKeyRequest With {
                .UserName = keys.TopStepXUsername,
                .ApiKey = keys.TopStepXApiKey
            }

            Dim json = JsonSerializer.Serialize(request)
            Dim content = New StringContent(json, Encoding.UTF8, "application/json")
            Dim url = $"{_settings.RestBaseUrl}/api/Auth/loginKey"

            Dim response = Await _httpClient.PostAsync(url, content, cancel)
            response.EnsureSuccessStatusCode()

            Dim body = Await response.Content.ReadAsStringAsync(cancel)
            _logger.LogDebug("TopStepX auth raw response: {Body}", body)

            Dim auth = JsonSerializer.Deserialize(Of AuthResponse)(body, JsonOpts)

            If auth Is Nothing OrElse Not auth.Success Then
                Dim err = If(Not String.IsNullOrEmpty(auth?.ErrorMessage), auth.ErrorMessage, "Unknown error")
                Dim code = If(auth IsNot Nothing, auth.ErrorCode, -1)
                _logger.LogError(
                    "TopStepX token refresh failed: {Error} (errorCode={Code}) | raw={Raw}",
                    err, code, body)
                Throw New InvalidOperationException(
                    $"TopStepX authentication failed (errorCode={code}): {err}" &
                    $"{Environment.NewLine}Check your username and API key in the API Keys view.")
            End If

            ' Some responses return the token in "newToken" rather than "token"
            Dim resolvedToken = If(Not String.IsNullOrEmpty(auth.Token), auth.Token, auth.NewToken)
            If String.IsNullOrEmpty(resolvedToken) Then
                _logger.LogError("TopStepX auth succeeded but token is empty. Raw: {Raw}", body)
                Throw New InvalidOperationException("TopStepX authentication succeeded but returned an empty token.")
            End If

            _token = resolvedToken
            _expiresAt = DateTimeOffset.UtcNow.AddHours(24)
            _logger.LogInformation("TopStepX token refreshed. Expires at {Expiry:O}", _expiresAt)
        End Function

    End Class

End Namespace
