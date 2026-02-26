Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.Auth
Imports TopStepTrader.Services.Backtest
Imports TopStepTrader.Services.Background
Imports TopStepTrader.Services.Feedback
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Risk
Imports TopStepTrader.Services.Signals
Imports TopStepTrader.Services.Trading

Namespace TopStepTrader.Services

    Public Module ServicesExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Sub AddApplicationServices(services As IServiceCollection)

            ' ── Repositories (Data layer, registered as Scoped by DataServiceExtensions)
            ' BarRepository, SignalRepository, OrderRepository registered by AddDataServices()
            ' BacktestRepository not yet registered there — add it here as Scoped
            services.AddScoped(Of BacktestRepository)()

            ' ── Auth
            services.AddSingleton(Of IAuthService, AuthService)()

            ' ── Account
            services.AddScoped(Of IAccountService, AccountService)()

            ' ── Market
            services.AddScoped(Of MarketDataService)()
            services.AddScoped(Of IMarketDataService)(Function(sp) sp.GetRequiredService(Of MarketDataService)())
            services.AddScoped(Of BarIngestionService)()

            ' ── Signals
            services.AddScoped(Of ISignalService, SignalService)()

            ' ── Risk guard — Singleton so halt state persists for the session lifetime
            services.AddSingleton(Of IRiskGuardService, RiskGuardService)()

            ' ── Trading
            services.AddScoped(Of IOrderService, OrderService)()
            services.AddScoped(Of AutoExecutionService)()
            services.AddScoped(Of TrendAnalysisService)()

            ' ── Backtest
            services.AddScoped(Of IBacktestService, BacktestEngine)()

            ' ── ML Feedback Loop (Phase 7)
            ' OutcomeTracker is Scoped — OutcomeMonitorWorker creates a scope per tick
            services.AddScoped(Of OutcomeTracker)()
            ' ModelTrainingService is Scoped — has Scoped repo dependencies
            services.AddScoped(Of IModelTrainingService, ModelTrainingService)()

            ' ── Background workers
            services.AddSingleton(Of BarIngestionWorker)()
            services.AddSingleton(Of TokenRefreshWorker)()
            services.AddSingleton(Of SignalGenerationWorker)()
            ' OutcomeMonitorWorker uses IServiceScopeFactory — safe as Singleton
            services.AddSingleton(Of OutcomeMonitorWorker)()

            services.AddHostedService(Function(sp) sp.GetRequiredService(Of TokenRefreshWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of BarIngestionWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of SignalGenerationWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of OutcomeMonitorWorker)())

        End Sub

    End Module

End Namespace
