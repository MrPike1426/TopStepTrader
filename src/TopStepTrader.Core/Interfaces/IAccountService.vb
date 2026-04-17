Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    Public Interface IAccountService
        Function GetActiveAccountsAsync() As Task(Of IEnumerable(Of Account))
        Function GetAccountAsync(accountId As Long) As Task(Of Account)
    End Interface

End Namespace
