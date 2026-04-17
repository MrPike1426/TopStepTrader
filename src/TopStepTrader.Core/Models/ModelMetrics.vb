Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Training evaluation metrics for a fitted ML model.
    ''' Returned by IModelTrainingService.RetrainAsync and stored in the audit trail.
    ''' Lives in Core (not ML) so the Core interface can reference it without a layering violation.
    ''' </summary>
    Public Class ModelMetrics
        Public Property Accuracy As Double
        Public Property AUC As Double
        Public Property F1Score As Double
        Public Property TrainedAt As DateTimeOffset
        Public Property TrainingSamples As Integer
        Public Property ModelVersion As String = String.Empty
    End Class

End Namespace
