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
    ''' Historical bar retrieval for TopStepX / ProjectX.
    ''' POST /api/History/retrieveBars — rate-limited to 50 req/30s.
    ''' Unit codes: 1=1min, 2=5min, 3=15min, 4=30min, 5=1hr, 6=1day.
    ''' </summary>
    Public Class PXHistoryClient
        Inherits PXHttpClientBase

        Private ReadOnly _settings As ProjectXSettings

        Public Sub New(options As IOptions(Of ProjectXSettings),
                       httpClientFactory As IHttpClientFactory,
                       tokenManager As ProjectXTokenManager,
                       rateLimiter As RateLimiter,
                       logger As ILogger(Of PXHistoryClient))
            MyBase.New(httpClientFactory, tokenManager, rateLimiter, logger)
            _settings = options.Value
        End Sub

        ''' <summary>
        ''' Fetch historical bars for a contract.
        ''' Uses the history rate-limit slot (50/30s) automatically.
        ''' </summary>
        ''' <param name="contractId">e.g. "CON.F.US.MES.H26"</param>
        ''' <param name="unit">1=1min, 2=5min, 3=15min, 4=30min, 5=1hr, 6=1day</param>
        ''' <param name="unitNumber">Same value as unit (required by API)</param>
        ''' <param name="limit">Max bars to return per call</param>
        ''' <param name="live">False = simulated/paper data</param>
        ''' <param name="includePartialBar">
        ''' True returns the still-forming current bar as the most recent entry — the bar's
        ''' <c>Close</c> tracks the latest print rather than the prior fully-closed bar.
        ''' Used by the live P&amp;L bar fallback so a stalled WebSocket loses at most one
        ''' polling interval of price freshness instead of up to one full bar period.
        ''' </param>
        Public Function RetrieveBarsAsync(
            contractId As String,
            unit As Integer,
            unitNumber As Integer,
            limit As Integer,
            Optional live As Boolean = False,
            Optional startTime As DateTimeOffset? = Nothing,
            Optional endTime As DateTimeOffset? = Nothing,
            Optional cancel As CancellationToken = Nothing,
            Optional includePartialBar As Boolean = False) As Task(Of BarResponse)

            Dim request As New PXRetrieveBarsRequest With {
                .ContractId = contractId,
                .Unit = unit,
                .UnitNumber = unitNumber,
                .Limit = limit,
                .Live = live,
                .StartTime = If(startTime.HasValue, startTime.Value.ToString("O"), Nothing),
                .EndTime = If(endTime.HasValue, endTime.Value.ToString("O"), Nothing),
                .IncludePartialBar = includePartialBar
            }

            Dim endpoint = $"{_settings.RestBaseUrl}/api/History/retrieveBars"
            Return PostAsync(Of PXRetrieveBarsRequest, BarResponse)(endpoint, request,
                                                                    useHistoryLimit:=True,
                                                                    cancel:=cancel)
        End Function

    End Class

End Namespace
