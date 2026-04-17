Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Connection settings for the TopStepX / ProjectX trading platform.
    ''' Credentials (username + API key) are stored in ApiKeySettings and read via IApiKeyStore.
    ''' </summary>
    Public Class ProjectXSettings
        ''' <summary>Base URL for all REST POST endpoints.</summary>
        Public Property RestBaseUrl As String = "https://api.topstepx.com"

        ''' <summary>SignalR user hub URL (orders, positions, trades).</summary>
        Public Property UserHubUrl As String = "https://rtc.topstepx.com/hubs/user"

        ''' <summary>SignalR market hub URL (quotes, trades, depth).</summary>
        Public Property MarketHubUrl As String = "https://rtc.topstepx.com/hubs/market"

        ''' <summary>Minutes before token expiry to proactively refresh. JWT tokens are valid for 24h.</summary>
        Public Property TokenRefreshMinutesBeforeExpiry As Integer = 30

        Public Property HttpTimeoutSeconds As Integer = 30

        ''' <summary>
        ''' Fixed stop-loss tick distance for all bracket orders (ClaudeTrader OCO template).
        ''' Overrides all ATR-based sizing. Clamped up to per-instrument minimums by
        ''' TopStepXInstrumentCatalog.ClampStopTicksAsync at order placement.
        ''' </summary>
        Public Property DefaultSlTicks As Integer = 50

        ''' <summary>
        ''' Fixed take-profit tick distance for all bracket orders (ClaudeTrader OCO template).
        ''' Set to match your saved TopStepX bracket preset.
        ''' </summary>
        Public Property DefaultTpTicks As Integer = 25
    End Class

End Namespace
