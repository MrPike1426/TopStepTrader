Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Auth
Imports TopStepTrader.Services.Background
Imports TopStepTrader.Services.Backtest
Imports TopStepTrader.Services.Debug
Imports TopStepTrader.Services.Diagnostics
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.Services.PostMortem
Imports TopStepTrader.Services.Trades
Imports TopStepTrader.Services.Trading

Namespace TopStepTrader.Services

    Public Module ServicesExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Sub AddApplicationServices(services As IServiceCollection)

            ' ── Repositories (Data layer, registered as Scoped by DataServiceExtensions)
            ' BarRepository, SignalRepository, OrderRepository registered by AddDataServices()
            ' BacktestRepository not yet registered there — add it here as Scoped
            services.AddScoped(Of BacktestRepository)()
            services.AddScoped(Of SuperTrendPlusConfigRepository)()

            ' ── API key store — Singleton: one file-backed store for the session lifetime
            services.AddSingleton(Of IApiKeyStore, ApiKeyStore)()

            ' ── Persona service — Singleton: global store for editable persona profiles
            '    Loads from SQLite on startup; falls back to appsettings.json Personas section.
            services.AddSingleton(Of IPersonaService, PersonaService)()

            ' ── Trading session context — Singleton: carries the user's chosen account
            '    Set by DashboardViewModel; read by engines, BrokerOrderService, and all VMs.
            services.AddSingleton(Of ITradingSessionContext, TradingSessionContext)()

            ' ── Auth (TopStepX — delegates to ProjectXTokenManager for JWT lifecycle)
            services.AddSingleton(Of IAuthService, ProjectXAuthService)()

            ' ── Account
            services.AddScoped(Of IAccountService, AccountService)()
            services.AddScoped(Of IBalanceHistoryService, BalanceHistoryService)()

            ' ── Market
            ' TopStepX — live trading bar source for all live views
            services.AddScoped(Of IBarIngestionService, TopStepXBarIngestionService)()
            ' TICKET-006: bar download + caching for the Backtest page
            services.AddScoped(Of IBarCollectionService, BarCollectionService)()
            ' Startup bar gap check + backfill for all favourite contracts (past 60 days)
            services.AddScoped(Of IStartupBarCheckService, StartupBarCheckService)()

            ' ── Contract metadata (TopStepX — resolves via PXContractClient)
            services.AddScoped(Of IContractMetadataService, ContractMetadataService)()

            ' ── Trading
            ' TopStepX instrument catalog (singleton — 15-min TTL cache shared across all scopes)
            services.AddSingleton(Of TopStepXInstrumentCatalog)()

            services.AddScoped(Of IOrderService, ProjectXOrderService)()
            services.AddScoped(Of TrendAnalysisService)()

            ' ── Scalper exit manager (QUAL-04) — reusable scalping TP/exit ladder
            services.AddSingleton(Of ScalperExitManager)()

            ' ── Diagnostic logger (one instance per engine — Transient matches engine lifetime)
            services.AddTransient(Of DiagnosticLogger)()

            ' ── AI-Assisted Trading
            services.AddScoped(Of StrategyParserService)()
            services.AddTransient(Of StrategyExecutionEngine)()
            ' CryptoJoe-specific engine: BUY-only + 100% confidence override.
            ' Wired exclusively through CryptoJoeViewModel — other pages remain on StrategyExecutionEngine.
            services.AddTransient(Of CryptoStrategyExecutionEngine)()
            services.AddScoped(Of IClaudeReviewService, ClaudeReviewService)()

            ' ── Backtest
            services.AddScoped(Of IBacktestService, BacktestEngine)()

            ' ── Trade history recording (Singleton — called from Transient VMs)
            services.AddSingleton(Of ITradeRecordService, TradeRecordService)()

            ' ── FEAT-51: Post-mortem launcher (wraps python script invocation; mockable)
            services.AddSingleton(Of IPostMortemLauncher, PostMortemLauncher)()

            ' ── Debug trade capture (FEAT-39) — Singleton; background Channel consumer
            services.AddSingleton(Of DebugTradeDbContext)()
            services.AddSingleton(Of IDebugTradeCaptureService, DebugTradeCaptureService)()

            ' ── Background workers
            services.AddSingleton(Of BarIngestionWorker)()
            services.AddSingleton(Of TokenRefreshWorker)()

            services.AddHostedService(Function(sp) sp.GetRequiredService(Of TokenRefreshWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of BarIngestionWorker)())

        End Sub

    End Module

End Namespace
