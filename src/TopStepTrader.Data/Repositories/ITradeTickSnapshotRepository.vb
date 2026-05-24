Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>
    ''' FEAT-59: persistence of per-closed-bar tick snapshots for live trade replay.
    ''' </summary>
    Public Interface ITradeTickSnapshotRepository

        Function AddAsync(entity As TradeTickSnapshotEntity) As Task(Of Long)

        Function AddBatchAsync(entities As IList(Of TradeTickSnapshotEntity)) As Task

        Function GetByTradeRecordAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeTickSnapshotEntity))

    End Interface

End Namespace
