Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Interface ITradeLifespanRepository
        Function SaveAsync(entity As TradeLifespanRecordEntity) As Task(Of Integer)
        Function UpdateAsync(entity As TradeLifespanRecordEntity) As Task
        Function GetByTradeOutcomeIdAsync(tradeOutcomeId As Long) As Task(Of TradeLifespanRecordEntity)
    End Interface

    ''' <summary>
    ''' Persists and retrieves trade lifespan records (MAE/MFE, duration, trail counts).
    ''' </summary>
    Public Class TradeLifespanRepository
        Implements ITradeLifespanRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        Public Async Function SaveAsync(entity As TradeLifespanRecordEntity) As Task(Of Integer) Implements ITradeLifespanRepository.SaveAsync
            _db.TradeLifespanRecords.Add(entity)
            Await _db.SaveChangesAsync()
            Return entity.Id
        End Function

        Public Async Function UpdateAsync(entity As TradeLifespanRecordEntity) As Task Implements ITradeLifespanRepository.UpdateAsync
            entity.UpdatedAt = DateTimeOffset.UtcNow
            Dim tracked = _db.ChangeTracker.Entries(Of TradeLifespanRecordEntity)() _
                             .FirstOrDefault(Function(e) e.Entity.Id = entity.Id)
            If tracked IsNot Nothing Then
                tracked.CurrentValues.SetValues(entity)
            Else
                _db.TradeLifespanRecords.Update(entity)
            End If
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function GetByTradeOutcomeIdAsync(tradeOutcomeId As Long) As Task(Of TradeLifespanRecordEntity) Implements ITradeLifespanRepository.GetByTradeOutcomeIdAsync
            Return Await _db.TradeLifespanRecords _
                            .AsNoTracking() _
                            .FirstOrDefaultAsync(Function(r) r.TradeOutcomeId = tradeOutcomeId)
        End Function

    End Class

End Namespace
