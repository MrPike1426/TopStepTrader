Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-72: persisted store for the adaptive watchlist toggle / size / refresh
    ''' cadence and the pinned + blacklisted root symbols. Loaded once at app start
    ''' (JSON in LocalApplicationData) and re-saved on user edits via Settings UI.
    ''' </summary>
    Public Interface IOpportunityScorePreferences
        ''' <summary>Returns the live in-memory copy. Callers mutate via <see cref="Save"/>.</summary>
        Function GetSettings() As OpportunityScoreSettings

        ''' <summary>Persists the supplied settings and replaces the in-memory copy.</summary>
        Sub Save(settings As OpportunityScoreSettings)

        ''' <summary>Raised after a successful <see cref="Save"/>. Subscribers
        ''' (e.g. AdaptiveWatchlistService) react by refreshing.</summary>
        Event Changed As EventHandler
    End Interface

End Namespace
