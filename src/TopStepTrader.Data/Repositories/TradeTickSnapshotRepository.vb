Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class TradeTickSnapshotRepository
        Implements ITradeTickSnapshotRepository

        Private ReadOnly _db As TradeHistoryDbContext

        Public Sub New(db As TradeHistoryDbContext)
            _db = db
        End Sub

        Public Async Function AddAsync(entity As TradeTickSnapshotEntity) As Task(Of Long) _
            Implements ITradeTickSnapshotRepository.AddAsync
            _db.TradeTickSnapshots.Add(entity)
            Await _db.SaveChangesAsync()
            Return entity.Id
        End Function

        Public Async Function AddBatchAsync(entities As IList(Of TradeTickSnapshotEntity)) As Task _
            Implements ITradeTickSnapshotRepository.AddBatchAsync
            If entities Is Nothing OrElse entities.Count = 0 Then Return
            _db.TradeTickSnapshots.AddRange(entities)
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function GetByTradeRecordAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeTickSnapshotEntity)) _
            Implements ITradeTickSnapshotRepository.GetByTradeRecordAsync
            ' EF Core SQLite cannot translate ORDER BY on DateTimeOffset (UAT-BUG-003) —
            ' fetch filtered rows then sort in-memory.
            Dim rows = Await _db.TradeTickSnapshots _
                .Where(Function(r) r.LiveTradeRecordId = liveTradeRecordId) _
                .ToListAsync()
            Return rows.OrderBy(Function(r) r.BarTimestamp).ToList()
        End Function

    End Class

End Namespace
