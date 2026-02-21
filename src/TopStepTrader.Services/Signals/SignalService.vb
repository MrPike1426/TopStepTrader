Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Prediction

Namespace TopStepTrader.Services.Signals

    ''' <summary>
    ''' Generates trade signals using the ML predictor, persists them to the database,
    ''' and raises the SignalGenerated event for downstream consumers (AutoExecutionService).
    ''' </summary>
    Public Class SignalService
        Implements ISignalService

        Private ReadOnly _predictor As SignalPredictor
        Private ReadOnly _signalRepo As SignalRepository
        Private ReadOnly _logger As ILogger(Of SignalService)
        Private _lastSignal As TradeSignal

        Public Event SignalGenerated As EventHandler(Of SignalGeneratedEventArgs) _
            Implements ISignalService.SignalGenerated

        Public ReadOnly Property LastSignal As TradeSignal Implements ISignalService.LastSignal
            Get
                Return _lastSignal
            End Get
        End Property

        Public Sub New(predictor As SignalPredictor,
                       signalRepo As SignalRepository,
                       logger As ILogger(Of SignalService))
            _predictor = predictor
            _signalRepo = signalRepo
            _logger = logger
        End Sub

        Public Async Function GenerateSignalAsync(contractId As String,
                                                   recentBars As IEnumerable(Of MarketBar)) _
            As Task(Of TradeSignal) Implements ISignalService.GenerateSignalAsync

            Dim bars = recentBars.ToList()

            If Not _predictor.IsModelLoaded Then
                _logger.LogWarning("Signal requested but no ML model is loaded")
                Return Nothing
            End If

            Dim prediction = _predictor.Predict(bars)
            If prediction Is Nothing Then
                _logger.LogDebug("Predictor returned Nothing for contract {Id}", contractId)
                Return Nothing
            End If

            Dim signalType = prediction.ToSignalType(0.65F)

            ' Build domain signal
            Dim signal = New TradeSignal With {
                .ContractId = contractId,
                .GeneratedAt = DateTimeOffset.UtcNow,
                .SignalType = signalType,
                .Confidence = prediction.Confidence,
                .ModelVersion = _predictor.ModelVersion,
                .ReasoningTags = BuildTags(prediction, signalType)
            }

            ' Persist to DB (repo accepts the domain TradeSignal)
            Try
                signal.Id = Await _signalRepo.SaveSignalAsync(signal)
            Catch ex As Exception
                _logger.LogError(ex, "Failed to persist signal for contract {Id}", contractId)
            End Try

            _lastSignal = signal

            _logger.LogInformation("Signal generated: {Type} (conf={Conf:F3}) for contract {Id}",
                                   signalType, signal.Confidence, contractId)

            RaiseEvent SignalGenerated(Me, New SignalGeneratedEventArgs(signal))

            Return signal
        End Function

        Public Async Function GetSignalHistoryAsync(contractId As String,
                                                     from As DateTime,
                                                     [to] As DateTime) _
            As Task(Of IEnumerable(Of TradeSignal)) Implements ISignalService.GetSignalHistoryAsync
            ' Repository already returns mapped domain objects
            Return Await _signalRepo.GetSignalHistoryAsync(contractId, from, [to])
        End Function

        Private Shared Function BuildTags(prediction As ML.Models.SignalPrediction,
                                          signalType As SignalType) As List(Of String)
            Dim tags As New List(Of String)
            tags.Add($"prob={prediction.Probability:F3}")
            tags.Add($"score={prediction.Score:F3}")
            tags.Add($"signal={signalType}")
            Return tags
        End Function

    End Class

End Namespace
