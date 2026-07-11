Imports System.Threading

Namespace TopStepTrader.Services.VwapMeanReversion

    ''' <summary>
    ''' FEAT-75: Strategy-specific entry detection seam for the VWAP Mean-Reversion tab.
    ''' Returns the latest evaluation (signal-or-rejection) for one watchlist symbol.
    ''' </summary>
    Public Interface IVwapMeanReversionSignalDetector

        Function EvaluateAsync(symbol As String,
                                ct As CancellationToken) As Task(Of VwapMeanReversionEvaluation)

    End Interface

End Namespace
