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
    ''' Order and position management for TopStepX / ProjectX.
    ''' POST /api/Order/place, /cancel, /modify, /search, /searchOpen
    ''' POST /api/Position/searchOpen, /closeContract, /partialCloseContract
    ''' POST /api/Trade/search
    ''' </summary>
    Public Class PXOrderClient
        Inherits PXHttpClientBase

        Private ReadOnly _settings As ProjectXSettings

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       httpClientFactory As IHttpClientFactory,
                       tokenManager As ProjectXTokenManager,
                       rateLimiter As RateLimiter,
                       logger As ILogger(Of PXOrderClient))
            MyBase.New(httpClientFactory, tokenManager, rateLimiter, logger)
            _settings = options.Value
        End Sub

        ' ── Orders ──────────────────────────────────────────────────────────────────

        Public Function PlaceOrderAsync(request As PXPlaceOrderRequest,
                                        Optional cancel As CancellationToken = Nothing) As Task(Of PXPlaceOrderResponse)
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Order/place"
            Return PostAsync(Of PXPlaceOrderRequest, PXPlaceOrderResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function CancelOrderAsync(accountId As Long, orderId As Long,
                                         Optional cancel As CancellationToken = Nothing) As Task(Of PXBaseResponse)
            Dim request = New PXCancelOrderRequest With {.AccountId = accountId, .OrderId = orderId}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Order/cancel"
            Return PostAsync(Of PXCancelOrderRequest, PXBaseResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function ModifyOrderAsync(request As PXModifyOrderRequest,
                                         Optional cancel As CancellationToken = Nothing) As Task(Of PXBaseResponse)
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Order/modify"
            Return PostAsync(Of PXModifyOrderRequest, PXBaseResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function SearchOrdersAsync(accountId As Long,
                                          Optional startTimestamp As Long? = Nothing,
                                          Optional endTimestamp As Long? = Nothing,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of PXOrderSearchResponse)
            Dim request = New PXSearchOrderRequest With {
                .AccountId = accountId,
                .StartTimestamp = startTimestamp,
                .EndTimestamp = endTimestamp
            }
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Order/search"
            Return PostAsync(Of PXSearchOrderRequest, PXOrderSearchResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function SearchOpenOrdersAsync(accountId As Long,
                                              Optional cancel As CancellationToken = Nothing) As Task(Of PXOrderSearchResponse)
            Dim request = New PXAccountIdRequest With {.AccountId = accountId}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Order/searchOpen"
            Return PostAsync(Of PXAccountIdRequest, PXOrderSearchResponse)(endpoint, request, cancel:=cancel)
        End Function

        ' ── Positions ───────────────────────────────────────────────────────────────

        Public Function SearchOpenPositionsAsync(accountId As Long,
                                                 Optional cancel As CancellationToken = Nothing) As Task(Of PXPositionSearchResponse)
            Dim request = New PXAccountIdRequest With {.AccountId = accountId}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Position/searchOpen"
            Return PostAsync(Of PXAccountIdRequest, PXPositionSearchResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function CloseContractAsync(accountId As Long, contractId As String,
                                           Optional cancel As CancellationToken = Nothing) As Task(Of PXBaseResponse)
            Dim request = New PXCloseContractRequest With {.AccountId = accountId, .ContractId = contractId}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Position/closeContract"
            Return PostAsync(Of PXCloseContractRequest, PXBaseResponse)(endpoint, request, cancel:=cancel)
        End Function

        Public Function PartialCloseAsync(accountId As Long, contractId As String, size As Integer,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of PXBaseResponse)
            Dim request = New PXPartialCloseRequest With {.AccountId = accountId, .ContractId = contractId, .Size = size}
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Position/partialCloseContract"
            Return PostAsync(Of PXPartialCloseRequest, PXBaseResponse)(endpoint, request, cancel:=cancel)
        End Function

        ' ── Trades ──────────────────────────────────────────────────────────────────

        Public Function SearchTradesAsync(accountId As Long,
                                          Optional startTimestamp As Long? = Nothing,
                                          Optional endTimestamp As Long? = Nothing,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of PXTradeSearchResponse)
            Dim request = New PXSearchOrderRequest With {
                .AccountId = accountId,
                .StartTimestamp = startTimestamp,
                .EndTimestamp = endTimestamp
            }
            Dim endpoint = $"{_settings.RestBaseUrl}/api/Trade/search"
            Return PostAsync(Of PXSearchOrderRequest, PXTradeSearchResponse)(endpoint, request, cancel:=cancel)
        End Function

    End Class

End Namespace
