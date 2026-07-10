Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.API.Hubs
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
    '''
    ''' FEAT-74 (trailing max-drawdown / MLL): each combine tick models account equity —
    ''' latest <c>GatewayUserAccount</c> balance push + unrealised aggregate, falling back
    ''' to persisted starting balance + cumulative realised + today's P&amp;L before the
    ''' first push — and runs <see cref="CombineRuleEvaluator.EvaluateTrail"/>. The peak
    ''' equity trail is persisted per account (<c>CombineAccountState</c>) so it survives
    ''' restarts. An MLL breach flattens and halts with <see cref="RiskHaltReason.MaxDrawdown"/>;
    ''' unlike daily halts it does NOT auto-release at the 17:00-CT rollover — the combine
    ''' is failed and only a manual reset (practice-account reuse) clears it.
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

        ' ── FEAT-74 trailing max-drawdown state ─────────────────────────────────
        ''' <summary>Latest GatewayUserAccount balance per account (TopStepX balance is realised-only).</summary>
        Private ReadOnly _brokerBalances As New ConcurrentDictionary(Of Long, Decimal)()
        Private _userHub As UserHubClient
        ''' <summary>Cached persisted trail row for <see cref="_trailAccountId"/>; guarded by <c>_stateLock</c>.</summary>
        Private _trail As Data.Entities.CombineAccountStateEntity
        Private _trailAccountId As Long
        ''' <summary>Last modelled equity — sampled into the peak at rollover when TrailMode="EndOfDay".</summary>
        Private _lastModelledEquity As Decimal?

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
            ' FEAT-74: subscribe to the broker account push for the equity model.
            ' GetService (not Required) — tests and non-TopStepX hosts run without a hub.
            If _combineSettings.Enabled Then
                Try
                    Using scope = _scopeFactory.CreateScope()
                        _userHub = scope.ServiceProvider.GetService(Of UserHubClient)()
                    End Using
                    If _userHub IsNot Nothing Then
                        AddHandler _userHub.AccountUpdated, AddressOf OnAccountUpdated
                    End If
                Catch ex As Exception
                    _logger?.LogWarning(ex, "DailyLossGuard could not attach to UserHubClient — MLL equity falls back to persisted model")
                End Try
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
            If _userHub IsNot Nothing Then
                RemoveHandler _userHub.AccountUpdated, AddressOf OnAccountUpdated
                _userHub = Nothing
            End If
            _timer?.Dispose()
        End Sub

        ''' <summary>FEAT-74: cache the latest broker balance per account for the equity model.</summary>
        Private Sub OnAccountUpdated(sender As Object, e As PXAccountUpdateEventArgs)
            If e?.AccountData Is Nothing Then Return
            _brokerBalances(e.AccountData.AccountId) = CDec(e.AccountData.Balance)
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
                ' FEAT-74: clearing a MaxDrawdown halt re-baselines the persisted trail
                ' (practice-account reuse — the combine itself is failed at TopStep).
                If snapshotForLog.Reason = RiskHaltReason.MaxDrawdown Then
                    Await ReBaselineTrailAsync()
                End If
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

            ' FEAT-74: trailing MLL runs alongside the daily rules; a breach outranks
            ' every daily verdict. Nothing when inactive (no account / no repository).
            Dim trailResult = Await EvaluateTrailAsync(realised, unrealised)
            Dim trail As TrailVerdict = trailResult.Verdict

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

            If trail IsNot Nothing AndAlso trail.Breached Then
                verdict.Kind = CombineVerdictKind.MaxDrawdownFlatten
                verdict.Reason = RiskHaltReason.MaxDrawdown
                verdict.Message = trail.Message
            End If

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
                If trail IsNot Nothing Then
                    updated.MllTrailActive = True
                    updated.EquityNow = trailResult.Equity
                    updated.PeakEquity = trail.PeakEquity
                    updated.MllFloor = trail.MllFloor
                End If

                Dim hardVerdict As Boolean =
                    verdict.Kind = CombineVerdictKind.HardHaltFlatten OrElse
                    verdict.Kind = CombineVerdictKind.ProfitLockFlatten OrElse
                    verdict.Kind = CombineVerdictKind.MaxDrawdownFlatten

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
                Dim eventType As String
                Select Case verdict.Kind
                    Case CombineVerdictKind.ProfitLockFlatten
                        eventType = "CombineProfitLock"
                    Case CombineVerdictKind.MaxDrawdownFlatten
                        eventType = "CombineMaxDrawdown"
                    Case Else
                        eventType = "CombineHardLoss"
                End Select
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

                Dim ruleValue As Decimal
                Select Case verdict.Kind
                    Case CombineVerdictKind.ProfitLockFlatten
                        ruleValue = _combineSettings.ProfitLockFloorDollars
                    Case CombineVerdictKind.MaxDrawdownFlatten
                        ruleValue = trail.MllFloor
                    Case Else
                        ruleValue = _combineSettings.DailyLossHardDollars
                End Select
                Await PersistRiskEventAsync(updated, eventType, verdict.Message, ruleValue)
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

        ' ── FEAT-74 trailing max-drawdown internals ─────────────────────────────

        ''' <summary>
        ''' F2/F3: models account equity and runs the trailing-MLL check.
        ''' Primary equity: latest broker balance push + unrealised aggregate (TopStepX
        ''' pushes OpenPnL=0 for futures, so the balance is realised-only). Fallback
        ''' before the first push: persisted StartingBalance + CumulativeRealisedPnl +
        ''' today's realised + unrealised. Persists peak/floor only when they change.
        ''' Returns (Nothing, 0) when the trail is inactive (no account selected or
        ''' repository unavailable).
        ''' </summary>
        Private Async Function EvaluateTrailAsync(realised As Decimal, unrealised As Decimal) _
            As Task(Of (Verdict As TrailVerdict, Equity As Decimal))
            Dim accountId As Long = ResolveAccountId()
            If accountId = 0 Then Return (Nothing, 0D)

            Dim trailState = Await GetOrLoadTrailStateAsync(accountId)
            If trailState Is Nothing Then Return (Nothing, 0D)

            Dim balance As Decimal
            Dim equity As Decimal
            If _brokerBalances.TryGetValue(accountId, balance) Then
                equity = balance + unrealised
            Else
                equity = trailState.StartingBalance + trailState.CumulativeRealisedPnl + realised + unrealised
            End If

            ' EndOfDay mode defers the peak ratchet to the day-rollover sample.
            Dim ratchet As Boolean = Not String.Equals(_combineSettings.TrailMode, "EndOfDay",
                                                       StringComparison.OrdinalIgnoreCase)
            Dim verdict As TrailVerdict = CombineRuleEvaluator.EvaluateTrail(
                _combineSettings, equity, trailState.PeakEquity, ratchet)

            SyncLock _stateLock
                _lastModelledEquity = equity
            End SyncLock

            If verdict.PeakEquity <> trailState.PeakEquity OrElse verdict.MllFloor <> trailState.MllFloor Then
                trailState.PeakEquity = verdict.PeakEquity
                trailState.MllFloor = verdict.MllFloor
                Await PersistTrailStateAsync(trailState)
            End If

            Return (verdict, equity)
        End Function

        ''' <summary>F4: lazy per-account load so the trail continues where a restart left it.</summary>
        Private Async Function GetOrLoadTrailStateAsync(accountId As Long) As Task(Of Data.Entities.CombineAccountStateEntity)
            SyncLock _stateLock
                If _trail IsNot Nothing AndAlso _trailAccountId = accountId Then Return _trail
            End SyncLock
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetService(Of ICombineAccountStateRepository)()
                    If repo Is Nothing Then Return Nothing
                    Dim loaded = Await repo.GetOrCreateAsync(accountId, _combineSettings.StartingBalance)
                    SyncLock _stateLock
                        _trail = loaded
                        _trailAccountId = accountId
                    End SyncLock
                    _logger?.LogInformation(
                        "Combine trail state loaded for account {AccountId}: peak=${Peak:F2}, floor=${Floor:F2}, cumPnl=${Cum:F2}",
                        accountId, loaded.PeakEquity, loaded.MllFloor, loaded.CumulativeRealisedPnl)
                    Return loaded
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "Combine trail state load failed for account {AccountId}", accountId)
                Return Nothing
            End Try
        End Function

        Private Async Function PersistTrailStateAsync(entity As Data.Entities.CombineAccountStateEntity) As Task
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim repo = scope.ServiceProvider.GetService(Of ICombineAccountStateRepository)()
                    If repo Is Nothing Then Return
                    Await repo.UpsertAsync(entity)
                End Using
            Catch ex As Exception
                _logger?.LogWarning(ex, "Combine trail state persistence failed for account {AccountId}", entity.AccountId)
            End Try
        End Function

        ''' <summary>
        ''' F4: day-rollover bookkeeping — folds the finished day's realised P&amp;L into
        ''' the cumulative figure (equity fallback for future sessions) and, for
        ''' TrailMode="EndOfDay", samples the last modelled equity into the peak.
        ''' </summary>
        Private Async Function RollTrailStateAsync(dayKey As String, dayRealised As Decimal) As Task
            Dim entity As Data.Entities.CombineAccountStateEntity
            Dim eodEquity As Decimal?
            SyncLock _stateLock
                entity = _trail
                eodEquity = _lastModelledEquity
                _lastModelledEquity = Nothing
            End SyncLock
            If entity Is Nothing Then Return

            entity.CumulativeRealisedPnl += dayRealised
            If String.Equals(_combineSettings.TrailMode, "EndOfDay", StringComparison.OrdinalIgnoreCase) AndAlso
               eodEquity.HasValue AndAlso eodEquity.Value > entity.PeakEquity Then
                entity.PeakEquity = eodEquity.Value
            End If
            Dim floor As Decimal = entity.PeakEquity + _combineSettings.TrailingMaxDrawdownDollars
            If _combineSettings.TrailFreezeAtStartBalance Then
                floor = Math.Min(floor, entity.StartingBalance)
            End If
            entity.MllFloor = floor
            entity.TradingDayKey = dayKey
            Await PersistTrailStateAsync(entity)
        End Function

        ''' <summary>
        ''' Manual-reset path after a MaxDrawdown halt: re-baselines the persisted trail
        ''' (peak back to starting balance, cumulative P&amp;L cleared) for practice-account
        ''' reuse, and drops cached broker balances so a broker-side account reset is
        ''' picked up fresh from the next push.
        ''' </summary>
        Private Async Function ReBaselineTrailAsync() As Task
            Dim entity As Data.Entities.CombineAccountStateEntity
            SyncLock _stateLock
                entity = _trail
                _lastModelledEquity = Nothing
            End SyncLock
            _brokerBalances.Clear()
            If entity Is Nothing Then Return

            entity.PeakEquity = entity.StartingBalance
            entity.CumulativeRealisedPnl = 0D
            Dim floor As Decimal = entity.StartingBalance + _combineSettings.TrailingMaxDrawdownDollars
            If _combineSettings.TrailFreezeAtStartBalance Then
                floor = Math.Min(floor, entity.StartingBalance)
            End If
            entity.MllFloor = floor
            Await PersistTrailStateAsync(entity)
            _logger?.LogInformation(
                "Combine trail re-baselined after MaxDrawdown reset (peak=${Peak:F2}, floor=${Floor:F2})",
                entity.PeakEquity, entity.MllFloor)
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
                .CombineEnabled = s.CombineEnabled,
                .MllTrailActive = s.MllTrailActive,
                .EquityNow = s.EquityNow,
                .PeakEquity = s.PeakEquity,
                .MllFloor = s.MllFloor
            }
        End Function

        ''' <summary>
        ''' ARCH-21 F3 (LF-7): when the TopStep trading day rolls over (17:00 CT),
        ''' auto-release any active halt, persist a "DayRollover" risk event, and let
        ''' the caller's evaluation continue against the new day window. The first
        ''' observation after startup only records the key — no release.
        ''' FEAT-73: combine mode also resets soft halts, profit-lock state and counters.
        ''' FEAT-74: a MaxDrawdown (MLL) halt survives the rollover — the combine is
        ''' failed; only a manual reset clears it. Trail bookkeeping (cumulative realised
        ''' fold + EndOfDay peak sample) also happens here.
        ''' </summary>
        Private Async Function HandleDayRolloverAsync(dayKey As String) As Task
            Dim wasBlocked As Boolean = False
            Dim mllHaltPersists As Boolean = False
            Dim snapshotForEvent As DailyLossGuardState = Nothing
            SyncLock _stateLock
                If String.Equals(_lastTradingDayKey, dayKey, StringComparison.Ordinal) Then Return
                Dim isFirstObservation As Boolean = _lastTradingDayKey Is Nothing
                _lastTradingDayKey = dayKey
                If isFirstObservation Then Return
                mllHaltPersists = _state.IsHalted AndAlso _state.Reason = RiskHaltReason.MaxDrawdown
                ' Legacy mode never sets SoftHalted, so wasBlocked == IsHalted there —
                ' the pre-FEAT-73 release semantics are unchanged.
                wasBlocked = (_state.IsHalted OrElse _state.SoftHalted) AndAlso Not mllHaltPersists
                snapshotForEvent = _state
                If wasBlocked OrElse mllHaltPersists OrElse _combineSettings.Enabled Then
                    Dim fresh As New DailyLossGuardState With {
                        .IsHalted = False,
                        .Reason = RiskHaltReason.None,
                        .LimitDollars = ActiveLimitDollars(),
                        .CombineEnabled = _combineSettings.Enabled,
                        .HaltedAtUtc = Nothing,
                        .HaltMessage = String.Empty
                    }
                    If mllHaltPersists Then
                        fresh.IsHalted = True
                        fresh.Reason = snapshotForEvent.Reason
                        fresh.HaltMessage = snapshotForEvent.HaltMessage
                        fresh.HaltedAtUtc = snapshotForEvent.HaltedAtUtc
                        fresh.MllTrailActive = snapshotForEvent.MllTrailActive
                        fresh.EquityNow = snapshotForEvent.EquityNow
                        fresh.PeakEquity = snapshotForEvent.PeakEquity
                        fresh.MllFloor = snapshotForEvent.MllFloor
                    End If
                    _state = fresh
                End If
            End SyncLock

            ' FEAT-74 F4: trail bookkeeping for the finished day (no-op until loaded).
            If _combineSettings.Enabled Then
                Await RollTrailStateAsync(dayKey, If(snapshotForEvent?.RealisedDailyPnl, 0D))
            End If

            If wasBlocked Then
                Await PersistRiskEventAsync(snapshotForEvent,
                                            "DayRollover",
                                            $"Trading day rolled over to {dayKey}; halt auto-released")
                _logger?.LogInformation("DailyLossGuard day rollover to {DayKey} — halt auto-released", dayKey)
                SafeRaiseReleased()
            ElseIf mllHaltPersists Then
                _logger?.LogWarning(
                    "DailyLossGuard day rollover to {DayKey} — MaxDrawdown (MLL) halt persists; manual reset required",
                    dayKey)
            End If
        End Function

    End Class

End Namespace
