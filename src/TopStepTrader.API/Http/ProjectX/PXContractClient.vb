Imports System.Net.Http
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.API.Models.Requests
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.API.RateLimiting
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.API.Http.ProjectX

    ''' <summary>
    ''' Contract search and lookup for TopStepX / ProjectX.
    ''' POST /api/Contract/available  — full tradeable list.
    ''' POST /api/Contract/search     — text search (up to 20 results).
    ''' POST /api/Contract/searchById — exact lookup by string contractId.
    ''' </summary>
    Public Class PXContractClient
        Inherits PXHttpClientBase

        Private ReadOnly _settings As ProjectXSettings

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       httpClientFactory As IHttpClientFactory,
                       tokenManager As ProjectXTokenManager,
                       rateLimiter As RateLimiter,
                       logger As ILogger(Of PXContractClient))
            MyBase.New(httpClientFactory, tokenManager, rateLimiter, logger)
            _settings = options.Value
        End Sub

        Public Function GetAvailableContractsAsync(
            Optional live As Boolean = True,
            Optional cancel As CancellationToken = Nothing) As Task(Of ContractAvailableResponse)

            Dim request = New PXContractAvailableRequest With {.Live = live, .SearchText = String.Empty}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Contract/available"
            Return PostAsync(Of PXContractAvailableRequest, ContractAvailableResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function SearchContractsAsync(
            searchText As String,
            Optional live As Boolean = True,
            Optional cancel As CancellationToken = Nothing) As Task(Of ContractAvailableResponse)

            Dim request = New PXContractAvailableRequest With {.Live = live, .SearchText = searchText}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Contract/search"
            Return PostAsync(Of PXContractAvailableRequest, ContractAvailableResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function SearchByIdAsync(
            contractId As String,
            Optional cancel As CancellationToken = Nothing) As Task(Of ContractAvailableResponse)

            Dim request = New ContractSearchByIdRequest With {.ContractId = contractId}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Contract/searchById"
            Return PostAsync(Of ContractSearchByIdRequest, ContractAvailableResponse)(endpoint, request, cancel:=cancel)
        End Function

    End Class

End Namespace
