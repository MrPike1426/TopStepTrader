Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' ARCH-20: strategy-agnostic per-tick position management. Owns the per-tick
    ''' broker snapshot, MissCount escalation, EntryPrice / PositionId backfill, live-P&amp;L
    ''' refresh, MAE/MFE tracking, ExitSignalEngine evaluation + score-threshold gate,
    ''' phased stop ratcheting, TradeStopAdjustment persistence, TradeTickSnapshot
    ''' persistence (FEAT-59), and one-shot bracket-stop verification.
    '''
    ''' Mutates <paramref name="slot"/> in place; never touches UI bindings or raises
    ''' events. When the tick decides an exit is required, returns a
    ''' <see cref="PositionManagementResult"/> with
    ''' <c>Outcome = PositionManagementOutcome.ExitRequested</c> and a populated
    ''' <c>ExitReason</c> / <c>ExitTrigger</c> — the caller must invoke
    ''' <c>IExitExecutionService.CloseAsync</c> to perform the actual close.
    ''' </summary>
    Public Interface IPositionManagementService

        ''' <summary>
        ''' Runs one management tick for <paramref name="slot"/>. Honours
        ''' <see cref="PositionManagementTickContext.ForceSnapshot"/> /
        ''' <see cref="PositionManagementTickContext.ReleasedThisTick"/> and applies the
        ''' alternating-tick skip when the slot is in steady state.
        ''' </summary>
        Function UpdateAsync(slot As PositionSlot,
                             tickContext As PositionManagementTickContext,
                             ct As CancellationToken) As Task(Of PositionManagementResult)

        ''' <summary>
        ''' BUG-80: One-shot verification that a resting Stop bracket order exists for the
        ''' slot. Re-creates a missing stop via the broker order service; degrades health
        ''' and escalates to exit-request on two consecutive missing ticks. Intended for the
        ''' inline use inside <see cref="UpdateAsync"/> — exposed as a public seam so the
        ''' strategy VM can also invoke it during reconciliation paths.
        ''' </summary>
        Function VerifyBracketStopAsync(slot As PositionSlot,
                                          ct As CancellationToken) As Task(Of PositionManagementResult)

    End Interface

End Namespace
