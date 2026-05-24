Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Services.Market

    ''' <summary>
    ''' FEAT-68: cache correctness under the per-timeframe freshness windows.
    '''
    ''' BarCollectionService itself is exercised by integration use (live SQLite + PXHistoryClient),
    ''' so these tests target the pure in-memory memoization class that the service delegates to.
    ''' The acceptance criteria for FEAT-68 — cached path short-circuits, stale path retries,
    ''' failure doesn't poison, distinct keys are independent, invalidate clears — all reduce
    ''' to behavior of <see cref="BarEnsureMemoCache"/> with a deterministic clock.
    ''' </summary>
    Public Class BarEnsureMemoCacheTests

        Private ReadOnly _from As DateTimeOffset = New DateTimeOffset(2026, 5, 21, 0, 0, 0, TimeSpan.Zero)
        Private ReadOnly _to As DateTimeOffset = New DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero)

        Private Class FakeClock
            Public Now As DateTimeOffset
            Public Function GetNow() As DateTimeOffset
                Return Now
            End Function
        End Class

        <Fact>
        Public Sub WindowFor_FiveMinute_Is4Min()
            Assert.Equal(TimeSpan.FromMinutes(4), BarEnsureMemoCache.WindowFor(BarTimeframe.FiveMinute))
        End Sub

        <Fact>
        Public Sub WindowFor_OneMinute_Is50Sec()
            Assert.Equal(TimeSpan.FromSeconds(50), BarEnsureMemoCache.WindowFor(BarTimeframe.OneMinute))
        End Sub

        <Fact>
        Public Sub WindowFor_FifteenMinute_Is14Min()
            Assert.Equal(TimeSpan.FromMinutes(14), BarEnsureMemoCache.WindowFor(BarTimeframe.FifteenMinute))
        End Sub

        <Fact>
        Public Sub WindowFor_OneHour_Is55Min()
            Assert.Equal(TimeSpan.FromMinutes(55), BarEnsureMemoCache.WindowFor(BarTimeframe.OneHour))
        End Sub

        <Fact>
        Public Sub WindowFor_Daily_Is12Hours()
            Assert.Equal(TimeSpan.FromHours(12), BarEnsureMemoCache.WindowFor(BarTimeframe.Daily))
        End Sub

        <Fact>
        Public Sub WindowFor_UnmappedTimeframe_FallsBackToFiveMinuteDefault()
            ' ThirtyMinute is not in the table — should hit the 5-minute fallback.
            Assert.Equal(TimeSpan.FromMinutes(5), BarEnsureMemoCache.WindowFor(BarTimeframe.ThirtyMinute))
        End Sub

        <Fact>
        Public Sub CachedPath_WithinWindow_IsFreshReturnsTrue()
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)
            Dim key = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)

            cache.Record(key)
            clock.Now = clock.Now.AddSeconds(30)

            Assert.True(cache.IsFresh(key, BarTimeframe.FiveMinute))
        End Sub

        <Fact>
        Public Sub StalePath_AfterWindowExpires_IsFreshReturnsFalse()
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)
            Dim key = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)

            cache.Record(key)
            ' 5-minute window for FiveMinute is 4 minutes — advance past it.
            clock.Now = clock.Now.AddMinutes(5)

            Assert.False(cache.IsFresh(key, BarTimeframe.FiveMinute))
        End Sub

        <Fact>
        Public Sub FailureDoesNotPoisonCache_NoRecordCallMeansNotFresh()
            ' Contract: BarCollectionService only calls Record on success. Simulate "first call failed"
            ' by skipping Record entirely — IsFresh must remain false so the next call retries.
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)
            Dim key = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)

            Assert.False(cache.IsFresh(key, BarTimeframe.FiveMinute))
        End Sub

        <Fact>
        Public Sub DifferentKeysAreIndependent_DifferentContract()
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)
            Dim mesKey = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)
            Dim mnqKey = BarEnsureMemoCache.BuildKey("MNQ", BarTimeframe.FiveMinute, _from, _to)

            cache.Record(mesKey)

            Assert.True(cache.IsFresh(mesKey, BarTimeframe.FiveMinute))
            Assert.False(cache.IsFresh(mnqKey, BarTimeframe.FiveMinute))
        End Sub

        <Fact>
        Public Sub DifferentKeysAreIndependent_DifferentTimeframe()
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)
            Dim fiveMinKey = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)
            Dim fifteenMinKey = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FifteenMinute, _from, _to)

            cache.Record(fiveMinKey)

            Assert.True(cache.IsFresh(fiveMinKey, BarTimeframe.FiveMinute))
            Assert.False(cache.IsFresh(fifteenMinKey, BarTimeframe.FifteenMinute))
        End Sub

        <Fact>
        Public Sub Invalidate_RemovesAllEntriesForContractAndTimeframe()
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)

            ' Two different date ranges, same (MES, FiveMinute) — both should be cleared.
            Dim k1 = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)
            Dim k2 = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from.AddDays(-1), _to.AddDays(-1))
            ' And a sibling (MNQ, FiveMinute) — must remain.
            Dim mnq = BarEnsureMemoCache.BuildKey("MNQ", BarTimeframe.FiveMinute, _from, _to)
            cache.Record(k1)
            cache.Record(k2)
            cache.Record(mnq)

            cache.Invalidate("MES", BarTimeframe.FiveMinute)

            Assert.False(cache.IsFresh(k1, BarTimeframe.FiveMinute))
            Assert.False(cache.IsFresh(k2, BarTimeframe.FiveMinute))
            Assert.True(cache.IsFresh(mnq, BarTimeframe.FiveMinute))
        End Sub

        <Fact>
        Public Sub Invalidate_DoesNotTouchOtherTimeframesForSameContract()
            Dim clock = New FakeClock With {.Now = New DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero)}
            Dim cache = New BarEnsureMemoCache(AddressOf clock.GetNow)

            Dim mesFive = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)
            Dim mesFifteen = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FifteenMinute, _from, _to)
            cache.Record(mesFive)
            cache.Record(mesFifteen)

            cache.Invalidate("MES", BarTimeframe.FiveMinute)

            Assert.False(cache.IsFresh(mesFive, BarTimeframe.FiveMinute))
            Assert.True(cache.IsFresh(mesFifteen, BarTimeframe.FifteenMinute))
        End Sub

        <Fact>
        Public Sub BuildKey_IsDeterministicAndIncludesAllParts()
            Dim k1 = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)
            Dim k2 = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from, _to)
            Dim kDifferentContract = BarEnsureMemoCache.BuildKey("MNQ", BarTimeframe.FiveMinute, _from, _to)
            Dim kDifferentTf = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FifteenMinute, _from, _to)
            Dim kDifferentRange = BarEnsureMemoCache.BuildKey("MES", BarTimeframe.FiveMinute, _from.AddDays(-1), _to)

            Assert.Equal(k1, k2)
            Assert.NotEqual(k1, kDifferentContract)
            Assert.NotEqual(k1, kDifferentTf)
            Assert.NotEqual(k1, kDifferentRange)
        End Sub

    End Class

End Namespace
