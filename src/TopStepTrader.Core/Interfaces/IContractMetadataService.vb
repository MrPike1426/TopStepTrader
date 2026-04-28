Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Resolves contract/instrument metadata for TopStepX.
    ''' Searches the ProjectX contract catalogue by symbol or text.
    ''' </summary>
    Public Interface IContractMetadataService

        ''' <summary>
        ''' Returns the TopStepX contract identifier for the given display symbol.
        ''' Returns the contractId string (e.g. "CON.F.US.MGC.J26").
        ''' Returns Nothing if not found.
        ''' </summary>
        Function ResolveContractIdAsync(symbol As String) As Task(Of String)

        ''' <summary>Returns contract details (tick size, name, etc.) for the given broker contract ID.</summary>
        Function GetContractAsync(contractId As String) As Task(Of Contract)

        ''' <summary>Text search — returns up to 20 matching contracts from the active broker.</summary>
        Function SearchContractsAsync(searchText As String) As Task(Of IEnumerable(Of Contract))

    End Interface

End Namespace
