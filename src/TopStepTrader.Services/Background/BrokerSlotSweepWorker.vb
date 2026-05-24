Imports System.Threading
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Background

    ''' <summary>
    ''' BUG-90 F1: fourth independent close-detection channel.
    '''
    ''' Periodically (every 60 s) queries the broker for the authoritative position state
    ''' of every currently-occupied UI slot. Any slot whose backing instrument shows flat
    ''' at the broker is released via <see cref="IOpenSlotReleaseSink.ForceReleaseAsync"/>
    ''' with reason <c>"Closed by Broker (sweep)"</c>, independent of:
    '''   • the SignalR <c>GatewayUserPosition</c> hub event (BUG-79),
    '''   • the per-tick <c>MissCount</c> escalation,
    '''   • the <c>SnapshotStalenessGuard</c> on snapshot silence.
    '''
    ''' This is the authoritative backstop for the UAT-2026-05-18 incident where M6E/MES
    ''' stayed occupied for 27+ minutes after the broker flattened them. The first three
    ''' channels can each fail in scenarios captured by hypotheses H1–H4 of BUG-90; the
    ''' sweep cannot be fooled by a stale REST row because the absence of a non-zero NetPos
    ''' is itself the close signal.
    '''
    ''' Sibling to <see cref="TradeReconciliationWorker"/> (BUG-86 F2). They share the
    ''' broker query primitive (<see cref="IOrderService.GetLivePositionSnapshotAsync"/>)
    ''' but operate on different state — this worker on live UI slots, that one on
    ''' <c>LiveTradeRecord</c> rows. They are intentionally not merged.
    '''
    ''' Cadence is 60 s: with up to 3 occupied slots that is 3 calls/min, well inside the
    ''' TopStepX 100/min budget shared with the existing 15 s tick.
    ''' </summary>
    Public Class BrokerSlotSweepWorker
        Implements IHostedService, IDisposable

        Friend Shared ReadOnly InitialDelay As TimeSpan = TimeSpan.FromSeconds(30)
        Friend Shared ReadOnly Interval As TimeSpan = TimeSpan.FromSeconds(60)

        Private ReadOnly _registry As OpenSlotReleaseSinkRegistry
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of BrokerSlotSweepWorker)
        Private _timer As System.Threading.Timer
        Private _running As Integer = 0
        Private _disposed As Boolean = False

        Public Sub New(registry As OpenSlotReleaseSinkRegistry,
                       orderService As IOrderService,
                       session As ITradingSessionContext,
                       logger As ILogger(Of BrokerSlotSweepWorker))
            _registry = registry
            _orderService = orderService
            _session = session
            _logger = logger
        End Sub

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            _logger.LogInformation("BrokerSlotSweepWorker started (interval: {Interval})", Interval)
            _timer = New System.Threading.Timer(AddressOf TimerCallback, Nothing, InitialDelay, Interval)
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _logger.LogInformation("BrokerSlotSweepWorker stopping")
            _timer?.Change(Timeout.Infinite, 0)
            Return Task.CompletedTask
        End Function

        Private Async Sub TimerCallback(state As Object)
            Try
                Await SweepOnceAsync()
            Catch ex As Exception
                _logger.LogWarning(ex, "BrokerSlotSweepWorker: sweep pass crashed")
            End Try
        End Sub

        ''' <summary>
        ''' Exposed for the BUG-90 F6 H1 test. Runs one sweep pass synchronously; the
        ''' re-entrancy guard mirrors the timer-driven path so a long-running broker call
        ''' cannot cause overlapping passes.
        ''' </summary>
        Public Async Function SweepOnceAsync() As Task
            If Interlocked.CompareExchange(_running, 1, 0) <> 0 Then Return
            Try
                Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
                If accountId = 0 Then Return

                For Each sink In _registry.Snapshot()
                    Dim occupied As IReadOnlyList(Of PositionSlot)
                    Try
                        occupied = sink.OccupiedSlots
                    Catch ex As Exception
                        _logger.LogWarning(ex, "BrokerSlotSweepWorker: sink OccupiedSlots access failed")
                        Continue For
                    End Try
                    If occupied Is Nothing Then Continue For

                    For Each slot In occupied
                        If slot Is Nothing OrElse Not slot.IsOpen OrElse String.IsNullOrEmpty(slot.Instrument) Then Continue For

                        Dim snapshot As LivePositionSnapshot = Nothing
                        Try
                            snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                                accountId, slot.Instrument, slot.PositionId)
                        Catch ex As Exception
                            ' A thrown broker call is NOT a close signal — the per-tick MissCount
                            ' path covers transient REST failures. The sweep only acts on a
                            ' definitive "flat" reading so an outage cannot mass-release slots.
                            _logger.LogDebug(ex,
                                "BrokerSlotSweepWorker: snapshot query failed for [Slot {Idx}] {Contract} — skipping this pass",
                                slot.SlotIndex, slot.Instrument)
                            Continue For
                        End Try

                        If LivePositionSnapshotValidator.IsConfirmedOpen(snapshot) Then Continue For

                        _logger.LogWarning(
                            "BrokerSlotSweepWorker: [Slot {Idx}] {Contract} broker reports flat (snapshotNull={Null}) — releasing via sweep",
                            slot.SlotIndex, slot.Instrument, snapshot Is Nothing)
                        Try
                            Await sink.ForceReleaseAsync(slot.SlotIndex,
                                                          "Closed by Broker (sweep)",
                                                          "sweep")
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "BrokerSlotSweepWorker: ForceReleaseAsync failed for [Slot {Idx}] {Contract}",
                                slot.SlotIndex, slot.Instrument)
                        End Try
                    Next
                Next
            Finally
                Interlocked.Exchange(_running, 0)
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _timer?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
