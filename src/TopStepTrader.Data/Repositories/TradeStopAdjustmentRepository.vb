Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class TradeStopAdjustmentRepository
        Implements ITradeStopAdjustmentRepository

        Private ReadOnly _db As TradeHistoryDbContext

        Public Sub New(db As TradeHistoryDbContext)
            _db = db
        End Sub

        Public Async Function AddAsync(entity As TradeStopAdjustmentEntity) As Task(Of Long) _
            Implements ITradeStopAdjustmentRepository.AddAsync
            _db.TradeStopAdjustments.Add(entity)
            Await _db.SaveChangesAsync()
            Return entity.Id
        End Function

        Public Async Function GetByTradeRecordAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeStopAdjustmentEntity)) _
            Implements ITradeStopAdjustmentRepository.GetByTradeRecordAsync
            Return Await _db.TradeStopAdjustments _
                .Where(Function(r) r.LiveTradeRecordId = liveTradeRecordId) _
                .OrderBy(Function(r) r.Timestamp) _
                .ToListAsync()
        End Function

    End Class

End Namespace
