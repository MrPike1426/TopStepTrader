Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>Loads and saves API keys from/to local storage.</summary>
    Public Interface IApiKeyStore
        Function Load() As ApiKeySettings
        Sub Save(settings As ApiKeySettings)
    End Interface

End Namespace
