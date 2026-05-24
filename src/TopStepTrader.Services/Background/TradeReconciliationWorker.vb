Imports System.Threading
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' BUG-86 F2: periodic reconciliation pass that calls
    ''' <see cref="ITradeRecordService.RecoverOpenTradesAsync"/> while the app is running.
    '''
    ''' BUG-94 F2/F3: a second phase scans the broker for open positions that have no
    ''' matching open <c>LiveTradeRecord</c> ("orphans") — manual broker entries, cross-app
    ''' races, or strategy-side persistence regressions — alarms the user, and (when
    ''' <see cref="SafetyNetSettings.AutoStopLossEnabled"/> is True) places a protective
    ''' Stop Market derived from a per-symbol safety profile.
    '''
    ''' Cadence is intentionally low (5 minutes) so the existing RateLimiter on PX REST
    ''' is unaffected. Both passes are idempotent.
    ''' </summary>
    Public Class TradeReconciliationWorker
        Implements IHostedService, IDisposable

        Private Shared ReadOnly InitialDelay As TimeSpan = TimeSpan.FromMinutes(1)
        Private Shared ReadOnly Interval As TimeSpan = TimeSpan.FromMinutes(5)

        ''' <summary>
        ''' BUG-94 F4: how long a per-position-id alarm dedup entry survives before we
        ''' re-alarm. 30 minutes — long enough to suppress chatter when the user has
        ''' seen the alarm and is acting on it, short enough that a persistent orphan
        ''' the user dismissed without acting re-alarms.
        ''' </summary>
        Friend Shared ReadOnly AlarmedTtl As TimeSpan = TimeSpan.FromMinutes(30)

        Private ReadOnly _tradeRecord As ITradeRecordService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _safetyNet As IOptions(Of SafetyNetSettings)
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of TradeReconciliationWorker)
        Private _timer As System.Threading.Timer
        Private _running As Integer = 0
        Private _disposed As Boolean = False

        ' BUG-94 F4: per-position-id "alarmed at" map. Dedups alarms inside AlarmedTtl.
        Private ReadOnly _alarmedAt As New Dictionary(Of Long, DateTimeOffset)
        Private ReadOnly _alarmLock As New Object()

        ''' <summary>
        ''' BUG-94 F2/F4: raised whenever the orphan scan detects a broker position that has
        ''' no matching open LiveTradeRecord. Consumed by the UI to surface a toast / banner.
        ''' Within <see cref="AlarmedTtl"/> the same positionId will not re-raise this event.
        ''' </summary>
        Public Event OrphanPositionDetected As EventHandler(Of OrphanPositionDetectedEventArgs)

        Public Sub New(tradeRecord As ITradeRecordService,
                       orderService As IOrderService,
                       safetyNet As IOptions(Of SafetyNetSettings),
                       session As ITradingSessionContext,
                       logger As ILogger(Of TradeReconciliationWorker))
            _tradeRecord = tradeRecord
            _orderService = orderService
            _safetyNet = safetyNet
            _session = session
            _logger = logger
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            _logger.LogInformation("TradeReconciliationWorker started (interval: {Interval})", Interval)
            AddHandler _session.AccountChanged, AddressOf OnAccountChanged
            _timer = New System.Threading.Timer(AddressOf DoWork, Nothing, InitialDelay, Interval)
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _logger.LogInformation("TradeReconciliationWorker stopping")
            RemoveHandler _session.AccountChanged, AddressOf OnAccountChanged
            _timer?.Change(Timeout.Infinite, 0)
            Return Task.CompletedTask
        End Function

        ' Fire an immediate pass when an account first becomes available so a user who
        ' opens the app after a long downtime sees stuck-open trades reconcile within
        ' seconds of picking an account, not after the next 5-minute tick.
        Private Sub OnAccountChanged(sender As Object, account As Account)
            If account Is Nothing OrElse account.Id = 0 Then Return
            _timer?.Change(TimeSpan.FromSeconds(2), Interval)
        End Sub

        Private Async Sub DoWork(state As Object)
            ' Re-entrancy guard: a slow REST call must not cause overlapping passes.
            If Interlocked.CompareExchange(_running, 1, 0) <> 0 Then Return
            Try
                Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
                If accountId = 0 Then
                    _logger.LogDebug("TradeReconciliationWorker: no account selected, skipping pass")
                    Return
                End If
                Await ReconcileOpenTradesAsync(accountId)
                Await ScanOrphansAsync(accountId, CancellationToken.None)
            Finally
                Interlocked.Exchange(_running, 0)
            End Try
        End Sub

        Private Async Function ReconcileOpenTradesAsync(accountId As Long) As Task
            Try
                Await _tradeRecord.RecoverOpenTradesAsync(accountId)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeReconciliationWorker: recovery pass failed")
            End Try
        End Function

        ''' <summary>
        ''' BUG-94 F2: test seam — drives one orphan-scan iteration synchronously. Public so
        ''' <c>TradeReconciliationOrphanScanTests</c> can run a deterministic single pass
        ''' without spinning a Timer.
        ''' </summary>
        Public Async Function ScanOrphansAsync(accountId As Long, cancel As CancellationToken) As Task
            If accountId = 0 Then Return

            Dim positions As IEnumerable(Of LivePositionSnapshot)
            Try
                positions = Await _orderService.GetOpenPositionsAsync(accountId, cancel)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeReconciliationWorker: GetOpenPositionsAsync failed — orphan scan skipped")
                Return
            End Try
            If positions Is Nothing Then Return

            Dim now = DateTimeOffset.UtcNow
            Dim graceSeconds As Integer = Math.Max(0, If(_safetyNet?.Value?.OrphanGraceSeconds, 0))
            PurgeExpiredAlarms(now)

            For Each pos In positions
                If pos Is Nothing OrElse Math.Abs(pos.NetPos) = 0 Then Continue For

                Dim age As TimeSpan = now - pos.OpenedAtUtc
                If age.TotalSeconds < graceSeconds Then
                    Continue For
                End If

                Dim match As LiveTradeRecord = Nothing
                Try
                    match = Await _tradeRecord.FindOpenByContractIdAsync(accountId, pos.ContractId)
                Catch ex As Exception
                    _logger.LogWarning(ex, "TradeReconciliationWorker: FindOpenByContractIdAsync failed for {Contract} — treating as orphan", pos.ContractId)
                End Try
                If match IsNot Nothing Then Continue For

                ' BUG-94 F4: dedup within AlarmedTtl.
                If WasAlarmedRecently(pos.PositionId, now) Then Continue For

                Dim args = BuildOrphanArgs(pos, age, now)
                Try
                    Await ApplyAutoSlIfEnabledAsync(pos, args, accountId, cancel)
                Catch ex As Exception
                    _logger.LogWarning(ex, "TradeReconciliationWorker: auto-SL path threw for positionId={Id}", pos.PositionId)
                    args.AutoSlSkippedReason = "EditFailed"
                End Try

                _logger.LogError(
                    "ORPHAN POSITION: positionId={Id} contract={Contract} side={Side} size={Size} netPrice={Price} openPnL={Pnl} age={Age}s autoSlApplied={Applied} reason={Reason}",
                    args.PositionId, args.ContractId, args.Side, args.Size,
                    args.NetPrice, args.OpenPnLUsd, CInt(age.TotalSeconds),
                    args.AutoSlApplied, args.AutoSlSkippedReason)

                RecordAlarmed(pos.PositionId, now)
                RaiseEvent OrphanPositionDetected(Me, args)
            Next
        End Function

        Private Function BuildOrphanArgs(pos As LivePositionSnapshot, age As TimeSpan, now As DateTimeOffset) As OrphanPositionDetectedEventArgs
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(pos.ContractId)
            Dim symbolDisplay = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxRootSymbol),
                                   fav.PxRootSymbol, pos.ContractId)
            Return New OrphanPositionDetectedEventArgs With {
                .PositionId = pos.PositionId,
                .ContractId = pos.ContractId,
                .Symbol = symbolDisplay,
                .Side = If(pos.NetPos > 0, "Long", "Short"),
                .Size = Math.Abs(pos.NetPos),
                .NetPrice = pos.OpenRate,
                .OpenPnLUsd = pos.UnrealizedPnlUsd,
                .Age = age,
                .DetectedAtUtc = now
            }
        End Function

        ''' <summary>
        ''' BUG-94 F3: when AutoStopLossEnabled is True, places a protective Stop Market on
        ''' the orphan position derived from the SafetyNetSettings PerSymbol map (or the
        ''' default fallback). Mutates <paramref name="args"/> in place — sets
        ''' <c>AutoSlApplied</c> or <c>AutoSlSkippedReason</c>. Never throws — internal
        ''' failures are logged and surfaced as the reason "EditFailed".
        ''' </summary>
        Private Async Function ApplyAutoSlIfEnabledAsync(pos As LivePositionSnapshot,
                                                         args As OrphanPositionDetectedEventArgs,
                                                         accountId As Long,
                                                         cancel As CancellationToken) As Task
            Dim settings = _safetyNet?.Value
            If settings Is Nothing OrElse Not settings.AutoStopLossEnabled Then
                args.AutoSlSkippedReason = "Disabled"
                Return
            End If

            Dim existing As Decimal? = Nothing
            Try
                existing = Await _orderService.TryGetBracketStopPriceAsync(accountId, pos.ContractId, cancel)
            Catch ex As Exception
                _logger.LogDebug(ex, "TradeReconciliationWorker: TryGetBracketStopPriceAsync failed for {Contract} — proceeding as if no bracket", pos.ContractId)
            End Try
            If existing.HasValue Then
                args.AutoSlSkippedReason = "ExistingBracketDetected"
                Return
            End If

            Dim fav = FavouriteContracts.TryGetBySymbolResolved(pos.ContractId)
            If fav Is Nothing Then
                args.AutoSlSkippedReason = "UnknownContract"
                Return
            End If

            Dim dollars As Decimal
            If settings.PerSymbol IsNot Nothing AndAlso settings.PerSymbol.TryGetValue(fav.PxRootSymbol, dollars) Then
                ' use PerSymbol entry
            Else
                dollars = settings.DefaultFallbackStopDollars
            End If

            Dim stopPrice As Decimal
            Try
                stopPrice = ComputeFallbackStopPrice(pos.NetPos > 0, pos.OpenRate, dollars, fav.PxTickSize, fav.PxTickValue)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeReconciliationWorker: ComputeFallbackStopPrice failed for {Contract} dollars={Dollars}", pos.ContractId, dollars)
                args.AutoSlSkippedReason = "EditFailed"
                Return
            End Try

            Dim ok As Boolean = False
            Try
                ok = Await _orderService.EditPositionSlTpAsync(pos.PositionId, stopPrice, Nothing, False, cancel)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeReconciliationWorker: EditPositionSlTpAsync threw for positionId={Id}", pos.PositionId)
            End Try

            If ok Then
                args.AutoSlApplied = True
                args.AutoSlPriceApplied = stopPrice
            Else
                args.AutoSlSkippedReason = "EditFailed"
            End If
        End Function

        ''' <summary>
        ''' BUG-94 F3: pure helper computing the protective stop price for an orphan position.
        ''' Returns a tick-snapped price that is <paramref name="dollars"/> away from
        ''' <paramref name="entry"/> in the direction adverse to the position. Used by both
        ''' the live path and the unit tests so the rounding contract is exercised once.
        ''' </summary>
        Friend Shared Function ComputeFallbackStopPrice(isBuy As Boolean,
                                                         entry As Decimal,
                                                         dollars As Decimal,
                                                         tickSize As Decimal,
                                                         tickValue As Decimal) As Decimal
            If tickSize <= 0D Then Throw New ArgumentOutOfRangeException(NameOf(tickSize))
            If tickValue <= 0D Then Throw New ArgumentOutOfRangeException(NameOf(tickValue))
            Dim distanceTicks As Integer = CInt(Math.Ceiling(CDbl(dollars / tickValue)))
            Dim distance As Decimal = distanceTicks * tickSize
            Dim raw As Decimal = If(isBuy, entry - distance, entry + distance)
            Return If(isBuy,
                Math.Floor(raw / tickSize) * tickSize,
                Math.Ceiling(raw / tickSize) * tickSize)
        End Function

        Private Function WasAlarmedRecently(positionId As Long, now As DateTimeOffset) As Boolean
            SyncLock _alarmLock
                Dim ts As DateTimeOffset
                If _alarmedAt.TryGetValue(positionId, ts) Then
                    Return (now - ts) < AlarmedTtl
                End If
                Return False
            End SyncLock
        End Function

        Private Sub RecordAlarmed(positionId As Long, now As DateTimeOffset)
            SyncLock _alarmLock
                _alarmedAt(positionId) = now
            End SyncLock
        End Sub

        Private Sub PurgeExpiredAlarms(now As DateTimeOffset)
            SyncLock _alarmLock
                Dim stale = _alarmedAt _
                    .Where(Function(kv) (now - kv.Value) >= AlarmedTtl) _
                    .Select(Function(kv) kv.Key) _
                    .ToList()
                For Each k In stale
                    _alarmedAt.Remove(k)
                Next
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _timer?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
