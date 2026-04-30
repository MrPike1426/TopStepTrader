Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Provides live ProjectX contract IDs for all favourite instruments.
    ''' Resolved once per day at startup from the ProjectX API and cached in SQLite.
    ''' All strategies must use this service — never hardcode contract ID strings.
    ''' </summary>
    Public Interface IContractResolutionService

        ''' <summary>
        ''' Initialises the in-memory contract cache.
        ''' Call once at app startup before any strategy scan begins.
        ''' For each root symbol in FavouriteContracts:
        '''   - If the SQLite row is fresh (LastUpdated = today) the API is NOT called.
        '''   - If any row is stale or missing, the ProjectX API is queried for all stale symbols.
        ''' On API failure the affected symbols are recorded; trading is disabled for them only.
        ''' </summary>
        Function InitialiseAsync(Optional cancel As Threading.CancellationToken = Nothing) As Task

        ''' <summary>
        ''' Returns the live ProjectX contract ID for the given root symbol (e.g. "MES", "MCLE").
        ''' Reads from the in-memory cache — zero DB or API latency.
        ''' Throws InvalidOperationException if the symbol was not resolved during initialisation.
        ''' </summary>
        Function GetContractId(rootSymbol As String) As String

        ''' <summary>
        ''' Returns True if the given root symbol resolved successfully and is safe to trade.
        ''' </summary>
        Function IsResolved(rootSymbol As String) As Boolean

        ''' <summary>
        ''' Root symbols that failed to resolve during the last InitialiseAsync call.
        ''' Empty if all symbols resolved successfully.
        ''' </summary>
        ReadOnly Property FailedSymbols As IReadOnlyList(Of String)

    End Interface

End Namespace
