Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-72: registered by strategy VMs / orchestrators that own open <c>PositionSlot</c>s.
    ''' The adaptive watchlist service queries every registered source at refresh time to
    ''' pin instruments with live exposure into the watchlist regardless of score, so a
    ''' contract cannot drop out from under an open slot.
    ''' </summary>
    Public Interface IOpenSlotInstrumentSource
        ''' <summary>Root symbols (e.g. "MES") that currently have at least one open slot
        ''' owned by this source. Must be safe to call from any thread.</summary>
        Function GetOpenInstrumentRootSymbols() As IEnumerable(Of String)
    End Interface

End Namespace
