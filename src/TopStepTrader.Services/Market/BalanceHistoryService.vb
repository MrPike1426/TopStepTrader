Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Service for tracking and retrieving account balance history.
    ''' </summary>
    Public Class BalanceHistoryService
        Implements IBalanceHistoryService

        Private ReadOnly _dbContext As AppDbContext
        Private ReadOnly _logger As ILogger(Of BalanceHistoryService)

        Public Sub New(dbContext As AppDbContext, logger As ILogger(Of BalanceHistoryService))
            _dbContext = dbContext
            _logger = logger
        End Sub

        Public Async Function RecordBalanceAsync(accountId As Long, accountName As String, balance As Decimal, recordedDate As DateTime) As Task(Of Boolean) _
            Implements IBalanceHistoryService.RecordBalanceAsync
            Try
                ' Check if we already have a record for this account on this date
                Dim dateOnly = recordedDate.Date
                Dim existing = Await _dbContext.BalanceHistory _
                    .FirstOrDefaultAsync(Function(b) b.AccountId = accountId AndAlso b.RecordedDate.Date = dateOnly)

                If existing IsNot Nothing Then
                    ' Update existing record
                    existing.Balance = balance
                    existing.AccountName = accountName
                Else
                    ' Create new record
                    Dim entity = New BalanceHistoryEntity With {
                        .AccountId = accountId,
                        .AccountName = accountName,
                        .Balance = balance,
                        .RecordedDate = dateOnly
                    }
                    Await _dbContext.BalanceHistory.AddAsync(entity)
                End If

                Await _dbContext.SaveChangesAsync()
                Return True

            Catch ex As Exception
                _logger.LogError(ex, "Error recording balance for account {AccountId}", accountId)
                Return False
            End Try
        End Function

        Public Async Function GetRecentBalanceHistoryAsync(accountId As Long, daysBack As Integer) As Task(Of IEnumerable(Of BalanceHistory)) _
            Implements IBalanceHistoryService.GetRecentBalanceHistoryAsync
            Try
                Dim cutoffDate = DateTime.UtcNow.AddDays(-daysBack).Date
                Dim query = _dbContext.BalanceHistory _
                    .Where(Function(b) b.AccountId = accountId AndAlso b.RecordedDate >= cutoffDate) _
                    .OrderByDescending(Function(b) b.RecordedDate)

                Dim history = Await query.ToListAsync()

                Return history.Select(Function(b) New BalanceHistory With {
                    .AccountId = b.AccountId,
                    .AccountName = b.AccountName,
                    .Balance = b.Balance,
                    .RecordedDate = b.RecordedDate
                })

            Catch ex As Exception
                _logger.LogError(ex, "Error retrieving balance history for account {AccountId}", accountId)
                Return Enumerable.Empty(Of BalanceHistory)()
            End Try
        End Function

        Public Async Function GetAllAccountsRecentHistoryAsync(daysBack As Integer) As Task(Of Dictionary(Of Long, List(Of BalanceHistory))) _
            Implements IBalanceHistoryService.GetAllAccountsRecentHistoryAsync
            Try
                Dim cutoffDate = DateTime.UtcNow.AddDays(-daysBack).Date
                Dim query = _dbContext.BalanceHistory _
                    .Where(Function(b) b.RecordedDate >= cutoffDate) _
                    .OrderByDescending(Function(b) b.RecordedDate)

                Dim history = Await query.ToListAsync()

                ' Group by account
                Dim grouped = history _
                    .GroupBy(Function(b) b.AccountId) _
                    .ToDictionary(
                        Function(g) g.Key,
                        Function(g) g.Select(Function(b) New BalanceHistory With {
                            .AccountId = b.AccountId,
                            .AccountName = b.AccountName,
                            .Balance = b.Balance,
                            .RecordedDate = b.RecordedDate
                        }).ToList())

                Return grouped

            Catch ex As Exception
                _logger.LogError(ex, "Error retrieving all balance history")
                Return New Dictionary(Of Long, List(Of BalanceHistory))()
            End Try
        End Function

    End Class

End Namespace
