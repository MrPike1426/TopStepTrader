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
    ''' Round-trip tests for TradeLifespanRepository using an in-memory SQLite database.
    ''' Acceptance criteria: FEAT-01 TradeLifespanRepositoryTests.
    ''' </summary>
    Public Class TradeLifespanRepositoryTests
        Implements IDisposable

        Private ReadOnly _conn As SqliteConnection
        Private ReadOnly _ctx As AppDbContext
        Private ReadOnly _sut As TradeLifespanRepository

        Public Sub New()
            _conn = New SqliteConnection("Data Source=:memory:")
            _conn.Open()
            Dim opts = New DbContextOptionsBuilder(Of AppDbContext)().UseSqlite(_conn).Options
            _ctx = New AppDbContext(opts)
            _ctx.Database.EnsureCreated()
            _ctx.EnsureSchemaCurrent()
            _sut = New TradeLifespanRepository(_ctx)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _ctx.Dispose()
            _conn.Dispose()
        End Sub

        <Fact>
        Public Async Function SaveAsync_RoundTrip_ReturnsId() As Task
            Dim entity As New TradeLifespanRecordEntity With {
                .TradeOutcomeId = 10L,
                .EntrySessionWindow = "London",
                .CreatedAt = DateTimeOffset.UtcNow,
                .UpdatedAt = DateTimeOffset.UtcNow
            }

            Dim id = Await _sut.SaveAsync(entity)
            Assert.True(id > 0)

            Dim retrieved = Await _sut.GetByTradeOutcomeIdAsync(10L)
            Assert.NotNull(retrieved)
            Assert.Equal(10L, retrieved.TradeOutcomeId)
            Assert.Equal("London", retrieved.EntrySessionWindow)
        End Function

        <Fact>
        Public Async Function UpdateAsync_PersistsChanges() As Task
            Dim entity As New TradeLifespanRecordEntity With {
                .TradeOutcomeId = 20L,
                .MaxAdverseExcursionDollars = 0D,
                .MaxFavorableExcursionDollars = 0D,
                .CreatedAt = DateTimeOffset.UtcNow,
                .UpdatedAt = DateTimeOffset.UtcNow
            }
            Await _sut.SaveAsync(entity)

            ' Simulate what PersistTradeClose does after the trade ends
            Dim lifespan = Await _sut.GetByTradeOutcomeIdAsync(20L)
            Assert.NotNull(lifespan)

            ' EF Core AsNoTracking — need a fresh tracked entity for update
            Dim toUpdate As New TradeLifespanRecordEntity With {
                .Id = lifespan.Id,
                .TradeOutcomeId = 20L,
                .MaxAdverseExcursionDollars = -45.5D,
                .MaxFavorableExcursionDollars = 120.0D,
                .SlRatchetCount = 3,
                .FreeRideActivated = True,
                .FreeRideActivatedAtMinutes = 12.5F,
                .DurationMinutes = 37.2F,
                .BarsInTrade = 7,
                .EntrySessionWindow = "US Session",
                .ExitSessionWindow = "US Session",
                .RMultiple = 2.1F,
                .CreatedAt = lifespan.CreatedAt,
                .UpdatedAt = DateTimeOffset.UtcNow
            }
            Await _sut.UpdateAsync(toUpdate)

            Dim updated = Await _sut.GetByTradeOutcomeIdAsync(20L)
            Assert.NotNull(updated)
            Assert.Equal(-45.5D, updated.MaxAdverseExcursionDollars)
            Assert.Equal(120.0D, updated.MaxFavorableExcursionDollars)
            Assert.Equal(3, updated.SlRatchetCount)
            Assert.True(updated.FreeRideActivated)
            Assert.Equal(2.1F, updated.RMultiple, precision:=1)
        End Function

        <Fact>
        Public Async Function GetByTradeOutcomeIdAsync_NoMatch_ReturnsNothing() As Task
            Dim result = Await _sut.GetByTradeOutcomeIdAsync(9999L)
            Assert.Null(result)
        End Function

    End Class

End Namespace
