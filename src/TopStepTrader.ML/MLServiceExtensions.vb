Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.ML.Features
Imports TopStepTrader.ML.Prediction
Imports TopStepTrader.ML.Training

Namespace TopStepTrader.ML

    Public Module MLServiceExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Sub AddMLServices(services As IServiceCollection)

            ' Feature extraction — Singleton (stateless, thread-safe)
            services.AddSingleton(Of BarFeatureExtractor)()

            ' Predictor — Singleton (holds loaded model, uses internal lock)
            services.AddSingleton(Of SignalPredictor)()

            ' Model manager — Singleton (owns FileSystemWatcher lifetime)
            services.AddSingleton(Of ModelManager)()

            ' Trainer — Transient (stateless, created on demand for training runs)
            services.AddTransient(Of SignalModelTrainer)()

        End Sub

    End Module

End Namespace
