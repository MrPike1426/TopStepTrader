Imports System.Windows
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.UI.Infrastructure

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

            Dim mainWindow = _host.Services.GetRequiredService(Of MainWindow)()
            mainWindow.Show()
        End Sub

        Protected Overrides Async Sub OnExit(e As ExitEventArgs)
            If _host IsNot Nothing Then
                Await _host.StopAsync(TimeSpan.FromSeconds(5))
                _host.Dispose()
            End If
            MyBase.OnExit(e)
        End Sub

    End Class

End Namespace
