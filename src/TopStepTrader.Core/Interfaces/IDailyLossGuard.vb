Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-71: Snapshot of <see cref="IDailyLossGuard"/> state. Surfaced to the
    ''' Dashboard banner and consumed by strategy VMs / orchestrators at the entry gate.
    ''' </summary>
    Public Class DailyLossGuardState
        Public Property IsHalted As Boolean
        Public Property Reason As RiskHaltReason
        Public Property RealisedDailyPnl As Decimal
        Public Property UnrealisedDailyPnl As Decimal
        Public Property CombinedDailyPnl As Decimal
        Public Property LimitDollars As Decimal
        Public Property HaltedAtUtc As DateTimeOffset?
        Public Property HaltMessage As String = String.Empty
    End Class

    ''' <summary>
    ''' FEAT-71: Daily-loss-cap kill switch.
    '''
    ''' Sums realised PnL from <c>LiveTradeRecords</c> (closed today) with unrealised
    ''' PnL aggregated across registered <see cref="IOpenSlotPnlSource"/> instances.
    ''' When the combined figure crosses
    ''' <c>RiskSettings.DailyLossLimitDollars</c>, the guard transitions to halted —
    ''' new entries are blocked until <see cref="ResetAsync"/> is called or the
    ''' trading day rolls over.
    '''
    ''' Strategies opt in by consulting <see cref="CanEnterNewTrade"/> before opening
    ''' a slot; the guard does not force-flatten existing positions.
    ''' </summary>
    Public Interface IDailyLossGuard

        ''' <summary>Snapshot of guard state for UI binding + entry gating. Cheap to call (cached).</summary>
        Function GetState() As DailyLossGuardState

        ''' <summary>True if a new entry is permitted. Strategy VMs MUST consult this before opening a slot.</summary>
        Function CanEnterNewTrade() As Boolean

        ''' <summary>
        ''' Evaluate the guard against current realised + unrealised P&amp;L; idempotent.
        ''' Called by a background ticker AND directly by callers that want the freshest
        ''' state (e.g. an entry path that just observed a large unrealised drawdown).
        ''' </summary>
        Function EvaluateAsync() As Task(Of DailyLossGuardState)

        ''' <summary>
        ''' User-initiated reset (e.g. day rollover, manual override after review).
        ''' Clears the halted flag, persists a RiskEvent row, and raises <see cref="Released"/>.
        ''' </summary>
        Function ResetAsync(reason As String) As Task

        ''' <summary>Register a source of unrealised PnL (a strategy VM or orchestrator that owns open slots).</summary>
        Sub RegisterOpenSlotPnlSource(source As IOpenSlotPnlSource)

        ''' <summary>Unregister a previously registered <see cref="IOpenSlotPnlSource"/>.</summary>
        Sub UnregisterOpenSlotPnlSource(source As IOpenSlotPnlSource)

        ''' <summary>Raised when the guard transitions to Halted. UI banner subscribes.</summary>
        Event Halted As EventHandler(Of DailyLossGuardState)

        ''' <summary>Raised when the guard transitions out of Halted (manual reset or day rollover).</summary>
        Event Released As EventHandler
    End Interface

    ''' <summary>
    ''' FEAT-71: Provider of an unrealised-PnL aggregate for the daily-loss guard.
    ''' Implemented by strategy VMs / orchestrators that own open <c>PositionSlot</c>s.
    ''' Implementations must return 0 when no positions are open.
    ''' </summary>
    Public Interface IOpenSlotPnlSource
        ''' <summary>
        ''' Sum of <c>slot.UnrealizedPnl</c> across all currently-open slots owned by
        ''' this source. Returns 0 (not negative) when flat. Must be safe to call from
        ''' any thread — implementations should snapshot state under their own lock.
        ''' </summary>
        Function GetUnrealisedAggregate() As Decimal

        ''' <summary>True when this source currently has at least one open slot. Used by
        ''' the guard to pick its fast/slow polling cadence.</summary>
        Function HasOpenSlots() As Boolean
    End Interface

End Namespace
