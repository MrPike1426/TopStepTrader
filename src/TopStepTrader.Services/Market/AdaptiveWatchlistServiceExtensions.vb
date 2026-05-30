Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.DependencyInjection.Extensions
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' FEAT-72: DI registration helpers for the adaptive watchlist pipeline
    ''' (preferences store, scorer, watchlist service + hosted refresh).
    ''' </summary>
    Public Module AdaptiveWatchlistServiceExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Sub AddAdaptiveWatchlist(services As IServiceCollection)
            services.TryAddSingleton(Of IOpportunityScorePreferences, OpportunityScorePreferencesService)()
            services.TryAddSingleton(Of InstrumentOpportunityScorer)()
            services.TryAddSingleton(Of AdaptiveWatchlistService)()
            services.AddHostedService(Function(sp) sp.GetRequiredService(Of AdaptiveWatchlistService)())
        End Sub

    End Module

End Namespace
