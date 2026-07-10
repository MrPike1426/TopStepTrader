Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Risk

    ''' <summary>
    ''' FEAT-71: Singleton implementation of <see cref="IDailyLossGuard"/>.
    '''
    ''' Background ticker re-evaluates combined PnL on a 5 s cadence while any registered
    ''' <see cref="IOpenSlotPnlSource"/> is non-empty, 30 s otherwise. Strategy entry
    ''' paths call <see cref="CanEnterNewTrade"/> synchronously and read the cached
    ''' <c>IsHalted</c> flag — they do not block on the ticker. Halts persist a
    ''' <see cref="RiskEventEntity"/> row so a restart can audit the kill-switch event.
    '''
    ''' Day boundary (ARCH-21): trading day resets at 17:00 US Central via
    ''' <see cref="TradingDayClock"/>, matching TopStep's daily-loss accounting.
    ''' On the first tick after rollover any active halt is auto-released and a
    ''' "DayRollover" risk event is persisted (LF-7).
    '''
    ''' FEAT-73 (combine mode, <c>CombineSettings.Enabled</c>): daily P&amp;L is realised
    ''' net of fees, verdicts come from <see cref="CombineRuleEvaluator"/> — soft halts
    ''' block new entries only; hard verdicts (hard loss line, profit lock) force-flatten
    ''' via <see cref="IPositionFlattener"/> and raise <c>ForceFlattened</c>. LF-11:
    ''' <see cref="CanEnterNewTrade"/> re-evaluates synchronously when its cached state is
    ''' older than 1 s while a slot is open. With combine mode off, behaviour is exactly
    ''' FEAT-71's (RiskSettings daily loss, entry-block only, no flatten).
    ''' </summary>
    Public Class DailyLossGuardService
        Implements IDailyLossGuard, IHostedService, IDisposable

        Private Shared ReadOnly FastCadence As TimeSpan = TimeSpan.FromSeconds(5)
        Private Shared ReadOnly SlowCadence As TimeSpan = TimeSpan.FromSeconds(30)
        Private Shared ReadOnly InitialDelay As TimeSpan = TimeSpan.FromSeconds(3)
        ''' <summary>LF-11: max age of a cached evaluation the entry gate may answer from while a slot is open.</summary>
        Private Shared ReadOnly StaleEvaluationTolerance As TimeSpan = TimeSpan.FromSeconds(1)

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _riskSettings As RiskSettings
        Private ReadOnly _combineSettings As CombineSettings
        Private ReadOnly _flattener As IPositionFlattener
        Private ReadOnly _logger As ILogger(Of DailyLossGuardService)
        Private ReadOnly _sources As New ConcurrentDictionary(Of IOpenSlotPnlSource, Byte)()
        Private ReadOnly _stateLock As New Object()
        Private _state As DailyLossGuardState
        Private _timer As Timer
        Private _ticking As Integer
        Private _currentCadence As TimeSpan = SlowCadence
        Private _disposed As Boolean
        Private _lastTradingDayKey As String
        Private _lastEvaluationUtc As DateTimeOffset = DateTimeOffset.MinValue

        ''' <summary>Test seam: injectable clock; production uses <see cref="DateTimeOffset.UtcNow"/>.</summary>
        Friend Property UtcNowProvider As Func(Of DateTimeOffset) = Function() DateTimeOffset.UtcNow

        Public Event Halted As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.Halted
        Public Event Released As EventHandler Implements IDailyLossGuard.Released
        Public Event ForceFlattened As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.ForceFlattened

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       riskOptions As IOptions(Of RiskSettings),
                       combineOptions As IOptions(Of CombineSettings),
                       flattener As IPositionFlattener,
                       logger As ILogger(Of DailyLossGuardService))
            _scopeFactory = scopeFactory
            _riskSettings = riskOptions.Value
            _combineSettings = If(combineOptions?.Value, New CombineSettings())
            _flattener = flattener
            _logger = logger
            _state = New DailyLossGuardState With {
                .IsHalted = False,
                .Reason = RiskHaltReason.None,
                .LimitDollars = ActiveLimitDollars(),
                .CombineEnabled = _combineSettings.Enabled
            }
        End Sub

        ' ─── Hosted lifetime ────────────────────────────────────────────────────

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            _logger?.LogInformation(
                "DailyLossGuardService starting (limit ${Limit:F2}, combine={Combine}, slow={Slow}s, fast={Fast}s)",
                ActiveLimitDollars(), _combineSettings.Enabled,
                SlowCadence.TotalSeconds, FastCadence.TotalSeconds)
            If _combineSettings.Enabled Then
                _logger?.LogInformation(
                    "Combine guard active ({Tier}): soft ${Soft:F2}, hard ${Hard:F2}, lock trigger ${Trigger:F2} / floor ${Floor:F2}, maxTrades={MaxTrades}, maxLosers={MaxLosers}, feesInPnl={Fees}",
                    _combineSettings.Tier, _combineSettings.DailyLossSoftDollars,
                    _combineSettings.DailyLossHardDollars, _combineSettings.ProfitLockTriggerDollars,
                    _combineSettings.ProfitLockFloorDollars, _combineSettings.MaxTradesPerDay,
                    _combineSettings.MaxConsecutiveLosers, _combineSettings.IncludeFeesInDailyPnl)
            End If
            _timer = New Timer(AddressOf TickCallback, Nothing, InitialDelay, _currentCadence)
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _timer?.Change(Timeout.Infinite, 0)
            Return Task.CompletedTask
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _timer?.Dispose()
        End Sub

        ' ─── Public API ─────────────────────────────────────────────────────────

        Public Function GetState() As DailyLossGuardState Implements IDailyLossGuard.GetState
            SyncLock _stateLock
                Return Clone(_state)
            End SyncLock
        End Function

        Public Function CanEnterNewTrade() As Boolean Implements IDailyLossGuard.CanEnterNewTrade
            ' LF-11 (combine mode only): never answer from an evaluation older than 1 s
            ' while a slot is open — a stale cache can wave a trade through after the
            ' book has already breached a line. Task.Run keeps the inner awaits off the
            ' caller's SynchronizationContext so a UI-thread caller cannot deadlock.
            If _combineSettings.Enabled Then
                Dim stale As Boolean
                SyncLock _stateLock
                    stale = (UtcNowProvider.Invoke() - _lastEvaluationUtc) > StaleEvaluationTolerance
                End SyncLock
                If stale AndAlso HasOpenSlots() Then
                    Try
                        Task.Run(Function() EvaluateAsync()).GetAwaiter().GetResult()
                    Catch ex As Exception
                        _logger?.LogWarning(ex, "DailyLossGuard LF-11 synchronous re-evaluation failed — answering from cache")
                    End Try
                End If
            End If
            SyncLock _stateLock
                Return Not (_state.IsHalted OrElse _state.SoftHalted)
            End SyncLock
        End Function

        Public Async Function EvaluateAsync() As Task(Of DailyLossGuardState) _
            Implements IDailyLossGuard.EvaluateAsync
            Try
                Dim nowUtc As DateTimeOffset = UtcNowProvider.Invoke()
                Await HandleDayRolloverAsync(TradingDayClock.TradingDayKey(nowUtc))
                Dim dayStartUtc As DateTimeOffset = TradingDayClock.TradingDayStartUtc(nowUtc)

                Dim snapshot As DailyLossGuardState
                If _combineSettings.Enabled Then
                    snapshot = Await EvaluateCombineAsync(dayStartUtc)
                Else
                    snapshot = Await EvaluateLegacyAsync(dayStartUtc)
                End If

                SyncLock _stateLock
                    _lastEvaluationUtc = UtcNowProvider.Invoke()
                End SyncLock
                Return snapshot
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyLossGuard EvaluateAsync failed")
                Return GetState()
            End Try
        End Function

        Public Async Function ResetAsync(reason As String) As Task Implements IDailyLossGuard.ResetAsync
            Dim wasHalted As Boolean
            Dim snapshotForLog As DailyLossGuardState
            SyncLock _stateLock
                wasHalted = _state.IsHalted
                snapshotForLog = _state
                _state = New DailyLossGuardState With {
                    .IsHalted = False,
                    .Reason = RiskHaltReason.None,
                    .LimitDollars = ActiveLimitDollars(),
                    .CombineEnabled = _combineSettings.Enabled,
                    .RealisedDailyPnl = snapshotForLog.RealisedDailyPnl,
                    .UnrealisedDailyPnl = snapshotForLog.UnrealisedDailyPnl,
                    .CombinedDailyPnl = snapshotForLog.CombinedDailyPnl,
                    .HaltedAtUtc = Nothing,
                    .HaltMessage = String.Empty
                }
            End SyncLock

            If wasHalted Then
                Await PersistRiskEventAsync(snapshotForLog,
                                            "DailyLossReset",
                                            If(String.IsNullOrWhiteSpace(reason), "Manual reset", reason))
                _logger?.LogInformation("DailyLossGuard RESET — reason={Reason}", reason)
                SafeRaiseReleased()
            End If
        End Function

        Public Sub RegisterOpenSlotPnlSource(source As IOpenSlotPnlSource) _
            Implements IDailyLossGuard.RegisterOpenSlotPnlSource
            If source Is Nothing Then Return
            _sources.TryAdd(source, 0)
        End Sub

        Public Sub UnregisterOpenSlotPnlSource(source As IOpenSlotPnlSource) _
            Implements IDailyLossGuard.UnregisterOpenSlotPnlSource
            If source Is Nothing Then Return
            Dim ignored As Byte
            _sources.TryRemove(source, ignored)
        End Sub

        ' ─── Evaluation paths ───────────────────────────────────────────────────

        ''' <summary>FEAT-71 behaviour, unchanged: single loss line, entry-block only, no flatten.</summary>
        Private Async Function EvaluateLegacyAsync(dayStartUtc As DateTimeOffset) As Task(Of DailyLossGuardState)
            Dim realised As Decimal = Await LoadRealisedPnlAsync(dayStartUtc)
            Dim unrealised As Decimal = SumUnrealisedAggregate()
            Dim combined As Decimal = realised + unrealised
            Dim limit As Decimal = _riskSettings.DailyLossLimitDollars
            Dim shouldHalt As Boolean = combined <= limit

            Dim previous As DailyLossGuardState
            Dim updated As DailyLossGuardState
            Dim transitionedToHalt As Boolean = False
            SyncLock _stateLock
                previous = _state
                updated = New DailyLossGuardState With {
                    .RealisedDailyPnl = realised,
                    .UnrealisedDailyPnl = unrealised,
                    .CombinedDailyPnl = combined,
                    .LimitDollars = limit
                }
                If shouldHalt Then
                    updated.IsHalted = True
                    updated.Reason = RiskHaltReason.DailyLossLimit
                    updated.HaltedAtUtc = If(previous.IsHalted AndAlso previous.HaltedAtUtc.HasValue,
                                             previous.HaltedAtUtc,
                                             CType(DateTimeOffset.UtcNow, DateTimeOffset?))
                    updated.HaltMessage = $"Daily loss limit reached (combined PnL ${combined:F2}, limit ${limit:F2}). New entries disabled until reset."
                    transitionedToHalt = Not previous.IsHalted
                Else
                    updated.IsHalted = previous.IsHalted
                    updated.Reason = previous.Reason
                    updated.HaltedAtUtc = previous.HaltedAtUtc
                    updated.HaltMessage = previous.HaltMessage
                End If
                _state = updated
            End SyncLock

            AdjustCadence()

            If transitionedToHalt Then
                Await PersistRiskEventAsync(updated, "DailyLossLimit", "Daily loss limit reached")
                _logger?.LogWarning(
                    "DailyLossGuard HALT — combined PnL ${Combined:F2} <= limit ${Limit:F2} (realised={Realised:F2}, unrealised={Unrealised:F2})",
                    combined, limit, realised, unrealised)
                SafeRaiseHalted(updated)
            End If

            Return Clone(updated)
        End Function

        ''' <summary>
        ''' FEAT-73 F4: combine-mode evaluation. Verdicts come from the pure
        ''' <see cref="CombineRuleEvaluator"/>; hard verdicts flatten exactly once per
        ''' transition (the <c>transitionedToHalt</c> branch), soft verdicts block new
        ''' entries only. Trade-count / consecutive-loser soft halts are sticky for the
        ''' trading day; the loss-line soft halt clears when combined P&amp;L recovers.
        ''' </summary>
        Private Async Function EvaluateCombineAsync(dayStartUtc As DateTimeOffset) As Task(Of DailyLossGuardState)
            Dim stats As DailyCloseStats = Await LoadDailyCloseStatsAsync(dayStartUtc)
            Dim realised As Decimal = If(_combineSettings.IncludeFeesInDailyPnl,
                                         stats.NetPnlAfterFees, stats.GrossPnl)
            Dim unrealised As Decimal = SumUnrealisedAggregate()
            Dim combined As Decimal = realised + unrealised
            Dim anyOpen As Boolean = HasOpenSlots()

            Dim priorArmed As Boolean
            Dim priorHighWater As Decimal
            SyncLock _stateLock
                priorArmed = _state.ProfitLockArmed
                priorHighWater = _state.ProfitLockHighWater
            End SyncLock

            Dim verdict As CombineVerdict = CombineRuleEvaluator.Evaluate(
                _combineSettings, realised, unrealised,
                stats.TradeCount, stats.ConsecutiveLosers,
                priorArmed, priorHighWater, anyOpen)

            Dim previous As DailyLossGuardState
            Dim updated As DailyLossGuardState
            Dim transitionedToHalt As Boolean = False
            SyncLock _stateLock
                previous = _state
                updated = New DailyLossGuardState With {
                    .CombineEnabled = True,
                    .RealisedDailyPnl = realised,
                    .UnrealisedDailyPnl = unrealised,
                    .CombinedDailyPnl = combined,
                    .LimitDollars = _combineSettings.DailyLossHardDollars,
                    .TradesToday = stats.TradeCount,
                    .ConsecutiveLosers = stats.ConsecutiveLosers,
                    .ProfitLockArmed = verdict.ProfitLockArmed,
                    .ProfitLockHighWater = verdict.ProfitLockHighWater
                }

                Dim hardVerdict As Boolean =
                    verdict.Kind = CombineVerdictKind.HardHaltFlatten OrElse
                    verdict.Kind = CombineVerdictKind.ProfitLockFlatten

                If hardVerdict OrElse previous.IsHalted Then
                    ' Hard halts persist for the trading day (until reset/rollover), even
                    ' if the flattened book pulls combined P&L back inside the lines.
                    updated.IsHalted = True
                    updated.Reason = If(previous.IsHalted, previous.Reason, verdict.Reason)
                    updated.HaltMessage = If(previous.IsHalted, previous.HaltMessage, verdict.Message)
                    updated.HaltedAtUtc = If(previous.IsHalted AndAlso previous.HaltedAtUtc.HasValue,
                                             previous.HaltedAtUtc,
                                             CType(DateTimeOffset.UtcNow, DateTimeOffset?))
                    transitionedToHalt = hardVerdict AndAlso Not previous.IsHalted
                Else
                    ' Trade-count and loser-count soft halts persist for the day even if a
                    ' late winner resets the derived counters; the loss-line soft halt
                    ' clears automatically when combined P&L recovers above the soft line.
                    Dim stickySoft As Boolean =
                        previous.SoftHalted AndAlso
                        (previous.Reason = RiskHaltReason.MaxTradesPerDay OrElse
                         previous.Reason = RiskHaltReason.ConsecutiveLosses)
                    If verdict.Kind = CombineVerdictKind.SoftHalt Then
                        updated.SoftHalted = True
                        updated.Reason = verdict.Reason
                        updated.HaltMessage = verdict.Message
                    ElseIf stickySoft Then
                        updated.SoftHalted = True
                        updated.Reason = previous.Reason
                        updated.HaltMessage = previous.HaltMessage
                    End If
                End If
                _state = updated
            End SyncLock

            AdjustCadence()

            If transitionedToHalt Then
                Dim eventType As String = If(verdict.Kind = CombineVerdictKind.ProfitLockFlatten,
                                             "CombineProfitLock", "CombineHardLoss")
                _logger?.LogWarning(
                    "DailyLossGuard COMBINE HALT ({EventType}) — {Message} (realised={Realised:F2}, unrealised={Unrealised:F2}, trades={Trades}, losers={Losers})",
                    eventType, verdict.Message, realised, unrealised, stats.TradeCount, stats.ConsecutiveLosers)

                ' Flatten even when the book looks flat: pre-staged stop-entry orders
                ' must be swept so a halt cannot be re-entered by a resting order.
                Try
                    Dim flattenResult = Await _flattener.FlattenAllAsync(ResolveAccountId())
                    If Not flattenResult.Complete Then
                        _logger?.LogError(
                            "DailyLossGuard combine flatten INCOMPLETE — flattened {Flattened}/{Attempted}, failed=[{Failed}], ordersCancelled={OrdersCancelled}. Reconciliation workers are the backstop.",
                            flattenResult.FlattenedContracts, flattenResult.AttemptedContracts,
                            String.Join(",", flattenResult.FailedContractIds), flattenResult.OrdersCancelled)
                    End If
                Catch ex As Exception
                    _logger?.LogError(ex, "DailyLossGuard combine flatten threw — reconciliation workers are the backstop")
                End Try

                Await PersistRiskEventAsync(updated, eventType, verdict.Message,
                                            If(verdict.Kind = CombineVerdictKind.ProfitLockFlatten,
                                               _combineSettings.ProfitLockFloorDollars,
                                               _combineSettings.DailyLossHardDollars))
                SafeRaiseHalted(updated)
                SafeRaiseForceFlattened(updated)
            ElseIf updated.SoftHalted AndAlso Not previous.SoftHalted AndAlso Not updated.IsHalted Then
                _logger?.LogWarning(
                    "DailyLossGuard COMBINE SOFT HALT ({Reason}) — {Message}",
                    updated.Reason, updated.HaltMessage)
            End If

            Return Clone(updated)
        End Function

        ' ─── Internals ──────────────────────────────────────────────────────────

        Private Async Sub TickCallback(state As Object)
            If Interlocked.Exchange(_ticking, 1) = 1 Then Return
            Try
                Await EvaluateAsync()
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyLossGuard tick failed")
            Finally
                Interlocked.Exchange(_ticking, 0)
            End Try
        End Sub

        ''' <summary>Hard line in combine mode; RiskSettings daily loss otherwise.</summary>
        Private Function ActiveLimitDollars() As Decimal
            Return If(_combineSettings.Enabled,
                      _combineSettings.DailyLossHardDollars,
                      _riskSettings.DailyLossLimitDollars)
        End Function

        Private Function SumUnrealisedAggregate() As Decimal
            Dim total As Decimal = 0D
            For Each src In _sources.Keys
                Try
                    total += src.GetUnrealisedAggregate()
                Catch ex As Exception
                    _logger?.LogDebug(ex, "Open-slot PnL source threw — skipping")
                End Try
            Next
            Return total
        End Function

        Private Function HasOpenSlots() As Boolean
            For Each src In _sources.Keys
                Try
                    If src.HasOpenSlots() Then Return True
                Catch
                End Try
            Next
            Return False
        End Function

        Private Sub AdjustCadence()
            Dim desired As TimeSpan = If(HasOpenSlots(), FastCadence, SlowCadence)
            If desired = _currentCadence Then Return
            _currentCadence = desired
            Try
                _timer?.Change(desired, desired)
            Catch
            End Try
        End Sub

        Private Async Function LoadRealisedPnlAsync(sinceUtc As DateTimeOffset) As Task(Of Decimal)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Return Await repo.SumRealisedPnlSinceAsync(sinceUtc)
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyLossGuard realised-PnL load failed — assuming 0")
                Return 0D
            End Try
        End Function

        Private Async Function LoadDailyCloseStatsAsync(sinceUtc As DateTimeOffset) As Task(Of DailyCloseStats)
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                    Return Await repo.GetDailyCloseStatsAsync(sinceUtc)
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyLossGuard daily close-stats load failed — assuming empty day")
                Return New DailyCloseStats()
            End Try
        End Function

        ''' <summary>Active account for the force-flatten sweep; 0 when no account is selected.</summary>
        Private Function ResolveAccountId() As Long
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim session = scope.ServiceProvider.GetService(Of ITradingSessionContext)()
                    Return If(session?.SelectedAccount?.Id, 0L)
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyLossGuard could not resolve active account for flatten")
                Return 0L
            End Try
        End Function

        Private Async Function PersistRiskEventAsync(snapshot As DailyLossGuardState,
                                                      eventType As String,
                                                      detail As String,
                                                      Optional ruleValue As Decimal? = Nothing) As Task
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                    db.RiskEvents.Add(New RiskEventEntity With {
                        .OccurredAt = DateTimeOffset.UtcNow,
                        .EventType = eventType,
                        .DailyPnLAtEvent = snapshot.RealisedDailyPnl,
                        .DrawdownAtEvent = snapshot.CombinedDailyPnl,
                        .RuleValue = If(ruleValue, _riskSettings.DailyLossLimitDollars),
                        .DetailsJson = detail,
                        .Acknowledged = False
                    })
                    Await db.SaveChangesAsync()
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "DailyLossGuard RiskEvent persistence failed (event={Event})", eventType)
            End Try
        End Function

        Private Sub SafeRaiseHalted(snapshot As DailyLossGuardState)
            Try
                RaiseEvent Halted(Me, Clone(snapshot))
            Catch ex As Exception
                _logger?.LogDebug(ex, "DailyLossGuard Halted handler threw")
            End Try
        End Sub

        Private Sub SafeRaiseReleased()
            Try
                RaiseEvent Released(Me, EventArgs.Empty)
            Catch ex As Exception
                _logger?.LogDebug(ex, "DailyLossGuard Released handler threw")
            End Try
        End Sub

        Private Sub SafeRaiseForceFlattened(snapshot As DailyLossGuardState)
            Try
                RaiseEvent ForceFlattened(Me, Clone(snapshot))
            Catch ex As Exception
                _logger?.LogDebug(ex, "DailyLossGuard ForceFlattened handler threw")
            End Try
        End Sub

        Private Shared Function Clone(s As DailyLossGuardState) As DailyLossGuardState
            Return New DailyLossGuardState With {
                .IsHalted = s.IsHalted,
                .Reason = s.Reason,
                .RealisedDailyPnl = s.RealisedDailyPnl,
                .UnrealisedDailyPnl = s.UnrealisedDailyPnl,
                .CombinedDailyPnl = s.CombinedDailyPnl,
                .LimitDollars = s.LimitDollars,
                .HaltedAtUtc = s.HaltedAtUtc,
                .HaltMessage = s.HaltMessage,
                .SoftHalted = s.SoftHalted,
                .ProfitLockArmed = s.ProfitLockArmed,
                .ProfitLockHighWater = s.ProfitLockHighWater,
                .TradesToday = s.TradesToday,
                .ConsecutiveLosers = s.ConsecutiveLosers,
                .CombineEnabled = s.CombineEnabled
            }
        End Function

        ''' <summary>
        ''' ARCH-21 F3 (LF-7): when the TopStep trading day rolls over (17:00 CT),
        ''' auto-release any active halt, persist a "DayRollover" risk event, and let
        ''' the caller's evaluation continue against the new day window. The first
        ''' observation after startup only records the key — no release.
        ''' FEAT-73: combine mode also resets soft halts, profit-lock state and counters.
        ''' </summary>
        Private Async Function HandleDayRolloverAsync(dayKey As String) As Task
            Dim wasBlocked As Boolean = False
            Dim snapshotForEvent As DailyLossGuardState = Nothing
            SyncLock _stateLock
                If String.Equals(_lastTradingDayKey, dayKey, StringComparison.Ordinal) Then Return
                Dim isFirstObservation As Boolean = _lastTradingDayKey Is Nothing
                _lastTradingDayKey = dayKey
                If isFirstObservation Then Return
                ' Legacy mode never sets SoftHalted, so wasBlocked == IsHalted there —
                ' the pre-FEAT-73 release semantics are unchanged.
                wasBlocked = _state.IsHalted OrElse _state.SoftHalted
                snapshotForEvent = _state
                If wasBlocked OrElse _combineSettings.Enabled Then
                    _state = New DailyLossGuardState With {
                        .IsHalted = False,
                        .Reason = RiskHaltReason.None,
                        .LimitDollars = ActiveLimitDollars(),
                        .CombineEnabled = _combineSettings.Enabled,
                        .HaltedAtUtc = Nothing,
                        .HaltMessage = String.Empty
                    }
                End If
            End SyncLock

            If wasBlocked Then
                Await PersistRiskEventAsync(snapshotForEvent,
                                            "DayRollover",
                                            $"Trading day rolled over to {dayKey}; halt auto-released")
                _logger?.LogInformation("DailyLossGuard day rollover to {DayKey} — halt auto-released", dayKey)
                SafeRaiseReleased()
            End If
        End Function

    End Class

End Namespace
