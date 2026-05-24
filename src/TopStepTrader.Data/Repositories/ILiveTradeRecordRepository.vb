Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Interface ILiveTradeRecordRepository

        Function AddAsync(entity As LiveTradeRecordEntity) As Task(Of Long)

        Function CloseAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                            pnl As Decimal, exitReason As String) As Task

        ''' <summary>
        ''' BUG-92: amends an already-closed record with broker-confirmed exit data. Used by
        ''' the post-flatten reconciliation pass when the broker stream returns the actual
        ''' ExecutePrice on the closing fill, which can differ from the engine-derived ExitPrice
        ''' persisted at close-decision time. <paramref name="exitOrderId"/> stores the broker's
        ''' closing order ID and doubles as the "broker-reconciled" flag (non-zero ⇒ reconciled).
        ''' </summary>
        Function ReconcileExitAsync(id As Long,
                                    exitPrice As Decimal,
                                    pnl As Decimal,
                                    exitOrderId As Long,
                                    exitTime As DateTimeOffset?) As Task

        Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task

        Function ResolveTopStepXTradeIdAsync(id As Long, topStepXTradeId As Long) As Task

        Function GetOpenRecordsAsync() As Task(Of IList(Of LiveTradeRecordEntity))

        ''' <summary>BUG-64: efficient single-record lookup via primary key.</summary>
        Function GetByIdAsync(id As Long) As Task(Of LiveTradeRecordEntity)

        Function GetRecentAsync(count As Integer,
                                Optional symbolFilter As String = Nothing,
                                Optional strategyFilter As String = Nothing,
                                Optional personaFilter As String = Nothing,
                                Optional pnlFilter As PnLFilterType = PnLFilterType.All,
                                Optional closedOnly As Boolean = False) As Task(Of IList(Of LiveTradeRecordEntity))

    End Interface

End Namespace
