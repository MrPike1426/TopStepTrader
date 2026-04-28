Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' API key storage model.  4 named provider slots + 4 user-labelled future slots.
    ''' Serialised to %LOCALAPPDATA%\TopStepTrader\apikeys.json by ApiKeyStore.
    ''' </summary>
    Public Class ApiKeySettings
        ' ── Active broker ────────────────────────────────────────────────────────────
        ''' <summary>"topstepx" — which broker is currently active.</summary>
        Public Property ActiveBroker As String = "topstepx"

        ' ── Named providers — credential (non-secret) + API key (secret) ────────────
        Public Property LegacyKeyName As String = String.Empty       ' Display label for the key set
        ''' <summary>"demo" or "live" — selects which legacy provider trading environment to connect to.</summary>
        Public Property LegacyAccountMode As String = "demo"
        Public Property LegacyApiKey As String = String.Empty        ' x-api-key  (developer portal key — shared across accounts)
        Public Property LegacyUserKey As String = String.Empty       ' x-user-key for the Demo account
        Public Property LegacyLiveUserKey As String = String.Empty   ' x-user-key for the Live account
        Public Property TopStepXUsername As String = String.Empty   ' Account email address
        Public Property TopStepXApiKey As String = String.Empty
        Public Property ClaudeOrgId As String = String.Empty        ' Organisation / Workspace ID (optional)
        Public Property ClaudeApiKey As String = String.Empty
        Public Property BinanceApiKey As String = String.Empty      ' API Key (public half)
        Public Property BinanceSecretKey As String = String.Empty   ' Secret Key (private half)

        ' ── Future slots — editable label + username/email + API key ──────────────
        Public Property Future1Label As String = String.Empty
        Public Property Future1Username As String = String.Empty
        Public Property Future1Key As String = String.Empty
        Public Property Future2Label As String = String.Empty
        Public Property Future2Username As String = String.Empty
        Public Property Future2Key As String = String.Empty
        Public Property Future3Label As String = String.Empty
        Public Property Future3Username As String = String.Empty
        Public Property Future3Key As String = String.Empty
        Public Property Future4Label As String = String.Empty
        Public Property Future4Username As String = String.Empty
        Public Property Future4Key As String = String.Empty
    End Class

End Namespace
