Imports Microsoft.ML.Data
Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.ML.Models

    ''' <summary>ML.NET output schema for binary signal classification.</summary>
    Public Class SignalPrediction

        <ColumnName("PredictedLabel")>
        Public Property PredictedLabel As Boolean

        ''' <summary>Probability that the signal is profitable (0.0 to 1.0).</summary>
        <ColumnName("Probability")>
        Public Property Probability As Single

        ''' <summary>Raw model score before calibration.</summary>
        <ColumnName("Score")>
        Public Property Score As Single

        ''' <summary>
        ''' Derived signal type from probability.
        ''' BUY if Probability >= BuyThreshold,
        ''' SELL if Probability <= SellThreshold,
        ''' HOLD otherwise.
        ''' Thresholds are applied from RiskSettings.MinSignalConfidence.
        ''' </summary>
        Public Function ToSignalType(minConfidence As Single) As SignalType
            If Probability >= minConfidence Then Return SignalType.Buy
            If Probability <= (1.0F - minConfidence) Then Return SignalType.Sell
            Return SignalType.Hold
        End Function

        ''' <summary>Confidence = distance of Probability from 0.5, scaled to 0-1.</summary>
        Public ReadOnly Property Confidence As Single
            Get
                Return Math.Abs(Probability - 0.5F) * 2.0F
            End Get
        End Property

    End Class

    ' ModelMetrics has been moved to TopStepTrader.Core.Models.ModelMetrics
    ' to avoid a layering violation (Core must not depend on ML).

End Namespace
