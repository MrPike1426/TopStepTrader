Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for BarCollectionService input-validation paths.
    ''' These tests exercise the fast-fail guards that run before any I/O occurs,
    ''' allowing them to pass Nothing for the HistoryClient and BarRepository
    ''' dependencies without causing NullReferenceExceptions.
    ''' TICKET-006 Phase 5.
    ''' </summary>
    Public Class BarCollectionServiceTests

        Private Shared Function MakeSut() As BarCollectionService
            ' Dependencies are Nothing — safe because the tested paths return before
            ' either _yahooClient or _barRepository is accessed.
            Return New BarCollectionService(
                Nothing,
                Nothing,
                NullLogger(Of BarCollectionService).Instance)
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' Empty / blank contract ID — fast-fail guard
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_ReturnsFail() As Task
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.False(result.Success)
            Assert.Equal(0, result.BarCount)
        End Function

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_MessageIndicatesRequired() As Task
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.Contains("required", result.Message, StringComparison.OrdinalIgnoreCase)
        End Function

        <Fact>
        Public Async Function EnsureBarsAsync_WhitespaceContractId_ReturnsFail() As Task
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("   ", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.False(result.Success)
            Assert.Equal(0, result.BarCount)
        End Function

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_ResultContractIdPreserved() As Task
            ' The Fail helper echoes back the contractId supplied by the caller.
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.Equal("", result.ContractId)
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' Progress reporting on fast-fail path
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_ReportsFailureSymbol() As Task
            Dim sut = MakeSut()
            Dim messages As New List(Of String)()
            ' SyncProgress invokes the callback synchronously, ensuring messages are
            ' populated before the assertion — no race with ThreadPool.
            Dim progress As IProgress(Of String) = New SyncProgress(Of String)(
                Sub(msg) messages.Add(msg))

            Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute, progress)

            Assert.NotEmpty(messages)
            Assert.Contains(messages, Function(m) m.StartsWith("✗"))
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' TEST-03: Non-Native Timeframe Aggregation
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Two consecutive 5-min bars in the same 10-min UTC window are merged into
        ''' one 10-min candle.  OHLCV: Open=first, High=Max, Low=Min, Close=last, Volume=Sum.
        ''' Timestamp = first bar's timestamp.
        ''' </summary>
        <Fact>
        Public Sub AggregateBars_FiveMinToTenMin_TwoBarsProduceOneCandle()
            Dim t0 = New DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)
            Dim t1 = t0.AddMinutes(5)

            Dim src As New List(Of MarketBar) From {
                New MarketBar With {
                    .ContractId = "TEST", .Timeframe = BarTimeframe.FiveMinute,
                    .Timestamp = t0,
                    .Open = 100D, .High = 105D, .Low = 98D, .Close = 103D,
                    .Volume = 1000L, .VWAP = 101.5D
                },
                New MarketBar With {
                    .ContractId = "TEST", .Timeframe = BarTimeframe.FiveMinute,
                    .Timestamp = t1,
                    .Open = 103D, .High = 107D, .Low = 102D, .Close = 106D,
                    .Volume = 1500L, .VWAP = 104.5D
                }
            }

            Dim result = BarCollectionService.AggregateBars(src, BarTimeframe.TenMinute)

            Assert.Equal(1, result.Count)
            Dim agg = result(0)
            Assert.Equal(BarTimeframe.TenMinute, agg.Timeframe)
            Assert.Equal(t0, agg.Timestamp)          ' timestamp = first bar
            Assert.Equal(100D, agg.Open)              ' Open = first bar open
            Assert.Equal(107D, agg.High)              ' max(105, 107)
            Assert.Equal(98D, agg.Low)                ' min(98, 102)
            Assert.Equal(106D, agg.Close)             ' Close = last bar close
            Assert.Equal(2500L, agg.Volume)           ' 1000 + 1500
        End Sub

        ''' <summary>
        ''' Three 1-hr bars at 00:00, 01:00, 02:00 UTC.
        ''' 00:00 and 01:00 are in the same 2-hr window; 02:00 starts a new window.
        ''' The odd (unpaired) bar forms its own 2-hr candle — no data is dropped.
        ''' </summary>
        <Fact>
        Public Sub AggregateBars_OneHrToTwoHr_OddCountProducesTwoCandles()
            Dim t0 = New DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)

            Dim src As New List(Of MarketBar) From {
                New MarketBar With {
                    .ContractId = "TEST", .Timeframe = BarTimeframe.OneHour,
                    .Timestamp = t0,
                    .Open = 200D, .High = 210D, .Low = 195D, .Close = 205D,
                    .Volume = 5000L, .VWAP = 202.5D
                },
                New MarketBar With {
                    .ContractId = "TEST", .Timeframe = BarTimeframe.OneHour,
                    .Timestamp = t0.AddHours(1),
                    .Open = 205D, .High = 215D, .Low = 203D, .Close = 212D,
                    .Volume = 6000L, .VWAP = 209.0D
                },
                New MarketBar With {
                    .ContractId = "TEST", .Timeframe = BarTimeframe.OneHour,
                    .Timestamp = t0.AddHours(2),
                    .Open = 212D, .High = 220D, .Low = 208D, .Close = 218D,
                    .Volume = 4000L, .VWAP = 214.0D
                }
            }

            Dim result = BarCollectionService.AggregateBars(src, BarTimeframe.TwoHour)

            ' 3 bars → 2 candles (00:00+01:00 merged, 02:00 alone)
            Assert.Equal(2, result.Count)

            Dim c1 = result(0)
            Assert.Equal(t0, c1.Timestamp)
            Assert.Equal(200D, c1.Open)
            Assert.Equal(215D, c1.High)         ' max(210, 215)
            Assert.Equal(195D, c1.Low)          ' min(195, 203)
            Assert.Equal(212D, c1.Close)
            Assert.Equal(11000L, c1.Volume)     ' 5000 + 6000

            Dim c2 = result(1)
            Assert.Equal(t0.AddHours(2), c2.Timestamp)
            Assert.Equal(212D, c2.Open)
            Assert.Equal(220D, c2.High)
            Assert.Equal(208D, c2.Low)
            Assert.Equal(218D, c2.Close)
            Assert.Equal(4000L, c2.Volume)
        End Sub

        ''' <summary>
        ''' Five 1-hr bars at 00:00–04:00 UTC.
        ''' Bars 0–3 fall in the same 4-hr window; bar 4 (04:00) starts a new window.
        ''' Verifies OHLCV aggregation across four source bars and correct window boundary.
        ''' </summary>
        <Fact>
        Public Sub AggregateBars_OneHrToFourHr_FourBarWindowAndOrphanCandle()
            Dim t0 = New DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)

            Dim src As New List(Of MarketBar)
            For i As Integer = 0 To 4
                src.Add(New MarketBar With {
                    .ContractId = "TEST", .Timeframe = BarTimeframe.OneHour,
                    .Timestamp = t0.AddHours(i),
                    .Open  = 300D + i,
                    .High  = 310D + i,
                    .Low   = 295D + i,
                    .Close = 305D + i,
                    .Volume = 1000L + i * 100L,
                    .VWAP = 302.5D + i
                })
            Next

            Dim result = BarCollectionService.AggregateBars(src, BarTimeframe.FourHour)

            ' 5 bars → 2 candles (4-bar window + orphan bar)
            Assert.Equal(2, result.Count)

            Dim c1 = result(0)
            Assert.Equal(t0, c1.Timestamp)              ' first bar's timestamp
            Assert.Equal(300D, c1.Open)                  ' bar[0].Open
            Assert.Equal(313D, c1.High)                  ' max(310,311,312,313)
            Assert.Equal(295D, c1.Low)                   ' min(295,296,297,298)
            Assert.Equal(308D, c1.Close)                 ' bar[3].Close = 305+3
            Assert.Equal(4600L, c1.Volume)               ' 1000+1100+1200+1300

            Dim c2 = result(1)
            Assert.Equal(t0.AddHours(4), c2.Timestamp)
            Assert.Equal(304D, c2.Open)
            Assert.Equal(314D, c2.High)
            Assert.Equal(299D, c2.Low)
            Assert.Equal(309D, c2.Close)
            Assert.Equal(1400L, c2.Volume)
        End Sub

    End Class

    ' ══════════════════════════════════════════════════════════════════════════════
    ' TEST-04 — BarCollectionService: Staleness and Dedup
    ' ══════════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Integration-style tests for the EnsureBarsAsync cache-hit / staleness logic
    ''' using an in-memory SQLite database (same pattern as BarRepositoryTests).
    ''' These tests exercise the Step 1 branch in BarCollectionService without
    ''' touching the PX History API (_pxHistoryClient = Nothing).
    ''' </summary>
    Public Class BarCollectionServiceStalenessTests
        Implements IDisposable

        Private ReadOnly _conn As SqliteConnection
        Private ReadOnly _ctx As AppDbContext
        Private ReadOnly _repo As BarRepository
        Private ReadOnly _sut As BarCollectionService

        Public Sub New()
            _conn = New SqliteConnection("Data Source=:memory:")
            _conn.Open()
            Dim opts = New DbContextOptionsBuilder(Of AppDbContext)() _
                .UseSqlite(_conn) _
                .Options
            _ctx = New AppDbContext(opts)
            _ctx.Database.EnsureCreated()
            _repo = New BarRepository(_ctx, NullLogger(Of BarRepository).Instance)
            _sut = New BarCollectionService(Nothing, _repo, NullLogger(Of BarCollectionService).Instance)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _ctx.Dispose()
            _conn.Dispose()
        End Sub

        ''' <summary>
        ''' Build a minimal 5-minute MarketBar at the given timestamp.
        ''' </summary>
        Private Shared Function MakeBar(contractId As String, ts As DateTimeOffset) As MarketBar
            Return New MarketBar With {
                .ContractId = contractId,
                .Timeframe = BarTimeframe.FiveMinute,
                .Timestamp = ts,
                .Open = 100D, .High = 101D, .Low = 99D, .Close = 100.5D,
                .Volume = 500L, .VWAP = 100.2D
            }
        End Function

        ''' <summary>
        ''' 50 fresh bars (latest bar < 24 h old, rangeSpan ≤ 7 → spanOk)
        ''' should return Success=True with the "already available" cache-hit message.
        ''' </summary>
        <Fact>
        Public Async Function EnsureBarsAsync_FreshBars_ReturnsCacheHit() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim bars = Enumerable.Range(0, 50) _
                .Select(Function(i) MakeBar("MES", now.AddMinutes(-i * 5))) _
                .ToList()

            Await _repo.BulkInsertAsync(bars, BarTimeframe.FiveMinute)

            Dim result = Await _sut.EnsureBarsAsync(
                "MES",
                now.AddDays(-1).Date,
                now.Date,
                BarTimeframe.FiveMinute)

            Assert.True(result.Success)
            Assert.Contains("already available", result.Message)
            Assert.Equal(50, result.BarCount)
        End Function

        ''' <summary>
        ''' 50 stale bars (latest bar > 24 h old) should NOT return the cache-hit
        ''' message — the service falls through to the download path.  Because
        ''' _pxHistoryClient is Nothing the download loop exits immediately and the
        ''' Step 4 final count still reflects the bars already in the database.
        ''' </summary>
        <Fact>
        Public Async Function EnsureBarsAsync_StaleBars_DoesNotReturnCacheHit() As Task
            Dim staleBase = DateTimeOffset.UtcNow.AddHours(-48)
            Dim bars = Enumerable.Range(0, 50) _
                .Select(Function(i) MakeBar("MES", staleBase.AddMinutes(i * 5))) _
                .ToList()

            Await _repo.BulkInsertAsync(bars, BarTimeframe.FiveMinute)

            Dim result = Await _sut.EnsureBarsAsync(
                "MES",
                staleBase.AddDays(-1).Date,
                staleBase.Date,
                BarTimeframe.FiveMinute)

            Assert.DoesNotContain("already available", result.Message)
        End Function

        ''' <summary>
        ''' Inserting the same 50 bars twice via BulkInsertAsync must not produce
        ''' duplicates — the unique index / IGNORE semantics deduplicate on the second
        ''' pass.  GetBarsAsync should still return exactly 50 bars.
        ''' </summary>
        <Fact>
        Public Async Function BulkInsertAsync_DuplicateBars_AreDeduped() As Task
            Dim now = DateTimeOffset.UtcNow
            Dim bars = Enumerable.Range(0, 50) _
                .Select(Function(i) MakeBar("MES", now.AddMinutes(-i * 5))) _
                .ToList()

            Await _repo.BulkInsertAsync(bars, BarTimeframe.FiveMinute)
            Await _repo.BulkInsertAsync(bars, BarTimeframe.FiveMinute)   ' exact duplicates

            Dim stored = Await _repo.GetBarsAsync(
                "MES",
                BarTimeframe.FiveMinute,
                now.AddDays(-1),
                now.AddMinutes(1))

            Assert.Equal(50, stored.Count)
        End Function

    End Class

    ''' <summary>
    ''' Synchronous IProgress(Of T) implementation for use in unit tests.
    ''' The built-in Progress(Of T) posts callbacks to the ThreadPool when no
    ''' SynchronizationContext is present, making assertions race-prone.
    ''' SyncProgress calls the delegate inline on the reporting thread.
    ''' </summary>
    Friend NotInheritable Class SyncProgress(Of T)
        Implements IProgress(Of T)

        Private ReadOnly _callback As Action(Of T)

        Public Sub New(callback As Action(Of T))
            _callback = callback
        End Sub

        Public Sub Report(value As T) Implements IProgress(Of T).Report
            _callback(value)
        End Sub

    End Class

End Namespace
