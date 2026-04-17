Imports System.Net.Http
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.API.Models.Requests
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.API.RateLimiting
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.API.Http.ProjectX

    ''' <summary>POST /api/Account/search — returns active TopStepX accounts.</summary>
    Public Class PXAccountClient
        Inherits PXHttpClientBase

        Private ReadOnly _settings As ProjectXSettings

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       httpClientFactory As IHttpClientFactory,
                       tokenManager As ProjectXTokenManager,
                       rateLimiter As RateLimiter,
                       logger As ILogger(Of PXAccountClient))
            MyBase.New(httpClientFactory, tokenManager, rateLimiter, logger)
            _settings = options.Value
        End Sub

        Public Function SearchAccountsAsync(
            Optional onlyActive As Boolean = True,
            Optional cancel As CancellationToken = Nothing) As Task(Of PXAccountSearchResponse)

            Dim request = New PXAccountSearchRequest With {.OnlyActiveAccounts = onlyActive}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Account/search"
            Return PostAsync(Of PXAccountSearchRequest, PXAccountSearchResponse)(endpoint, request, cancel:=cancel)
        End Function

    End Class

End Namespace
