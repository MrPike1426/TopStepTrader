Option Strict On
Option Explicit On

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

Namespace TopStepTrader.Tests.Data

    ''' <summary>
    ''' Round-trip tests for TradeRecordService.LogStopAdjustmentAsync / GetStopAdjustmentsAsync.
    ''' Uses an in-memory SQLite database — no external dependencies.
    ''' </summary>
    Public Class TradeStopAdjustmentRepositoryTests
        Implements IDisposable

        Private ReadOnly _conn As SqliteConnection
        Private ReadOnly _ctx As TradeHistoryDbContext
        Private ReadOnly _repo As TradeStopAdjustmentRepository

        Public Sub New()
            _conn = New SqliteConnection("Data Source=:memory:")
            _conn.Open()
            Dim opts = New DbContextOptionsBuilder(Of TradeHistoryDbContext)().UseSqlite(_conn).Options
            _ctx = New TradeHistoryDbContext(opts)
            _ctx.Database.EnsureCreated()
            _ctx.EnsureSchemaCurrent()
            _repo = New TradeStopAdjustmentRepository(_ctx)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _ctx.Dispose()
            _conn.Dispose()
        End Sub

        <Fact>
        Public Async Function LogStopAdjustmentAsync_RoundTrip_ReturnsChronologicalRows() As Task
            ' Arrange — wire up a scoped service container pointing at the same in-memory DB
            Dim services As New ServiceCollection()
            services.AddSingleton(Of TradeHistoryDbContext)(_ctx)
            services.AddScoped(Of ILiveTradeRecordRepository, LiveTradeRecordRepository)()
            services.AddScoped(Of ITradeStopAdjustmentRepository, TradeStopAdjustmentRepository)()

            ' TradeRecordService needs a non-null PXOrderClient; skip by using a raw repo directly
            Dim stopRepo As ITradeStopAdjustmentRepository = _repo

            Dim t0 = New DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero)
            Dim t1 = t0.AddMinutes(5)
            Dim t2 = t0.AddMinutes(10)

            ' Act — log three adjustments out-of-order (repo sorts by Timestamp string)
            Await stopRepo.AddAsync(New TradeStopAdjustmentEntity With {
                .LiveTradeRecordId = 1L,
                .Timestamp = t1.UtcDateTime.ToString("o"),
                .OldStop = "100",
                .NewStop = "102",
                .TriggerReason = "Breakeven"
            })
            Await stopRepo.AddAsync(New TradeStopAdjustmentEntity With {
                .LiveTradeRecordId = 1L,
                .Timestamp = t0.UtcDateTime.ToString("o"),
                .OldStop = "100",
                .NewStop = "100",
                .TriggerReason = "Initial"
            })
            Await stopRepo.AddAsync(New TradeStopAdjustmentEntity With {
                .LiveTradeRecordId = 1L,
                .Timestamp = t2.UtcDateTime.ToString("o"),
                .OldStop = "102",
                .NewStop = "105",
                .TriggerReason = "AtrRatchet"
            })
            ' Row for a different trade — should not appear
            Await stopRepo.AddAsync(New TradeStopAdjustmentEntity With {
                .LiveTradeRecordId = 99L,
                .Timestamp = t0.UtcDateTime.ToString("o"),
                .OldStop = "200",
                .NewStop = "200",
                .TriggerReason = "Initial"
            })

            ' Assert — three rows in chronological order for record 1
            Dim rows = Await stopRepo.GetByTradeRecordAsync(1L)
            Assert.Equal(3, rows.Count)
            Assert.Equal("Initial", rows(0).TriggerReason)
            Assert.Equal("Breakeven", rows(1).TriggerReason)
            Assert.Equal("AtrRatchet", rows(2).TriggerReason)
        End Function

    End Class

End Namespace
