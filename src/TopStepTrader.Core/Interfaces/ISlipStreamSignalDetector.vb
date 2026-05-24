Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-70: Evaluates the SlipStream confluence (trend + extended-pullback + RSI + DI +
    ''' ADX + ATR percentile, optionally gated by an HTF EMA filter) for a single instrument
    ''' and returns a <see cref="SlipStreamEvaluation"/> describing the most recent closed
    ''' signal-timeframe bar. Called by <c>SlipStreamOrchestrator</c> once per scan tick per
    ''' watchlist symbol.
    ''' </summary>
    Public Interface ISlipStreamSignalDetector

        Function EvaluateAsync(symbol As String,
                               ct As CancellationToken) As Task(Of SlipStreamEvaluation)

    End Interface

End Namespace
