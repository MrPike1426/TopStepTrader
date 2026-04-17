Imports System.IO
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Data

    Public Module DataServiceExtensions

        ''' <summary>Register EF Core DbContext (SQLite) and all repositories into the DI container.</summary>
        <System.Runtime.CompilerServices.Extension>
        Public Sub AddDataServices(services As IServiceCollection, configuration As IConfiguration)

            ' Resolve DB path — if the connection string is a bare filename, place it
            ' next to the running executable so it is always in the same folder as the app.
            Dim raw = configuration.GetConnectionString("DefaultConnection") ' e.g. "TopStepTrader.db"
            Dim dbPath As String
            If raw IsNot Nothing AndAlso Not raw.StartsWith("Data Source", StringComparison.OrdinalIgnoreCase) Then
                ' Bare filename — make it absolute relative to the exe directory
                dbPath = $"Data Source={Path.Combine(AppContext.BaseDirectory, raw)}"
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

        End Sub

    End Module

End Namespace
