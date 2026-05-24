Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' ARCH-20: per-tick input for <c>IPositionManagementService.UpdateAsync</c>. Bundles
    ''' the strategy-TF bars (optional — service refetches when absent), the strategy
    ''' timeframe, "as-of" UTC clock, and the strategy-specific scalars that previously
    ''' lived as VM fields (PnL Guard, exit-score threshold, ST multiplier, early-mode cap).
    ''' The VM owns construction and may mutate per-tick fields between calls.
    ''' </summary>
    Public Class PositionManagementTickContext

        ''' <summary>
        ''' Strategy-TF bar series including the current bar. Optional — when Nothing or
        ''' shorter than 14 bars, the service refetches via <c>IBarIngestionService</c>
        ''' (paper feed, BUG-72) and falls back to the live feed when the paper feed is
        ''' stale (BUG-81).
        ''' </summary>
        Public Property Bars As IList(Of MarketBar)

        ''' <summary>Strategy timeframe (5m / 15m / 1h) — drives the staleness guard and any refetch.</summary>
        Public Property StrategyTimeframe As BarTimeframe = BarTimeframe.FifteenMinute

        ''' <summary>UTC "as of" clock — used for staleness checks and lifespan duration math.</summary>
        Public Property AsOfUtc As DateTime = DateTime.UtcNow

        ''' <summary>When True, the caller forces a fresh broker snapshot REST call regardless
        ''' of the alternating-tick skip state (e.g. just after the slot was released earlier
        ''' in the same tick).</summary>
        Public Property ForceSnapshot As Boolean = False

        ''' <summary>SuperTrend multiplier (persona-derived). Pumped through to TechnicalIndicators.</summary>
        Public Property StMultiplier As Double = 3.0R

        ''' <summary>Bars-required threshold for the cumulative-score two-bar exit gate. Mirrors
        ''' <c>SuperTrendPlusConfig.ExitScoreThreshold</c>.</summary>
        Public Property ExitScoreThreshold As Integer = 4

        ''' <summary>Early-mode grace cap in minutes (0 = no cap). Mirrors
        ''' <c>SuperTrendPlusConfig.EarlyModeMaxAgeMinutes</c>.</summary>
        Public Property EarlyModeMaxAgeMinutes As Integer = 0

        ''' <summary>Optional P&amp;L Guard configuration. When <see cref="PnLGuardSettings.IsActive"/>
        ''' is True the service aggregates <see cref="AggregatedInstrumentPnl"/> and applies
        ''' <see cref="PnLGuardSettings.ShouldFlatten"/>.</summary>
        Public Property PnLGuard As PnLGuardSettings

        ''' <summary>Aggregated unrealised P&amp;L across all open slots on the same instrument.
        ''' Caller-computed because the VM owns the slot collection. Read only when
        ''' <see cref="PnLGuard"/> reports active.</summary>
        Public Property AggregatedInstrumentPnl As Decimal

        ''' <summary>True if this slot is the primary owner of the bracket SL edit for its instrument
        ''' (i.e. it has the lowest <c>SlotIndex</c> among open slots on the same contract). When False
        ''' the service defers the broker stop modify and only stamps slot state — the primary slot
        ''' will perform the edit on its own tick.</summary>
        Public Property IsPrimaryForBracketEdit As Boolean = True

        ''' <summary>Mirrors <c>SuperTrendPlusViewModel._releasedThisTick</c>: True if any other slot
        ''' was released earlier in the same tick. The service treats this as one of the "must
        ''' snapshot" predicates so the alternate-tick skip cannot survive a release event.</summary>
        Public Property ReleasedThisTick As Boolean = False

        ''' <summary>True when <c>IsDebugCaptureEnabled</c> on the host VM is set. Mirrors the
        ''' same gate that surrounds every debug-capture record/snapshot call. Read by the
        ''' service only — debug capture itself is dispatched via the injected
        ''' <c>IDebugTradeCaptureService</c>.</summary>
        Public Property IsDebugCaptureEnabled As Boolean = False

        ''' <summary>Strategy-supplied ADX-band classifier used by the scale-in path. Required
        ''' for scale-in to fire; when Nothing the service skips the scale-in evaluation.</summary>
        Public Property BandForAdx As Func(Of Single, Integer)

        ''' <summary>
        ''' UI-selected leverage multiplier (1, 2, or 3) — scales both the per-band
        ''' contract delta and the scale-in cap. Mirrors
        ''' <c>SuperTrendPlusConfig.LeverageMultiplier</c>.
        ''' </summary>
        Public Property LeverageMultiplier As Integer = 1

        ''' <summary>Strategy-supplied scale-in callback. Invoked with (slot, addContracts) when
        ''' the ADX band advances above <c>slot.LastAdxBand</c>. Awaited inline so any broker
        ''' failures surface as warnings on the same tick.</summary>
        Public Property OnScaleInRequested As Func(Of PositionSlot, Integer, Task)

        ''' <summary>
        ''' FEAT-63: global $-denominated TP-ladder increment. When &gt; 0, the position-
        ''' management tick suppresses the <c>ExitGate</c> (E1–E9 force-close path) and
        ''' instead merges a laddered SL with the phased-stop SL — whichever is more
        ''' conservative wins. Mirrors <c>SuperTrendPlusConfig.LadderTpDollars</c>.
        ''' </summary>
        Public Property LadderTpDollars As Decimal = 0D

    End Class

End Namespace
