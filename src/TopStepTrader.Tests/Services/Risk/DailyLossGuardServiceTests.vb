Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
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
    ''' production. The day window comes from <see cref="TradingDayClock"/> evaluated at
    ''' the service's injectable clock (ARCH-21), so realised-PnL rows are stamped at
    ''' "now" to land inside the current trading window.
    ''' </summary>
    Public Class DailyLossGuardServiceTests
        Implements IDisposable

        Private ReadOnly _appDbPath As String
        Private ReadOnly _tradeDbPath As String
        Private ReadOnly _provider As ServiceProvider
        Private ReadOnly _service As DailyLossGuardService
        Private ReadOnly _extraServices As New List(Of DailyLossGuardService)()
        Private ReadOnly _session As New StubSessionContext()
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
            services.AddScoped(Of ICombineAccountStateRepository, CombineAccountStateRepository)() ' FEAT-74
            ' FEAT-74: session context stub; SelectedAccount stays Nothing until a trail
            ' test selects one, so pre-FEAT-74 tests run with the trail inactive.
            services.AddSingleton(Of ITradingSessionContext)(_session)
            services.AddSingleton(Of IOptions(Of RiskSettings))(
                Options.Create(New RiskSettings With {.DailyLossLimitDollars = Limit}))

            _provider = services.BuildServiceProvider()

            Using scope = _provider.CreateScope()
                scope.ServiceProvider.GetRequiredService(Of AppDbContext)().Database.EnsureCreated()
                scope.ServiceProvider.GetRequiredService(Of TradeHistoryDbContext)().Database.EnsureCreated()
            End Using

            ' Legacy-mode service (combine disabled): pre-FEAT-73 behaviour under test.
            _service = New DailyLossGuardService(
                _provider.GetRequiredService(Of IServiceScopeFactory)(),
                _provider.GetRequiredService(Of IOptions(Of RiskSettings))(),
                Options.Create(New CombineSettings()),
                New StubFlattener(),
                NullLogger(Of DailyLossGuardService).Instance)
        End Sub

        ''' <summary>FEAT-73: guard instance with combine mode on, sharing this fixture's databases.</summary>
        Private Function CreateCombineService(flattener As StubFlattener,
                                              Optional combine As CombineSettings = Nothing) As DailyLossGuardService
            Dim svc As New DailyLossGuardService(
                _provider.GetRequiredService(Of IServiceScopeFactory)(),
                _provider.GetRequiredService(Of IOptions(Of RiskSettings))(),
                Options.Create(If(combine, New CombineSettings With {.Enabled = True})),
                flattener,
                NullLogger(Of DailyLossGuardService).Instance)
            _extraServices.Add(svc)
            Return svc
        End Function

        ''' <summary>FEAT-74: minimal session context so ResolveAccountId sees a selected account.</summary>
        Private Class StubSessionContext
            Implements ITradingSessionContext

            Private _account As Account

            Public Event AccountChanged As EventHandler(Of Account) _
                Implements ITradingSessionContext.AccountChanged
            Public Event AutoExecutionChanged As EventHandler _
                Implements ITradingSessionContext.AutoExecutionChanged

            Public ReadOnly Property SelectedAccount As Account _
                Implements ITradingSessionContext.SelectedAccount
                Get
                    Return _account
                End Get
            End Property

            Public ReadOnly Property ActiveBroker As BrokerType _
                Implements ITradingSessionContext.ActiveBroker
                Get
                    Return BrokerType.TopStepX
                End Get
            End Property

            Public ReadOnly Property AutoExecutionEnabled As Boolean _
                Implements ITradingSessionContext.AutoExecutionEnabled
                Get
                    Return False
                End Get
            End Property

            Public Sub SelectAccount(account As Account) _
                Implements ITradingSessionContext.SelectAccount
                _account = account
            End Sub

            Public Sub SetAutoExecution(enabled As Boolean) _
                Implements ITradingSessionContext.SetAutoExecution
            End Sub
        End Class

        Private Class StubFlattener
            Implements IPositionFlattener
            Public Property CallCount As Integer
            Public Property LastAccountId As Long

            Public Function FlattenAllAsync(accountId As Long) As Task(Of FlattenAllResult) _
                Implements IPositionFlattener.FlattenAllAsync
                CallCount += 1
                LastAccountId = accountId
                Return Task.FromResult(New FlattenAllResult With {.OrdersCancelled = True})
            End Function
        End Class

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                For Each svc In _extraServices
                    svc.Dispose()
                Next
                _service.Dispose()
                _provider.Dispose()
                SqliteConnection.ClearAllPools()
                If File.Exists(_appDbPath) Then File.Delete(_appDbPath)
                If File.Exists(_tradeDbPath) Then File.Delete(_tradeDbPath)
            Catch
            End Try
        End Sub

        Private Async Function InsertClosedTradeAsync(pnl As Decimal,
                                                       Optional exitOffset As TimeSpan? = Nothing,
                                                       Optional commission As Decimal = 0D,
                                                       Optional fees As Decimal = 0D) As Task
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
                    .CommissionUsd = commission,
                    .FeesUsd = fees,
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
            ' Service computes "today" via TradingDayClock, so a 5-day-old close
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

        ' ── ARCH-21 F3: auto-release on trading-day rollover ──────────────────

        <Fact>
        Public Async Function DayRollover_AutoReleasesHalt_PersistsEvent_WindowResets() As Task
            Dim t0 As DateTimeOffset = DateTimeOffset.UtcNow
            _service.UtcNowProvider = Function() t0

            Await InsertClosedTradeAsync(pnl:=-1500D)
            Dim haltedState = Await _service.EvaluateAsync()
            Assert.True(haltedState.IsHalted)
            Assert.False(_service.CanEnterNewTrade())

            Dim releasedFireCount As Integer = 0
            AddHandler _service.Released, Sub(s, e) releasedFireCount += 1

            ' Advance the injected clock two days — unambiguously past the next 17:00 CT.
            Dim t1 As DateTimeOffset = t0.AddDays(2)
            Assert.NotEqual(TradingDayClock.TradingDayKey(t0), TradingDayClock.TradingDayKey(t1))
            _service.UtcNowProvider = Function() t1

            Dim state = Await _service.EvaluateAsync()

            Assert.False(state.IsHalted)
            Assert.True(_service.CanEnterNewTrade())
            Assert.Equal(1, releasedFireCount)
            Assert.Equal(1, Await CountRiskEventsAsync("DayRollover"))
            ' Realised window now starts at the new 17:00 CT boundary, excluding the old loss.
            Assert.Equal(0D, state.RealisedDailyPnl)
        End Function

        <Fact>
        Public Async Function SameTradingDay_ReEvaluation_DoesNotRelease() As Task
            Dim t0 As DateTimeOffset = DateTimeOffset.UtcNow
            _service.UtcNowProvider = Function() t0

            Await InsertClosedTradeAsync(pnl:=-1500D)
            Await _service.EvaluateAsync()
            Assert.False(_service.CanEnterNewTrade())

            ' Second tick inside the same trading day: halt must persist, no rollover event.
            Dim state = Await _service.EvaluateAsync()
            Assert.True(state.IsHalted)
            Assert.Equal(0, Await CountRiskEventsAsync("DayRollover"))
        End Function

        ' ═══ FEAT-73: combine mode ═════════════════════════════════════════════

        <Fact>
        Public Async Function Combine_SoftLoss_BlocksEntries_NoFlatten_ClearsOnRecovery() As Task
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener)

            Await InsertClosedTradeAsync(pnl:=-600D)
            Dim state = Await svc.EvaluateAsync()

            Assert.True(state.SoftHalted)
            Assert.False(state.IsHalted)
            Assert.Equal(RiskHaltReason.DailyLossLimit, state.Reason)
            Assert.False(svc.CanEnterNewTrade())
            Assert.Equal(0, flattener.CallCount)

            ' Loss-line soft halt clears automatically when combined P&L recovers.
            Dim source As New StubPnlSource With {.Unrealised = 100D}
            svc.RegisterOpenSlotPnlSource(source)
            Dim recovered = Await svc.EvaluateAsync()

            Assert.False(recovered.SoftHalted)
            Assert.True(svc.CanEnterNewTrade())
        End Function

        <Fact>
        Public Async Function Combine_HardLoss_FlattensOnce_RaisesForceFlattened_PersistsEvent() As Task
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener)

            Await InsertClosedTradeAsync(pnl:=-800D)

            Dim forceCount As Integer = 0
            Dim haltCount As Integer = 0
            AddHandler svc.ForceFlattened, Sub(s, e) forceCount += 1
            AddHandler svc.Halted, Sub(s, e) haltCount += 1

            Dim state = Await svc.EvaluateAsync()

            Assert.True(state.IsHalted)
            Assert.Equal(RiskHaltReason.DailyLossLimit, state.Reason)
            Assert.False(svc.CanEnterNewTrade())
            Assert.Equal(1, flattener.CallCount)
            Assert.Equal(1, forceCount)
            Assert.Equal(1, haltCount)
            Assert.Equal(1, Await CountRiskEventsAsync("CombineHardLoss"))

            ' Re-evaluating with the same data must not flatten or fire again.
            Await svc.EvaluateAsync()
            Assert.Equal(1, flattener.CallCount)
            Assert.Equal(1, forceCount)
            Assert.Equal(1, Await CountRiskEventsAsync("CombineHardLoss"))
        End Function

        <Fact>
        Public Async Function Combine_FeesIncludedInDailyPnl_WhenConfigured() As Task
            ' Gross −500 stays above the −600 soft line; net of $60 commission + $45 fees
            ' (−605) breaches it. TopStep counts fees, so the halt must fire.
            Await InsertClosedTradeAsync(pnl:=-500D, commission:=60D, fees:=45D)

            Dim withFees = CreateCombineService(New StubFlattener())
            Dim state = Await withFees.EvaluateAsync()
            Assert.Equal(-605D, state.RealisedDailyPnl)
            Assert.True(state.SoftHalted)

            Dim withoutFees = CreateCombineService(
                New StubFlattener(),
                New CombineSettings With {.Enabled = True, .IncludeFeesInDailyPnl = False})
            Dim grossState = Await withoutFees.EvaluateAsync()
            Assert.Equal(-500D, grossState.RealisedDailyPnl)
            Assert.False(grossState.SoftHalted)
        End Function

        <Fact>
        Public Async Function Combine_ProfitLock_ArmsAtTrigger_FlattensOnFloorRetrace() As Task
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener)
            Dim source As New StubPnlSource With {.Unrealised = 220D}
            svc.RegisterOpenSlotPnlSource(source)

            Dim armedState = Await svc.EvaluateAsync()
            Assert.True(armedState.ProfitLockArmed)
            Assert.Equal(220D, armedState.ProfitLockHighWater)
            Assert.False(armedState.IsHalted)
            Assert.True(svc.CanEnterNewTrade())

            ' Pullback above the floor: high-water holds, still trading.
            source.Unrealised = 180D
            Dim holdState = Await svc.EvaluateAsync()
            Assert.Equal(220D, holdState.ProfitLockHighWater)
            Assert.False(holdState.IsHalted)

            ' Retrace to the +170 floor: flatten + halt with DailyProfitLock.
            source.Unrealised = 90D
            Dim lockedState = Await svc.EvaluateAsync()
            Assert.True(lockedState.IsHalted)
            Assert.Equal(RiskHaltReason.DailyProfitLock, lockedState.Reason)
            Assert.Equal(1, flattener.CallCount)
            Assert.Equal(1, Await CountRiskEventsAsync("CombineProfitLock"))
        End Function

        <Fact>
        Public Async Function Combine_ProfitLock_BanksImmediately_WhenFlatAtTrigger() As Task
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener)

            Await InsertClosedTradeAsync(pnl:=230D)
            Dim state = Await svc.EvaluateAsync()

            Assert.True(state.IsHalted)
            Assert.Equal(RiskHaltReason.DailyProfitLock, state.Reason)
            ' Flattener still runs on a flat book: it sweeps pre-staged stop entries.
            Assert.Equal(1, flattener.CallCount)
        End Function

        <Fact>
        Public Async Function Combine_MaxTrades_SoftHalt_PersistsAcrossEvaluations() As Task
            Dim svc = CreateCombineService(New StubFlattener())
            ' Positive offsets keep every close inside the current trading day even when
            ' the test runs moments after the 17:00 CT rollover.
            For i = 1 To 4
                Await InsertClosedTradeAsync(pnl:=10D, exitOffset:=TimeSpan.FromMinutes(i))
            Next

            Dim state = Await svc.EvaluateAsync()
            Assert.True(state.SoftHalted)
            Assert.Equal(RiskHaltReason.MaxTradesPerDay, state.Reason)
            Assert.Equal(4, state.TradesToday)
            Assert.False(svc.CanEnterNewTrade())

            Dim again = Await svc.EvaluateAsync()
            Assert.True(again.SoftHalted)
        End Function

        <Fact>
        Public Async Function Combine_ConsecutiveLosers_SoftHalt_StickyEvenAfterLateWinner() As Task
            Dim svc = CreateCombineService(New StubFlattener())
            ' Positive offsets: inside the current trading day regardless of wall clock.
            Await InsertClosedTradeAsync(pnl:=-50D, exitOffset:=TimeSpan.FromMinutes(1))
            Await InsertClosedTradeAsync(pnl:=-50D, exitOffset:=TimeSpan.FromMinutes(2))

            Dim state = Await svc.EvaluateAsync()
            Assert.True(state.SoftHalted)
            Assert.Equal(RiskHaltReason.ConsecutiveLosses, state.Reason)
            Assert.Equal(2, state.ConsecutiveLosers)

            ' A position that was already open closes green: the derived counter resets,
            ' but the circuit breaker stays tripped for the day.
            Await InsertClosedTradeAsync(pnl:=50D, exitOffset:=TimeSpan.FromMinutes(3))
            Dim sticky = Await svc.EvaluateAsync()
            Assert.Equal(0, sticky.ConsecutiveLosers)
            Assert.True(sticky.SoftHalted)
            Assert.Equal(RiskHaltReason.ConsecutiveLosses, sticky.Reason)
            Assert.False(svc.CanEnterNewTrade())
        End Function

        <Fact>
        Public Sub Combine_Lf11_EntryGate_ReEvaluatesWhenStaleWithOpenSlot()
            ' No explicit EvaluateAsync: the gate must not answer from the (stale,
            ' never-evaluated) cache while a slot is open and deep underwater.
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener)
            svc.RegisterOpenSlotPnlSource(New StubPnlSource With {.Unrealised = -800D})

            Assert.False(svc.CanEnterNewTrade())
            Assert.True(svc.GetState().IsHalted)
            Assert.Equal(1, flattener.CallCount)
        End Sub

        <Fact>
        Public Async Function Combine_DayRollover_ResetsLockStateAndCounters() As Task
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener)
            Dim t0 As DateTimeOffset = DateTimeOffset.UtcNow
            svc.UtcNowProvider = Function() t0

            Await InsertClosedTradeAsync(pnl:=-800D)
            Dim haltedState = Await svc.EvaluateAsync()
            Assert.True(haltedState.IsHalted)

            Dim t1 As DateTimeOffset = t0.AddDays(2)
            svc.UtcNowProvider = Function() t1
            Dim state = Await svc.EvaluateAsync()

            Assert.False(state.IsHalted)
            Assert.False(state.SoftHalted)
            Assert.False(state.ProfitLockArmed)
            Assert.Equal(0D, state.ProfitLockHighWater)
            Assert.Equal(0, state.TradesToday)
            Assert.Equal(0, state.ConsecutiveLosers)
            Assert.True(svc.CanEnterNewTrade())
        End Function

        ' ═══ FEAT-74: trailing max-drawdown (MLL) ══════════════════════════════

        Private Const MllAccountId As Long = 4242L

        ''' <summary>
        ''' Combine settings tuned so only the trail can fire: daily lines pushed far
        ''' away, counters disabled. Start 50 000; trailing/buffer per test.
        ''' </summary>
        Private Shared Function MllSettings(Optional trailing As Decimal = -100D,
                                            Optional buffer As Decimal = 0D,
                                            Optional trailMode As String = "IntradayPeak") As CombineSettings
            Return New CombineSettings With {
                .Enabled = True,
                .StartingBalance = 50000D,
                .DailyLossSoftDollars = -9000D,
                .DailyLossHardDollars = -9500D,
                .MaxTradesPerDay = 0,
                .MaxConsecutiveLosers = 0,
                .TrailingMaxDrawdownDollars = trailing,
                .SafetyBufferDollars = buffer,
                .TrailMode = trailMode
            }
        End Function

        Private Sub SelectMllAccount()
            _session.SelectAccount(New Account With {.Id = MllAccountId})
        End Sub

        <Fact>
        Public Async Function Mll_Breach_HaltsAndFlattens_WithMaxDrawdownReason() As Task
            SelectMllAccount()
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener, MllSettings())

            ' No broker push in the fixture → fallback equity model:
            ' 50 000 + 0 cumulative + (−150 realised) = 49 850 <= floor 49 900.
            Await InsertClosedTradeAsync(pnl:=-150D)
            Dim state = Await svc.EvaluateAsync()

            Assert.True(state.IsHalted)
            Assert.Equal(RiskHaltReason.MaxDrawdown, state.Reason)
            Assert.True(state.MllTrailActive)
            Assert.Equal(49850D, state.EquityNow)
            Assert.Equal(49900D, state.MllFloor)
            Assert.False(svc.CanEnterNewTrade())
            Assert.Equal(1, flattener.CallCount)
            Assert.Equal(1, Await CountRiskEventsAsync("CombineMaxDrawdown"))

            ' Idempotent: same data must not flatten or log again.
            Await svc.EvaluateAsync()
            Assert.Equal(1, flattener.CallCount)
            Assert.Equal(1, Await CountRiskEventsAsync("CombineMaxDrawdown"))
        End Function

        <Fact>
        Public Async Function Mll_PeakRatchets_AndSurvivesRestart() As Task
            SelectMllAccount()
            Dim svc = CreateCombineService(New StubFlattener(), MllSettings(trailing:=-300D))
            Dim source As New StubPnlSource With {.Unrealised = 200D}
            svc.RegisterOpenSlotPnlSource(source)

            Dim first = Await svc.EvaluateAsync()
            Assert.Equal(50200D, first.PeakEquity)
            Assert.Equal(49900D, first.MllFloor) ' min(50 200 − 300, 50 000 freeze)
            Assert.False(first.IsHalted)
            svc.Dispose()

            ' "Restart": fresh service, same database — the trail resumes at the
            ' ratcheted peak instead of resetting to the starting balance.
            Dim restarted = CreateCombineService(New StubFlattener(), MllSettings(trailing:=-300D))
            Dim state = Await restarted.EvaluateAsync()
            Assert.Equal(50200D, state.PeakEquity)
            Assert.Equal(49900D, state.MllFloor)
            Assert.False(state.IsHalted) ' fallback equity 50 000 stays above 49 900
        End Function

        <Fact>
        Public Async Function MllHalt_SurvivesDayRollover_NoAutoRelease() As Task
            SelectMllAccount()
            Dim flattener As New StubFlattener()
            Dim svc = CreateCombineService(flattener, MllSettings())
            Dim t0 As DateTimeOffset = DateTimeOffset.UtcNow
            svc.UtcNowProvider = Function() t0

            Await InsertClosedTradeAsync(pnl:=-150D)
            Dim haltedState = Await svc.EvaluateAsync()
            Assert.True(haltedState.IsHalted)
            Assert.Equal(RiskHaltReason.MaxDrawdown, haltedState.Reason)

            Dim releasedFireCount As Integer = 0
            AddHandler svc.Released, Sub(s, e) releasedFireCount += 1

            svc.UtcNowProvider = Function() t0.AddDays(2)
            Dim state = Await svc.EvaluateAsync()

            Assert.True(state.IsHalted)
            Assert.Equal(RiskHaltReason.MaxDrawdown, state.Reason)
            Assert.False(svc.CanEnterNewTrade())
            Assert.Equal(0, releasedFireCount)
            Assert.Equal(1, flattener.CallCount) ' no re-flatten across the rollover
        End Function

        <Fact>
        Public Async Function MllHalt_ManualReset_ReBaselinesPersistedTrail() As Task
            SelectMllAccount()
            Dim svc = CreateCombineService(New StubFlattener(), MllSettings())

            Await InsertClosedTradeAsync(pnl:=-150D)
            Dim haltedState = Await svc.EvaluateAsync()
            Assert.Equal(RiskHaltReason.MaxDrawdown, haltedState.Reason)

            Await svc.ResetAsync("Practice account reset")

            Assert.True(svc.CanEnterNewTrade())
            Assert.False(svc.GetState().IsHalted)
            Assert.Equal(1, Await CountRiskEventsAsync("DailyLossReset"))

            ' Persisted row re-baselined for practice-account reuse.
            Using scope = _provider.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of ICombineAccountStateRepository)()
                Dim row = Await repo.GetOrCreateAsync(MllAccountId, 50000D)
                Assert.Equal(50000D, row.PeakEquity)
                Assert.Equal(0D, row.CumulativeRealisedPnl)
                Assert.Equal(49900D, row.MllFloor)
            End Using
        End Function

        <Fact>
        Public Async Function Mll_EndOfDayMode_SamplesPeakOnlyAtRollover() As Task
            SelectMllAccount()
            Dim svc = CreateCombineService(New StubFlattener(),
                                           MllSettings(trailing:=-300D, trailMode:="EndOfDay"))
            Dim t0 As DateTimeOffset = DateTimeOffset.UtcNow
            svc.UtcNowProvider = Function() t0
            Dim source As New StubPnlSource With {.Unrealised = 200D}
            svc.RegisterOpenSlotPnlSource(source)

            ' Intraday high does NOT move the peak in EndOfDay mode.
            Dim first = Await svc.EvaluateAsync()
            Assert.Equal(50000D, first.PeakEquity)
            Assert.Equal(49700D, first.MllFloor)

            ' Rollover samples the last modelled equity (50 200) into the peak.
            svc.UtcNowProvider = Function() t0.AddDays(2)
            Dim state = Await svc.EvaluateAsync()
            Assert.Equal(50200D, state.PeakEquity)
            Assert.Equal(49900D, state.MllFloor)
            Assert.False(state.IsHalted)
        End Function

    End Class

End Namespace
