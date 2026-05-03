Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    Public Interface ITradeRecordService

        ''' <summary>Persists an opening trade record. Returns the new record ID (stored on the slot).</summary>
        Function OpenTradeAsync(record As LiveTradeRecord) As Task(Of Long)

        ''' <summary>Marks a record closed with exit data and final P&amp;L.</summary>
        Function CloseTradeAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                                 pnL As Decimal, exitReason As String) As Task

        ''' <summary>Updates the entry price once confirmed from the broker snapshot.</summary>
        Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task

        ''' <summary>Updates TopStepXTradeId after async resolution via /api/Trade/search.</summary>
        Function ResolveTopStepXTradeIdAsync(recordId As Long, topStepXTradeId As Long) As Task

        ''' <summary>Returns the most recent trades, newest first, applying optional filters.</summary>
        Function GetRecentTradesAsync(count As Integer, Optional filter As TradeFilter = Nothing) As Task(Of IList(Of LiveTradeRecord))

        ''' <summary>
        ''' On app startup, finds IsOpen records in the DB and attempts to resolve their
        ''' exit fills via the TopStepX trade history API.
        ''' </summary>
        Function RecoverOpenTradesAsync(accountId As Long) As Task

    End Interface

End Namespace
