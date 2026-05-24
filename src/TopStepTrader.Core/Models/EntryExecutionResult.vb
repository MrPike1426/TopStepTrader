Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' ARCH-19: outcome of <c>IEntryExecutionService.PlaceAsync</c>. On failure the slot
    ''' has already been released via the request's <c>OnReleaseSlot</c> callback before
    ''' the result is returned; callers only need to read <see cref="Success"/> to decide
    ''' whether to fire post-entry UI updates (slot box flash, TradeOpened event).
    ''' </summary>
    Public Class EntryExecutionResult

        Public Property Success As Boolean

        ''' <summary>Short reason string when <see cref="Success"/> is False; empty otherwise.</summary>
        Public Property AbortReason As String = String.Empty

        ''' <summary>Broker position id when reported by <c>PlaceOrderAsync</c>; otherwise Nothing.</summary>
        Public Property PlacedPositionId As Long?

        ''' <summary>Initial stop in ticks after the PxMinStopDollars + PhasedTrail clamps.</summary>
        Public Property InitialStopTicks As Integer?

    End Class

End Namespace
