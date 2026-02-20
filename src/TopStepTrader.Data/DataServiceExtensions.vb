Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Data

    Public Module DataServiceExtensions

        ''' <summary>Register EF Core DbContext and all repositories into the DI container.</summary>
        <System.Runtime.CompilerServices.Extension>
        Public Sub AddDataServices(services As IServiceCollection, configuration As IConfiguration)

            services.AddDbContext(Of AppDbContext)(
                Sub(opts)
                    opts.UseSqlServer(configuration.GetConnectionString("DefaultConnection"),
                        Sub(sqlOpts)
                            sqlOpts.EnableRetryOnFailure(maxRetryCount:=3,
                                                         maxRetryDelay:=TimeSpan.FromSeconds(5),
                                                         errorNumbersToAdd:=Nothing)
                        End Sub)
                    opts.EnableSensitiveDataLogging(False)
                End Sub)

            services.AddScoped(Of BarRepository)()
            services.AddScoped(Of SignalRepository)()
            services.AddScoped(Of OrderRepository)()

        End Sub

    End Module

End Namespace
