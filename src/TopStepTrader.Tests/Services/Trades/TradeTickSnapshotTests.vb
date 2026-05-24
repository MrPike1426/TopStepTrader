Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Trades
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trades

    ''' <summary>
    ''' FEAT-59: round-trip tests for the TradeTickSnapshots persistence path —
    ''' repository AddAsync / AddBatchAsync / GetByTradeRecordAsync, the service-level
    ''' best-effort behaviour, and the per-bar throttle contract used by the
    ''' SuperTrendPlusViewModel management tick.
    ''' </summary>
    Public Class TradeTickSnapshotTests
        Implements IDisposable

        Private ReadOnly _dbPath As String
        Private ReadOnly _provider As ServiceProvider
        Private ReadOnly _service As TradeRecordService

        Public Sub New()
            ' Temp file (not :memory:) so EF Core's multiple short-lived scopes all share state.
            _dbPath = Path.Combine(Path.GetTempPath(), $"feat59_{Guid.NewGuid():N}.db")
            Dim connStr As String = $"Data Source={_dbPath}"

            Dim services As New ServiceCollection()
            services.AddDbContext(Of TradeHistoryDbContext)(Sub(opts) opts.UseSqlite(connStr))
            services.AddScoped(Of ITradeTickSnapshotRepository, TradeTickSnapshotRepository)()
            _provider = services.BuildServiceProvider()

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of TradeHistoryDbContext)()
                db.Database.EnsureCreated()
                db.EnsureSchemaCurrent()
            End Using

            Dim scopeFactory = _provider.GetRequiredService(Of IServiceScopeFactory)()
            _service = New TradeRecordService(scopeFactory,
                                              orderClient:=Nothing,
                                              session:=Nothing,
                                              logger:=NullLogger(Of TradeRecordService).Instance)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _provider.Dispose()
                SqliteConnection.ClearAllPools()
                If File.Exists(_dbPath) Then File.Delete(_dbPath)
            Catch
            End Try
        End Sub

        Private Shared Function MakeSnapshot(barTs As DateTimeOffset,
                                             Optional close As Decimal = 21000D,
                                             Optional phase As String = "Initial",
                                             Optional exitScore As Integer = 0) As TradeTickSnapshot
            Return New TradeTickSnapshot With {
                .BarTimestamp = barTs,
                .BarOpen = close - 5D,
                .BarHigh = close + 10D,
                .BarLow = close - 12D,
                .BarClose = close,
                .BarVolume = 1234L,
                .SuperTrendLine = close - 20D,
                .SuperTrendDirection = 1,
                .Atr = 1.75F,
                .Adx = 28.5F,
                .PlusDi = 30.2F,
                .MinusDi = 12.1F,
                .CurrentStopPrice = close - 25D,
                .CurrentTakeProfitPrice = close + 50D,
                .UnrealisedPnlDollars = 120D,
                .MaxAdverseExcursionDollars = -45D,
                .MaxFavorableExcursionDollars = 200D,
                .StopPhase = phase,
                .ExitScore = exitScore
            }
        End Function

        <Fact>
        Public Async Function AddAsync_RoundTripsAllFields() As Task
            Const recordId As Long = 4242
            Dim barTs = New DateTimeOffset(2026, 5, 17, 14, 30, 0, TimeSpan.Zero)

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim id = Await repo.AddAsync(New TradeTickSnapshotEntity With {
                    .LiveTradeRecordId = recordId,
                    .BarTimestamp = barTs,
                    .BarOpen = 20995D, .BarHigh = 21010D, .BarLow = 20988D, .BarClose = 21000D,
                    .BarVolume = 1234L,
                    .SuperTrendLine = 20980D, .SuperTrendDirection = 1,
                    .Atr = 1.75F, .Adx = 28.5F, .PlusDi = 30.2F, .MinusDi = 12.1F,
                    .CurrentStopPrice = 20975D, .CurrentTakeProfitPrice = 21050D,
                    .UnrealisedPnlDollars = 120D,
                    .MaxAdverseExcursionDollars = -45D, .MaxFavorableExcursionDollars = 200D,
                    .StopPhase = "Breakeven",
                    .ExitScore = 3
                })
                Assert.True(id > 0)
            End Using

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim rows = Await repo.GetByTradeRecordAsync(recordId)
                Assert.Single(rows)
                Dim r = rows(0)
                Assert.Equal(recordId, r.LiveTradeRecordId)
                Assert.Equal(barTs, r.BarTimestamp)
                Assert.Equal(21000D, r.BarClose)
                Assert.Equal(1234L, r.BarVolume)
                Assert.Equal(1, r.SuperTrendDirection)
                Assert.Equal(20975D, r.CurrentStopPrice)
                Assert.Equal(21050D, r.CurrentTakeProfitPrice)
                Assert.Equal("Breakeven", r.StopPhase)
                Assert.Equal(3, r.ExitScore)
                Assert.Equal(-45D, r.MaxAdverseExcursionDollars)
                Assert.Equal(200D, r.MaxFavorableExcursionDollars)
            End Using
        End Function

        <Fact>
        Public Async Function AddBatchAsync_PersistsAllRowsAndOrdersByTimestamp() As Task
            Const recordId As Long = 7
            Dim t0 = New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)
            Dim entities As New List(Of TradeTickSnapshotEntity) From {
                MakeEntity(recordId, t0.AddMinutes(30), 21015D),
                MakeEntity(recordId, t0, 21000D),
                MakeEntity(recordId, t0.AddMinutes(15), 21008D)
            }

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Await repo.AddBatchAsync(entities)
            End Using

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim rows = Await repo.GetByTradeRecordAsync(recordId)
                Assert.Equal(3, rows.Count)
                Assert.Equal(t0, rows(0).BarTimestamp)
                Assert.Equal(t0.AddMinutes(15), rows(1).BarTimestamp)
                Assert.Equal(t0.AddMinutes(30), rows(2).BarTimestamp)
            End Using
        End Function

        <Fact>
        Public Async Function GetByTradeRecordAsync_FiltersByRecordId() As Task
            Dim t0 = New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)
            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Await repo.AddBatchAsync(New List(Of TradeTickSnapshotEntity) From {
                    MakeEntity(1L, t0, 21000D),
                    MakeEntity(2L, t0.AddMinutes(1), 21001D),
                    MakeEntity(1L, t0.AddMinutes(2), 21002D)
                })
            End Using

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim rows = Await repo.GetByTradeRecordAsync(1L)
                Assert.Equal(2, rows.Count)
                Assert.All(rows, Sub(r) Assert.Equal(1L, r.LiveTradeRecordId))
            End Using
        End Function

        <Fact>
        Public Async Function LogTickSnapshotAsync_RoundTripsViaService() As Task
            Const recordId As Long = 99
            Dim barTs = New DateTimeOffset(2026, 5, 17, 15, 0, 0, TimeSpan.Zero)

            Await _service.LogTickSnapshotAsync(recordId, MakeSnapshot(barTs, close:=21050D, phase:="ProfitTrail", exitScore:=2))

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim rows = Await repo.GetByTradeRecordAsync(recordId)
                Assert.Single(rows)
                Assert.Equal("ProfitTrail", rows(0).StopPhase)
                Assert.Equal(2, rows(0).ExitScore)
                Assert.Equal(21050D, rows(0).BarClose)
            End Using
        End Function

        <Fact>
        Public Async Function LogTickSnapshotAsync_ZeroRecordIdIsNoOp() As Task
            ' Best-effort guard: zero record ID short-circuits before any DB I/O.
            Await _service.LogTickSnapshotAsync(0L, MakeSnapshot(DateTimeOffset.UtcNow))
            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of TradeHistoryDbContext)()
                Assert.Equal(0, db.TradeTickSnapshots.Count())
            End Using
        End Function

        <Fact>
        Public Async Function LogTickSnapshotAsync_NullSnapshotIsNoOp() As Task
            ' Best-effort guard: null snapshot is swallowed instead of throwing into the hot path.
            Await _service.LogTickSnapshotAsync(1L, Nothing)
            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of TradeHistoryDbContext)()
                Assert.Equal(0, db.TradeTickSnapshots.Count())
            End Using
        End Function

        <Fact>
        Public Async Function LogTickSnapshotAsync_RepoFailureIsSwallowed() As Task
            ' Build a service whose scope factory hands out a TradeHistoryDbContext that has not
            ' had its schema created — every SaveChangesAsync inside the repo will throw. The
            ' service must swallow that exception so the management tick is never disrupted.
            Dim broken As New ServiceCollection()
            Dim badPath = Path.Combine(Path.GetTempPath(), $"feat59_bad_{Guid.NewGuid():N}.db")
            broken.AddDbContext(Of TradeHistoryDbContext)(Sub(opts) opts.UseSqlite($"Data Source={badPath}"))
            broken.AddScoped(Of ITradeTickSnapshotRepository, TradeTickSnapshotRepository)()
            Using brokenProvider = broken.BuildServiceProvider()
                ' Deliberately do NOT call EnsureCreated / EnsureSchemaCurrent — table is missing.
                Dim svc = New TradeRecordService(brokenProvider.GetRequiredService(Of IServiceScopeFactory)(),
                                                  orderClient:=Nothing,
                                                  session:=Nothing,
                                                  logger:=NullLogger(Of TradeRecordService).Instance)
                ' Must not throw.
                Await svc.LogTickSnapshotAsync(1L, MakeSnapshot(DateTimeOffset.UtcNow))
            End Using
            Try
                SqliteConnection.ClearAllPools()
                If File.Exists(badPath) Then File.Delete(badPath)
            Catch
            End Try
        End Function

        ''' <summary>
        ''' FEAT-59 throttle contract: the SuperTrendPlusViewModel skips the write when
        ''' <c>bars(n).Timestamp == slot.LastTickSnapshotBarTime</c>, so a second tick
        ''' on the same closed bar must not produce a duplicate row. We simulate that
        ''' caller-side decision here against the real persistence path.
        ''' </summary>
        <Fact>
        Public Async Function PerBarThrottle_TwoTicksSameBarTimestampProduceOneRow() As Task
            Const recordId As Long = 314
            Dim slot As New PositionSlot With {.TradeRecordId = recordId}
            Dim barTs = New DateTimeOffset(2026, 5, 17, 16, 0, 0, TimeSpan.Zero)

            ' Tick 1 — new bar timestamp, write fires.
            If barTs > slot.LastTickSnapshotBarTime Then
                slot.LastTickSnapshotBarTime = barTs
                Await _service.LogTickSnapshotAsync(slot.TradeRecordId, MakeSnapshot(barTs))
            End If

            ' Tick 2 — same bar timestamp (the management timer fired again before a new bar
            ' closed). Write must be skipped.
            If barTs > slot.LastTickSnapshotBarTime Then
                slot.LastTickSnapshotBarTime = barTs
                Await _service.LogTickSnapshotAsync(slot.TradeRecordId, MakeSnapshot(barTs))
            End If

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim rows = Await repo.GetByTradeRecordAsync(recordId)
                Assert.Single(rows)
            End Using

            ' Tick 3 — new bar closes, throttle allows the next write through.
            Dim nextBarTs = barTs.AddMinutes(15)
            If nextBarTs > slot.LastTickSnapshotBarTime Then
                slot.LastTickSnapshotBarTime = nextBarTs
                Await _service.LogTickSnapshotAsync(slot.TradeRecordId, MakeSnapshot(nextBarTs))
            End If

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeTickSnapshotRepository)()
                Dim rows = Await repo.GetByTradeRecordAsync(recordId)
                Assert.Equal(2, rows.Count)
            End Using
        End Function

        Private Shared Function MakeEntity(recordId As Long, barTs As DateTimeOffset, close As Decimal) As TradeTickSnapshotEntity
            Return New TradeTickSnapshotEntity With {
                .LiveTradeRecordId = recordId,
                .BarTimestamp = barTs,
                .BarOpen = close - 1D, .BarHigh = close + 2D, .BarLow = close - 2D, .BarClose = close,
                .BarVolume = 100L,
                .SuperTrendLine = close - 10D, .SuperTrendDirection = 1,
                .Atr = 1.0F, .Adx = 20.0F, .PlusDi = 25.0F, .MinusDi = 15.0F,
                .CurrentStopPrice = close - 15D, .CurrentTakeProfitPrice = close + 30D,
                .UnrealisedPnlDollars = 0D,
                .MaxAdverseExcursionDollars = 0D, .MaxFavorableExcursionDollars = 0D,
                .StopPhase = "Initial",
                .ExitScore = 0
            }
        End Function

    End Class

End Namespace
