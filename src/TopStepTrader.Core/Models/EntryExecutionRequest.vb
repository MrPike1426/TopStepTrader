Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' ARCH-19: per-trade input for <c>IEntryExecutionService.PlaceAsync</c>. Bundles the
    ''' strategy-supplied entry candidate, the already-opened slot, persona/timeframe
    ''' context for AI veto + persistence, and the UI-side callbacks the service must
    ''' invoke (AI log entries, watchlist status, live-tracking startup, slot release).
    '''
    ''' The strategy ViewModel owns construction. The service is free to mutate
    ''' <see cref="Slot"/> in place (entry price, stop price, order ids, P&amp;L state).
    ''' </summary>
    Public Class EntryExecutionRequest

        ' ── Core trade inputs ───────────────────────────────────────────────────

        ''' <summary>Candidate produced by the strategy's entry-detection seam.</summary>
        Public Property Candidate As EntryCandidate

        ''' <summary>Slot already opened by <c>SlotManager.TryOpenSlot</c>. Mutated in place.</summary>
        Public Property Slot As PositionSlot

        ''' <summary>Account id resolved by the ViewModel (0 = abort with reason).</summary>
        Public Property AccountId As Long

        ''' <summary>
        ''' String contract symbol used by every TopStepX-facing API and persisted record
        ''' (e.g. "MNQ", "MGC"). The numeric <see cref="EntryCandidate.ContractId"/> is the
        ''' broker contract identifier; this is the strategy-watchlist root symbol.
        ''' </summary>
        Public Property ContractSymbol As String = String.Empty

        ''' <summary>
        ''' Bar close at signal time. Used as reference price for the SL distance clamp and
        ''' as the synthesised signal/entry price on the LiveTradeRecord / Signal rows.
        ''' </summary>
        Public Property LastClose As Decimal

        ''' <summary>Bar timestamp recorded on the slot for re-entry suppression checks.</summary>
        Public Property BarTime As DateTimeOffset

        ''' <summary>Strategy-suggested stop price (the "ST line" for SuperTrend+).</summary>
        Public Property StopReferencePrice As Decimal

        ' ── Strategy / persona / timeframe context (forward-fed into AI + persistence) ──

        ''' <summary>"SuperTrend+" / "BreakAndBounce" — persisted on LiveTradeRecord/Snapshot.</summary>
        Public Property StrategyName As String = String.Empty

        ''' <summary>Human-readable strategy label fed to the AI ctx (e.g. "SuperTrend+ Autopilot").</summary>
        Public Property StrategyDisplayName As String = String.Empty

        ''' <summary>"SuperTrendPlus.v1" — TradeSignal.ModelVersion + TradeOutcome.ModelVersion.</summary>
        Public Property ModelVersion As String = String.Empty

        ''' <summary>Active persona name (Damian/Lewis/Joe).</summary>
        Public Property Persona As String = String.Empty

        ''' <summary>Active persona's minimum entry ADX (rendered into the AI ctx string).</summary>
        Public Property PersonaMinAdx As Single

        ''' <summary>Active persona's R:R target (rendered into the AI ctx string).</summary>
        Public Property PersonaRrRatio As Decimal

        ''' <summary>Timeframe label as exposed by the strategy ("5min", "15min", "1hr").</summary>
        Public Property TimeframeLabel As String = String.Empty

        ''' <summary>Strategy timeframe expressed in minutes (5 / 15 / 60). Stored on outcomes.</summary>
        Public Property TimeframeMinutes As Integer

        ''' <summary>Mapped <see cref="BarTimeframe"/> for bar service calls.</summary>
        Public Property TimeframeForBars As BarTimeframe = BarTimeframe.FifteenMinute

        ''' <summary>Free-text exit-strategy description handed to the AI veto prompt.</summary>
        Public Property ExitStrategyDescription As String = String.Empty

        ' ── AI gating + debug capture ───────────────────────────────────────────

        ''' <summary>Whether the AI pre-trade check should run (mirrors the VM toggle).</summary>
        Public Property IsAiEnabled As Boolean

        ''' <summary>Whether trade-by-trade debug capture (FEAT-39) should run.</summary>
        Public Property DebugCaptureEnabled As Boolean

        ''' <summary>JSON snapshot of the strategy config for debug-capture persistence.</summary>
        Public Property StrategyConfigJson As String = String.Empty

        ''' <summary>"Preemptive" / "BarClose" — recorded on the debug trade header.</summary>
        Public Property EntryModeLabel As String = String.Empty

        ' ── Strategy-side helpers (callbacks the service invokes) ───────────────

        ''' <summary>
        ''' AI Check Log seam — invoked as (indicator, checkResult). The ViewModel marshals
        ''' to its dispatcher and appends to the on-tab AI history list.
        ''' </summary>
        Public Property OnAiLogEntry As Action(Of String, String)

        ''' <summary>
        ''' Watchlist row status seam — invoked as (contractId, statusText). Drives the
        ''' green "AI Checked" / red veto reason that replaces the SignalReason cell.
        ''' </summary>
        Public Property OnWatchlistAiStatus As Action(Of String, String)

        ''' <summary>
        ''' Slot release callback. Invoked with <c>Slot.SlotIndex</c> on any abort path so
        ''' the SlotManager (VM-owned) can close the slot and reset its in-flight flag.
        ''' </summary>
        Public Property OnReleaseSlot As Action(Of Integer)

        ''' <summary>
        ''' Invoked after the order is accepted so the VM can begin its live P&amp;L /
        ''' MarketHub subscription path (which captures VM-private callbacks).
        ''' </summary>
        Public Property OnSlotEntered As Action(Of PositionSlot)

        ''' <summary>
        ''' Maps an ADX value to its 0/1/2/3 strength band. Strategy-specific (lives on
        ''' SlotManager for SuperTrend+); passed through so the service can populate
        ''' the LongCount/ShortCount/UpPct/DownPct fields of TradeSetupSnapshot.
        ''' </summary>
        Public Property BandForAdx As Func(Of Single, Integer)

    End Class

End Namespace
