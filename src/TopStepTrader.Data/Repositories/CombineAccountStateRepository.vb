Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>FEAT-74: SQLite-backed store for the trailing max-drawdown state.</summary>
    Public Class CombineAccountStateRepository
        Implements ICombineAccountStateRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        Public Async Function GetOrCreateAsync(accountId As Long, startingBalance As Decimal) As Task(Of CombineAccountStateEntity) _
            Implements ICombineAccountStateRepository.GetOrCreateAsync
            Dim existing = Await _db.CombineAccountStates _
                .AsNoTracking() _
                .FirstOrDefaultAsync(Function(s) s.AccountId = accountId)
            If existing IsNot Nothing Then Return existing

            Dim fresh As New CombineAccountStateEntity With {
                .AccountId = accountId,
                .StartingBalance = startingBalance,
                .PeakEquity = startingBalance,
                .MllFloor = startingBalance,
                .CumulativeRealisedPnl = 0D,
                .TradingDayKey = String.Empty,
                .UpdatedAtUtc = DateTimeOffset.UtcNow
            }
            _db.CombineAccountStates.Add(fresh)
            Await _db.SaveChangesAsync()
            _db.Entry(fresh).State = EntityState.Detached
            Return fresh
        End Function

        Public Async Function UpsertAsync(state As CombineAccountStateEntity) As Task _
            Implements ICombineAccountStateRepository.UpsertAsync
            state.UpdatedAtUtc = DateTimeOffset.UtcNow
            Dim tracked = Await _db.CombineAccountStates _
                .FirstOrDefaultAsync(Function(s) s.AccountId = state.AccountId)
            If tracked Is Nothing Then
                _db.CombineAccountStates.Add(state)
            Else
                tracked.StartingBalance = state.StartingBalance
                tracked.PeakEquity = state.PeakEquity
                tracked.MllFloor = state.MllFloor
                tracked.CumulativeRealisedPnl = state.CumulativeRealisedPnl
                tracked.TradingDayKey = state.TradingDayKey
                tracked.UpdatedAtUtc = state.UpdatedAtUtc
            End If
            Await _db.SaveChangesAsync()
        End Function

    End Class

End Namespace
