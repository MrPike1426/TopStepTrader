Imports System.Collections.Concurrent
Imports System.Threading
Imports TopStepTrader.API.Models.Responses

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' PERF-02: singleton TTL cache for <see cref="IOpenPositionsCache"/>.
    ''' Default TTL = 5 s — covers all per-slot calls within a single 15-s strategy tick.
    ''' Per-account SemaphoreSlim provides single-flight stampede protection.
    ''' </summary>
    Public Class OpenPositionsCache
        Implements IOpenPositionsCache
        Implements IDisposable

        Private ReadOnly _ttl As TimeSpan
        Private ReadOnly _entries As New ConcurrentDictionary(Of Long, CacheEntry)()
        Private ReadOnly _semaphores As New ConcurrentDictionary(Of Long, SemaphoreSlim)()

        Public Sub New(Optional ttl As TimeSpan? = Nothing)
            _ttl = If(ttl.HasValue, ttl.Value, TimeSpan.FromSeconds(5))
        End Sub

        Public Async Function GetOrFetchAsync(accountId As Long,
                                              fetcher As Func(Of CancellationToken, Task(Of PXPositionSearchResponse)),
                                              Optional bypassCache As Boolean = False,
                                              Optional cancel As CancellationToken = Nothing) _
                                             As Task(Of PXPositionSearchResponse) _
                                             Implements IOpenPositionsCache.GetOrFetchAsync
            ' Fast path: check cache before acquiring the semaphore.
            If Not bypassCache Then
                Dim quick As CacheEntry = Nothing
                If _entries.TryGetValue(accountId, quick) AndAlso
                   (DateTime.UtcNow - quick.FetchedUtc) < _ttl Then
                    Return quick.Response
                End If
            End If

            Dim sem = _semaphores.GetOrAdd(accountId, Function(k) New SemaphoreSlim(1, 1))
            Await sem.WaitAsync(cancel)
            Try
                ' Double-check after acquiring — another caller may have populated the cache.
                If Not bypassCache Then
                    Dim entry As CacheEntry = Nothing
                    If _entries.TryGetValue(accountId, entry) AndAlso
                       (DateTime.UtcNow - entry.FetchedUtc) < _ttl Then
                        Return entry.Response
                    End If
                End If

                Dim resp = Await fetcher(cancel)
                _entries(accountId) = New CacheEntry With {.Response = resp, .FetchedUtc = DateTime.UtcNow}
                Return resp
            Finally
                sem.Release()
            End Try
        End Function

        Public Sub Invalidate(accountId As Long) Implements IOpenPositionsCache.Invalidate
            Dim removed As CacheEntry = Nothing
            _entries.TryRemove(accountId, removed)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            For Each sem In _semaphores.Values
                Try : sem.Dispose() : Catch : End Try
            Next
        End Sub

        Private Class CacheEntry
            Public Property Response As PXPositionSearchResponse
            Public Property FetchedUtc As DateTime
        End Class

    End Class

End Namespace
