Namespace TopStepTrader.Core.Settings

    Public Class ApiSettings
        ''' <summary>Legacy REST API base URL (unused; TopStepX uses ProjectXSettings).</summary>
        Public Property BaseUrl As String = "https://api.topstepx.com"

        ''' <summary>x-api-key — legacy API key (unused).</summary>
        Public Property ApiKey As String = String.Empty

        ''' <summary>x-user-key — legacy demo user key (unused).</summary>
        Public Property UserKey As String = String.Empty

        ''' <summary>x-user-key — legacy live user key (unused).</summary>
        Public Property LiveUserKey As String = String.Empty

        ''' <summary>"demo" or "live" — active trading environment. Defaults to "demo".</summary>
        Public Property AccountMode As String = "demo"

        Public Property HttpTimeoutSeconds As Integer = 30
    End Class

End Namespace
