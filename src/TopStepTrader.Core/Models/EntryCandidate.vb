Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' A candidate entry produced by an <see cref="Interfaces.IEntrySignalProvider"/>.
    ''' Returned by per-tick strategy evaluation when all strategy-specific gates pass.
    ''' The trade orchestrator (not the provider) is responsible for placing the order,
    ''' creating the slot, running the AI veto, and clamping the suggested stop.
    ''' </summary>
    Public Class EntryCandidate

        ''' <summary>TopStepX numeric contract identifier.</summary>
        Public Property ContractId As Integer

        ''' <summary>Buy = long entry; Sell = short entry.</summary>
        Public Property Side As OrderSide

        ''' <summary>Name of the strategy that produced this candidate, e.g. "SuperTrendPlus" or "BreakAndBounce".</summary>
        Public Property StrategyName As String = String.Empty

        ''' <summary>Short human-readable reason, e.g. "BB retest + hammer" or "ST flip Long + ADX 34".</summary>
        Public Property EntryReason As String = String.Empty

        ''' <summary>
        ''' Last bar close at signal time. The orchestrator uses this for the PreTradeContext
        ''' and as the reference price for SL-distance clamps.
        ''' </summary>
        Public Property ReferencePrice As Decimal

        ''' <summary>
        ''' Provider-suggested initial stop price. The orchestrator may clamp this further
        ''' (e.g. minimum-tick floor) but will not move it closer to entry.
        ''' Nothing = let the orchestrator compute a default stop from strategy config.
        ''' </summary>
        Public Property SuggestedInitialStopPrice As Decimal?

        ''' <summary>
        ''' Optional pre-built PreTradeContext for the AI veto.
        ''' Nothing = orchestrator builds a default context from session state.
        ''' </summary>
        Public Property PreTradeContext As PreTradeContext

    End Class

End Namespace
