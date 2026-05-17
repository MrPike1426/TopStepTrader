Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Core.Models


    ''' <summary>
    ''' Plain state object representing a single concurrent position slot.
    ''' Replaces the PersonaBoxVm identity concept — slots are index-based,
    ''' not person-based.
    ''' </summary>
    Public Class PositionSlot

        Public Property SlotIndex As Integer

        Public Property Instrument As String = String.Empty

        Public Property Side As String = String.Empty

        Public Property EntryPrice As Decimal

        Public Property EntryBarTime As DateTimeOffset = DateTimeOffset.MinValue

        Public Property EntryAdx As Single

        Public Property StopPrice As Decimal

        Public Property TakeProfitPrice As Decimal

        Public Property PositionId As Long?

        Public Property AccountId As Long

        Public Property Contracts As Integer = 1

        Public Property IsOpen As Boolean

        Public Property Health As SlotHealth = SlotHealth.Healthy

        Public Property EntryReason As String = String.Empty

        Public Property EntryTime As DateTime = DateTime.MinValue

        Public Property MissCount As Integer

        ''' <summary>
        ''' BUG-79: UTC timestamp of the most recent successful live-position snapshot.
        ''' Updated in HandleOpenPositionAsync when a non-null snapshot is observed; stays at
        ''' DateTime.MinValue while the first snapshot is still pending. The per-tick reconcile
        ''' uses this to force-release a slot whose snapshot has not refreshed for several
        ''' minutes, defending against the case where SearchOpenPositionsAsync keeps returning
        ''' a stale row long after the broker has actually closed the position.
        ''' </summary>
        Public Property LastSnapshotOkUtc As DateTime = DateTime.MinValue

        Public Property UnrealizedPnl As Decimal

        ''' <summary>14-period ATR value at the time this slot was opened.</summary>
        Public Property EntryAtr As Decimal

        ''' <summary>Initial risk in price — |entry - stop| at the moment of entry confirmation.</summary>
        Public Property InitialRisk As Decimal

        ''' <summary>Dollar value of 1R — InitialRisk converted to USD once entry and stop are both confirmed.</summary>
        Public Property InitialRiskDollars As Decimal

        ''' <summary>Current phased stop phase. Only advances forward.</summary>
        Public Property StopPhase As StopPhase = StopPhase.Initial

        ''' <summary>
        ''' Number of consecutive bars on which the exit engine scored Exiting health
        ''' (score >= 6, no E1 flip). Exit only fires when this reaches 2, preventing
        ''' single-bar consolidation at trend highs from triggering premature closure.
        ''' Resets to zero whenever the bar scores Warning or Healthy.
        ''' </summary>
        Public Property ConsecutiveExitBars As Integer = 0

        ''' <summary>
        ''' True between TryOpenSlot succeeding and the FireEntryAsync completing (order
        ''' accepted or rejected). Guards against the 15-second re-evaluation tick
        ''' re-opening the same instrument while the first PlaceOrder call is in-flight.
        ''' </summary>
        Public Property IsEntryInFlight As Boolean = False

        ''' <summary>
        ''' Set True when this slot was opened via early-mode entry (before closed-bar ST flip confirmation).
        ''' While True, E1 (SuperTrend flip) is suppressed — the SL bracket handles protection.
        ''' Cleared once ST direction confirms the slot's side; E1 then resumes normal operation.
        ''' </summary>
        Public Property IsEarlyModeEntry As Boolean = False

        ''' <summary>
        ''' ADX band (0/1/2/3) at the time this slot was opened. Ratchets upward as ADX strengthens;
        ''' never decreases. Used by the scale-in logic to detect when ADX crosses into a higher band
        ''' and additional contracts should be added to the position.
        ''' </summary>
        Public Property LastAdxBand As Integer = 0

        ''' <summary>TopStepX broker order ID for the entry fill (from PXPlaceOrderResponse.OrderId). Used to resolve the Trades panel ID.</summary>
        Public Property EntryOrderId As Long?

        ''' <summary>Row ID in LiveTradeRecords (TradeHistory.db). 0 = not yet persisted.</summary>
        Public Property TradeRecordId As Long = 0

        ''' <summary>FEAT-57: Row ID in TradeOutcomes (app.db). 0 = not yet persisted.
        ''' Set when the outcome row is opened at entry confirmation; consumed when
        ''' ReleaseSlotAsync resolves the outcome at exit.</summary>
        Public Property TradeOutcomeId As Long = 0

        ''' <summary>FEAT-58: Worst unrealised P&amp;L (most-negative dollars) observed during the trade.
        ''' Updated on every management tick alongside <c>UnrealizedPnl</c>. Persisted at close as the
        ''' absolute value on <c>TradeLifespanRecords.MaxAdverseExcursionDollars</c>.</summary>
        Public Property MaxAdverseExcursionUsd As Decimal = 0D

        ''' <summary>FEAT-58: Best unrealised P&amp;L (most-positive dollars) observed during the trade.
        ''' Updated on every management tick alongside <c>UnrealizedPnl</c>. Persisted at close on
        ''' <c>TradeLifespanRecords.MaxFavorableExcursionDollars</c>.</summary>
        Public Property MaxFavorableExcursionUsd As Decimal = 0D

        ''' <summary>FEAT-58: Number of times the ExitSignalEngine returned a different phase or
        ''' a different stop price during the trade. Counts stop-trail steps as well as ladder
        ''' phase transitions. Persisted at close on <c>TradeLifespanRecords.SlRatchetCount</c>.</summary>
        Public Property SlRatchetCount As Integer = 0

        ''' <summary>FEAT-58: Minutes from entry to the first tick where <c>StopPhase</c> reached
        ''' <c>FreeRide</c>. Stays at 0 if the trade never reached FreeRide. Persisted on
        ''' <c>TradeLifespanRecords.FreeRideActivatedAtMinutes</c>; the boolean
        ''' <c>FreeRideActivated</c> column is derived as <c>(FreeRideActivatedAtMinutes &gt; 0)</c>.</summary>
        Public Property FreeRideActivatedAtMinutes As Single = 0F

        ''' <summary>FEAT-58: Trading session window label (e.g. "Asia", "London", "US-Pre", "US-RTH",
        ''' "US-Post") captured at entry confirmation. Persisted on
        ''' <c>TradeLifespanRecords.EntrySessionWindow</c> and used to derive
        ''' <c>CrossedSessionBoundary</c> at close.</summary>
        Public Property EntrySessionWindow As String = String.Empty

        ''' <summary>Most recent ADX value from the 15-second monitoring tick. Updated live; 0 until first tick.</summary>
        Public Property CurrentAdx As Single = 0F

        ''' <summary>FEAT-39: GUID linking this slot to its DebugTrades row while debug capture is on. Nothing when capture disabled.</summary>
        Public Property DebugTradeId As String

        ''' <summary>
        ''' Most recent live market price for this instrument, sourced from the SignalR
        ''' UserHub real-time push (preferred) or the live 5-second bar close (fallback).
        ''' Used for the slot card "Live Price" display and as the input to the Scalper
        ''' stop-ratchet logic. 0 until the first hub or bar update arrives.
        ''' </summary>
        Public Property LivePrice As Decimal

        ''' <summary>
        ''' UTC timestamp of the most recent live price refresh (hub push or bar fetch).
        ''' Used to surface stale-price warnings when no update has arrived for several ticks.
        ''' </summary>
        Public Property LivePriceUtc As DateTime = DateTime.MinValue

        ''' <summary>
        ''' Number of consecutive monitoring ticks where the live price fetch failed or
        ''' returned a stale value. Resets to 0 on a successful fresh price update.
        ''' Slots with a sustained stale count surface a degraded health warning.
        ''' </summary>
        Public Property PriceStaleCount As Integer = 0

        ''' <summary>
        ''' BUG-80: Number of consecutive monitoring ticks where the broker-side
        ''' resting Stop bracket (type=4) for this slot's contract was absent.
        ''' Resets to 0 once a resting Stop is observed again. The slot is flattened
        ''' (reason "Bracket SL missing") once this reaches 2 consecutive ticks.
        ''' </summary>
        Public Property BracketMissingTickCount As Integer = 0

        ''' <summary>
        ''' FEAT-54: Runtime-only handle returned by <c>ILivePnLService.Subscribe</c>
        ''' when this slot opens. Disposed by <c>SlotManager.EndLiveTracking</c> on
        ''' slot release or before re-subscribing on side flip / contract delta.
        ''' Not serialised — must be re-established on app restart.
        ''' </summary>
        Public Property LivePriceSubscription As IDisposable

        ''' <summary>
        ''' FEAT-54: Source of the most recent <see cref="LivePrice"/> tick — Quote (sub-second
        ''' MarketHub push), Bar (2-second history-bar fallback), or None (no live tick yet
        ''' or metadata-only correction). Drives the slot-card source badge.
        ''' </summary>
        Public Property LivePriceSource As LivePriceSource = LivePriceSource.None

    End Class

End Namespace
