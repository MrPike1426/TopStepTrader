Option Strict On
Option Explicit On

Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports Xunit

Namespace TopStepTrader.Tests.Data

    ''' <summary>
    ''' Round-trip tests for TradeSetupSnapshotRepository using an in-memory SQLite database.
    ''' Acceptance criteria: FEAT-01 TradeSetupSnapshotRepositoryTests.
    ''' </summary>
    Public Class TradeSetupSnapshotRepositoryTests
        Implements IDisposable

        Private ReadOnly _conn As SqliteConnection
        Private ReadOnly _ctx As AppDbContext
        Private ReadOnly _sut As TradeSetupSnapshotRepository

        Public Sub New()
            _conn = New SqliteConnection("Data Source=:memory:")
            _conn.Open()
            Dim opts = New DbContextOptionsBuilder(Of AppDbContext)().UseSqlite(_conn).Options
            _ctx = New AppDbContext(opts)
            _ctx.Database.EnsureCreated()
            _ctx.EnsureSchemaCurrent()
            _sut = New TradeSetupSnapshotRepository(_ctx)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _ctx.Dispose()
            _conn.Dispose()
        End Sub

        <Fact>
        Public Async Function SaveAsync_RoundTrip_ReturnsCorrectSnapshot() As Task
            Dim entity As New TradeSetupSnapshotEntity With {
                .TradeOutcomeId = 42L,
                .CapturedAt = DateTimeOffset.UtcNow,
                .StrategyName = "MultiConfluence",
                .PersonaName = "Damian",
                .SlMultiple = 1.5F,
                .TpMultiple = 3.0F,
                .TimeframeMinutes = 5,
                .AtrValue = 12.5D,
                .AdxValue = 28.3F,
                .Ema21 = 4500.25D,
                .Ema50 = 4490.10D,
                .UpPct = 85,
                .DownPct = 15,
                .LongCount = 6,
                .TotalConditions = 7,
                .SessionWindow = "London-US Overlap",
                .DayOfWeek = 2,
                .HourOfDay = 14
            }

            Dim id = Await _sut.SaveAsync(entity)
            Assert.True(id > 0L)

            Dim retrieved = Await _sut.GetByTradeOutcomeIdAsync(42L)
            Assert.NotNull(retrieved)
            Assert.Equal(42L, retrieved.TradeOutcomeId)
            Assert.Equal("MultiConfluence", retrieved.StrategyName)
            Assert.Equal("Damian", retrieved.PersonaName)
            Assert.Equal(1.5F, retrieved.SlMultiple)
            Assert.Equal(85, retrieved.UpPct)
            Assert.Equal(28.3F, retrieved.AdxValue, precision:=1)
            Assert.Equal("London-US Overlap", retrieved.SessionWindow)
        End Function

        <Fact>
        Public Async Function GetByTradeOutcomeIdAsync_NoMatch_ReturnsNothing() As Task
            Dim result = Await _sut.GetByTradeOutcomeIdAsync(9999L)
            Assert.Null(result)
        End Function

    End Class

End Namespace
