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

        ''' <summary>Broker derived from SelectedAccount. Falls back to TopStepX when no account is selected.</summary>
        ReadOnly Property ActiveBroker As BrokerType

        ''' <summary>Update the globally selected account. Raises AccountChanged.</summary>
        Sub SelectAccount(account As Account)

        ''' <summary>Raised whenever SelectedAccount changes (fired on the calling thread).</summary>
        Event AccountChanged As EventHandler(Of Account)

        ''' <summary>Whether autonomous order execution is enabled. False = practice accounts only.</summary>
        ReadOnly Property AutoExecutionEnabled As Boolean

        ''' <summary>Update the AutoExecutionEnabled flag. Raises AutoExecutionChanged.</summary>
        Sub SetAutoExecution(enabled As Boolean)

        ''' <summary>Raised when AutoExecutionEnabled changes so all ViewModels can refresh their account lists.</summary>
        Event AutoExecutionChanged As EventHandler
    End Interface

End Namespace
