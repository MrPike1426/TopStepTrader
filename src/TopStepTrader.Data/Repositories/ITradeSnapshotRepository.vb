Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>
    ''' Persists per-trade Order/Position/Trade snapshots captured at trade close
    ''' (FEAT-50). All reads/writes are scoped to a single LiveTradeRecord.
    ''' </summary>
    Public Interface ITradeSnapshotRepository

        Function AddOrdersAsync(rows As IEnumerable(Of TradeOrderSnapshotEntity)) As Task
        Function AddPositionsAsync(rows As IEnumerable(Of TradePositionSnapshotEntity)) As Task
        Function AddFillsAsync(rows As IEnumerable(Of TradeFillSnapshotEntity)) As Task

        Function GetOrdersAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeOrderSnapshotEntity))
        Function GetPositionsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradePositionSnapshotEntity))
        Function GetFillsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeFillSnapshotEntity))

        ''' <summary>True when at least one snapshot row of any kind exists for the record.</summary>
        Function HasAnySnapshotsAsync(liveTradeRecordId As Long) As Task(Of Boolean)

    End Interface

End Namespace
