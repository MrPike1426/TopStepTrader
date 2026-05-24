Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-64: Evaluates the three-element confluence (5m MA200, session VWAP, RSI(14)
    ''' with recency filter) for a single instrument and returns an
    ''' <see cref="UltimateScalperEvaluation"/> describing the most recent closed bar.
    ''' Called by <c>UltimateScalperOrchestrator</c> once per closed 5-minute bar
    ''' per scanned symbol (MES / MNQ / MGC).
    ''' </summary>
    Public Interface IUltimateScalperSignalDetector

        Function EvaluateAsync(symbol As String,
                               ct As CancellationToken) As Task(Of UltimateScalperEvaluation)

    End Interface

End Namespace
