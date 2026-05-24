Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-64: One quote-tick decision from <c>IScalperTrailEngine.OnQuote</c>. The
    ''' orchestrator inspects this to decide whether to (a) push a new SL price to the
    ''' broker via <c>EditPositionSlTpAsync</c> and/or (b) close the position immediately
    ''' because the local trail has been violated before the broker's resting SL fires.
    ''' </summary>
    Public Class ScalperTrailUpdate

        ''' <summary>Stop price the trail engine wants. Equal to previous stop when no advance.</summary>
        Public Property NewStopPrice As Decimal

        ''' <summary>True iff <see cref="NewStopPrice"/> is monotonically better than the prior stop.</summary>
        Public Property StopAdvanced As Boolean

        ''' <summary>
        ''' True when last price has crossed through the current stop. Orchestrator should
        ''' close immediately rather than waiting for the broker's resting SL — covers the
        ''' race between edit-submit and broker acknowledgement.
        ''' </summary>
        Public Property ExitRequested As Boolean

        ''' <summary>
        ''' True when the broker edit should fire on this tick: <see cref="StopAdvanced"/>,
        ''' the improvement is at least the configured min-tick step, and the throttle
        ''' counter still has headroom this second.
        ''' </summary>
        Public Property BrokerEditShouldFire As Boolean

        ''' <summary>Short human-readable reason for diagnostics / release log.</summary>
        Public Property Reason As String = String.Empty

    End Class

End Namespace
