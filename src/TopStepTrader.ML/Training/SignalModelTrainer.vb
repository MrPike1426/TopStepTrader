Imports System.IO
Imports Microsoft.Extensions.Logging
Imports Microsoft.ML
Imports Microsoft.ML.Trainers.FastTree
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.ML.Models

Namespace TopStepTrader.ML.Training

    ''' <summary>
    ''' Trains a FastTree binary classification model on labelled BarFeatureVectors.
    ''' Saves the trained model as a .zip file to disk.
    ''' </summary>
    Public Class SignalModelTrainer

        Private ReadOnly _mlContext As MLContext
        Private ReadOnly _featureExtractor As BarFeatureExtractor
        Private ReadOnly _logger As ILogger(Of SignalModelTrainer)

        ' All feature column names (must match BarFeatureVector properties minus Label)
        Private Shared ReadOnly FeatureColumns As String() = {
            NameOf(BarFeatureVector.RSI14), NameOf(BarFeatureVector.RSI7),
            NameOf(BarFeatureVector.EMA9vsEMA21), NameOf(BarFeatureVector.PriceVsEMA21),
            NameOf(BarFeatureVector.MACDLine), NameOf(BarFeatureVector.MACDSignal),
            NameOf(BarFeatureVector.MACDHistogram), NameOf(BarFeatureVector.MACDHistogramChange),
            NameOf(BarFeatureVector.ATR14), NameOf(BarFeatureVector.BBWidth),
            NameOf(BarFeatureVector.PriceVsBBMiddle), NameOf(BarFeatureVector.VolumeRatio),
            NameOf(BarFeatureVector.PriceVsVWAP), NameOf(BarFeatureVector.BarRange),
            NameOf(BarFeatureVector.BodyRatio), NameOf(BarFeatureVector.UpperWick),
            NameOf(BarFeatureVector.LowerWick), NameOf(BarFeatureVector.IsBullish),
            NameOf(BarFeatureVector.Return1Bar), NameOf(BarFeatureVector.Return5Bar),
            NameOf(BarFeatureVector.Return20Bar)
        }

        Public Sub New(featureExtractor As BarFeatureExtractor,
                       logger As ILogger(Of SignalModelTrainer))
            _mlContext = New MLContext(seed:=42)
            _featureExtractor = featureExtractor
            _logger = logger
        End Sub

        ''' <summary>
        ''' Build training data from bar history, train the model, and save to disk.
        ''' </summary>
        ''' <param name="allBars">Full bar history (ascending time order).</param>
        ''' <param name="outputPath">Full path to write the .zip model file.</param>
        ''' <param name="lookAheadBars">How many bars forward to check for profitability.</param>
        ''' <param name="outcomeLabels">
        ''' Optional real trade outcome labels keyed by bar timestamp.
        ''' When provided, the look-ahead heuristic label is replaced by the actual P&amp;L result
        ''' for any bar whose timestamp matches a recorded outcome.
        ''' </param>
        Public Function TrainAndSave(allBars As IList(Of MarketBar),
                                     outputPath As String,
                                     Optional lookAheadBars As Integer = 5,
                                     Optional outcomeLabels As Dictionary(Of DateTimeOffset, Boolean) = Nothing) As ModelMetrics

            Dim overrideCount = If(outcomeLabels IsNot Nothing, outcomeLabels.Count, 0)
            _logger.LogInformation(
                "Building training dataset from {Count} bars ({Overrides} real-outcome overrides)...",
                allBars.Count, overrideCount)

            ' Build labelled feature vectors
            Dim samples = New List(Of BarFeatureVector)()
            For i = BarFeatureExtractor.MinBarsRequired To allBars.Count - lookAheadBars - 1
                Dim fv = _featureExtractor.ExtractWithLabel(allBars, i, lookAheadBars)
                If fv IsNot Nothing Then
                    ' Override look-ahead heuristic label with real P&L outcome when available
                    If outcomeLabels IsNot Nothing Then
                        Dim barTimestamp = allBars(i).Timestamp
                        Dim realLabel As Boolean
                        If outcomeLabels.TryGetValue(barTimestamp, realLabel) Then
                            fv.Label = realLabel
                        End If
                    End If
                    samples.Add(fv)
                End If
            Next

            If samples.Count < 100 Then
                Throw New InvalidOperationException(
                    $"Insufficient training samples: {samples.Count}. Need at least 100.")
            End If

            _logger.LogInformation("Training on {Count} samples ({Pos} buy, {Neg} hold/sell)...",
                                   samples.Count,
                                   samples.Where(Function(s) s.Label).Count(),
                                   samples.Where(Function(s) Not s.Label).Count())

            Dim data = _mlContext.Data.LoadFromEnumerable(samples)

            ' 80/20 train-test split (stratified on Label)
            Dim split = _mlContext.Data.TrainTestSplit(data, testFraction:=0.2, seed:=42)

            ' Build pipeline
            Dim pipeline = _mlContext.Transforms _
                .Concatenate("Features", FeatureColumns) _
                .Append(_mlContext.BinaryClassification.Trainers.FastTree(
                    New FastTreeBinaryTrainer.Options With {
                        .LabelColumnName = "Label",
                        .FeatureColumnName = "Features",
                        .NumberOfTrees = 100,
                        .NumberOfLeaves = 20,
                        .MinimumExampleCountPerLeaf = 10,
                        .LearningRate = 0.2
                    }))

            ' Train
            _logger.LogInformation("Training FastTree model (100 trees)...")
            Dim trainedModel = pipeline.Fit(split.TrainSet)

            ' Evaluate on test set
            Dim predictions = trainedModel.Transform(split.TestSet)
            Dim metrics = _mlContext.BinaryClassification.Evaluate(predictions,
                                                                    labelColumnName:="Label")

            _logger.LogInformation(
                "Model metrics — Accuracy: {Acc:P1}, AUC: {AUC:F3}, F1: {F1:F3}",
                metrics.Accuracy, metrics.AreaUnderRocCurve, metrics.F1Score)

            ' Save
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath))
            _mlContext.Model.Save(trainedModel, data.Schema, outputPath)
            _logger.LogInformation("Model saved to {Path}", outputPath)

            Return New ModelMetrics With {
                .Accuracy = metrics.Accuracy,
                .AUC = metrics.AreaUnderRocCurve,
                .F1Score = metrics.F1Score,
                .TrainedAt = DateTimeOffset.UtcNow,
                .TrainingSamples = samples.Count,
                .ModelVersion = Path.GetFileNameWithoutExtension(outputPath)
            }
        End Function

    End Class

End Namespace
