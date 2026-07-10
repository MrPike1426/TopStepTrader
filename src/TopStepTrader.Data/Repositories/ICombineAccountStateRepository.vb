Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>
    ''' FEAT-74: persistence for the per-account trailing max-drawdown state.
    ''' Follows the house repository pattern (interface alongside the implementation
    ''' in the Data layer, scoped registration, consumed via IServiceScopeFactory).
    ''' </summary>
    Public Interface ICombineAccountStateRepository

        ''' <summary>
        ''' Returns the persisted state for <paramref name="accountId"/>, creating a fresh
        ''' row (peak = starting balance, cumulative P&amp;L = 0) when none exists yet.
        ''' </summary>
        Function GetOrCreateAsync(accountId As Long, startingBalance As Decimal) As Task(Of CombineAccountStateEntity)

        ''' <summary>Insert-or-update by <c>AccountId</c>; stamps <c>UpdatedAtUtc</c>.</summary>
        Function UpsertAsync(state As CombineAccountStateEntity) As Task

    End Interface

End Namespace
