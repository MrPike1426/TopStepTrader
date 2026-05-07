Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class TradeSnapshotRepository
        Implements ITradeSnapshotRepository

        Private ReadOnly _db As TradeHistoryDbContext

        Public Sub New(db As TradeHistoryDbContext)
            _db = db
        End Sub

        Public Async Function AddOrdersAsync(rows As IEnumerable(Of TradeOrderSnapshotEntity)) As Task _
            Implements ITradeSnapshotRepository.AddOrdersAsync
            If rows Is Nothing Then Return
            Dim list = rows.ToList()
            If list.Count = 0 Then Return
            _db.TradeOrderSnapshots.AddRange(list)
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function AddPositionsAsync(rows As IEnumerable(Of TradePositionSnapshotEntity)) As Task _
            Implements ITradeSnapshotRepository.AddPositionsAsync
            If rows Is Nothing Then Return
            Dim list = rows.ToList()
            If list.Count = 0 Then Return
            _db.TradePositionSnapshots.AddRange(list)
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function AddFillsAsync(rows As IEnumerable(Of TradeFillSnapshotEntity)) As Task _
            Implements ITradeSnapshotRepository.AddFillsAsync
            If rows Is Nothing Then Return
            Dim list = rows.ToList()
            If list.Count = 0 Then Return
            _db.TradeFillSnapshots.AddRange(list)
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function GetOrdersAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeOrderSnapshotEntity)) _
            Implements ITradeSnapshotRepository.GetOrdersAsync
            Return Await _db.TradeOrderSnapshots _
                .Where(Function(r) r.LiveTradeRecordId = liveTradeRecordId) _
                .OrderBy(Function(r) r.CreatedAt) _
                .ToListAsync()
        End Function

        Public Async Function GetPositionsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradePositionSnapshotEntity)) _
            Implements ITradeSnapshotRepository.GetPositionsAsync
            Return Await _db.TradePositionSnapshots _
                .Where(Function(r) r.LiveTradeRecordId = liveTradeRecordId) _
                .OrderBy(Function(r) r.OpenedAt) _
                .ToListAsync()
        End Function

        Public Async Function GetFillsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeFillSnapshotEntity)) _
            Implements ITradeSnapshotRepository.GetFillsAsync
            Return Await _db.TradeFillSnapshots _
                .Where(Function(r) r.LiveTradeRecordId = liveTradeRecordId) _
                .OrderBy(Function(r) r.Timestamp) _
                .ToListAsync()
        End Function

        Public Async Function HasAnySnapshotsAsync(liveTradeRecordId As Long) As Task(Of Boolean) _
            Implements ITradeSnapshotRepository.HasAnySnapshotsAsync
            Dim hasOrders = Await _db.TradeOrderSnapshots.AnyAsync(Function(r) r.LiveTradeRecordId = liveTradeRecordId)
            If hasOrders Then Return True
            Dim hasPositions = Await _db.TradePositionSnapshots.AnyAsync(Function(r) r.LiveTradeRecordId = liveTradeRecordId)
            If hasPositions Then Return True
            Return Await _db.TradeFillSnapshots.AnyAsync(Function(r) r.LiveTradeRecordId = liveTradeRecordId)
        End Function

    End Class

End Namespace
