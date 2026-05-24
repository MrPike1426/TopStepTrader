Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Features
Imports TopStepTrader.ML.Training
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Training
Imports Xunit

Namespace TopStepTrader.Tests.ML

    ''' <summary>
    ''' FEAT-60: end-to-end tests for the TrainingOrchestrator — verifies it loads
    ''' resolved outcomes from TradeOutcomes, aligns them to bar timestamps per the
    ''' "latest at-or-before EntryTime" rule, and produces a model .zip via
    ''' SignalModelTrainer.TrainAndSave.
    ''' </summary>
    Public Class TrainingOrchestratorTests
        Implements IDisposable

        Private ReadOnly _dbPath As String
        Private ReadOnly _provider As ServiceProvider
        Private ReadOnly _tempDir As String

        Private Const TestContract As String = "CON.F.US.MNQ.U26"
        Private Const OtherContract As String = "CON.F.US.OTHER.U26"
        Private Const SyntheticBars As Integer = 200
        Private Shared ReadOnly BarOriginUtc As DateTimeOffset =
            New DateTimeOffset(2026, 4, 1, 13, 30, 0, TimeSpan.Zero)
        Private Const TimeframeMinutes As Integer = 15

        Public Sub New()
            _dbPath = Path.Combine(Path.GetTempPath(), $"feat60_{Guid.NewGuid():N}.db")
            _tempDir = Path.Combine(Path.GetTempPath(), $"feat60out_{Guid.NewGuid():N}")
            Directory.CreateDirectory(_tempDir)
            Dim connStr As String = $"Data Source={_dbPath}"

            Dim services As New ServiceCollection()
            services.AddDbContext(Of AppDbContext)(Sub(opts) opts.UseSqlite(connStr))
            services.AddScoped(Of TradeOutcomeRepository)()
            _provider = services.BuildServiceProvider()

            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                db.Database.EnsureCreated()
            End Using
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _provider.Dispose()
                SqliteConnection.ClearAllPools()
                If File.Exists(_dbPath) Then File.Delete(_dbPath)
                If Directory.Exists(_tempDir) Then Directory.Delete(_tempDir, recursive:=True)
            Catch
            End Try
        End Sub

        ' ── Test 1: happy path — 5 resolved outcomes produce a .zip and 5 overrides
        <Fact>
        Public Async Function RunAsync_HappyPath_AppliesAllMatchingOutcomes() As Task
            Dim bars = BuildSyntheticBars()
            Dim outcomeBarIndexes = New Integer() {40, 70, 100, 130, 160}
            Dim winnerMask = New Boolean() {True, True, False, True, False}

            For i = 0 To outcomeBarIndexes.Length - 1
                Await SeedOutcomeAsync(TestContract,
                                       bars(outcomeBarIndexes(i)).Timestamp,
                                       winnerMask(i))
            Next

            Dim logger As New CapturingLogger(Of TrainingOrchestrator)()
            Dim outputPath = Path.Combine(_tempDir, "happy.zip")
            Dim metrics = Await BuildOrchestrator(bars, logger).RunAsync(
                TestContract, "15min", BarOriginUtc.AddMinutes(-5), outputPath)

            Assert.True(metrics.TrainingSamples > 0)
            Assert.True(File.Exists(outputPath), $"Model .zip not written to {outputPath}")
            Assert.Contains(logger.Messages,
                Function(m) m.Contains("applied=5") AndAlso m.Contains("skipped=0"))
        End Function

        ' ── Test 2: contract mismatch — overrides remain empty, trainer still runs
        <Fact>
        Public Async Function RunAsync_NoMatchingOutcomes_TrainerStillInvoked() As Task
            Dim bars = BuildSyntheticBars()
            For i = 0 To 2
                Await SeedOutcomeAsync(OtherContract,
                                       bars(50 + i * 20).Timestamp,
                                       isWinner:=True)
            Next

            Dim logger As New CapturingLogger(Of TrainingOrchestrator)()
            Dim outputPath = Path.Combine(_tempDir, "no-match.zip")
            Dim metrics = Await BuildOrchestrator(bars, logger).RunAsync(
                TestContract, "15min", BarOriginUtc.AddMinutes(-5), outputPath)

            Assert.True(metrics.TrainingSamples > 0)
            Assert.True(File.Exists(outputPath))
            Assert.Contains(logger.Messages,
                Function(m) m.Contains("applied=0") AndAlso m.Contains("skipped=0"))
        End Function

        ' ── Test 3: intra-bar entry — outcome at bar(50).Timestamp + 37s aligns to bar(50)
        <Fact>
        Public Async Function RunAsync_IntraBarEntry_AlignsToPriorBar() As Task
            Dim bars = BuildSyntheticBars()
            Dim intraBarEntry = bars(50).Timestamp.AddSeconds(37)
            Await SeedOutcomeAsync(TestContract, intraBarEntry, isWinner:=True)

            Dim trainerSpy As New CapturingTrainer()
            Dim logger As New CapturingLogger(Of TrainingOrchestrator)()
            Dim outputPath = Path.Combine(_tempDir, "intra-bar.zip")
            Await BuildOrchestrator(bars, logger, trainerSpy).RunAsync(
                TestContract, "15min", BarOriginUtc.AddMinutes(-5), outputPath)

            Assert.NotNull(trainerSpy.CapturedLabels)
            Assert.True(trainerSpy.CapturedLabels.ContainsKey(bars(50).Timestamp),
                $"Expected override at bars(50).Timestamp ({bars(50).Timestamp:o}); " &
                $"got keys [{String.Join(", ", trainerSpy.CapturedLabels.Keys)}]")
            Assert.False(trainerSpy.CapturedLabels.ContainsKey(bars(51).Timestamp),
                "Intra-bar entry must not align to the next bar")
            Assert.Contains(logger.Messages,
                Function(m) m.Contains("applied=1") AndAlso m.Contains("skipped=0"))
        End Function

        ' ── Test 4: outcome predates bar history — counted as skipped
        <Fact>
        Public Async Function RunAsync_OutcomeOlderThanBars_LoggedAsSkipped() As Task
            Dim bars = BuildSyntheticBars()
            Dim ancientEntry = bars(0).Timestamp.AddHours(-1)
            Await SeedOutcomeAsync(TestContract, ancientEntry, isWinner:=True)

            Dim trainerSpy As New CapturingTrainer()
            Dim logger As New CapturingLogger(Of TrainingOrchestrator)()
            Dim outputPath = Path.Combine(_tempDir, "old-outcome.zip")
            Await BuildOrchestrator(bars, logger, trainerSpy).RunAsync(
                TestContract, "15min", BarOriginUtc.AddHours(-2), outputPath)

            Assert.NotNull(trainerSpy.CapturedLabels)
            Assert.Empty(trainerSpy.CapturedLabels)
            Assert.Contains(logger.Messages,
                Function(m) m.Contains("applied=0") AndAlso m.Contains("skipped=1"))
        End Function

        ' ── Helpers ──────────────────────────────────────────────────────────

        Private Function BuildOrchestrator(bars As IList(Of MarketBar),
                                            logger As ILogger(Of TrainingOrchestrator),
                                            Optional trainer As SignalModelTrainer = Nothing) As TrainingOrchestrator
            Dim scopeFactory = _provider.GetRequiredService(Of IServiceScopeFactory)()
            Dim realTrainer = If(trainer,
                                  New SignalModelTrainer(New BarFeatureExtractor(),
                                                          NullLogger(Of SignalModelTrainer).Instance))
            Dim barSvc As New StubBarIngestionService(bars)
            Return New TrainingOrchestrator(realTrainer, scopeFactory, barSvc, logger)
        End Function

        Private Async Function SeedOutcomeAsync(contractId As String,
                                                entryTime As DateTimeOffset,
                                                isWinner As Boolean) As Task
            Using scope = _provider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                db.TradeOutcomes.Add(New TradeOutcomeEntity With {
                    .ContractId = contractId,
                    .Timeframe = TimeframeMinutes,
                    .SignalType = "Buy",
                    .SignalConfidence = 0.7F,
                    .ModelVersion = "test",
                    .EntryTime = entryTime,
                    .EntryPrice = 21000D,
                    .ExitTime = entryTime.AddMinutes(15),
                    .ExitPrice = 21015D,
                    .PnL = If(isWinner, 50D, -50D),
                    .IsWinner = isWinner,
                    .ExitReason = "test",
                    .IsOpen = False,
                    .CreatedAt = entryTime
                })
                Await db.SaveChangesAsync()
            End Using
        End Function

        ''' <summary>
        ''' Synthetic price walk with sufficient volatility for the FastTree to train.
        ''' Mixes a slow linear trend with a sine wave; volume varies as well to keep
        ''' the volume-ratio feature non-degenerate.
        ''' </summary>
        Private Shared Function BuildSyntheticBars() As IList(Of MarketBar)
            Dim list As New List(Of MarketBar)(SyntheticBars)
            Dim basePrice As Decimal = 21000D
            For i = 0 To SyntheticBars - 1
                Dim t = BarOriginUtc.AddMinutes(i * TimeframeMinutes)
                Dim sine = CDec(Math.Sin(i / 8.0) * 12.0)
                Dim trend = CDec(i * 0.5)
                Dim close = basePrice + trend + sine
                Dim open_ = close - CDec(Math.Cos(i / 5.0) * 2.5)
                Dim high = Math.Max(open_, close) + 3D
                Dim low = Math.Min(open_, close) - 3D
                Dim vol = 1000L + CLng(Math.Abs(Math.Sin(i / 4.0)) * 500.0)
                list.Add(New MarketBar With {
                    .ContractId = TestContract,
                    .Timestamp = t,
                    .Timeframe = BarTimeframe.FifteenMinute,
                    .Open = open_,
                    .High = high,
                    .Low = low,
                    .Close = close,
                    .Volume = vol
                })
            Next
            Return list
        End Function

        ' ── Stubs ────────────────────────────────────────────────────────────

        Private NotInheritable Class StubBarIngestionService
            Implements IBarIngestionService

            Private ReadOnly _bars As IList(Of MarketBar)

            Public Sub New(bars As IList(Of MarketBar))
                _bars = bars
            End Sub

            Public Function IngestAsync(contractId As String,
                                         timeframe As BarTimeframe,
                                         Optional barsToFetch As Integer = 500,
                                         Optional cancel As CancellationToken = Nothing) As Task(Of Integer) _
                Implements IBarIngestionService.IngestAsync
                Return Task.FromResult(0)
            End Function

            Public Function GetBarsForMLAsync(contractId As String,
                                               timeframe As BarTimeframe,
                                               Optional maxBars As Integer = 200,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetBarsForMLAsync
                Return Task.FromResult(_bars)
            End Function

            Public Function GetLatestPriceAsync(contractId As String,
                                                 Optional cancel As CancellationToken = Nothing) As Task(Of Decimal) _
                Implements IBarIngestionService.GetLatestPriceAsync
                Return Task.FromResult(0D)
            End Function

            Public Function GetLiveBarsAsync(contractId As String,
                                              timeframe As BarTimeframe,
                                              barCount As Integer,
                                              Optional cancel As CancellationToken = Nothing,
                                              Optional live As Boolean = False) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetLiveBarsAsync
                Return Task.FromResult(_bars)
            End Function
        End Class

        ''' <summary>
        ''' SignalModelTrainer subclass that captures the outcomeLabels dictionary
        ''' passed to TrainAndSave without actually fitting a model — used by the
        ''' intra-bar-alignment and outcome-too-old tests where the .zip is not
        ''' the interesting artefact.
        ''' </summary>
        Private NotInheritable Class CapturingTrainer
            Inherits SignalModelTrainer

            Public Property CapturedLabels As Dictionary(Of DateTimeOffset, Boolean)

            Public Sub New()
                MyBase.New(New BarFeatureExtractor(),
                           NullLogger(Of SignalModelTrainer).Instance)
            End Sub

            Public Overrides Function TrainAndSave(allBars As IList(Of MarketBar),
                                                    outputPath As String,
                                                    Optional lookAheadBars As Integer = 5,
                                                    Optional outcomeLabels As Dictionary(Of DateTimeOffset, Boolean) = Nothing) As ModelMetrics
                CapturedLabels = outcomeLabels
                Return New ModelMetrics With {
                    .Accuracy = 0,
                    .AUC = 0,
                    .F1Score = 0,
                    .TrainedAt = DateTimeOffset.UtcNow,
                    .TrainingSamples = 0,
                    .ModelVersion = "spy"
                }
            End Function
        End Class

        Private NotInheritable Class CapturingLogger(Of T)
            Implements ILogger(Of T)

            Public ReadOnly Messages As New List(Of String)()

            Public Function BeginScope(Of TState)(state As TState) As IDisposable _
                Implements ILogger.BeginScope
                Return NullScope.Instance
            End Function

            Public Function IsEnabled(logLevel As LogLevel) As Boolean _
                Implements ILogger.IsEnabled
                Return True
            End Function

            Public Sub Log(Of TState)(logLevel As LogLevel,
                                       eventId As EventId,
                                       state As TState,
                                       exception As Exception,
                                       formatter As Func(Of TState, Exception, String)) _
                Implements ILogger.Log
                If formatter IsNot Nothing Then
                    Messages.Add(formatter(state, exception))
                End If
            End Sub

            Private NotInheritable Class NullScope
                Implements IDisposable

                Public Shared ReadOnly Instance As New NullScope()
                Private Sub New() : End Sub
                Public Sub Dispose() Implements IDisposable.Dispose : End Sub
            End Class
        End Class

    End Class

End Namespace
