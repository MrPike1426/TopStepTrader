Imports System.Threading
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' BUG-86 F2: periodic reconciliation pass that calls
    ''' <see cref="ITradeRecordService.RecoverOpenTradesAsync"/> while the app is running.
    '''
    ''' Catches force-closes (daily-loss flat, drawdown-rule force-close, broker SL fill
    ''' during a SignalR hub drop, EOD flat, etc.) that the in-session per-tick MissCount
    ''' mechanism may not have escalated. Also handles the "app was off when the force-close
    ''' happened" case for any open record whose EntryTime falls inside the recovery window
    ''' once the user picks an account post-login.
    '''
    ''' Cadence is intentionally low (5 minutes) so the existing RateLimiter on PX REST
    ''' is unaffected. RecoverOpenTradesAsync is idempotent: it filters by IsOpen=true on
    ''' the read side, so a no-op pass costs one cheap SQLite query.
    ''' </summary>
    Public Class TradeReconciliationWorker
        Implements IHostedService, IDisposable

        Private Shared ReadOnly InitialDelay As TimeSpan = TimeSpan.FromMinutes(1)
        Private Shared ReadOnly Interval As TimeSpan = TimeSpan.FromMinutes(5)

        Private ReadOnly _tradeRecord As ITradeRecordService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of TradeReconciliationWorker)
        Private _timer As System.Threading.Timer
        Private _running As Integer = 0
        Private _disposed As Boolean = False

        Public Sub New(tradeRecord As ITradeRecordService,
                       session As ITradingSessionContext,
                       logger As ILogger(Of TradeReconciliationWorker))
            _tradeRecord = tradeRecord
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
            ' Schedule via the timer so we don't block the session event thread.
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
                Try
                    Await _tradeRecord.RecoverOpenTradesAsync(accountId)
                Catch ex As Exception
                    _logger.LogWarning(ex, "TradeReconciliationWorker: recovery pass failed")
                End Try
            Finally
                Interlocked.Exchange(_running, 0)
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _timer?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
