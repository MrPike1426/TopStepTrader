Imports System.Threading

Namespace TopStepTrader.Services.BreakAndBounce

    ''' <summary>
    ''' FEAT-62: Strategy-specific entry detection seam for the Break and Bounce tab.
    ''' Returns the latest evaluation (signal-or-rejection) for one watchlist symbol.
    ''' </summary>
    Public Interface IBreakAndBounceSignalDetector

        Function EvaluateAsync(symbol As String,
                                ct As CancellationToken) As Task(Of BreakAndBounceEvaluation)

    End Interface

End Namespace
