Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Auth
Imports TopStepTrader.Services.Background
Imports TopStepTrader.Services.Debug
Imports TopStepTrader.Services.Diagnostics
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.Services.PostMortem
Imports TopStepTrader.Services.Scalper
Imports TopStepTrader.Services.SlipStream
Imports TopStepTrader.Services.Trades
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.Services.Training
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Services

    Public Module ServicesExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Sub AddApplicationServices(services As IServiceCollection)

            ' ── Repositories (Data layer, registered as Scoped by DataServiceExtensions)
            ' BarRepository, SignalRepository, OrderRepository registered by AddDataServices()
            services.AddScoped(Of SuperTrendPlusConfigRepository)()
            services.AddScoped(Of UltimateScalperConfigRepository)()  ' FEAT-64
            services.AddScoped(Of SlipStreamConfigRepository)()       ' FEAT-70

            ' ── FEAT-64: Ultimate Scalper. Config resolved once per scope from the
            '    repository so both the orchestrator and the signal detector share
            '    the same instance; the VM holds its own copy for UI editing.
            services.AddScoped(Of UltimateScalperConfig)(Function(sp)
                                                              Dim repo = sp.GetRequiredService(Of UltimateScalperConfigRepository)()
                                                              Return repo.LoadAsync().GetAwaiter().GetResult()
                                                          End Function)
            services.AddScoped(Of IUltimateScalperSignalDetector, UltimateScalperSignalDetector)()

            ' Trail engine is pure (no state of its own — state lives on ScalperTrailState).
            services.AddSingleton(Of IScalperTrailEngine, QuoteDrivenTrailEngine)()

            ' FEAT-69: Singleton — owns the per-symbol arming state machine + rolling rate
            ' counter. Subscribes to IOrderService.OrderFilled on construction; must outlive
            ' the orchestrator so arm-state survives tab navigation.
            services.AddSingleton(Of IScalperStopEntryManager, ScalperStopEntryManager)()

            ' Singleton: state (last-fired-bar dictionary, enabled flag, live position)
            ' must outlive any single UI scope so the strategy survives tab navigation.
            ' Registered as a hosted service below so the scan timer starts at app startup.
            services.AddSingleton(Of UltimateScalperOrchestrator)()
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of UltimateScalperOrchestrator)())

            ' ── FEAT-70: SlipStream trend-pullback strategy.
            services.AddScoped(Of SlipStreamConfig)(Function(sp)
                                                         Dim repo = sp.GetRequiredService(Of SlipStreamConfigRepository)()
                                                         Return repo.LoadAsync().GetAwaiter().GetResult()
                                                     End Function)
            services.AddScoped(Of ISlipStreamSignalDetector, SlipStreamSignalDetector)()
            services.AddSingleton(Of SlipStreamOrchestrator)()
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of SlipStreamOrchestrator)())

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
            ' FEAT-68 — Singleton memoization table for EnsureBarsAsync. Must outlive the
            ' per-scan scope rebuild so the orchestrator's 5 s tick loop doesn't hit SQLite
            ' on every call when bars are already known fresh.
            services.AddSingleton(Of BarEnsureMemoCache)()
            ' Bar download + caching used by the startup gap-fill flow
            services.AddScoped(Of IBarCollectionService, BarCollectionService)()
            ' Startup bar gap check + backfill for all favourite contracts (past 60 days)
            services.AddScoped(Of IStartupBarCheckService, StartupBarCheckService)()

            ' ARCH-06 — Singleton live price + P&L stream consumed by Scalper Test (and future views).
            services.AddSingleton(Of Core.Interfaces.ILivePnLService, LivePnLService)()

            ' ── Contract metadata (TopStepX — resolves via PXContractClient)
            services.AddScoped(Of IContractMetadataService, ContractMetadataService)()

            ' ── Trading
            ' TopStepX instrument catalog (singleton — 15-min TTL cache shared across all scopes)
            services.AddSingleton(Of TopStepXInstrumentCatalog)()

            services.AddSingleton(Of IOpenPositionsCache, OpenPositionsCache)()
            services.AddScoped(Of IOrderService, ProjectXOrderService)()
            services.AddScoped(Of TrendAnalysisService)()

            ' ── Exit signal engine (ARCH-15) — PDF strategy; reusable phased stop ladder
            services.AddSingleton(Of ExitSignalEngine)()

            ' ── FEAT-61: enriches TradeSetupSnapshot with Ichimoku/EMA/MACD/StochRSI/VIDYA/CMO/ΔVol
            '    columns the live SuperTrend+ strategy does not itself compute.
            services.AddSingleton(Of TradeSetupSnapshotEnricher)()

            ' ── ARCH-19: Strategy-agnostic entry execution pipeline (extracted from
            '    SuperTrendPlusViewModel.FireEntryAsync). Singleton so the AI-veto
            '    suppression dictionary outlives a single strategy ViewModel and is
            '    shared across strategies (e.g. SuperTrend+ and Break and Bounce).
            services.AddSingleton(Of IEntryExecutionService, EntryExecutionService)()

            ' ── ARCH-20: Strategy-agnostic position-management + exit-execution
            '    pipelines (extracted from SuperTrendPlusViewModel.HandleOpenPositionAsync
            '    and ReleaseSlotAsync). Singleton so the alternating-tick snapshot-skip
            '    set is shared across strategies that join the same instrument.
            services.AddSingleton(Of IPositionManagementService, PositionManagementService)()
            services.AddSingleton(Of IExitExecutionService, ExitExecutionService)()

            ' ── Diagnostic logger (one instance per engine — Transient matches engine lifetime)
            services.AddTransient(Of DiagnosticLogger)()

            ' ── AI-Assisted Trading
            services.AddScoped(Of StrategyParserService)()
            services.AddScoped(Of IClaudeReviewService, ClaudeReviewService)()

            ' ── Trade history recording (Singleton — called from Transient VMs)
            services.AddSingleton(Of ITradeRecordService, TradeRecordService)()

            ' ── BUG-93 F2: broker-fill audit floor — writes a LiveTradeRecord for every
            '    broker fill the strategy layer does not attribute. Hosted so the
            '    subscription is established at app start and cleared at shutdown.
            services.AddSingleton(Of BrokerFillTradeLogger)()
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of BrokerFillTradeLogger)())

            ' ── FEAT-60: closes the ML feedback loop — pulls resolved real-world
            '    outcomes from TradeOutcomeRepository, aligns them to entry bars,
            '    and feeds them into SignalModelTrainer.TrainAndSave as labels.
            services.AddTransient(Of TrainingOrchestrator)()

            ' ── FEAT-51: Post-mortem launcher (wraps python script invocation; mockable)
            services.AddSingleton(Of IPostMortemLauncher, PostMortemLauncher)()

            ' ── Debug trade capture (FEAT-39) — Singleton; background Channel consumer
            services.AddSingleton(Of DebugTradeDbContext)()
            services.AddSingleton(Of IDebugTradeCaptureService, DebugTradeCaptureService)()
            ' ── Debug trade reconciliation (FEAT-56 / BUG-83)
            services.AddSingleton(Of IDebugReconciliationOrderClient, PXOrderClientReconciliationAdapter)()
            services.AddSingleton(Of IDebugTradeReconciliationService, DebugTradeReconciliationService)()

            ' ── Background workers
            services.AddSingleton(Of BarIngestionWorker)()
            services.AddSingleton(Of TokenRefreshWorker)()
            ' BUG-86 F2: reconciles stuck "open" LiveTradeRecords every 5 minutes.
            services.AddSingleton(Of TradeReconciliationWorker)()
            ' BUG-90 F1: fourth release channel — 60 s broker-authoritative sweep over
            ' currently-occupied UI slots. Sinks register via OpenSlotReleaseSinkRegistry.
            services.AddSingleton(Of Core.Trading.OpenSlotReleaseSinkRegistry)()
            services.AddSingleton(Of BrokerSlotSweepWorker)()

            services.AddHostedService(Function(sp) sp.GetRequiredService(Of TokenRefreshWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of BarIngestionWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of TradeReconciliationWorker)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of BrokerSlotSweepWorker)())

        End Sub

    End Module

End Namespace
