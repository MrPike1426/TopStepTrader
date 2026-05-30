Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' ARCH-20: outcome of one <c>IExitExecutionService.CloseAsync</c> call. The service
    ''' has already performed the data-side close — broker flatten, TradeRecord close,
    ''' TradeOutcomes resolution, TradeLifespanRecord persistence, and
    ''' <c>SlotManager.CloseSlot</c> — by the time the result is returned. The VM
    ''' consumes the result to wire up the strategy-side UI cleanup (slot box reset,
    ''' debug capture EndTrade, MarketHub unsubscribe) and to raise <c>TradeClosed</c>.
    ''' </summary>
    Public Class ExitExecutionResult

        ''' <summary>True when the slot was released cleanly (a no-op when the slot was
        ''' already closed via another channel before <c>CloseAsync</c> was called).</summary>
        Public Property Released As Boolean

        ''' <summary>Engine-derived exit price computed from <c>UnrealizedPnl</c> and contract
        ''' tick metadata. Nothing when entry price is unset or the contract is unknown.
        ''' Mirrors the legacy ReleaseSlotAsync exit-price math (BUG-82 F3).</summary>
        Public Property ExitPrice As Decimal?

        ''' <summary>Realised P&amp;L recorded on the trade. Mirrors <c>slot.UnrealizedPnl</c>
        ''' as observed at the moment of release.</summary>
        Public Property RealizedPnlUsd As Decimal

        ''' <summary>R-multiple recorded on the lifespan record. Nothing when initial risk
        ''' was never established (slot closed before first snapshot or zero-risk synthetic).</summary>
        Public Property RMultiple As Decimal?

        ''' <summary>Instrument symbol the slot was closing on. Captured before
        ''' <c>SlotManager.CloseSlot</c> wipes the slot, so the VM can use it for
        ''' the MarketHub unsubscribe path without having to remember it itself.</summary>
        Public Property ClosingInstrument As String = String.Empty

        ''' <summary>Slot index that was closed.</summary>
        Public Property ClosingSlotIndex As Integer = -1

        ''' <summary>Resolved <c>FavouriteContract.PxContractId</c> for the closing instrument,
        ''' if available — saves the VM a second lookup in the post-close unsubscribe path.</summary>
        Public Property ClosingPxContractId As String

        ''' <summary>
        ''' BUG-100: provenance of <see cref="ExitPrice"/> / <see cref="RealizedPnlUsd"/>:
        ''' <c>"hub"</c> (SignalR fill push), <c>"rest-poll"</c> (REST history fallback),
        ''' or <c>"engine-fallback"</c> (no broker fill within the timeout — values kept
        ''' from the pre-flatten engine estimate).
        ''' </summary>
        Public Property CloseFillSource As String = String.Empty

    End Class

End Namespace
