Namespace TopStepTrader.Core.Settings

    Public Class ApiSettings
        Public Property RestBaseUrl As String = "https://api.topstepx.com"
        Public Property UserHubUrl As String = "https://rtc.topstepx.com/hubs/user"
        Public Property MarketHubUrl As String = "https://rtc.topstepx.com/hubs/market"
        Public Property UserName As String = String.Empty
        Public Property ApiKey As String = String.Empty
        Public Property TokenRefreshMinutesBeforeExpiry As Integer = 5
        Public Property HttpTimeoutSeconds As Integer = 30
    End Class

End Namespace
