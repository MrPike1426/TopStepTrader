Imports System.IO
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports Microsoft.ML

Namespace TopStepTrader.ML.Prediction

    Public Class MLSettings
        Public Property ModelDirectory As String = "Models"
        Public Property CurrentModelFile As String = "signal_v1.zip"
        Public Property RetrainingMinSamples As Integer = 1000
        Public Property FeatureLookbackBars As Integer = 50
    End Class

    ''' <summary>
    ''' Manages model lifecycle: loading, versioning, hot-reload.
    ''' Watches the Models directory for new .zip files and reloads automatically.
    ''' </summary>
    Public Class ModelManager
        Implements IDisposable

        Private ReadOnly _settings As MLSettings
        Private ReadOnly _predictor As SignalPredictor
        Private ReadOnly _logger As ILogger(Of ModelManager)
        Private ReadOnly _mlContext As New MLContext(seed:=42)
        Private _watcher As FileSystemWatcher
        Private _disposed As Boolean = False

        Public ReadOnly Property MLContext As MLContext
            Get
                Return _mlContext
            End Get
        End Property

        Public Sub New(options As IOptions(Of MLSettings),
                       predictor As SignalPredictor,
                       logger As ILogger(Of ModelManager))
            _settings = options.Value
            _predictor = predictor
            _logger = logger
        End Sub

        ''' <summary>Load the current model and start watching for updates.</summary>
        Public Sub Initialize()
            Dim modelPath = GetCurrentModelPath()

            If File.Exists(modelPath) Then
                _predictor.LoadModel(modelPath, _mlContext)
            Else
                _logger.LogWarning(
                    "Model file not found at {Path}. Run training first via Backtest → Train Model.",
                    modelPath)
            End If

            StartFileWatcher()
        End Sub

        ''' <summary>Returns full path to the currently configured model file.</summary>
        Public Function GetCurrentModelPath() As String
            Return Path.Combine(AppContext.BaseDirectory, _settings.ModelDirectory, _settings.CurrentModelFile)
        End Function

        ''' <summary>Returns the next version filename (e.g. signal_v1.zip → signal_v2.zip).</summary>
        Public Function GetNextModelPath() As String
            Dim dir = Path.Combine(AppContext.BaseDirectory, _settings.ModelDirectory)
            Directory.CreateDirectory(dir)
            Dim existing = Directory.GetFiles(dir, "signal_v*.zip")
            Dim maxVersion = 0
            For Each f In existing
                Dim name = Path.GetFileNameWithoutExtension(f)
                Dim numStr = name.Replace("signal_v", "")
                Dim num As Integer
                If Integer.TryParse(numStr, num) AndAlso num > maxVersion Then
                    maxVersion = num
                End If
            Next
            Return Path.Combine(dir, $"signal_v{maxVersion + 1}.zip")
        End Function

        Private Sub StartFileWatcher()
            Dim dir = Path.Combine(AppContext.BaseDirectory, _settings.ModelDirectory)
            If Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If

            _watcher = New FileSystemWatcher(dir, "*.zip") With {
                .NotifyFilter = NotifyFilters.LastWrite Or NotifyFilters.FileName,
                .EnableRaisingEvents = True
            }

            AddHandler _watcher.Created,
                Sub(sender, e)
                    _logger.LogInformation("New model detected: {File}. Reloading...", e.Name)
                    Threading.Thread.Sleep(500) ' Brief wait for write to complete
                    Try
                        _predictor.LoadModel(e.FullPath, _mlContext)
                    Catch ex As Exception
                        _logger.LogError(ex, "Failed to load new model {File}", e.Name)
                    End Try
                End Sub

            _logger.LogInformation("Watching {Dir} for new model files", dir)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _watcher?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
