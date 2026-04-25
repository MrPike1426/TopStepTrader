Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Interface ITradeSetupSnapshotRepository
        Function SaveAsync(entity As TradeSetupSnapshotEntity) As Task(Of Long)
        Function GetByTradeOutcomeIdAsync(tradeOutcomeId As Long) As Task(Of TradeSetupSnapshotEntity)
    End Interface

    ''' <summary>
    ''' Persists and retrieves trade setup snapshots (indicator state at signal time).
    ''' </summary>
    Public Class TradeSetupSnapshotRepository
        Implements ITradeSetupSnapshotRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        Public Async Function SaveAsync(entity As TradeSetupSnapshotEntity) As Task(Of Long) Implements ITradeSetupSnapshotRepository.SaveAsync
            _db.TradeSetupSnapshots.Add(entity)
            Await _db.SaveChangesAsync()
            Return entity.Id
        End Function

        Public Async Function GetByTradeOutcomeIdAsync(tradeOutcomeId As Long) As Task(Of TradeSetupSnapshotEntity) Implements ITradeSetupSnapshotRepository.GetByTradeOutcomeIdAsync
            Return Await _db.TradeSetupSnapshots _
                            .AsNoTracking() _
                            .FirstOrDefaultAsync(Function(s) s.TradeOutcomeId = tradeOutcomeId)
        End Function

    End Class

End Namespace
