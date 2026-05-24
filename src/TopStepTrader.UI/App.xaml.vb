Imports System.IO
Imports System.Windows
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Training
Imports TopStepTrader.UI.Infrastructure
Imports TopStepTrader.UI.ViewModels
Imports TopStepTrader.UI.Views

Namespace TopStepTrader.UI

    Partial Public Class App
        Inherits Application

        Private _host As IHost

        ''' <summary>Service provider exposed for controls that cannot use constructor DI (e.g. UserControls).</summary>
        Friend Shared Services As IServiceProvider

        ''' <summary>Command-line args forwarded from Program.Main (FEAT-50: --backfill-snapshots).</summary>
        Public Property StartupArgs As String() = Array.Empty(Of String)()

        Protected Overrides Async Sub OnStartup(e As StartupEventArgs)
            MyBase.OnStartup(e)

            _host = AppBootstrapper.BuildHost()
            Services = _host.Services
            Await _host.StartAsync()

            ' ── Start SignalR hub connections ──────────────────────────────
            ' UserHub  : order fills, position updates (needed for bracket placement)
            ' MarketHub: live quotes (needed for P&L and price-based logic)
            Try
                Await _host.Services.GetRequiredService(Of UserHubClient)().StartAsync()
            Catch ex As Exception
                System.Diagnostics.Debug.WriteLine($"UserHub startup warning: {ex.Message}")
            End Try
            Try
                Await _host.Services.GetRequiredService(Of MarketHubClient)().StartAsync()
            Catch ex As Exception
                System.Diagnostics.Debug.WriteLine($"MarketHub startup warning: {ex.Message}")
            End Try

            ' Initialise ML model manager (loads model file + starts file watcher)
            AppBootstrapper.InitialiseServices(_host)

            ' Resolve MainWindow first so Application.MainWindow is correctly set before
            ' the bar-check progress window is displayed, preventing premature app shutdown.
            Dim mainWindow = _host.Services.GetRequiredService(Of MainWindow)()
            Application.Current.MainWindow = mainWindow

            ' ── Contract cache initialisation ─────────────────────────────────
            ' Resolves live contract IDs from ProjectX API (once per day; SQLite-cached).
            ' Must complete before any strategy scan or bar fetch begins.
            Dim contractService = _host.Services.GetRequiredService(Of IContractResolutionService)()
            Await contractService.InitialiseAsync()
            FavouriteContracts.SetResolver(contractService)
            If contractService.FailedSymbols.Count > 0 Then
                Dim failed = String.Join(", ", contractService.FailedSymbols)
                MessageBox.Show(
                    $"Contract resolution failed for: {failed}{Environment.NewLine}{Environment.NewLine}" &
                    "Trading is disabled for these instruments until the next successful resolution." &
                    $"{Environment.NewLine}Check your network connection and ProjectX API status.",
                    "Contract Cache Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning)
            End If

            ' ── Startup bar gap check ──────────────────────────────────────────
            Await CheckAndPromptMissingBarsAsync()

            ' FEAT-50: optional snapshot backfill triggered by --backfill-snapshots arg.
            If StartupArgs IsNot Nothing AndAlso
               StartupArgs.Any(Function(a) String.Equals(a, "--backfill-snapshots", StringComparison.OrdinalIgnoreCase)) Then
                Try
                    Dim tradeRecordService = _host.Services.GetRequiredService(Of ITradeRecordService)()
                    Dim session = _host.Services.GetRequiredService(Of ITradingSessionContext)()
                    Dim accountId As Long = If(session?.SelectedAccount?.Id, 0L)
                    If accountId <> 0 Then
                        Await tradeRecordService.BackfillSnapshotsAsync(accountId)
                    End If
                Catch ex As Exception
                    System.Diagnostics.Debug.WriteLine($"BackfillSnapshots startup error: {ex.Message}")
                End Try
            End If

            ' BUG-92: optional ExitPrice/PnL backfill triggered by --backfill-exit-prices arg.
            ' Rewrites every closed record's ExitPrice/PnL from the broker's closing-fill
            ' ExecutePrice. Safe to re-run — already-reconciled records (ExitOrderId != 0)
            ' are skipped automatically.
            If StartupArgs IsNot Nothing AndAlso
               StartupArgs.Any(Function(a) String.Equals(a, "--backfill-exit-prices", StringComparison.OrdinalIgnoreCase)) Then
                Try
                    Dim tradeRecordService = _host.Services.GetRequiredService(Of ITradeRecordService)()
                    Dim session = _host.Services.GetRequiredService(Of ITradingSessionContext)()
                    Dim accountId As Long = If(session?.SelectedAccount?.Id, 0L)
                    If accountId <> 0 Then
                        Await tradeRecordService.BackfillExitPricesAsync(accountId)
                    End If
                Catch ex As Exception
                    System.Diagnostics.Debug.WriteLine($"BackfillExitPrices startup error: {ex.Message}")
                End Try
            End If

            ' FEAT-60: optional model retrain triggered by --retrain-model[=<contractId>] arg.
            ' Exits the process directly when the arg is present — does not return to the WPF loop.
            Dim retrainArg = If(StartupArgs Is Nothing,
                                Nothing,
                                StartupArgs.FirstOrDefault(Function(a) a IsNot Nothing AndAlso
                                    a.StartsWith("--retrain-model", StringComparison.OrdinalIgnoreCase)))
            If retrainArg IsNot Nothing Then
                Await RunRetrainModelCliAsync(retrainArg)
                Return
            End If

            mainWindow.Show()
        End Sub

        ''' <summary>
        ''' FEAT-60 CLI: resolves TrainingOrchestrator from DI, runs against the supplied
        ''' (or default) contract, prints the metrics line to stdout, and exits with 0/1.
        ''' Format of <paramref name="arg"/>: <c>--retrain-model</c> or <c>--retrain-model=&lt;contractId&gt;</c>.
        ''' </summary>
        Private Async Function RunRetrainModelCliAsync(arg As String) As Task
            Try
                Dim explicitContract As String = Nothing
                Dim eqIdx = arg.IndexOf("="c)
                If eqIdx >= 0 AndAlso eqIdx < arg.Length - 1 Then
                    explicitContract = arg.Substring(eqIdx + 1).Trim().Trim(""""c)
                End If

                Dim contractId As String = explicitContract
                If String.IsNullOrWhiteSpace(contractId) Then
                    Dim fav = FavouriteContracts.GetDefaults().FirstOrDefault()
                    If fav Is Nothing Then
                        Console.Error.WriteLine("Train failed: no favourite contracts configured")
                        Environment.Exit(1)
                        Return
                    End If
                    Dim resolved = FavouriteContracts.TryGetBySymbolResolved(fav.Name)
                    contractId = If(resolved IsNot Nothing, resolved.PxContractId, fav.PxContractId)
                End If

                Const Timeframe As String = "15min"
                Dim fromUtc = DateTimeOffset.UtcNow.AddDays(-90)
                Dim diagnosticsRoot = DebugTradeDbContext.ResolveDiagnosticsFolder()
                Dim modelsDir = Path.Combine(diagnosticsRoot, "models")
                Directory.CreateDirectory(modelsDir)
                Dim stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                Dim outputPath = Path.Combine(modelsDir,
                    $"signal-model-{contractId}-{Timeframe}-{stamp}.zip")

                Dim orchestrator = _host.Services.GetRequiredService(Of TrainingOrchestrator)()
                Dim metrics = Await orchestrator.RunAsync(contractId, Timeframe, fromUtc, outputPath)
                Console.Out.WriteLine(
                    $"Trained: {metrics.TrainingSamples} samples, AUC={metrics.AUC:F3}, " &
                    $"Accuracy={metrics.Accuracy:P1} → {outputPath}")
                Environment.Exit(0)
            Catch ex As Exception
                Console.Error.WriteLine($"Train failed: {ex.Message}")
                Environment.Exit(1)
            End Try
        End Function

        Protected Overrides Async Sub OnExit(e As ExitEventArgs)
            If _host IsNot Nothing Then
                Await _host.StopAsync(TimeSpan.FromSeconds(5))
                _host.Dispose()
            End If
            MyBase.OnExit(e)
        End Sub

        ''' <summary>
        ''' Checks for missing bar data across all favourite contracts over the past 60 days.
        ''' If gaps are found, shows a progress window listing every contract/timeframe slot
        ''' and lets the user choose to download or skip before the main window opens.
        ''' </summary>
        Private Async Function CheckAndPromptMissingBarsAsync() As Task
            Try
                Using scope = _host.Services.CreateScope()
                    Dim checkService = scope.ServiceProvider.GetRequiredService(Of IStartupBarCheckService)()

                    Dim missing = Await checkService.CheckMissingBarsAsync()
                    If missing.Count = 0 Then Return

                    ' Build the per-item download delegate so the ViewModel stays
                    ' independent of the service layer.
                    Dim downloadDelegate =
                        Async Function(item As TopStepTrader.Services.Market.StartupBarCheckResult,
                                       prog As IProgress(Of String),
                                       ct As System.Threading.CancellationToken) As Task
                            Dim startDate = DateTime.UtcNow.AddDays(-60).Date
                            Dim endDate = DateTime.UtcNow.Date
                            Await checkService.BackfillAsync(
                                New List(Of TopStepTrader.Services.Market.StartupBarCheckResult) From {item},
                                prog, ct)
                        End Function

                    Dim vm = New BarDownloadProgressViewModel(missing, downloadDelegate)
                    Dim progressWindow = New BarDownloadProgressWindow(vm)
                    progressWindow.Show()

                    ' Await until the user clicks Close (after download) or Skip.
                    Await vm.CompletionTask

                    progressWindow.Close()
                End Using
            Catch ex As Exception
                System.Diagnostics.Debug.WriteLine($"StartupBarCheck error: {ex.Message}")
            End Try
        End Function

    End Class

End Namespace
