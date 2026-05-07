Imports System.IO
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Data

    Public Module DataServiceExtensions

        ''' <summary>Register EF Core DbContext (SQLite) and all repositories into the DI container.</summary>
        <System.Runtime.CompilerServices.Extension>
        Public Sub AddDataServices(services As IServiceCollection, configuration As IConfiguration)

            ' Resolve DB path — if the connection string is a bare filename, place it
            ' in the TopStepTrader_Diagnostics folder at solution root.
            Dim raw = configuration.GetConnectionString("DefaultConnection") ' e.g. "TopStepTrader.db"
            Dim dbPath As String
            If raw IsNot Nothing AndAlso Not raw.StartsWith("Data Source", StringComparison.OrdinalIgnoreCase) Then
                ' Bare filename — make it absolute relative to the diagnostics folder
                Dim diagnosticsFolder = DebugTradeDbContext.ResolveDiagnosticsFolder()
                dbPath = $"Data Source={Path.Combine(diagnosticsFolder, raw)}"
            Else
                dbPath = raw  ' already a full connection string
            End If

            services.AddDbContext(Of AppDbContext)(
                Sub(opts)
                    opts.UseSqlite(dbPath)
                    opts.EnableSensitiveDataLogging(False)
                End Sub)

            services.AddScoped(Of BarRepository)()
            services.AddScoped(Of SignalRepository)()
            services.AddScoped(Of OrderRepository)()
            services.AddScoped(Of TradeOutcomeRepository)()
            services.AddScoped(Of ITradeSetupSnapshotRepository, TradeSetupSnapshotRepository)()
            services.AddScoped(Of ITradeLifespanRepository, TradeLifespanRepository)()
            services.AddScoped(Of IAdaptiveParametersRepository, AdaptiveParametersRepository)()

            ' ── Trade history (separate TradeHistory.db) ──────────────────────
            Dim diagnosticsFolderPath = DebugTradeDbContext.ResolveDiagnosticsFolder()
            Directory.CreateDirectory(diagnosticsFolderPath)
            Dim tradeDbPath = $"Data Source={Path.Combine(diagnosticsFolderPath, "TradeHistory.db")}"
            services.AddDbContext(Of TradeHistoryDbContext)(
                Sub(opts)
                    opts.UseSqlite(tradeDbPath)
                    opts.EnableSensitiveDataLogging(False)
                End Sub)
            services.AddScoped(Of ILiveTradeRecordRepository, LiveTradeRecordRepository)()
            services.AddScoped(Of ITradeStopAdjustmentRepository, TradeStopAdjustmentRepository)()
            services.AddScoped(Of ITradeSnapshotRepository, TradeSnapshotRepository)()

        End Sub

    End Module

End Namespace
