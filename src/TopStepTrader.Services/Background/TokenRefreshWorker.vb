Imports System.Threading
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' Background worker that keeps authentication current.
    ''' eToro:    no-op — static header keys never expire.
    ''' TopStepX: proactively refreshes the JWT token 30 minutes before its 24-hour expiry.
    ''' Checks every 15 minutes whether a refresh is needed.
    ''' </summary>
    Public Class TokenRefreshWorker
        Implements IHostedService, IDisposable

        Private ReadOnly _keyStore As IApiKeyStore
        Private ReadOnly _tokenManager As ProjectXTokenManager
        Private ReadOnly _logger As ILogger(Of TokenRefreshWorker)
        Private ReadOnly _settings As ProjectXSettings
        Private _timer As Timer

        Private Const CheckIntervalMinutes As Integer = 15

        Public Sub New(keyStore As IApiKeyStore,
                       tokenManager As ProjectXTokenManager,
                       settings As Microsoft.Extensions.Options.IOptions(Of ProjectXSettings),
                       logger As ILogger(Of TokenRefreshWorker))
            _keyStore = keyStore
            _tokenManager = tokenManager
            _settings = settings.Value
            _logger = logger
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync

            ' TopStepX is the only broker — always start the JWT refresh timer.
            _logger.LogInformation(
                "TokenRefreshWorker: TopStepX mode — will check token every {Interval}m.",
                CheckIntervalMinutes)
            ' Perform initial login immediately
            _timer = New Timer(AddressOf RefreshCallback, Nothing,
                               TimeSpan.Zero,
                               TimeSpan.FromMinutes(CheckIntervalMinutes))

            Return Task.CompletedTask
        End Function

        Private Async Sub RefreshCallback(state As Object)
            Try
                If Not _tokenManager.IsConfigured Then
                    _logger.LogWarning("TokenRefreshWorker: TopStepX credentials not yet configured — skipping token refresh.")
                    Return
                End If

                Dim minutesUntilExpiry = (_tokenManager.TokenExpiresAt - DateTimeOffset.UtcNow).TotalMinutes

                If Not _tokenManager.IsAuthenticated OrElse
                   minutesUntilExpiry < _settings.TokenRefreshMinutesBeforeExpiry Then
                    _logger.LogInformation(
                        "TokenRefreshWorker: refreshing TopStepX token ({Min:F0}m until expiry).",
                        minutesUntilExpiry)
                    Await _tokenManager.ForceRefreshAsync()
                End If
            Catch ex As Exception
                _logger.LogError(ex, "TokenRefreshWorker: failed to refresh TopStepX token.")
            End Try
        End Sub

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _timer?.Change(Timeout.Infinite, 0)
            Return Task.CompletedTask
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _timer?.Dispose()
        End Sub

    End Class

End Namespace
