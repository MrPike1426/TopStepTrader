Namespace TopStepTrader.Core.Settings

    Public Class ApiSettings
        ''' <summary>eToro public REST API base URL.</summary>
        Public Property BaseUrl As String = "https://public-api.etoro.com"

        ''' <summary>x-api-key — eToro Public API Key from the developer portal.</summary>
        Public Property ApiKey As String = String.Empty

        ''' <summary>x-user-key — eToro User Key for the Demo account.</summary>
        Public Property UserKey As String = String.Empty

        ''' <summary>x-user-key — eToro User Key for the Live account (optional).</summary>
        Public Property LiveUserKey As String = String.Empty

        ''' <summary>"demo" or "live" — active trading environment. Defaults to "demo".</summary>
        Public Property AccountMode As String = "demo"

        Public Property HttpTimeoutSeconds As Integer = 30
    End Class

End Namespace
