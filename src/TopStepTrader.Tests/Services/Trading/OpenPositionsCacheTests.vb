Imports System.Threading
Imports System.Threading.Tasks
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trading

    ''' <summary>
    ''' PERF-02 F4b: correctness tests for <see cref="OpenPositionsCache"/>.
    ''' T1–T8 match the ticket spec: first-call fetch, TTL hit, TTL expiry,
    ''' bypassCache, stampede protection, account isolation, invalidate, exception no-cache.
    ''' </summary>
    Public Class OpenPositionsCacheTests

        Private Const AccountA As Long = 1001L
        Private Const AccountB As Long = 1002L

        Private Shared Function MakeResponse() As PXPositionSearchResponse
            Return New PXPositionSearchResponse With {.Positions = New List(Of PXPositionDto)()}
        End Function

        Private Shared Function MakeFetcher(resp As PXPositionSearchResponse, ByRef callCount As Integer) _
            As Func(Of CancellationToken, Task(Of PXPositionSearchResponse))
            Return Async Function(c As CancellationToken)
                       callCount += 1
                       Return resp
                   End Function
        End Function

        ' ── T1 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_FirstCall_InvokesFetcher() As Task
            Dim calls = 0
            Dim resp = MakeResponse()
            Dim cache As New OpenPositionsCache()

            Dim result = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))

            Assert.Equal(1, calls)
            Assert.Same(resp, result)
        End Function

        ' ── T2 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_SecondCallWithinTtl_UsesCache() As Task
            Dim calls = 0
            Dim resp = MakeResponse()
            Dim cache As New OpenPositionsCache()

            Dim r1 = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))
            Dim r2 = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))

            Assert.Equal(1, calls)
            Assert.Same(r1, r2)
        End Function

        ' ── T3 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_AfterTtlExpiry_RefetchesFromBroker() As Task
            Dim calls = 0
            Dim resp = MakeResponse()
            Dim cache As New OpenPositionsCache(ttl:=TimeSpan.FromMilliseconds(50))

            Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))
            Await Task.Delay(100)
            Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))

            Assert.Equal(2, calls)
        End Function

        ' ── T4 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_BypassCacheTrue_AlwaysFetches() As Task
            Dim calls = 0
            Dim resp = MakeResponse()
            Dim cache As New OpenPositionsCache()

            ' Populate cache
            Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))
            ' Bypass — should fetch again and update cache
            Dim bypassResp = MakeResponse()
            Dim bypassCalls = 0
            Dim r2 = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(bypassResp, bypassCalls), bypassCache:=True)

            Assert.Equal(1, calls)
            Assert.Equal(1, bypassCalls)
            Assert.Same(bypassResp, r2)

            ' Third call (default) should return the bypass response, not the original
            Dim defaultCalls = 0
            Dim r3 = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(New PXPositionSearchResponse(), defaultCalls))
            Assert.Equal(0, defaultCalls)
            Assert.Same(bypassResp, r3)
        End Function

        ' ── T5 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_ConcurrentCallers_SingleFetcherInvocation() As Task
            Const N = 8
            Dim fetchCount = 0
            Dim gate As New TaskCompletionSource(Of Boolean)()
            Dim resp = MakeResponse()
            Dim cache As New OpenPositionsCache()

            Dim fetcher As Func(Of CancellationToken, Task(Of PXPositionSearchResponse)) =
                Async Function(c As CancellationToken)
                    Interlocked.Increment(fetchCount)
                    Await gate.Task
                    Return resp
                End Function

            ' Kick off N concurrent callers
            Dim tasks = Enumerable.Range(0, N).
                Select(Function(i) cache.GetOrFetchAsync(AccountA, fetcher)).
                ToArray()

            ' Allow all tasks to queue on the semaphore before releasing the gate
            Await Task.Delay(50)
            gate.SetResult(True)

            Dim results = Await Task.WhenAll(tasks)

            Assert.Equal(1, fetchCount)
            Assert.All(results, Function(r) Assert.Same(resp, r))
        End Function

        ' ── T6 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_DifferentAccounts_DoNotShareCache() As Task
            Dim callsA = 0
            Dim callsB = 0
            Dim respA = MakeResponse()
            Dim respB = MakeResponse()
            Dim cache As New OpenPositionsCache()

            Dim rA1 = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(respA, callsA))
            Dim rB1 = Await cache.GetOrFetchAsync(AccountB, MakeFetcher(respB, callsB))
            ' Second calls within TTL — should still use per-account cache
            Dim rA2 = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(respA, callsA))
            Dim rB2 = Await cache.GetOrFetchAsync(AccountB, MakeFetcher(respB, callsB))

            Assert.Equal(1, callsA)
            Assert.Equal(1, callsB)
            Assert.Same(respA, rA1)
            Assert.Same(respA, rA2)
            Assert.Same(respB, rB1)
            Assert.Same(respB, rB2)
        End Function

        ' ── T7 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function Invalidate_RemovesEntry_NextGetFetches() As Task
            Dim calls = 0
            Dim resp = MakeResponse()
            Dim cache As New OpenPositionsCache()

            Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))
            cache.Invalidate(AccountA)
            Await cache.GetOrFetchAsync(AccountA, MakeFetcher(resp, calls))

            Assert.Equal(2, calls)
        End Function

        ' ── T8 ──────────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function GetOrFetchAsync_FetcherThrows_DoesNotCacheException() As Task
            Dim calls = 0
            Dim cache As New OpenPositionsCache()

            Dim failingFetcher As Func(Of CancellationToken, Task(Of PXPositionSearchResponse)) =
                Async Function(c As CancellationToken)
                    Interlocked.Increment(calls)
                    Throw New InvalidOperationException("simulated broker failure")
                    Return Nothing
                End Function

            ' First call should propagate the exception
            Await Assert.ThrowsAsync(Of InvalidOperationException)(
                Function() cache.GetOrFetchAsync(AccountA, failingFetcher))

            ' Second call must retry the fetcher (exception not cached)
            Dim goodResp = MakeResponse()
            Dim goodCalls = 0
            Dim result = Await cache.GetOrFetchAsync(AccountA, MakeFetcher(goodResp, goodCalls))

            Assert.Equal(1, calls)
            Assert.Equal(1, goodCalls)
            Assert.Same(goodResp, result)
        End Function

    End Class

End Namespace
