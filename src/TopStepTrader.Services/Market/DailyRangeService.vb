Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' FEAT-62: Previous-day reference range (yesterday's H/L) used by the Break
    ''' and Bounce strategy. The most recent closed daily bar for the contract is
    ''' fetched once and cached for the rest of the UTC trading day.
    ''' </summary>
    Public Interface IDailyRangeService

        ''' <summary>
        ''' Returns the most recent closed daily bar's High/Low for the contract.
        ''' Returns <c>Nothing</c> when no closed daily bar is available (warmup, data outage).
        ''' </summary>
        Function GetPreviousDayRangeAsync(pxContractId As String,
                                           ct As CancellationToken) As Task(Of DailyRange?)

    End Interface

    ''' <summary>FEAT-62: Immutable previous-day range result.</summary>
    Public Structure DailyRange
        Public Property PrevHigh As Decimal
        Public Property PrevLow As Decimal
        Public Property SourceBarDate As DateOnly
    End Structure

    ''' <summary>
    ''' Singleton implementation. Cache key is <c>pxContractId</c>; cache is invalidated
    ''' once per UTC day so the first call after midnight UTC refetches.
    ''' </summary>
    Public Class DailyRangeService
        Implements IDailyRangeService

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _logger As ILogger(Of DailyRangeService)
        Private ReadOnly _cache As New ConcurrentDictionary(Of String, CachedRange)(StringComparer.OrdinalIgnoreCase)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       logger As ILogger(Of DailyRangeService))
            _scopeFactory = scopeFactory
            _logger = logger
        End Sub

        Public Async Function GetPreviousDayRangeAsync(pxContractId As String,
                                                        ct As CancellationToken) _
            As Task(Of DailyRange?) Implements IDailyRangeService.GetPreviousDayRangeAsync

            If String.IsNullOrWhiteSpace(pxContractId) Then Return Nothing

            Dim today = DateTime.UtcNow.Date
            Dim cached As CachedRange = Nothing
            If _cache.TryGetValue(pxContractId, cached) AndAlso cached.CachedOnUtcDate = today Then
                Return cached.Range
            End If

            Dim bars As IList(Of MarketBar)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim barIngestion = scope.ServiceProvider.GetRequiredService(Of IBarIngestionService)()
                    bars = Await barIngestion.GetLiveBarsAsync(pxContractId,
                                                                 BarTimeframe.Daily,
                                                                 5,
                                                                 cancel:=ct,
                                                                 live:=False)
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyRangeService bar fetch failed for {Contract}", pxContractId)
                Return Nothing
            End Try

            If bars Is Nothing OrElse bars.Count = 0 Then Return Nothing

            Dim sortedDesc = bars.OrderByDescending(Function(b) b.Timestamp).ToList()
            For Each bar In sortedDesc
                Dim barDate = bar.Timestamp.UtcDateTime.Date
                If barDate >= today Then Continue For
                Dim range As New DailyRange With {
                    .PrevHigh = bar.High,
                    .PrevLow = bar.Low,
                    .SourceBarDate = DateOnly.FromDateTime(barDate)
                }
                _cache(pxContractId) = New CachedRange With {
                    .CachedOnUtcDate = today,
                    .Range = range
                }
                Return range
            Next

            Return Nothing
        End Function

        Private Class CachedRange
            Public Property CachedOnUtcDate As Date
            Public Property Range As DailyRange
        End Class

    End Class

End Namespace
