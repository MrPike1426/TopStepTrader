Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' BUG-90 F1: callback contract implemented by any view-model that owns a
    ''' <c>SlotManager</c>'s live slots. The <c>BrokerSlotSweepWorker</c> hosted service
    ''' queries each registered sink's occupied slots every 60 s and, when the broker
    ''' reports flat, asks the sink to release the slot — independent of the hub event,
    ''' <c>MissCount</c>, and <c>SnapshotStalenessGuard</c> channels.
    ''' </summary>
    Public Interface IOpenSlotReleaseSink

        ''' <summary>
        ''' Snapshot of currently-occupied slots. Implementations should return a fresh
        ''' list (or an immutable view) so the caller can iterate without locking.
        ''' </summary>
        ReadOnly Property OccupiedSlots As IReadOnlyList(Of PositionSlot)

        ''' <summary>
        ''' Releases the slot at <paramref name="slotIndex"/> with the supplied
        ''' <paramref name="reason"/> and structured <paramref name="trigger"/> tag.
        ''' Safe to invoke from a background thread — implementations marshal to the
        ''' UI dispatcher internally. A no-op when the slot is already closed.
        ''' </summary>
        Function ForceReleaseAsync(slotIndex As Integer,
                                    reason As String,
                                    trigger As String) As Task

    End Interface

End Namespace
