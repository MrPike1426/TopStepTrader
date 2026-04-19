Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.API.Http
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.API.RateLimiting

Namespace TopStepTrader.API

    Public Module ApiServiceExtensions

        ''' <summary>Register all broker API services (TopStepX only) into the DI container.</summary>
        <System.Runtime.CompilerServices.Extension>
        Public Sub AddApiServices(services As IServiceCollection)

            ' Shared rate limiter — Singleton so all clients share the same window
            services.AddSingleton(Of RateLimiter)()

            ' ── TopStepX / ProjectX ───────────────────────────────────────────────
            ' JWT token manager — Singleton: one token shared across all PX clients
            services.AddSingleton(Of ProjectXTokenManager)()

            ' Named HttpClient for ProjectX (60s timeout for history endpoint)
            services.AddHttpClient("ProjectX",
                Sub(client)
                    client.Timeout = TimeSpan.FromSeconds(60)
                    client.DefaultRequestHeaders.Add("Accept", "application/json")
                End Sub)

            ' ProjectX HTTP clients — Transient (token managed by Singleton token manager)
            services.AddTransient(Of PXAccountClient)()
            services.AddTransient(Of PXContractClient)()
            services.AddTransient(Of PXOrderClient)()
            services.AddTransient(Of PXHistoryClient)()

' ── SignalR hubs — Singleton (hold persistent connections) ────────────
            services.AddSingleton(Of MarketHubClient)()
            services.AddSingleton(Of UserHubClient)()

        End Sub

    End Module

End Namespace
