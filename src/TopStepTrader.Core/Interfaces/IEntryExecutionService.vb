Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' ARCH-19: strategy-agnostic entry execution pipeline. Takes a per-trade
    ''' <see cref="EntryExecutionRequest"/> (candidate + already-opened slot + persona/timeframe
    ''' context + UI callbacks) and runs the eight steps that previously lived inside
    ''' <c>SuperTrendPlusViewModel.FireEntryAsync</c>:
    '''
    '''   1. Resolve / validate the trading account.
    '''   2. Live-price guard against gap-through of the suggested stop.
    '''   3. Initial-stop tick computation (PxMinStopDollars + PhasedTrail clamps).
    '''   4. AI pre-trade veto (suppression window owned by the service).
    '''   5. Bracket order placement.
    '''   6. LiveTradeRecord / TradeOutcome / TradeSetupSnapshot persistence.
    '''   7. Begin live P&amp;L tracking + MarketHub subscription for the slot.
    '''   8. Strategy-side UI updates via callbacks on the request.
    '''
    ''' On any abort path the slot is released via <c>EntryExecutionRequest.OnReleaseSlot</c>
    ''' before the method returns, so the caller does not need to clean up.
    ''' </summary>
    Public Interface IEntryExecutionService

        Function PlaceAsync(request As EntryExecutionRequest,
                            ct As CancellationToken) As Task(Of EntryExecutionResult)

        ''' <summary>
        ''' Returns True when a previous AI veto for <paramref name="contractSymbol"/> is
        ''' still inside its 15-minute suppression window. Strategy ViewModels consult this
        ''' before opening a slot so they don't waste a tick on a candidate that the AI veto
        ''' will immediately reject + close.
        ''' </summary>
        Function IsAiSuppressed(contractSymbol As String) As Boolean

    End Interface

End Namespace
