Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data
Imports TopStepTrader.API
Imports TopStepTrader.ML
Imports TopStepTrader.ML.Models
Imports TopStepTrader.ML.Prediction
Imports TopStepTrader.Services
Imports TopStepTrader.UI.ViewModels
Imports TopStepTrader.UI.Views

Namespace TopStepTrader.UI.Infrastructure

    ''' <summary>
    ''' Composition root — builds and configures the DI host for the WPF application.
    ''' All layer registrations happen here so the UI project is the sole composition root.
    ''' </summary>
    Public Module AppBootstrapper

        Public Function BuildHost() As IHost
            Return Host.CreateDefaultBuilder() _
                .ConfigureAppConfiguration(
                    Sub(ctx, cfg)
                        cfg.SetBasePath(AppContext.BaseDirectory)
                        cfg.AddJsonFile("appsettings.json", optional:=False, reloadOnChange:=False)
                    End Sub) _
                .ConfigureServices(
                    Sub(ctx, services)

                        ' ── Settings ──────────────────────────────────────────────
                        services.Configure(Of ApiSettings)(ctx.Configuration.GetSection("Api"))
                        services.Configure(Of RiskSettings)(ctx.Configuration.GetSection("Risk"))
                        services.Configure(Of TradingSettings)(ctx.Configuration.GetSection("Trading"))
                        services.Configure(Of MLSettings)(ctx.Configuration.GetSection("ML"))

                        ' ── Data, API, ML, Services layers ────────────────────────
                        services.AddDataServices(ctx.Configuration)
                        services.AddApiServices()
                        services.AddMLServices()
                        services.AddApplicationServices()

                        ' ── WPF-specific: use IServiceScopeFactory in ViewModelLocator
                        '    so that Scoped EF Core services are resolved in a proper scope.

                        ' ViewModelLocator — Singleton (creates per-view scopes internally)
                        services.AddSingleton(Of ViewModelLocator)()

                        ' ViewModels — Transient; resolved from per-view scope inside Locator
                        services.AddTransient(Of DashboardViewModel)()
                        services.AddTransient(Of MarketDataViewModel)()
                        services.AddTransient(Of SignalsViewModel)()
                        services.AddTransient(Of OrderBookViewModel)()
                        services.AddTransient(Of RiskGuardViewModel)()
                        services.AddTransient(Of BacktestViewModel)()
                        services.AddTransient(Of SettingsViewModel)()

                        ' Views — Transient; resolved from per-view scope inside Locator
                        services.AddTransient(Of DashboardView)()
                        services.AddTransient(Of MarketDataView)()
                        services.AddTransient(Of SignalsView)()
                        services.AddTransient(Of OrderBookView)()
                        services.AddTransient(Of RiskGuardView)()
                        services.AddTransient(Of BacktestView)()
                        services.AddTransient(Of SettingsView)()

                        ' Main window — Singleton (one window per app session)
                        services.AddSingleton(Of MainWindow)()

                    End Sub) _
                .Build()
        End Function

        ''' <summary>
        ''' Call after host is built to initialise the ML model manager (loads model + starts watcher).
        ''' </summary>
        Public Sub InitialiseServices(host As IHost)
            Dim modelManager = host.Services.GetService(Of ModelManager)()
            modelManager?.Initialize()
        End Sub

    End Module

End Namespace
