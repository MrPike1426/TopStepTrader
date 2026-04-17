Imports Microsoft.Extensions.Logging
Imports Microsoft.ML
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.ML.Models

Namespace TopStepTrader.ML.Prediction

    ''' <summary>
    ''' Wraps the ML.NET PredictionEngine for single-record inference.
    ''' Thread safety: PredictionEngine is NOT thread-safe; use PredictionEnginePool
    ''' or ensure single-threaded access. We use a lock here for simplicity.
    ''' </summary>
    Public Class SignalPredictor

        Private ReadOnly _featureExtractor As BarFeatureExtractor
        Private ReadOnly _logger As ILogger(Of SignalPredictor)
        Private ReadOnly _lock As New Object()
        Private _predictionEngine As PredictionEngine(Of BarFeatureVector, SignalPrediction)
        Private _modelVersion As String = "none"

        Public Sub New(featureExtractor As BarFeatureExtractor,
                       logger As ILogger(Of SignalPredictor))
            _featureExtractor = featureExtractor
            _logger = logger
        End Sub

        Public ReadOnly Property IsModelLoaded As Boolean
            Get
                Return _predictionEngine IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property ModelVersion As String
            Get
                Return _modelVersion
            End Get
        End Property

        ''' <summary>Load (or hot-reload) a model from the given .zip path.</summary>
        Public Sub LoadModel(modelPath As String, mlContext As MLContext)
            SyncLock _lock
                _logger.LogInformation("Loading ML model from {Path}", modelPath)
                Dim loadedModel = mlContext.Model.Load(modelPath, Nothing)
                _predictionEngine = mlContext.Model.CreatePredictionEngine(
                    Of BarFeatureVector, SignalPrediction)(loadedModel)
                _modelVersion = IO.Path.GetFileNameWithoutExtension(modelPath)
                _logger.LogInformation("Model {Version} loaded successfully", _modelVersion)
            End SyncLock
        End Sub

        ''' <summary>
        ''' Generate a signal prediction from the most recent bars.
        ''' Returns Nothing if model is not loaded or insufficient bars.
        ''' </summary>
        Public Function Predict(recentBars As IList(Of MarketBar)) As SignalPrediction
            If Not IsModelLoaded Then
                _logger.LogWarning("Prediction requested but no model is loaded")
                Return Nothing
            End If

            If recentBars Is Nothing OrElse recentBars.Count < BarFeatureExtractor.MinBarsRequired Then
                _logger.LogWarning("Insufficient bars for prediction: {Count} (need {Min})",
                                   recentBars?.Count, BarFeatureExtractor.MinBarsRequired)
                Return Nothing
            End If

            Dim features = _featureExtractor.Extract(recentBars)

            SyncLock _lock
                Dim prediction = _predictionEngine.Predict(features)
                _logger.LogDebug("Prediction: {Type} (prob={Prob:F3}, conf={Conf:F3})",
                                 prediction.ToSignalType(0.65F), prediction.Probability, prediction.Confidence)
                Return prediction
            End SyncLock
        End Function

    End Class

End Namespace
