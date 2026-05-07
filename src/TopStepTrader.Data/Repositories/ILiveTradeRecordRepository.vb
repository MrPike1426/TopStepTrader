Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Interface ILiveTradeRecordRepository

        Function AddAsync(entity As LiveTradeRecordEntity) As Task(Of Long)

        Function CloseAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                            pnl As Decimal, exitReason As String) As Task

        Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task

        Function ResolveTopStepXTradeIdAsync(id As Long, topStepXTradeId As Long) As Task

        Function GetOpenRecordsAsync() As Task(Of IList(Of LiveTradeRecordEntity))

        Function GetRecentAsync(count As Integer,
                                Optional symbolFilter As String = Nothing,
                                Optional strategyFilter As String = Nothing,
                                Optional personaFilter As String = Nothing,
                                Optional pnlFilter As PnLFilterType = PnLFilterType.All,
                                Optional closedOnly As Boolean = False) As Task(Of IList(Of LiveTradeRecordEntity))

    End Interface

End Namespace
