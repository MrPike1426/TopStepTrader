Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-62: Configuration for the Break and Bounce strategy.
    '''
    ''' Long signal chain: previous-day H/L reference → 15m bar closes above prev high
    ''' (dir = +1) → 5m bar tags prev high then closes back above it → bar is a hammer
    ''' OR a strict outside-bar bullish engulfing. Short is the mirror. Bias persists
    ''' until invalidated either by a counter-breakout or by entry-window expiry.
    '''
    ''' Entries fire only inside <see cref="EntryWindowMinutesAfterOpen"/> minutes from
    ''' the instrument session open. SL is the larger of (PDF stop, fixed ticks floor,
    ''' ATR-fraction floor). No fixed TP — exit is owned by the orchestrator's
    ''' lifecycle (initial bracket SL + break-even ratchet + session-close flat).
    ''' </summary>
    Public Class BreakAndBounceConfig

        ' ── Session window (CME exchange-time HHmm-HHmm) ────────────────────

        ''' <summary>
        ''' Entry window in CME exchange time. Default <c>0830-1100</c> = RTH open +
        ''' 150 min, matching the PDF spec. The orchestrator clears the breakout-state
        ''' bias when the contract transitions out of this window.
        ''' </summary>
        Public Property EntryWindow As String = "0830-1100"

        ''' <summary>
        ''' Force-flat window in CME exchange time. Any open Break and Bounce position
        ''' is unconditionally closed once this window opens. Default <c>1450-1500</c>
        ''' (last 10 min of RTH).
        ''' </summary>
        Public Property FlatWindow As String = "1450-1500"

        ' ── Timeframes ──────────────────────────────────────────────────────

        ''' <summary>Bar timeframe used for the daily-range breakout detection.</summary>
        Public Property BreakoutTimeframe As String = "15min"

        ''' <summary>Bar timeframe used for the retest + candle-pattern entry.</summary>
        Public Property RetestTimeframe As String = "5min"

        ' ── SL floor (the furthest of the three wins) ───────────────────────

        ''' <summary>Minimum SL distance from entry in instrument ticks.</summary>
        Public Property MinimumStopDistanceTicks As Integer = 8

        ''' <summary>Minimum SL distance as a fraction of ATR(14) on the retest TF.</summary>
        Public Property MinimumStopAtrFraction As Double = 0.5

        ''' <summary>ATR period used by the SL-floor calculation.</summary>
        Public Property AtrLength As Integer = 14

        ' ── Direction-bias invalidation ─────────────────────────────────────

        ''' <summary>Clear <c>dir</c> when a subsequent 15m bar closes through the opposite reference.</summary>
        Public Property InvalidateDirOnCounterBreakout As Boolean = True

        ''' <summary>Clear <c>dir</c> once the entry window has expired for the day.</summary>
        Public Property InvalidateDirOnWindowExpiry As Boolean = True

        ' ── Sizing ──────────────────────────────────────────────────────────

        ''' <summary>Number of contracts per entry. Q5 decision: faithful to PDF = 1.</summary>
        Public Property ContractsPerEntry As Integer = 1

        ' ── AI veto + master enable ─────────────────────────────────────────

        ''' <summary>Run the AI pre-trade veto via <c>IEntryExecutionService</c>.</summary>
        Public Property AiVetoEnabled As Boolean = True

        ''' <summary>Allow long entries.</summary>
        Public Property EnableLong As Boolean = True

        ''' <summary>Allow short entries.</summary>
        Public Property EnableShort As Boolean = True

        ' ── Broker SL-edit throttle (shared shape with SlipStream / UltimateScalper) ─

        ''' <summary>Minimum SL improvement in ticks before a live edit is submitted.</summary>
        Public Property MinSlEditStepTicks As Integer = 1

        ''' <summary>
        ''' Forward-compat seam — concurrent positions capped at 1 by the orchestrator
        ''' (matches SlipStream / UltimateScalper). A future ticket may relax this.
        ''' </summary>
        Public Property MaxConcurrentPositions As Integer = 1

    End Class

End Namespace
