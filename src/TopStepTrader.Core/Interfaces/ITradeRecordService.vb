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
        ''' BUG-86 F3: returns every LiveTradeRecord still marked IsOpen, regardless of
        ''' age. The Dashboard uses this to surface stale-open trades (IsOpen=true with
        ''' an EntryTime older than the staleness threshold) so the user can trigger
        ''' an on-demand reconcile.
        ''' </summary>
        Function GetOpenTradesAsync() As Task(Of IList(Of LiveTradeRecord))

        ''' <summary>BUG-64: efficient single-record lookup by primary key.</summary>
        Function GetTradeByIdAsync(id As Long) As Task(Of LiveTradeRecord)

        ''' <summary>
        ''' On app startup, finds IsOpen records in the DB and attempts to resolve their
        ''' exit fills via the TopStepX trade history API.
        ''' </summary>
        Function RecoverOpenTradesAsync(accountId As Long) As Task

        ''' <summary>Persists a stop-loss adjustment event for a live trade.</summary>
        Function LogStopAdjustmentAsync(liveTradeRecordId As Long, timestamp As DateTimeOffset,
                                        oldStop As Decimal, newStop As Decimal,
                                        triggerReason As String,
                                        Optional notes As String = Nothing) As Task

        ''' <summary>Returns all stop adjustments for a trade in chronological order.</summary>
        Function GetStopAdjustmentsAsync(liveTradeRecordId As Long) As Task(Of IList(Of TradeStopAdjustment))

        ''' <summary>
        ''' FEAT-50: Captures TopStepX Order/Position/Trade snapshots into local SQLite
        ''' at trade close. Best-effort; failures are logged and swallowed.
        ''' </summary>
        Function CaptureClosingSnapshotsAsync(recordId As Long, accountId As Long) As Task

        ''' <summary>
        ''' FEAT-50: Walks every closed record without snapshots and runs capture for it.
        ''' Triggered from a Settings menu action or via the --backfill-snapshots startup arg.
        ''' </summary>
        Function BackfillSnapshotsAsync(accountId As Long) As Task

        ''' <summary>
        ''' FEAT-57: Persists a TradeSignal row and returns its Id. Used by the live trade
        ''' lifecycle to obtain a SignalId for the linked TradeOutcomes row.
        ''' </summary>
        Function SaveSignalAsync(signal As TradeSignal) As Task(Of Long)

        ''' <summary>
        ''' FEAT-57: Inserts a new TradeOutcomes row with IsOpen=true. The supplied
        ''' SignalId/recordId override whatever the caller may have set on the model so
        ''' the cross-DB linkage to LiveTradeRecord (recordId stashed in OrderId) and the
        ''' just-persisted Signal row is authoritative. Returns the new outcome Id.
        ''' </summary>
        Function OpenOutcomeAsync(signalId As Long, recordId As Long, model As TradeOutcome) As Task(Of Long)

        ''' <summary>
        ''' FEAT-57: Resolves an open TradeOutcomes row with the realised exit data.
        ''' No-op when outcomeId = 0.
        ''' </summary>
        Function ResolveOutcomeAsync(outcomeId As Long, exitTime As DateTimeOffset,
                                     exitPrice As Decimal, pnl As Decimal,
                                     isWinner As Boolean, exitReason As String) As Task

        ''' <summary>
        ''' FEAT-58: Persists a TradeSetupSnapshot (indicator + context snapshot at signal time)
        ''' linked to the supplied TradeOutcomeId. The supplied id overrides whatever was set
        ''' on the model. Returns the new row Id, or 0 on failure / outcomeId = 0.
        ''' </summary>
        Function SaveSetupSnapshotAsync(tradeOutcomeId As Long, snapshot As TradeSetupSnapshot) As Task(Of Long)

        ''' <summary>
        ''' FEAT-58: Upserts a TradeLifespan record (MAE/MFE, duration, trail counts, R-multiple)
        ''' for the supplied TradeOutcomeId. If a row already exists for the outcome it is updated
        ''' in place; otherwise a new row is inserted. No-op when outcomeId = 0.
        ''' </summary>
        Function SaveLifespanRecordAsync(tradeOutcomeId As Long, record As TradeLifespan) As Task

    End Interface

End Namespace
