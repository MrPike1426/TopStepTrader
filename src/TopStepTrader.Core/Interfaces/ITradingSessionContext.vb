Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Singleton session context carrying the user's chosen trading account.
    ''' Set on the Dashboard; read by all other tabs, services, and engines so
    ''' platform selection propagates everywhere without each consumer reading
    ''' the keystore independently.
    ''' </summary>
    Public Interface ITradingSessionContext
        ''' <summary>The account the user selected on the Dashboard. Nothing until an account is loaded.</summary>
        ReadOnly Property SelectedAccount As Account

        ''' <summary>Broker derived from SelectedAccount. Falls back to eToro when no account is selected.</summary>
        ReadOnly Property ActiveBroker As BrokerType

        ''' <summary>Update the globally selected account. Raises AccountChanged.</summary>
        Sub SelectAccount(account As Account)

        ''' <summary>Raised whenever SelectedAccount changes (fired on the calling thread).</summary>
        Event AccountChanged As EventHandler(Of Account)
    End Interface

End Namespace
