Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Per-tick context passed to an <see cref="Interfaces.IEntrySignalProvider"/>.
    ''' Bundles the bar cache, the watchlist scope, the currently-open slots, and the
    ''' as-of timestamp so providers can be unit-tested in isolation without requiring
    ''' UI ViewModels or the orchestrator itself.
    ''' </summary>
    Public Class EntryEvaluationContext

        ''' <summary>
        ''' Per-contract closed-bar cache keyed by TopStepX contract id, in chronological
        ''' order, on the <see cref="StrategyTimeframe"/>. Forming (non-closed) bars are
        ''' stripped before the cache is passed in.
        ''' </summary>
        Public Property BarCache As IReadOnlyDictionary(Of Integer, IList(Of MarketBar)) =
            New Dictionary(Of Integer, IList(Of MarketBar))()

        ''' <summary>Timeframe of the bars in <see cref="BarCache"/>.</summary>
        Public Property StrategyTimeframe As BarTimeframe = BarTimeframe.FiveMinute

        ''' <summary>
        ''' Contract IDs the orchestrator wants the provider to consider this tick.
        ''' Providers should ignore contracts not in this list even if BarCache has entries.
        ''' </summary>
        Public Property Instruments As IReadOnlyList(Of Integer) = New List(Of Integer)()

        ''' <summary>
        ''' Snapshot of currently-open position slots. Providers must skip any contract
        ''' that already has an open slot — the orchestrator does not deduplicate.
        ''' </summary>
        Public Property OpenSlots As IReadOnlyList(Of PositionSlot) = New List(Of PositionSlot)()

        ''' <summary>UTC timestamp of the tick being evaluated.</summary>
        Public Property AsOfUtc As DateTime = DateTime.UtcNow

    End Class

End Namespace
