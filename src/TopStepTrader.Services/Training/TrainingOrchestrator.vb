Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Training
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Training

    ''' <summary>
    ''' FEAT-60 — closes the consumer side of the ML feedback loop.
    ''' Loads resolved real-world outcomes from <see cref="TradeOutcomeRepository"/>,
    ''' aligns each one to its entry bar by timestamp, and feeds the resulting
    ''' override dictionary into <see cref="SignalModelTrainer.TrainAndSave"/> so the
    ''' synthetic look-ahead label is replaced by the actual P&amp;L outcome wherever
    ''' a recorded result exists.
    ''' </summary>
    ''' <remarks>
    ''' Ticket called for placement in TopStepTrader.ML; that would create a circular
    ''' project reference (ML depends on Core only; this orchestrator needs both
    ''' IBarIngestionService and TradeOutcomeRepository).
    ''' </remarks>
    Public Class TrainingOrchestrator

        Private ReadOnly _trainer As SignalModelTrainer
        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _barService As IBarIngestionService
        Private ReadOnly _logger As ILogger(Of TrainingOrchestrator)

        Public Sub New(trainer As SignalModelTrainer,
                       scopeFactory As IServiceScopeFactory,
                       barService As IBarIngestionService,
                       logger As ILogger(Of TrainingOrchestrator))
            _trainer = trainer
            _scopeFactory = scopeFactory
            _barService = barService
            _logger = logger
        End Sub

        Public Async Function RunAsync(contractId As String,
                                       timeframe As String,
                                       fromUtc As DateTimeOffset,
                                       modelOutputPath As String) As Task(Of ModelMetrics)

            Dim tfMinutes As Integer = TimeframeToMinutes(timeframe)
            Dim tfEnum As BarTimeframe = TimeframeToEnum(timeframe)

            ' 1. Resolved outcomes from the requested window
            Dim resolved As List(Of TradeOutcomeEntity)
            Using scope = _scopeFactory.CreateScope()
                Dim repo = scope.ServiceProvider.GetRequiredService(Of TradeOutcomeRepository)()
                resolved = Await repo.GetResolvedOutcomesAsync(fromUtc)
            End Using

            ' 2. Filter to outcomes matching the requested contract (case-insensitive).
            '    TradeOutcomes.ContractId does not carry the leading "/" prefix that
            '    LiveTradeRecords.Symbol uses; the caller's contractId is the canonical form.
            Dim contractOutcomes = resolved _
                .Where(Function(o) String.Equals(o.ContractId, contractId, StringComparison.OrdinalIgnoreCase)) _
                .ToList()

            ' 3. Load the bar history.  Cover [fromUtc, UtcNow] for the timeframe with
            '    100 bars of slack so feature extraction has the warm-up window it needs.
            Dim nowUtc = DateTimeOffset.UtcNow
            Dim spanMinutes = Math.Max(0R, (nowUtc - fromUtc).TotalMinutes)
            Dim count As Integer = CInt(Math.Ceiling(spanMinutes / tfMinutes)) + 100
            Dim bars = Await _barService.GetLiveBarsAsync(contractId, tfEnum, count)
            If bars Is Nothing Then bars = New List(Of MarketBar)()

            ' 4. Build the override dictionary.  Each outcome's entry bar is the most
            '    recent bar at-or-before EntryTime (entries fire intra-bar).
            Dim outcomeLabels As New Dictionary(Of DateTimeOffset, Boolean)()
            Dim applied As Integer = 0
            Dim skippedNoBar As Integer = 0
            For Each oc In contractOutcomes
                If Not oc.IsWinner.HasValue Then
                    skippedNoBar += 1
                    Continue For
                End If
                Dim entryBar = bars.LastOrDefault(Function(b) b.Timestamp <= oc.EntryTime)
                If entryBar Is Nothing Then
                    skippedNoBar += 1
                    _logger.LogDebug(
                        "Outcome {Id} skipped — EntryTime {Entry} predates available bar history.",
                        oc.Id, oc.EntryTime)
                    Continue For
                End If
                outcomeLabels(entryBar.Timestamp) = oc.IsWinner.Value
                applied += 1
            Next

            _logger.LogInformation(
                "TrainingOrchestrator: contract={Contract} tf={Tf} bars={Bars} resolved={Resolved} applied={Applied} skipped={Skipped}",
                contractId, timeframe, bars.Count, contractOutcomes.Count, applied, skippedNoBar)

            ' 5. Train.  Trainer handles "insufficient samples" by throwing — propagate.
            Dim metrics = _trainer.TrainAndSave(allBars:=bars,
                                                outputPath:=modelOutputPath,
                                                lookAheadBars:=5,
                                                outcomeLabels:=outcomeLabels)

            _logger.LogInformation(
                "Trained: {Samples} samples, AUC={Auc:F3}, Accuracy={Acc:P1}, {Overrides} real-outcome overrides ({Skipped} skipped) → {Path}",
                metrics.TrainingSamples, metrics.AUC, metrics.Accuracy, applied, skippedNoBar, modelOutputPath)

            Return metrics
        End Function

        Private Shared Function TimeframeToMinutes(tf As String) As Integer
            Select Case tf
                Case "5min" : Return 5
                Case "1hr" : Return 60
                Case Else : Return 15
            End Select
        End Function

        Private Shared Function TimeframeToEnum(tf As String) As BarTimeframe
            Select Case tf
                Case "5min" : Return BarTimeframe.FiveMinute
                Case "1hr" : Return BarTimeframe.OneHour
                Case Else : Return BarTimeframe.FifteenMinute
            End Select
        End Function

    End Class

End Namespace
