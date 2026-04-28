Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Evaluates one bar's pre-computed indicator scalars against the nine
    ''' Multi-Confluence conditions and returns a <see cref="MultiConfluenceResult"/>.
    ''' Both the live (<c>MultiConfluenceStrategy</c>) and backtest
    ''' (<c>MultiConfluenceSignalProvider</c>) paths delegate here — no condition
    ''' logic lives in two places (ARCH-04).
    ''' </summary>
    Public Interface IMultiConfluenceEvaluator

        ''' <summary>
        ''' Evaluate all nine conditions using pre-populated scalar inputs.
        ''' Returns a <see cref="MultiConfluenceResult"/> with Side = Nothing when no signal fires.
        ''' </summary>
        Function Evaluate(inputs As MultiConfluenceInputs) As MultiConfluenceResult

    End Interface

End Namespace
