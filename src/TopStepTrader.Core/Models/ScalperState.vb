Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Per-slot mutable state required by ScalperExitManager across ticks.
    ''' Held separately from <see cref="PositionSlot"/> so the slot model
    ''' remains strategy-neutral. View models maintain one instance per
    ''' open slot and pass it back into Evaluate on every tick.
    ''' </summary>
    Public Class ScalperState

        ''' <summary>
        ''' True once the one-way ScaredyCat trigger has fired for this slot.
        ''' Persists for the lifetime of the trade — never re-disengages.
        ''' </summary>
        Public Property IsScaredyCatActive As Boolean = False

        ''' <summary>
        ''' Timestamp of the latest closed 15s bar the manager has evaluated.
        ''' Updated by the manager on each Evaluate call.
        ''' </summary>
        Public Property LastEvaluatedBarTime As DateTimeOffset = DateTimeOffset.MinValue

    End Class

End Namespace
