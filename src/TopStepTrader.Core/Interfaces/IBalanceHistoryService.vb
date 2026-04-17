Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    Public Interface IBalanceHistoryService
        ''' <summary>
        ''' Records the current balance for an account on a specific date.
        ''' </summary>
        Function RecordBalanceAsync(accountId As Long, accountName As String, balance As Decimal, recordedDate As DateTime) As Task(Of Boolean)

        ''' <summary>
        ''' Gets the last N days of balance history for an account.
        ''' </summary>
        Function GetRecentBalanceHistoryAsync(accountId As Long, daysBack As Integer) As Task(Of IEnumerable(Of BalanceHistory))

        ''' <summary>
        ''' Gets balance history for all accounts for the past N days.
        ''' </summary>
        Function GetAllAccountsRecentHistoryAsync(daysBack As Integer) As Task(Of Dictionary(Of Long, List(Of BalanceHistory)))
    End Interface

End Namespace
