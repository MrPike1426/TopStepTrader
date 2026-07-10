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
            ' FEAT-73: force-flatten sweep used by hard combine verdicts.
            services.TryAddSingleton(Of IPositionFlattener, PositionFlattener)()
            services.TryAddSingleton(Of DailyLossGuardService)()
            services.TryAddSingleton(Of IDailyLossGuard)(Function(sp) sp.GetRequiredService(Of DailyLossGuardService)())
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of DailyLossGuardService)())
        End Sub

    End Module

End Namespace
