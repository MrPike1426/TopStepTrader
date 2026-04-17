Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Service for triggering model retraining and querying the feedback loop metrics.
    ''' </summary>
    Public Interface IModelTrainingService
        ''' <summary>
        ''' Retrain the ML model using bar history + real trade outcomes from the DB.
        ''' When <paramref name="contractId"/> is supplied the training fetches bars only for
        ''' that contract (backtest context); otherwise falls back to
        ''' TradingSettings.ActiveContractIds (live-trading default).
        ''' </summary>
        Function RetrainAsync(cancel As CancellationToken,
                              Optional contractId As String = Nothing) As Task(Of ModelMetrics)

        ''' <summary>Rolling win-rate of the last <paramref name="windowSize"/> resolved outcomes.</summary>
        Function GetRollingWinRateAsync(Optional windowSize As Integer = 50) As Task(Of Single)

        ''' <summary>Number of outcomes (open + resolved) recorded so far.</summary>
        Function GetOutcomeCountAsync() As Task(Of Integer)

        ''' <summary>Raised when a new model version finishes training.</summary>
        Event ModelRetrained As EventHandler(Of ModelRetrainedEventArgs)
    End Interface

    Public Class ModelRetrainedEventArgs
        Inherits EventArgs
        Public ReadOnly Property Metrics As ModelMetrics
        Public Sub New(metrics As ModelMetrics)
            Me.Metrics = metrics
        End Sub
    End Class

End Namespace
