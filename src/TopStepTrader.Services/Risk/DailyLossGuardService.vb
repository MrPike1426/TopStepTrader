Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
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
    ''' Day boundary: trading day in Europe/London time matches the user's session.
    ''' Roll happens automatically on the next tick after the local-time date crosses.
    ''' </summary>
    Public Class DailyLossGuardService
        Implements IDailyLossGuard, IHostedService, IDisposable

        Private Shared ReadOnly FastCadence As TimeSpan = TimeSpan.FromSeconds(5)
        Private Shared ReadOnly SlowCadence As TimeSpan = TimeSpan.FromSeconds(30)
        Private Shared ReadOnly InitialDelay As TimeSpan = TimeSpan.FromSeconds(3)

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _riskSettings As RiskSettings
        Private ReadOnly _logger As ILogger(Of DailyLossGuardService)
        Private ReadOnly _sources As New ConcurrentDictionary(Of IOpenSlotPnlSource, Byte)()
        Private ReadOnly _stateLock As New Object()
        Private _state As DailyLossGuardState
        Private _timer As Timer
        Private _ticking As Integer
        Private _currentCadence As TimeSpan = SlowCadence
        Private _disposed As Boolean

        Public Event Halted As EventHandler(Of DailyLossGuardState) Implements IDailyLossGuard.Halted
        Public Event Released As EventHandler Implements IDailyLossGuard.Released

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       riskOptions As IOptions(Of RiskSettings),
                       logger As ILogger(Of DailyLossGuardService))
            _scopeFactory = scopeFactory
            _riskSettings = riskOptions.Value
            _logger = logger
            _state = New DailyLossGuardState With {
                .IsHalted = False,
                .Reason = RiskHaltReason.None,
                .LimitDollars = _riskSettings.DailyLossLimitDollars
            }
        End Sub

        ' ─── Hosted lifetime ────────────────────────────────────────────────────

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            _logger?.LogInformation(
                "DailyLossGuardService starting (limit ${Limit:F2}, slow={Slow}s, fast={Fast}s)",
                _riskSettings.DailyLossLimitDollars,
                SlowCadence.TotalSeconds, FastCadence.TotalSeconds)
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
            SyncLock _stateLock
                Return Not _state.IsHalted
            End SyncLock
        End Function

        Public Async Function EvaluateAsync() As Task(Of DailyLossGuardState) _
            Implements IDailyLossGuard.EvaluateAsync
            Try
                Dim dayStartUtc As DateTimeOffset = TodayTradingDayStartUtc()
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
                Dim dayStartUtc = TodayTradingDayStartUtc()
                _state = New DailyLossGuardState With {
                    .IsHalted = False,
                    .Reason = RiskHaltReason.None,
                    .LimitDollars = _riskSettings.DailyLossLimitDollars,
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

        Private Async Function PersistRiskEventAsync(snapshot As DailyLossGuardState,
                                                      eventType As String,
                                                      detail As String) As Task
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                    db.RiskEvents.Add(New RiskEventEntity With {
                        .OccurredAt = DateTimeOffset.UtcNow,
                        .EventType = eventType,
                        .DailyPnLAtEvent = snapshot.RealisedDailyPnl,
                        .DrawdownAtEvent = snapshot.CombinedDailyPnl,
                        .RuleValue = _riskSettings.DailyLossLimitDollars,
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

        Private Shared Function Clone(s As DailyLossGuardState) As DailyLossGuardState
            Return New DailyLossGuardState With {
                .IsHalted = s.IsHalted,
                .Reason = s.Reason,
                .RealisedDailyPnl = s.RealisedDailyPnl,
                .UnrealisedDailyPnl = s.UnrealisedDailyPnl,
                .CombinedDailyPnl = s.CombinedDailyPnl,
                .LimitDollars = s.LimitDollars,
                .HaltedAtUtc = s.HaltedAtUtc,
                .HaltMessage = s.HaltMessage
            }
        End Function

        ''' <summary>
        ''' Returns the UTC instant at which the *current* trading day began for the user
        ''' (Europe/London midnight, the session boundary specified by the ticket). Falls
        ''' back to UTC midnight when the timezone DB lacks the requested zone.
        ''' </summary>
        Friend Shared Function TodayTradingDayStartUtc() As DateTimeOffset
            Dim tz As TimeZoneInfo
            Try
                tz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")
            Catch
                Try
                    tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London")
                Catch
                    tz = TimeZoneInfo.Utc
                End Try
            End Try
            Dim localNow As DateTimeOffset = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz)
            Dim localMidnight As New DateTimeOffset(localNow.Year, localNow.Month, localNow.Day,
                                                     0, 0, 0, localNow.Offset)
            Return localMidnight.ToUniversalTime()
        End Function

    End Class

End Namespace
