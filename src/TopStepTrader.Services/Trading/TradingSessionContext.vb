Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Singleton implementation of ITradingSessionContext.
    ''' The Dashboard sets SelectedAccount once after loading accounts;
    ''' all other ViewModels and services read ActiveBroker / SelectedAccount from here.
    ''' </summary>
    Public Class TradingSessionContext
        Implements ITradingSessionContext

        Private _selectedAccount As Account
        Private _autoExecutionEnabled As Boolean = False

        Public ReadOnly Property SelectedAccount As Account Implements ITradingSessionContext.SelectedAccount
            Get
                Return _selectedAccount
            End Get
        End Property

        Public ReadOnly Property ActiveBroker As BrokerType Implements ITradingSessionContext.ActiveBroker
            Get
                Return If(_selectedAccount IsNot Nothing, _selectedAccount.Broker, BrokerType.TopStepX)
            End Get
        End Property

        Public Sub SelectAccount(account As Account) Implements ITradingSessionContext.SelectAccount
            _selectedAccount = account
            RaiseEvent AccountChanged(Me, account)
        End Sub

        Public Event AccountChanged As EventHandler(Of Account) Implements ITradingSessionContext.AccountChanged

        Public ReadOnly Property AutoExecutionEnabled As Boolean Implements ITradingSessionContext.AutoExecutionEnabled
            Get
                Return _autoExecutionEnabled
            End Get
        End Property

        Public Sub SetAutoExecution(enabled As Boolean) Implements ITradingSessionContext.SetAutoExecution
            _autoExecutionEnabled = enabled
            RaiseEvent AutoExecutionChanged(Me, EventArgs.Empty)
        End Sub

        Public Event AutoExecutionChanged As EventHandler Implements ITradingSessionContext.AutoExecutionChanged

    End Class

End Namespace
