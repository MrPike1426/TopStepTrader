Imports System.IO
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Serilog
Imports TopStepTrader.API
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.Views
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.ML
Imports TopStepTrader.ML.Prediction
Imports TopStepTrader.Services
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Services.Debug
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Infrastructure

    ''' <summary>
    ''' Composition root — builds and configures the DI host for the WPF application.
    ''' All layer registrations happen here so the UI project is the sole composition root.
    ''' </summary>
    Public Module AppBootstrapper

        Public Function BuildHost() As IHost
            ' ── Serilog rolling file logger ──────────────────────────────────────
            Dim logPath = Path.Combine(AppContext.BaseDirectory, "logs", "topstep-.log")
            Log.Logger = New LoggerConfiguration() _
                .MinimumLevel.Information() _
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning) _
                .Enrich.FromLogContext() _
                .WriteTo.File(logPath,
                              rollingInterval:=RollingInterval.Day,
                              retainedFileCountLimit:=7,
                              outputTemplate:="{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}") _
                .CreateLogger()

            Return Host.CreateDefaultBuilder() _
                .UseSerilog() _
                .ConfigureAppConfiguration(
                    Sub(ctx, cfg)
                        cfg.SetBasePath(AppContext.BaseDirectory)
                        cfg.AddJsonFile("appsettings.json", optional:=False, reloadOnChange:=False)
                        ' DAMO_DEMO.txt credential overlay removed — no longer needed.
                        ' TopStepX credentials are loaded from appsettings.json (ProjectX section) only.
                    End Sub) _
                .ConfigureServices(
                    Sub(ctx, services)

                        ' ── Settings ──────────────────────────────────────────────
                        services.Configure(Of ApiSettings)(ctx.Configuration.GetSection("Api"))
                        services.Configure(Of ProjectXSettings)(ctx.Configuration.GetSection("ProjectX"))
                        services.Configure(Of RiskSettings)(ctx.Configuration.GetSection("Risk"))
                        services.Configure(Of TradingSettings)(ctx.Configuration.GetSection("Trading"))
                        services.Configure(Of MLSettings)(ctx.Configuration.GetSection("ML"))
                        services.Configure(Of ClaudeSettings)(ctx.Configuration.GetSection("Claude"))
                        services.Configure(Of PersonasSettings)(ctx.Configuration.GetSection("Personas"))

                        ' ── Data, API, ML, Services layers ────────────────────────
                        services.AddDataServices(ctx.Configuration)
                        services.AddApiServices()
                        services.AddMLServices()
                        services.AddApplicationServices()

                        ' ── WPF-specific: use IServiceScopeFactory in ViewModelLocator
                        '    so that Scoped EF Core services are resolved in a proper scope.

                        ' ── User preferences (persisted to LocalApplicationData) ──────
                        services.AddSingleton(Of IUserPreferencesService, UserPreferencesService)()

                        ' ── Contract resolution cache (daily, SQLite-backed) ───────────
                        services.AddSingleton(Of IContractResolutionService, ContractResolutionService)()

                        ' ViewModelLocator
                        services.AddSingleton(Of ViewModelLocator)()

                        ' ViewModels — Transient; resolved from per-view scope inside Locator
                        services.AddTransient(Of DashboardViewModel)()
                        services.AddTransient(Of SettingsViewModel)()

                        services.AddTransient(Of ApiKeysViewModel)()
                        services.AddTransient(Of PersonaViewModel)()
                        ' Views
                        services.AddTransient(Of DashboardView)()
                        services.AddTransient(Of SettingsView)()

                        services.AddTransient(Of SuperTrendPlusView)()
                        services.AddTransient(Of SuperTrendPlusViewModel)()
                        services.AddTransient(Of ApiKeysView)()
                        services.AddTransient(Of PersonaView)()
                        services.AddTransient(Of DebugTradeViewerViewModel)()
                        services.AddTransient(Of DebugTradeViewerView)()

                        ' Main window
                        services.AddSingleton(Of MainWindow)()

                    End Sub) _
                .Build()
        End Function

        ''' <summary>
        ''' Call after host is built to:
        '''   1. Ensure the SQL Server database and all tables exist (EnsureCreated).
        '''   2. Initialise the ML model manager (loads model + starts FileSystemWatcher).
        ''' </summary>
        Public Sub InitialiseServices(host As IHost)
            ' ── Database bootstrap ──────────────────────────────────────────────
            ' EnsureCreated creates the SQLite .db file + all tables from the EF model
            ' the first time the app runs. On subsequent startups it is a no-op.
            ' The .db file lives next to the executable (resolved in DataServiceExtensions).
            Try
                Using scope = host.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                    db.Database.EnsureCreated()
                    db.EnsureSchemaCurrent()

                    ' Seed dummy balance history if empty
                    SeedBalanceHistory(db)
                End Using
            Catch ex As Exception
                System.Diagnostics.Trace.TraceError(
                    "Database initialisation failed: {0}", ex.Message)
            End Try

            Try
                Using scope = host.Services.CreateScope()
                    Dim tradeDb = scope.ServiceProvider.GetRequiredService(Of Data.TradeHistoryDbContext)()
                    tradeDb.Database.EnsureCreated()
                    tradeDb.EnsureSchemaCurrent()
                End Using
            Catch ex As Exception
                System.Diagnostics.Trace.TraceError(
                    "TradeHistory database initialisation failed: {0}", ex.Message)
            End Try

            ' ── Apply persisted user preferences ────────────────────────────────
            Try
                Dim userPrefs = host.Services.GetRequiredService(Of IUserPreferencesService)()
                Dim session = host.Services.GetRequiredService(Of ITradingSessionContext)()
                session.SetAutoExecution(userPrefs.AutoExecutionEnabled)
            Catch ex As Exception
                System.Diagnostics.Trace.TraceWarning("Could not apply user preferences: {0}", ex.Message)
            End Try

            ' ── Debug trade capture schema ───────────────────────────────────────
            Try
                Dim debugDb = host.Services.GetRequiredService(Of DebugTradeDbContext)()
                debugDb.EnsureSchemaAsync().GetAwaiter().GetResult()
            Catch ex As Exception
                System.Diagnostics.Trace.TraceWarning("Debug capture schema init failed: {0}", ex.Message)
            End Try

            ' ── ML model manager ────────────────────────────────────────────────
            Dim modelManager = host.Services.GetService(Of ModelManager)()
            modelManager?.Initialize()
        End Sub

        ''' <summary>
        ''' Seed dummy balance history for demo accounts if the table is empty.
        ''' Uses the current balance values repeated for the last 5 days.
        ''' </summary>
        Private Sub SeedBalanceHistory(db As AppDbContext)
            Try
                ' Check if BalanceHistory table exists and has data
                If db.BalanceHistory.Any() Then
                    Return
                End If
            Catch
                ' Table doesn't exist yet, that's fine - we'll create it below
            End Try

            Try
                ' Dummy accounts matching the UI display
                Dim dummyAccounts = New List(Of (Id As Long, Name As String, Balance As Decimal)) From {
                    (19181464L, "50KTC-V2-315185-88187480", 52239D),
                    (19182027L, "PRAC-V2-315185-26809886", 149439D)
                }

                ' Create balance history for last 5 days
                For Each account In dummyAccounts
                    For dayOffset = 5 To 1 Step -1
                        Dim recordDate = DateTime.UtcNow.AddDays(-dayOffset).Date
                        db.BalanceHistory.Add(New BalanceHistoryEntity With {
                            .AccountId = account.Id,
                            .AccountName = account.Name,
                            .Balance = account.Balance,
                            .RecordedDate = recordDate,
                            .CreatedAt = DateTime.UtcNow
                        })
                    Next
                Next

                db.SaveChanges()
            Catch ex As Exception
                ' Log but don't crash - the app can still run without balance history
                System.Diagnostics.Trace.TraceWarning(
                    "Warning: Could not seed balance history: {0}", ex.Message)
            End Try
        End Sub

    End Module

End Namespace
