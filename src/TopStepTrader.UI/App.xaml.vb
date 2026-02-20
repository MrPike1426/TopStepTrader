Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports System.Windows

Namespace TopStepTrader.UI

    Partial Public Class App
        Inherits Application

        Private _host As IHost

        Protected Overrides Async Sub OnStartup(e As StartupEventArgs)
            MyBase.OnStartup(e)

            _host = AppBootstrapper.BuildHost()
            Await _host.StartAsync()

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
