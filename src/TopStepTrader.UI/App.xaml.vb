Imports System.Windows
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Services.Market
Imports TopStepTrader.UI.Infrastructure
Imports TopStepTrader.UI.ViewModels
Imports TopStepTrader.UI.Views

Namespace TopStepTrader.UI

    Partial Public Class App
        Inherits Application

        Private _host As IHost

        ''' <summary>Service provider exposed for controls that cannot use constructor DI (e.g. UserControls).</summary>
        Friend Shared Services As IServiceProvider

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

            mainWindow.Show()
        End Sub

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
