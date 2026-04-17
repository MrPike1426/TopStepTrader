Option Strict On
Option Explicit On

Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Repositories
Imports Xunit

Namespace TopStepTrader.Tests.Data

    ''' <summary>
    ''' Integration-style regression tests for BarRepository that run against a real
    ''' in-memory SQLite database.
    '''
    ''' WHY an in-memory SQLite database instead of the EF Core InMemory provider:
    '''   EF Core's InMemory provider does not execute SQL — it stores objects in a
    '''   dictionary.  FromSqlInterpolated is silently ignored by the InMemory provider,
    '''   so tests using it would pass even if the SQL were completely wrong.
    '''   A real SQLite connection (Data Source=:memory:) runs actual SQL, which means
    '''   FromSqlInterpolated, ORDER BY, LIMIT, and the unique index all behave exactly
    '''   as in production.
    '''
    ''' These tests are the automated proof for UAT-BUG-001.  A green run here means
    ''' the VB.NET string-comparison / EF Core translation bug is definitively fixed.
    ''' </summary>
    Public Class BarRepositoryTests
        Implements IDisposable

        Private ReadOnly _conn As SqliteConnection
        Private ReadOnly _ctx As AppDbContext
        Private ReadOnly _sut As BarRepository

        Public Sub New()
            ' Keep the connection open for the test lifetime so the in-memory database
            ' is not destroyed between EF Core operations.
            _conn = New SqliteConnection("Data Source=:memory:")
            _conn.Open()

            Dim opts = New DbContextOptionsBuilder(Of AppDbContext)() _
                .UseSqlite(_conn) _
                .Options

            _ctx = New AppDbContext(opts)
            _ctx.Database.EnsureCreated()

            _sut = New BarRepository(_ctx, NullLogger(Of BarRepository).Instance)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _ctx.Dispose()
            _conn.Dispose()
        End Sub

        ''' <summary>Build a minimal MarketBar with all required fields populated.</summary>
        Private Shared Function MakeBar(contractId As String,
                                        ts As DateTimeOffset,
                                        close As Decimal) As MarketBar
            Return New MarketBar With {
                .ContractId = contractId,
                .Timestamp = ts,
                .Open = close,
                .High = close + 1D,
                .Low = close - 1D,
                .Close = close,
                .Volume = 100
            }
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' GetBarsAsync — UAT-BUG-001 regression
        ' (These would have failed before the FromSqlInterpolated fix because
        ' EF Core could not translate VB.NET's String.Compare() to SQL.)
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function GetBarsAsync_ReturnsOnlyMatchingContract() As Task
            ' Arrange — two contracts, same timeframe, same time window.
            Dim ts As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Await _sut.BulkInsertAsync({MakeBar("MES", ts, 5000D),
                                        MakeBar("MNQ", ts, 21000D)}, BarTimeframe.OneMinute)

            ' Act
            Dim result = Await _sut.GetBarsAsync(
                "MES", BarTimeframe.OneMinute,
                ts.AddHours(-1), ts.AddHours(1))

            ' Assert — only the MES bar is returned (core UAT-BUG-001 assertion)
            Assert.Single(result)
            Assert.Equal("MES", result(0).ContractId)
        End Function

        <Fact>
        Public Async Function GetBarsAsync_ExcludesWrongTimeframe() As Task
            ' Arrange — same contract, two different timeframes.
            Dim ts As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Await _sut.BulkInsertAsync({MakeBar("MES", ts, 5000D)}, BarTimeframe.OneMinute)
            Await _sut.BulkInsertAsync({MakeBar("MES", ts, 5001D)}, BarTimeframe.FiveMinute)

            ' Act
            Dim result = Await _sut.GetBarsAsync(
                "MES", BarTimeframe.OneMinute,
                ts.AddHours(-1), ts.AddHours(1))

            ' Assert — only the OneMinute bar is returned
            Assert.Single(result)
            Assert.Equal(BarTimeframe.OneMinute, result(0).Timeframe)
        End Function

        <Fact>
        Public Async Function GetBarsAsync_ReturnsOldestFirst() As Task
            ' Arrange — 5 bars spaced 1 minute apart.
            Dim base As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(1, 5) _
                .Select(Function(i) MakeBar("MES", base.AddMinutes(i), CDec(5000 + i))) _
                .ToList()
            Await _sut.BulkInsertAsync(bars, BarTimeframe.OneMinute)

            ' Act
            Dim result = Await _sut.GetBarsAsync(
                "MES", BarTimeframe.OneMinute,
                base, base.AddMinutes(10))

            ' Assert — all 5 returned in ascending timestamp order
            Assert.Equal(5, result.Count)
            For i As Integer = 0 To result.Count - 2
                Assert.True(result(i).Timestamp < result(i + 1).Timestamp)
            Next
        End Function

        <Fact>
        Public Async Function GetBarsAsync_ReturnsOldestFirst_WhenStoredNewestFirst() As Task
            ' Arrange — insert bars in DESCENDING timestamp order (simulates the ProjectX API
            ' returning bars newest-first so BulkInsertAsync stores them in that order, giving
            ' newer bars lower Ids).
            '
            ' UAT-BUG-005 regression: OrderBy(Id) ascending would produce newest-first bars,
            ' reversing the backtest timeline and creating massive look-ahead bias.
            ' Fix: in-memory OrderBy(Timestamp) is always correct regardless of Id order.
            Dim base As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(1, 5) _
                .Select(Function(i) MakeBar("MES", base.AddMinutes(i), CDec(5000 + i))) _
                .ToList()
            ' Insert newest-first so that higher timestamps get lower Ids.
            Await _sut.BulkInsertAsync(bars.AsEnumerable().Reverse(), BarTimeframe.OneMinute)

            ' Act
            Dim result = Await _sut.GetBarsAsync(
                "MES", BarTimeframe.OneMinute,
                base, base.AddMinutes(10))

            ' Assert — oldest-first despite being stored newest-first
            Assert.Equal(5, result.Count)
            For i As Integer = 0 To result.Count - 2
                Assert.True(result(i).Timestamp < result(i + 1).Timestamp,
                            $"Expected ascending: [{i}]={result(i).Timestamp} should be < [{i + 1}]={result(i + 1).Timestamp}")
            Next
        End Function

        <Fact>
        Public Async Function GetBarsAsync_RespectsDateRange() As Task
            ' Arrange — insert bars before, inside, and after the query window.
            Dim base As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Await _sut.BulkInsertAsync(
                {MakeBar("MES", base.AddMinutes(-5), 4999D),   ' before window
                 MakeBar("MES", base, 5000D),                   ' at window start
                 MakeBar("MES", base.AddMinutes(5), 5001D),     ' inside window
                 MakeBar("MES", base.AddMinutes(10), 5002D)},   ' at window end
                BarTimeframe.OneMinute)

            ' Act — window is [base, base+10min]
            Dim result = Await _sut.GetBarsAsync(
                "MES", BarTimeframe.OneMinute,
                base, base.AddMinutes(10))

            ' Assert — only the 3 bars inside the window are returned
            Assert.Equal(3, result.Count)
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' GetRecentBarsAsync
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function GetRecentBarsAsync_ReturnsExactCount() As Task
            ' Arrange — 10 bars, request only 3.
            Dim base As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(1, 10) _
                .Select(Function(i) MakeBar("MES", base.AddMinutes(i), CDec(5000 + i))) _
                .ToList()
            Await _sut.BulkInsertAsync(bars, BarTimeframe.OneMinute)

            ' Act
            Dim result = Await _sut.GetRecentBarsAsync("MES", BarTimeframe.OneMinute, 3)

            Assert.Equal(3, result.Count)
        End Function

        <Fact>
        Public Async Function GetRecentBarsAsync_ReturnsNewestBars() As Task
            ' Arrange — 5 bars with close prices 5001..5005.
            Dim base As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(1, 5) _
                .Select(Function(i) MakeBar("MES", base.AddMinutes(i), CDec(5000 + i))) _
                .ToList()
            Await _sut.BulkInsertAsync(bars, BarTimeframe.OneMinute)

            ' Act — request 2 most recent
            Dim result = Await _sut.GetRecentBarsAsync("MES", BarTimeframe.OneMinute, 2)

            ' Assert — returns bars 4 and 5 (close=5004, 5005), returned oldest-first
            Assert.Equal(2, result.Count)
            Assert.Equal(5004D, result(0).Close)
            Assert.Equal(5005D, result(1).Close)
        End Function

        <Fact>
        Public Async Function GetRecentBarsAsync_ResultIsOldestFirst() As Task
            ' Arrange — 5 bars.
            Dim base As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(1, 5) _
                .Select(Function(i) MakeBar("MES", base.AddMinutes(i), CDec(5000 + i))) _
                .ToList()
            Await _sut.BulkInsertAsync(bars, BarTimeframe.OneMinute)

            ' Act
            Dim result = Await _sut.GetRecentBarsAsync("MES", BarTimeframe.OneMinute, 3)

            ' Assert — oldest first within the returned subset
            For i As Integer = 0 To result.Count - 2
                Assert.True(result(i).Timestamp < result(i + 1).Timestamp)
            Next
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' GetLatestTimestampAsync
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function GetLatestTimestampAsync_ReturnsNothingWhenEmpty() As Task
            ' No bars inserted for this contract.
            Dim result = Await _sut.GetLatestTimestampAsync("EMPTY_CONTRACT", BarTimeframe.OneMinute)

            Assert.Null(result)
        End Function

        <Fact>
        Public Async Function GetLatestTimestampAsync_ReturnsNewestTimestamp() As Task
            ' Arrange — two bars with known timestamps.
            Dim ts1 As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim ts2 As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 1, 0, TimeSpan.Zero)
            Await _sut.BulkInsertAsync(
                {MakeBar("MES", ts1, 5000D), MakeBar("MES", ts2, 5001D)},
                BarTimeframe.OneMinute)

            ' Act
            Dim result = Await _sut.GetLatestTimestampAsync("MES", BarTimeframe.OneMinute)

            ' Assert — returns the later of the two timestamps
            Assert.Equal(ts2, result)
        End Function

        <Fact>
        Public Async Function GetLatestTimestampAsync_IsolatedToContract() As Task
            ' Arrange — insert bars for two contracts, MNQ has a later timestamp.
            Dim t1 As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero)
            Dim t2 As DateTimeOffset = New DateTimeOffset(2024, 6, 1, 9, 59, 0, TimeSpan.Zero)
            Await _sut.BulkInsertAsync(
                {MakeBar("MES", t1, 5000D), MakeBar("MNQ", t2, 21000D)},
                BarTimeframe.OneMinute)

            ' Act — query for MES specifically
            Dim result = Await _sut.GetLatestTimestampAsync("MES", BarTimeframe.OneMinute)

            ' Assert — returns MES's timestamp, not MNQ's later timestamp
            Assert.Equal(t1, result)
        End Function

    End Class

End Namespace
