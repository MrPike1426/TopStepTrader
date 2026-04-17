Namespace TopStepTrader.Core.Enums

    ''' <summary>
    ''' Identifies the active trading broker/platform.
    ''' Stored as a lowercase string ("etoro" / "topstepx") in ApiKeySettings.ActiveBroker.
    ''' </summary>
    Public Enum BrokerType
        ''' <summary>Not explicitly set — BrokerOrderService falls back to ITradingSessionContext.ActiveBroker.</summary>
        None = 0
        eToro = 1
        TopStepX = 2
        Binance = 3
    End Enum

End Namespace
