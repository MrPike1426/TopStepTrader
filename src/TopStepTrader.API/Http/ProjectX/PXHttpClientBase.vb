Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.RateLimiting
Imports TopStepTrader.Core.Logging

Namespace TopStepTrader.API.Http.ProjectX

    ''' <summary>
    ''' Base class for all TopStepX / ProjectX HTTP clients.
    ''' All ProjectX endpoints are POST-only with Bearer JWT auth.
    ''' Rate limits: 200 req/60s general, 50 req/30s for history (enforced by RateLimiter slots).
    ''' </summary>
    Public MustInherit Class PXHttpClientBase

        Protected ReadOnly HttpClient As HttpClient
        Protected ReadOnly TokenManager As ProjectXTokenManager
        Protected ReadOnly RateLimiter As RateLimiter
        Protected ReadOnly Logger As ILogger

        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True
        }

        Protected Sub New(httpClientFactory As IHttpClientFactory,
                          tokenManager As ProjectXTokenManager,
                          rateLimiter As RateLimiter,
                          logger As ILogger)
            HttpClient = httpClientFactory.CreateClient("ProjectX")
            Me.TokenManager = tokenManager
            Me.RateLimiter = rateLimiter
            Me.Logger = logger
        End Sub

        ''' <summary>Authenticated, rate-limited POST with JSON body.</summary>
        Protected Async Function PostAsync(Of TRequest, TResponse)(
            endpoint As String,
            request As TRequest,
            Optional useHistoryLimit As Boolean = False,
            Optional cancel As CancellationToken = Nothing) As Task(Of TResponse)

            If useHistoryLimit Then
                Await RateLimiter.WaitForHistorySlotAsync(cancel)
            Else
                Await RateLimiter.WaitForGeneralSlotAsync(cancel)
            End If

            Dim token = Await TokenManager.GetValidTokenAsync(cancel)
            Dim httpRequest = New HttpRequestMessage(HttpMethod.Post, endpoint)
            httpRequest.Headers.Authorization = New AuthenticationHeaderValue("Bearer", token)

            Dim json = JsonSerializer.Serialize(request)
            httpRequest.Content = New StringContent(json, Encoding.UTF8, "application/json")

            Logger.LogDebug("PX POST {Endpoint} → {Body}", endpoint, json)
            Try : DebugLog.Log($"PX POST {endpoint} → {json}") : Catch : End Try

            Dim httpResponse = Await HttpClient.SendAsync(httpRequest, cancel)

            If httpResponse.StatusCode = Net.HttpStatusCode.TooManyRequests Then
                Logger.LogWarning("PX rate limit hit on {Endpoint}, backing off 5s", endpoint)
                Await Task.Delay(5000, cancel)
                Dim retryReq = New HttpRequestMessage(HttpMethod.Post, endpoint)
                retryReq.Headers.Authorization = New AuthenticationHeaderValue("Bearer", token)
                retryReq.Content = New StringContent(json, Encoding.UTF8, "application/json")
                httpResponse = Await HttpClient.SendAsync(retryReq, cancel)
            End If

            Dim body = Await httpResponse.Content.ReadAsStringAsync(cancel)

            If Not httpResponse.IsSuccessStatusCode Then
                Logger.LogError("PX API {Status} on {Endpoint}: {Body}",
                                CInt(httpResponse.StatusCode), endpoint, body)
                Throw New HttpRequestException(
                    $"ProjectX {CInt(httpResponse.StatusCode)} {httpResponse.ReasonPhrase} — {body}",
                    Nothing, httpResponse.StatusCode)
            End If

            Logger.LogDebug("PX Response ← {Endpoint} {Body}", endpoint, body)
            Try : DebugLog.Log($"PX Response {endpoint} ← {body}") : Catch : End Try

            Dim result = JsonSerializer.Deserialize(Of TResponse)(body, JsonOptions)
            If result Is Nothing Then
                Throw New InvalidOperationException($"Null response from {endpoint}")
            End If
            Return result
        End Function

    End Class

End Namespace
