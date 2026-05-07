Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Interface ITradeStopAdjustmentRepository

        Function AddAsync(entity As TradeStopAdjustmentEntity) As Task(Of Long)

        Function GetByTradeRecordAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeStopAdjustmentEntity))

    End Interface

End Namespace
