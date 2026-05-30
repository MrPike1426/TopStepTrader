Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-72: tunables for <c>InstrumentOpportunityScorer</c> and the adaptive
    ''' watchlist refresh loop. Bound to Settings UI sliders.
    ''' </summary>
    Public Class OpportunityScoreSettings
        ''' <summary>Master enable. When False, strategies fall back to <see cref="Trading.FavouriteContracts.GetDefaults"/>.</summary>
        Public Property AdaptiveWatchlistEnabled As Boolean = False

        ''' <summary>Maximum number of contracts in the live watchlist (3-10).</summary>
        Public Property AdaptiveWatchlistMaxSize As Integer = 5

        ''' <summary>Background refresh cadence in minutes (15-240).</summary>
        Public Property AdaptiveWatchlistRefreshMinutes As Integer = 60

        ''' <summary>
        ''' Hysteresis: once a contract enters the live watchlist it stays for at least
        ''' this many minutes even if a higher-score contract appears. Prevents churn.
        ''' </summary>
        Public Property AdaptiveWatchlistMinTenureMinutes As Integer = 120

        ''' <summary>Bars (15-min TF) fetched per contract for scoring.</summary>
        Public Property ScoreBarsCount As Integer = 60

        ''' <summary>ATR / ADX window. Standard Wilder length.</summary>
        Public Property IndicatorLength As Integer = 14

        ''' <summary>Pinned root symbols (always present in the live watchlist).</summary>
        Public Property PinnedRootSymbols As New List(Of String)()

        ''' <summary>Blacklisted root symbols (never present in the live watchlist).</summary>
        Public Property BlacklistedRootSymbols As New List(Of String)()
    End Class

End Namespace
