Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>
    ''' FEAT-73: aggregate over the trading day's closed trades, computed from a single
    ''' repository read so the sum and the counters describe the same snapshot.
    ''' </summary>
    Public Class DailyCloseStats
        ''' <summary>SUM(PnL) — same figure <c>SumRealisedPnlSinceAsync</c> returns.</summary>
        Public Property GrossPnl As Decimal
        ''' <summary>SUM(PnL − CommissionUsd − FeesUsd) — TopStep counts fees in daily P&amp;L.</summary>
        Public Property NetPnlAfterFees As Decimal
        ''' <summary>Closed trades since the window start.</summary>
        Public Property TradeCount As Integer
        ''' <summary>Consecutive closes with PnL &lt;= 0 walking newest-backwards until the first winner.</summary>
        Public Property ConsecutiveLosers As Integer
    End Class

    Public Interface ILiveTradeRecordRepository

        Function AddAsync(entity As LiveTradeRecordEntity) As Task(Of Long)

        Function CloseAsync(id As Long, exitTime As DateTimeOffset, exitPrice As Decimal,
                            pnl As Decimal, exitReason As String,
                            Optional closeFillSource As String = Nothing) As Task

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

        ''' <summary>
        ''' BUG-93 F2: lookup by broker entry-order id. Returns the LiveTradeRecord whose
        ''' <c>EntryOrderId</c> matches the broker-assigned order id (or Nothing). Used by the
        ''' broker-fill persistence floor to no-op when a strategy has already attributed the fill.
        ''' </summary>
        Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecordEntity)

        ''' <summary>
        ''' BUG-94 F1: returns any open LiveTradeRecord whose <c>ContractId</c> matches the
        ''' supplied broker contract id. Note: <c>LiveTradeRecordEntity</c> does not currently
        ''' carry an AccountId column — the caller is expected to filter to the active account
        ''' upstream (the worker always reconciles a single selected account at a time).
        ''' </summary>
        Function FindOpenByContractIdAsync(contractId As String) As Task(Of LiveTradeRecordEntity)

        Function GetRecentAsync(count As Integer,
                                Optional symbolFilter As String = Nothing,
                                Optional strategyFilter As String = Nothing,
                                Optional personaFilter As String = Nothing,
                                Optional pnlFilter As PnLFilterType = PnLFilterType.All,
                                Optional closedOnly As Boolean = False) As Task(Of IList(Of LiveTradeRecordEntity))

        ''' <summary>
        ''' FEAT-71: Sum of <c>PnL</c> across closed records whose <c>ExitTime</c> is at or
        ''' after <paramref name="sinceUtc"/>. Skips rows where <c>PnL</c> is NULL (close
        ''' fill not yet reconciled). Returns 0 when no rows match. Net of fees/commission
        ''' is NOT applied here — the caller can subtract <c>CommissionUsd + FeesUsd</c>
        ''' separately if needed; default $0.50/contract round-trip rounds to negligible
        ''' against a $1.5k cap so this is intentionally gross.
        ''' </summary>
        Function SumRealisedPnlSinceAsync(sinceUtc As DateTimeOffset) As Task(Of Decimal)

        ''' <summary>
        ''' FEAT-73: combine-mode day stats in one read — net-of-fees realised P&amp;L
        ''' (the ticket's <c>SumRealisedPnlNetOfFeesSinceAsync</c> role), gross P&amp;L,
        ''' closed-trade count and consecutive-loser streak, all over closed trades whose
        ''' <c>ExitTime</c> is at or after <paramref name="sinceUtc"/>. Rows with NULL
        ''' <c>PnL</c> (close fill not yet reconciled) are skipped.
        ''' </summary>
        Function GetDailyCloseStatsAsync(sinceUtc As DateTimeOffset) As Task(Of DailyCloseStats)

    End Interface

End Namespace
