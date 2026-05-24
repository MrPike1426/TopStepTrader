Imports System.Threading
Imports TopStepTrader.API.Models.Responses

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' PERF-02: per-account TTL cache for the raw broker open-positions response.
    ''' Sits behind <see cref="Core.Interfaces.IOrderService.GetLivePositionSnapshotAsync"/>
    ''' so that multiple per-contract callers within the TTL window share a single REST call.
    ''' Singleton DI lifetime so all scoped consumers of IOrderService share state.
    ''' </summary>
    Public Interface IOpenPositionsCache

        ''' <summary>
        ''' Returns the cached open-positions response for the account, or invokes
        ''' <paramref name="fetcher"/> when no entry exists or the entry is older than
        ''' the configured TTL. Concurrent callers for the same account share a single
        ''' in-flight fetch (single-flight stampede protection).
        ''' </summary>
        ''' <param name="bypassCache">When True, ignores any cached entry, fetches fresh,
        ''' and stores the new response for subsequent cached reads. Used by pre-entry
        ''' duplicate-position guards and manual force-reconcile paths that must have
        ''' ground-truth state.</param>
        Function GetOrFetchAsync(accountId As Long,
                                 fetcher As Func(Of CancellationToken, Task(Of PXPositionSearchResponse)),
                                 Optional bypassCache As Boolean = False,
                                 Optional cancel As CancellationToken = Nothing) _
                                As Task(Of PXPositionSearchResponse)

        ''' <summary>
        ''' Invalidates the cached entry for the account. Called by ProjectXOrderService
        ''' immediately before raising PositionUpdated so any handler that re-reads
        ''' position state sees fresh data.
        ''' </summary>
        Sub Invalidate(accountId As Long)

    End Interface

End Namespace
