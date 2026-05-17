Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Trades
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trades

    ''' <summary>
    ''' FEAT-57: round-trip tests for SaveSignalAsync / OpenOutcomeAsync / ResolveOutcomeAsync
    ''' on TradeRecordService. Uses an in-memory SQLite AppDbContext with a real DI container
    ''' so the IServiceScopeFactory + Scoped repository pattern matches production.
    ''' </summary>
    Public Class TradeRecordServiceOutcomeTests
        Implements IDisposable

        Private ReadOnly _dbPath As String
        Private ReadOnly _provider As ServiceProvider
        Private ReadOnly _service As TradeRecordService

        Public Sub New()
            ' Use a temp file (not :memory:) so EF Core's separate connections all see the
            ' same database across the multiple short-lived scopes the service creates.
            _dbPath = Path.Combine(Path.GetTempPath(), $"feat57_{Guid.NewGuid():N}.db")
            Dim connStr As String = $"Data Source={_dbPath}"

            Dim services As New ServiceCollection()
            services.AddDbContext(Of AppDbContext)(Sub(opts) opts.UseSqlite(connStr))
            services.AddScoped(Of SignalRepository)()
            services.AddScoped(Of TradeOutcomeRepository)()
            ' FEAT-58: register the snapshot + lifespan repos so the service can resolve them.
            services.AddScoped(Of ITradeSetupSnapshotRepository, TradeSetupSnapshotRepository)()
            services.AddScoped(Of ITradeLifespanRepository, TradeLifespanRepository)()
            services.AddSingleton(Of ILogger(Of SignalRepository))(NullLogger(Of SignalRepository).Instance)
            _provider = services.BuildServiceProvider()

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                db.Database.EnsureCreated()
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

        Private Function MakeSignal() As TradeSignal
            Return New TradeSignal With {
                .ContractId = "CON.F.US.MNQ.U26",
                .GeneratedAt = DateTimeOffset.UtcNow,
                .SignalType = Core.Enums.SignalType.Buy,
                .Confidence = 32.5F,
                .ModelVersion = "Test.v1",
                .SuggestedEntryPrice = 21000D,
                .SuggestedStopLoss = 20985D
            }
        End Function

        Private Function MakeOutcome() As TradeOutcome
            Return New TradeOutcome With {
                .ContractId = "CON.F.US.MNQ.U26",
                .Timeframe = 15,
                .SignalType = "Buy",
                .SignalConfidence = 32.5F,
                .ModelVersion = "Test.v1",
                .EntryTime = DateTimeOffset.UtcNow,
                .EntryPrice = 21000D
            }
        End Function

        <Fact>
        Public Async Function SaveSignalAsync_ReturnsNonZeroId() As Task
            Dim id = Await _service.SaveSignalAsync(MakeSignal())
            Assert.True(id > 0, $"Expected positive Signal Id, got {id}")
        End Function

        <Fact>
        Public Async Function OpenOutcomeAsync_InsertsOpenRowWithLinkedIds() As Task
            Const recordId As Long = 4242
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId, MakeOutcome())
            Assert.True(outcomeId > 0)

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim row = Await db.TradeOutcomes.FindAsync(outcomeId)
                Assert.NotNull(row)
                Assert.True(row.IsOpen)
                Assert.Equal(sigId, row.SignalId)
                Assert.Equal(recordId, row.OrderId.Value)
                Assert.False(row.IsWinner.HasValue)
                Assert.Equal("Buy", row.SignalType)
            End Using
        End Function

        <Fact>
        Public Async Function ResolveOutcomeAsync_RoundTripsExitData() As Task
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId:=1L, model:=MakeOutcome())

            Dim exitTime = DateTimeOffset.UtcNow.AddMinutes(15)
            Await _service.ResolveOutcomeAsync(outcomeId, exitTime, exitPrice:=21015D,
                                                pnl:=300D, isWinner:=True, exitReason:="ExitEngine: SuperTrend flip")

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim row = Await db.TradeOutcomes.FindAsync(outcomeId)
                Assert.NotNull(row)
                Assert.False(row.IsOpen)
                Assert.True(row.IsWinner.HasValue AndAlso row.IsWinner.Value)
                Assert.Equal(21015D, row.ExitPrice.Value)
                Assert.Equal(300D, row.PnL.Value)
                Assert.Equal("ExitEngine: SuperTrend flip", row.ExitReason)
            End Using

            ' Rolling win-rate over the most recent 10 trades — single winner → 1.0
            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                Dim winRate = Await repo.GetRollingWinRateAsync(10)
                Assert.Equal(1.0F, winRate)
            End Using
        End Function

        <Fact>
        Public Async Function ResolveOutcomeAsync_LossSetsIsWinnerFalse() As Task
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId:=2L, model:=MakeOutcome())

            Await _service.ResolveOutcomeAsync(outcomeId, DateTimeOffset.UtcNow,
                                                exitPrice:=20985D, pnl:=-300D,
                                                isWinner:=False, exitReason:="SL hit")

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim row = Await db.TradeOutcomes.FindAsync(outcomeId)
                Assert.NotNull(row)
                Assert.False(row.IsOpen)
                Assert.True(row.IsWinner.HasValue AndAlso Not row.IsWinner.Value)
            End Using
        End Function

        <Fact>
        Public Async Function OpenOutcomeAsync_ZeroRecordIdLeavesOrderIdNull() As Task
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId:=0L, model:=MakeOutcome())

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim row = Await db.TradeOutcomes.FindAsync(outcomeId)
                Assert.NotNull(row)
                Assert.False(row.OrderId.HasValue)
            End Using
        End Function

        <Fact>
        Public Async Function ResolveOutcomeAsync_ZeroIdIsNoOp() As Task
            ' Should not throw — covers the early-return guard in TradeRecordService.
            Await _service.ResolveOutcomeAsync(0L, DateTimeOffset.UtcNow, 0D, 0D, False, "n/a")
            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                Assert.Equal(0, Await repo.GetCountAsync())
            End Using
        End Function

        ' ─── FEAT-58 — TradeSetupSnapshot persistence ─────────────────────────
        Private Function MakeSnapshot() As TradeSetupSnapshot
            Return New TradeSetupSnapshot With {
                .AdxValue = 35.5F,
                .PlusDI = 28.1F,
                .MinusDI = 12.4F,
                .Rsi14 = 62.0F,
                .AtrValue = 1.75D,
                .LongCount = 2,
                .ShortCount = 0,
                .TotalConditions = 3,
                .UpPct = 66,
                .SignalBarOpen = 21000D,
                .SignalBarHigh = 21012D,
                .SignalBarLow = 20995D,
                .SignalBarClose = 21010D,
                .SignalBarVolume = 4200L,
                .SessionWindow = "US-RTH",
                .DayOfWeek = 2,
                .HourOfDay = 15,
                .StrategyName = "SuperTrendPlus",
                .PersonaName = "Damian",
                .SlMultiple = 1.0F,
                .TpMultiple = 2.5F,
                .TimeframeMinutes = 15
            }
        End Function

        <Fact>
        Public Async Function SaveSetupSnapshotAsync_RoundTripsFields() As Task
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId:=1L, model:=MakeOutcome())
            Dim snapId = Await _service.SaveSetupSnapshotAsync(outcomeId, MakeSnapshot())
            Assert.True(snapId > 0, $"Expected positive snapshot Id, got {snapId}")

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeSetupSnapshotRepository)()
                Dim row = Await repo.GetByTradeOutcomeIdAsync(outcomeId)
                Assert.NotNull(row)
                Assert.Equal(outcomeId, row.TradeOutcomeId)
                Assert.Equal(35.5F, row.AdxValue)
                Assert.Equal(21010D, row.SignalBarClose)
                Assert.Equal("SuperTrendPlus", row.StrategyName)
                Assert.Equal("Damian", row.PersonaName)
                Assert.Equal(15, row.TimeframeMinutes)
                Assert.Equal(2, row.LongCount)
            End Using
        End Function

        <Fact>
        Public Async Function SaveSetupSnapshotAsync_ZeroOutcomeIdIsNoOp() As Task
            Dim snapId = Await _service.SaveSetupSnapshotAsync(0L, MakeSnapshot())
            Assert.Equal(0L, snapId)
        End Function

        ' ─── FEAT-58 — TradeLifespan persistence ──────────────────────────────
        Private Function MakeLifespan() As TradeLifespan
            Return New TradeLifespan With {
                .MaxAdverseExcursionDollars = 45.0D,
                .MaxFavorableExcursionDollars = 320.0D,
                .MaxAdverseExcursionTicks = 12,
                .MaxFavorableExcursionTicks = 80,
                .SlRatchetCount = 4,
                .FreeRideActivated = True,
                .FreeRideActivatedAtMinutes = 18.5F,
                .DurationMinutes = 32.0F,
                .BarsInTrade = 2,
                .EntrySessionWindow = "London",
                .ExitSessionWindow = "US-Pre",
                .CrossedSessionBoundary = True,
                .RMultiple = 2.13F
            }
        End Function

        <Fact>
        Public Async Function SaveLifespanRecordAsync_InsertsRowAndRoundTrips() As Task
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId:=7L, model:=MakeOutcome())

            Await _service.SaveLifespanRecordAsync(outcomeId, MakeLifespan())

            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ITradeLifespanRepository)()
                Dim row = Await repo.GetByTradeOutcomeIdAsync(outcomeId)
                Assert.NotNull(row)
                Assert.Equal(outcomeId, row.TradeOutcomeId)
                Assert.Equal(45.0D, row.MaxAdverseExcursionDollars)
                Assert.Equal(320.0D, row.MaxFavorableExcursionDollars)
                Assert.Equal(4, row.SlRatchetCount)
                Assert.True(row.FreeRideActivated)
                Assert.Equal(18.5F, row.FreeRideActivatedAtMinutes)
                Assert.True(row.CrossedSessionBoundary)
                Assert.Equal(2.13F, row.RMultiple)
            End Using
        End Function

        <Fact>
        Public Async Function SaveLifespanRecordAsync_SecondCallUpsertsSameRow() As Task
            Dim sigId = Await _service.SaveSignalAsync(MakeSignal())
            Dim outcomeId = Await _service.OpenOutcomeAsync(sigId, recordId:=8L, model:=MakeOutcome())

            Await _service.SaveLifespanRecordAsync(outcomeId, MakeLifespan())

            Dim updated = MakeLifespan()
            updated.SlRatchetCount = 9
            updated.RMultiple = 3.5F
            Await _service.SaveLifespanRecordAsync(outcomeId, updated)

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim rows = db.TradeLifespanRecords.Where(Function(r) r.TradeOutcomeId = outcomeId).ToList()
                Assert.Single(rows)
                Assert.Equal(9, rows(0).SlRatchetCount)
                Assert.Equal(3.5F, rows(0).RMultiple)
            End Using
        End Function

        <Fact>
        Public Async Function SaveLifespanRecordAsync_ZeroOutcomeIdIsNoOp() As Task
            ' Should not throw — covers the early-return guard.
            Await _service.SaveLifespanRecordAsync(0L, MakeLifespan())
            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Assert.Equal(0, db.TradeLifespanRecords.Count())
            End Using
        End Function

    End Class

End Namespace
