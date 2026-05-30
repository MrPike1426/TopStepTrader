Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.DependencyInjection.Extensions
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services.Risk

    ''' <summary>
    ''' FEAT-71: DI registration helpers for the daily-loss guard.
    ''' </summary>
    Public Module DailyLossGuardServiceExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Sub AddDailyLossGuard(services As IServiceCollection)
            services.TryAddSingleton(Of DailyLossGuardService)()
            services.TryAddSingleton(Of IDailyLossGuard)(Function(sp) sp.GetRequiredService(Of DailyLossGuardService)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of DailyLossGuardService)())
        End Sub

    End Module

End Namespace
