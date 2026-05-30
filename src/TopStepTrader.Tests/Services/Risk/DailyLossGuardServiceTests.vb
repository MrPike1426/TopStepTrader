Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Risk
Imports Xunit

Namespace TopStepTrader.Tests.Services.Risk

    ''' <summary>
    ''' FEAT-71: Tests for the daily-loss kill switch.
    '''
    ''' Uses real SQLite-backed AppDbContext + TradeHistoryDbContext through a DI container
    ''' so the service's <c>IServiceScopeFactory</c> + scoped repository pattern matches
    ''' production. <see cref="DailyLossGuardService.TodayTradingDayStartUtc"/> is invoked
    ''' at evaluation time, so realised-PnL rows are stamped at "now" to land inside the
    ''' current trading window.
    ''' </summary>
    Public Class DailyLossGuardServiceTests
        Implements IDisposable

        Private ReadOnly _appDbPath As String
        Private ReadOnly _tradeDbPath As String
        Private ReadOnly _provider As ServiceProvider
        Private ReadOnly _service As DailyLossGuardService
        Private Const Limit As Decimal = -1000D

        Public Sub New()
            _appDbPath = Path.Combine(Path.GetTempPath(), $"feat71_app_{Guid.NewGuid():N}.db")
            _tradeDbPath = Path.Combine(Path.GetTempPath(), $"feat71_trades_{Guid.NewGuid():N}.db")

            Dim services As New ServiceCollection()
            services.AddDbContext(Of AppDbContext)(
                Sub(opts) opts.UseSqlite($"Data Source={_appDbPath}"))
            services.AddDbContext(Of TradeHistoryDbContext)(
                Sub(opts) opts.UseSqlite($"Data Source={_tradeDbPath}"))
            services.AddScoped(Of ILiveTradeRecordRepository, LiveTradeRecordRepository)()
            services.AddSingleton(Of IOptions(Of RiskSettings))(
                Options.Create(New RiskSettings With {.DailyLossLimitDollars = Limit}))

            _provider = services.BuildServiceProvider()

            Using scope = _provider.CreateScope()
                scope.ServiceProvider.GetRequiredService(Of AppDbContext)().Database.EnsureCreated()
                scope.ServiceProvider.GetRequiredService(Of TradeHistoryDbContext)().Database.EnsureCreated()
            End Using

            _service = New DailyLossGuardService(
                _provider.GetRequiredService(Of IServiceScopeFactory)(),
                _provider.GetRequiredService(Of IOptions(Of RiskSettings))(),
                NullLogger(Of DailyLossGuardService).Instance)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _service.Dispose()
                _provider.Dispose()
                SqliteConnection.ClearAllPools()
                If File.Exists(_appDbPath) Then File.Delete(_appDbPath)
                If File.Exists(_tradeDbPath) Then File.Delete(_tradeDbPath)
            Catch
            End Try
        End Sub

        Private Async Function InsertClosedTradeAsync(pnl As Decimal,
                                                       Optional exitOffset As TimeSpan? = Nothing) As Task
            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of TradeHistoryDbContext)()
                Dim now = DateTimeOffset.UtcNow
                Dim exitTime = now.Add(If(exitOffset.HasValue, exitOffset.Value, TimeSpan.Zero))
                db.LiveTradeRecords.Add(New LiveTradeRecordEntity With {
                    .EntryOrderId = 1L,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .Symbol = "MNQ",
                    .Direction = "Long",
                    .Sizes = 1,
                    .StrategyName = "Test",
                    .EntryTime = exitTime.AddMinutes(-5),
                    .ExitTime = exitTime,
                    .EntryPrice = 21000D,
                    .ExitPrice = 21000D + pnl,
                    .PnL = pnl,
                    .ExitReason = "TestClose",
                    .IsOpen = False,
                    .CreatedAt = exitTime,
                    .UpdatedAt = exitTime
                })
                Await db.SaveChangesAsync()
            End Using
        End Function

        Private Async Function CountRiskEventsAsync(eventType As String) As Task(Of Integer)
            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Return Await db.RiskEvents.CountAsync(Function(r) r.EventType = eventType)
            End Using
        End Function

        ' ── F5-a ──────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function BelowLimit_CanEnter_NotHalted() As Task
            Await InsertClosedTradeAsync(pnl:=-200D)
            Dim state = Await _service.EvaluateAsync()

            Assert.False(state.IsHalted)
            Assert.True(_service.CanEnterNewTrade())
            Assert.Equal(-200D, state.RealisedDailyPnl)
            Assert.Equal(0D, state.UnrealisedDailyPnl)
            Assert.Equal(-200D, state.CombinedDailyPnl)
        End Function

        ' ── F5-b ──────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function RealisedCrossesLimit_Halts_PersistsRiskEvent_FiresOnce() As Task
            Await InsertClosedTradeAsync(pnl:=-600D)
            Await InsertClosedTradeAsync(pnl:=-500D)

            Dim haltFireCount As Integer = 0
            AddHandler _service.Halted, Sub(s, e) haltFireCount += 1

            Dim first = Await _service.EvaluateAsync()
            Assert.True(first.IsHalted)
            Assert.Equal(RiskHaltReason.DailyLossLimit, first.Reason)
            Assert.Equal(-1100D, first.CombinedDailyPnl)
            Assert.False(_service.CanEnterNewTrade())
            Assert.Equal(1, Await CountRiskEventsAsync("DailyLossLimit"))

            ' Re-evaluating with the same data must NOT re-fire Halted nor write a 2nd row.
            Await _service.EvaluateAsync()
            Assert.Equal(1, haltFireCount)
            Assert.Equal(1, Await CountRiskEventsAsync("DailyLossLimit"))
        End Function

        ' ── F5-c ──────────────────────────────────────────────────────────────

        Private Class StubPnlSource
            Implements IOpenSlotPnlSource
            Public Property Unrealised As Decimal
            Public Property IsOpen As Boolean = True
            Public Function GetUnrealisedAggregate() As Decimal _
                Implements IOpenSlotPnlSource.GetUnrealisedAggregate
                Return Unrealised
            End Function
            Public Function HasOpenSlots() As Boolean _
                Implements IOpenSlotPnlSource.HasOpenSlots
                Return IsOpen
            End Function
        End Class

        <Fact>
        Public Async Function UnrealisedCrossesLimit_HaltsBeforeAnyClose() As Task
            Dim source As New StubPnlSource With {.Unrealised = -1200D}
            _service.RegisterOpenSlotPnlSource(source)

            Dim state = Await _service.EvaluateAsync()

            Assert.True(state.IsHalted)
            Assert.Equal(0D, state.RealisedDailyPnl)
            Assert.Equal(-1200D, state.UnrealisedDailyPnl)
            Assert.Equal(-1200D, state.CombinedDailyPnl)
            Assert.False(_service.CanEnterNewTrade())
        End Function

        ' ── F5-d ──────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function Reset_ClearsHalt_FiresReleased_PersistsResetEvent() As Task
            Await InsertClosedTradeAsync(pnl:=-1500D)
            Await _service.EvaluateAsync()
            Assert.False(_service.CanEnterNewTrade())

            Dim releasedFireCount As Integer = 0
            AddHandler _service.Released, Sub(s, e) releasedFireCount += 1

            Await _service.ResetAsync("Manual reset for test")

            Assert.True(_service.CanEnterNewTrade())
            Assert.False(_service.GetState().IsHalted)
            Assert.Equal(1, releasedFireCount)
            Assert.Equal(1, Await CountRiskEventsAsync("DailyLossReset"))
        End Function

        ' ── F5-e ──────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function Rollover_YesterdayLossDoesNotHaltToday() As Task
            ' Insert a closed trade with ExitTime well before today's trading-day start.
            ' Service computes "today" via TodayTradingDayStartUtc, so a 5-day-old close
            ' is unambiguously outside the window regardless of the test's local timezone.
            Await InsertClosedTradeAsync(pnl:=-5000D, exitOffset:=TimeSpan.FromDays(-5))

            Dim state = Await _service.EvaluateAsync()

            Assert.False(state.IsHalted)
            Assert.Equal(0D, state.RealisedDailyPnl)
            Assert.True(_service.CanEnterNewTrade())
        End Function

        ' ── F5-f ──────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function MultipleSources_AggregateSummed() As Task
            Dim a As New StubPnlSource With {.Unrealised = -300D}
            Dim b As New StubPnlSource With {.Unrealised = -250D}
            Dim c As New StubPnlSource With {.Unrealised = -600D}
            _service.RegisterOpenSlotPnlSource(a)
            _service.RegisterOpenSlotPnlSource(b)
            _service.RegisterOpenSlotPnlSource(c)

            Dim state = Await _service.EvaluateAsync()

            Assert.Equal(-1150D, state.UnrealisedDailyPnl)
            Assert.True(state.IsHalted, $"Combined {state.CombinedDailyPnl} should breach limit {Limit}")
        End Function

    End Class

End Namespace
