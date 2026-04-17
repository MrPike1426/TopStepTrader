Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.API.Http

    ''' <summary>
    ''' Fetches historical OHLCV bars from Yahoo Finance (free, no API key required).
    ''' Used exclusively for backtest bar downloads.
    ''' Endpoint: GET https://query1.finance.yahoo.com/v8/finance/chart/{symbol}
    ''' Limits: 1m = 7 days, 5m/15m/30m = 60 days, 60m = 730 days, 1d = unlimited.
    ''' </summary>
    Public Class YahooFinanceHistoryClient

        ' query2 is generally more stable and bypasses GDPR consent redirects on query1
        Private Const BaseUrl As String = "https://query2.finance.yahoo.com"

        ''' <summary>Maps BarCollectionService unit codes to Yahoo Finance interval strings.</summary>
        Public Shared ReadOnly IntervalMap As New Dictionary(Of Integer, String) From {
            {1, "1m"},
            {2, "5m"},
            {3, "15m"},
            {4, "30m"},
            {5, "60m"},
            {6, "1d"}
        }

        Private ReadOnly _httpClientFactory As IHttpClientFactory
        Private ReadOnly _logger As ILogger(Of YahooFinanceHistoryClient)

        Public Sub New(httpClientFactory As IHttpClientFactory,
                       logger As ILogger(Of YahooFinanceHistoryClient))
            _httpClientFactory = httpClientFactory
            _logger = logger
        End Sub

        ''' <summary>
        ''' Retrieves all available bars for the given symbol and date range in a single HTTP call.
        ''' </summary>
        ''' <param name="contractId">eToro symbol (e.g. "SPX500") — resolved via FavouriteContracts.YahooSymbol.</param>
        ''' <param name="unit">Bar timeframe unit code: 1=1m, 2=5m, 3=15m, 4=30m, 5=60m, 6=1d</param>
        ''' <param name="startTime">Start of date range (UTC).</param>
        ''' <param name="endTime">End of date range (UTC).</param>
        Public Async Function RetrieveBarsAsync(
                contractId As String,
                unit As Integer,
                startTime As DateTimeOffset,
                endTime As DateTimeOffset,
                Optional cancel As CancellationToken = Nothing) As Task(Of BarResponse)

            ' Resolve Yahoo symbol from FavouriteContracts
            Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
            Dim yahooSymbol As String
            If fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.YahooSymbol) Then
                yahooSymbol = fav.YahooSymbol
            Else
                ' Fall back: use contractId as-is (may work for stocks like "AAPL")
                yahooSymbol = contractId
                _logger.LogWarning(
                    "YahooFinanceHistoryClient: '{Id}' not found in FavouriteContracts — using as raw Yahoo symbol.",
                    contractId)
            End If

            Dim interval = If(IntervalMap.ContainsKey(unit), IntervalMap(unit), "5m")

            ' Build URL. Yahoo Finance intraday limits: 1m=7d, 5m/15m/30m=60d, 60m=730d.
            ' Using range= instead of period1/period2 for intraday is more reliable;
            ' for daily bars use period1/period2 to respect the user's date range.
            Dim encodedSymbol = Uri.EscapeDataString(yahooSymbol)
            Dim url As String
            If unit = 6 Then
                ' Daily: explicit date range
                Dim period1 = startTime.ToUnixTimeSeconds()
                Dim period2 = endTime.ToUnixTimeSeconds()
                url = $"{BaseUrl}/v8/finance/chart/{encodedSymbol}" &
                      $"?interval=1d&period1={period1}&period2={period2}&includePrePost=false"
            ElseIf unit = 5 Then
                ' 60-min: Yahoo allows up to 730 days
                url = $"{BaseUrl}/v8/finance/chart/{encodedSymbol}" &
                      $"?interval=60m&range=730d&includePrePost=false"
            ElseIf unit = 1 Then
                ' 1m: Yahoo allows up to 7 days only — 60d causes a "no data" error
                url = $"{BaseUrl}/v8/finance/chart/{encodedSymbol}" &
                      $"?interval=1m&range=7d&includePrePost=false"
            Else
                ' 5m/15m/30m: Yahoo allows up to 60 days
                url = $"{BaseUrl}/v8/finance/chart/{encodedSymbol}" &
                      $"?interval={interval}&range=60d&includePrePost=false"
            End If

            _logger.LogInformation(
                "YahooFinanceHistoryClient: GET {Url}",
                url)

            Try
                Dim client = _httpClientFactory.CreateClient("Yahoo")
                Dim response = Await client.GetAsync(url, cancel)

                If Not response.IsSuccessStatusCode Then
                    Dim body = Await response.Content.ReadAsStringAsync(cancel)
                    Dim preview = If(body.Length > 300, body.Substring(0, 300), body)
                    _logger.LogWarning(
                        "YahooFinanceHistoryClient: HTTP {Status} for {Symbol}. Response: {Body}",
                        CInt(response.StatusCode), yahooSymbol, preview)
                    Return New BarResponse With {
                        .Success = False,
                        .ErrorMessage = $"Yahoo Finance returned HTTP {CInt(response.StatusCode)} for symbol '{yahooSymbol}'. " &
                                        "Check that the symbol mapping is correct."
                    }
                End If

                Dim json = Await response.Content.ReadAsStringAsync(cancel)
                Return ParseResponse(json, yahooSymbol)

            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                _logger.LogError(ex, "YahooFinanceHistoryClient: error fetching bars for {Symbol}", yahooSymbol)
                Return New BarResponse With {
                    .Success = False,
                    .ErrorMessage = $"Yahoo Finance request failed: {ex.Message}"
                }
            End Try
        End Function

        ' ── JSON parsing ──────────────────────────────────────────────────────────────

        Private Function ParseResponse(json As String, symbol As String) As BarResponse
            Dim opts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
            Dim parsed = JsonSerializer.Deserialize(Of YahooChartResponse)(json, opts)

            Dim chartResult = parsed?.Chart?.Result?.FirstOrDefault()
            If chartResult Is Nothing OrElse
               chartResult.Timestamp Is Nothing OrElse
               chartResult.Timestamp.Count = 0 Then
                ' Check for Yahoo error message
                Dim errMsg = parsed?.Chart?.ChartError?.Description
                Return New BarResponse With {
                    .Success = False,
                    .ErrorMessage = If(String.IsNullOrEmpty(errMsg),
                        $"Yahoo Finance returned no data for '{symbol}'.",
                        $"Yahoo Finance error for '{symbol}': {errMsg}")
                }
            End If

            Dim quote = chartResult.Indicators?.Quote?.FirstOrDefault()
            If quote Is Nothing Then
                Return New BarResponse With {
                    .Success = False,
                    .ErrorMessage = $"Yahoo Finance response contained no OHLCV data for '{symbol}'."
                }
            End If

            Dim bars As New List(Of BarDto)
            For i As Integer = 0 To chartResult.Timestamp.Count - 1
                ' Skip bars with null prices (occur during market-closed periods for some symbols)
                If quote.Open Is Nothing OrElse i >= quote.Open.Count Then Continue For
                If Not quote.Open(i).HasValue OrElse Not quote.Close(i).HasValue Then Continue For

                Dim ts = DateTimeOffset.FromUnixTimeSeconds(chartResult.Timestamp(i))
                Dim vol As Long = 0L
                If quote.Volume IsNot Nothing AndAlso i < quote.Volume.Count AndAlso
                   quote.Volume(i).HasValue Then
                    vol = quote.Volume(i).Value
                End If

                bars.Add(New BarDto With {
                    .Timestamp = ts.ToString("o"),
                    .Open = quote.Open(i).GetValueOrDefault(),
                    .High = If(quote.High IsNot Nothing AndAlso i < quote.High.Count, quote.High(i).GetValueOrDefault(), quote.Open(i).GetValueOrDefault()),
                    .Low = If(quote.Low IsNot Nothing AndAlso i < quote.Low.Count, quote.Low(i).GetValueOrDefault(), quote.Open(i).GetValueOrDefault()),
                    .Close = quote.Close(i).GetValueOrDefault(),
                    .Volume = vol
                })
            Next

            _logger.LogInformation(
                "YahooFinanceHistoryClient: parsed {Count} bars for {Symbol}", bars.Count, symbol)

            Return New BarResponse With {
                .Success = True,
                .Bars = bars
            }
        End Function

    End Class

    ' ── Yahoo Finance v8 chart API response DTOs ──────────────────────────────────

    Friend Class YahooChartResponse
        <JsonPropertyName("chart")>
        Public Property Chart As YahooChart
    End Class

    Friend Class YahooChart
        <JsonPropertyName("result")>
        Public Property Result As List(Of YahooChartResult)
        <JsonPropertyName("error")>
        Public Property ChartError As YahooError
    End Class

    Friend Class YahooError
        <JsonPropertyName("code")>
        Public Property Code As String
        <JsonPropertyName("description")>
        Public Property Description As String
    End Class

    Friend Class YahooChartResult
        <JsonPropertyName("timestamp")>
        Public Property Timestamp As List(Of Long)
        <JsonPropertyName("indicators")>
        Public Property Indicators As YahooIndicators
    End Class

    Friend Class YahooIndicators
        <JsonPropertyName("quote")>
        Public Property Quote As List(Of YahooQuote)
    End Class

    Friend Class YahooQuote
        <JsonPropertyName("open")>
        Public Property Open As List(Of Double?)
        <JsonPropertyName("high")>
        Public Property High As List(Of Double?)
        <JsonPropertyName("low")>
        Public Property Low As List(Of Double?)
        <JsonPropertyName("close")>
        Public Property Close As List(Of Double?)
        <JsonPropertyName("volume")>
        Public Property Volume As List(Of Long?)
    End Class

End Namespace
