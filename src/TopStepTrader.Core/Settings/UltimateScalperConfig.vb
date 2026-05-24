Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-64: Configuration for the Ultimate Scalper strategy.
    '''
    ''' The scalper detects a three-element confluence (price vs 5m MA200, price vs session VWAP,
    ''' RSI(14) with recency filter) on 5-minute bars across MES/MNQ/MGC and runs a two-phase
    ''' quote-driven trailing stop after entry (Risk → BE snap → continuous monotonic trail).
    ''' </summary>
    Public Class UltimateScalperConfig

        ''' <summary>Working timeframe for confluence evaluation. Locked to "5min" for v1.</summary>
        Public ReadOnly Property SignalTimeframe As String = "5min"

        ''' <summary>Higher-TF moving-average length. 200 per the Pine Script reference.</summary>
        Public Property MaLength As Integer = 200

        ''' <summary>Higher-TF moving-average timeframe. "5min" per the indicator definition.</summary>
        Public ReadOnly Property MaTimeframe As String = "5min"

        ''' <summary>RSI period. 14 per the Pine Script reference.</summary>
        Public Property RsiLength As Integer = 14

        ''' <summary>RSI overbought threshold (default 70).</summary>
        Public Property RsiOverbought As Double = 70.0

        ''' <summary>RSI oversold threshold (default 30).</summary>
        Public Property RsiOversold As Double = 30.0

        ''' <summary>RSI midline used for the recency filter. 50 per the Pine Script.</summary>
        Public ReadOnly Property RsiMidline As Double = 50.0

        ''' <summary>
        ''' Maximum number of closed bars between the most recent RSI midline-cross and the
        ''' OB/OS reading for a signal to qualify. Default 10 per the Pine Script reference.
        ''' </summary>
        Public Property MaxBarsSinceMidlineCross As Integer = 10

        ''' <summary>
        ''' Safety ceiling TP distance in USD per contract. Placed wide of entry to satisfy the
        ''' broker's bracket-pair requirement; should not fire under normal trail behaviour.
        ''' </summary>
        Public Property SafetyCeilingTpDollars As Decimal = 200D

        ''' <summary>
        ''' Broker SL-edit throttle: minimum improvement (in instrument ticks) before the
        ''' trail engine submits a live <c>EditPositionSlTpAsync</c> call.
        ''' </summary>
        Public Property MinSlEditStepTicks As Integer = 1

        ''' <summary>
        ''' Broker SL-edit throttle: maximum live SL edits per second per position.
        ''' </summary>
        Public Property MaxSlEditsPerSecond As Integer = 5

        ''' <summary>
        ''' Per-trade contract size multiplier. Default 1 = one contract per signal. Persisted
        ''' alongside the other global tunables — unlike SuperTrend+'s LeverageMultiplier which
        ''' resets to 1 on restart, the scalper persists this value because position sizing is
        ''' the only quantity knob the strategy exposes.
        ''' </summary>
        Public Property Leverage As Integer = 1

        ' ── FEAT-69: Pre-staged stop-entry orders (Plan C) ────────────────────

        ''' <summary>
        ''' Master switch for the pre-staged stop-entry path. When True, primed symbols arm
        ''' resting <c>OrderType.StopOrder</c> orders at the broker; the orchestrator's
        ''' legacy market-on-close fire path is bypassed. When False, the legacy path runs.
        ''' </summary>
        Public Property PreStagedEntriesEnabled As Boolean = True

        ''' <summary>
        ''' Number of ticks past the last closed bar's high (long) / low (short) to place
        ''' the stop-entry trigger price. 1 tick = classic floor-scalper micro-breakout.
        ''' </summary>
        Public Property EntryTriggerOffsetTicks As Integer = 1

        ''' <summary>
        ''' Minimum trigger-price drift (in instrument ticks) that justifies a cancel-and-replace
        ''' on bar close. Below this, the existing armed order is left in place to conserve
        ''' broker API calls. Default 2 ticks.
        ''' </summary>
        Public Property RepriceThresholdTicks As Integer = 2

        ''' <summary>
        ''' Maximum age (minutes) of a resting stop-entry order before the manager cancels it
        ''' on the next evaluation. Default 30 = ~6 × 5-min bars of "still primed but no trigger".
        ''' </summary>
        Public Property ArmStaleMinutes As Integer = 30

        ''' <summary>
        ''' Minimum seconds between consecutive arm attempts on the same symbol. Prevents
        ''' arm/cancel/arm flicker from burning the broker rate-limit budget.
        ''' </summary>
        Public Property ReArmDebounceSeconds As Integer = 30

        ''' <summary>
        ''' Strict ceiling on broker API calls (place + cancel + edit) the scalper subsystem
        ''' may make in a rolling 60-second window. When reached, the manager defers new arm
        ''' and re-price actions; <b>cancels are never deferred</b> (stale orders are a worse
        ''' failure mode than missed opportunities). Default 80 = 20-call buffer under
        ''' TopStepX's 100/min limit.
        ''' </summary>
        Public Property MaxBrokerCallsPerMinute As Integer = 80

        ''' <summary>
        ''' Forward-compat seam: maximum concurrent live positions the scalper may hold.
        ''' v1 clamps to 1 regardless of stored value (the orchestrator enforces this with
        ''' a warning log if a non-1 value is read from the DB). A follow-up ticket lifts.
        ''' </summary>
        Public Property MaxConcurrentPositions As Integer = 1

        ''' <summary>
        ''' Per-instrument trail parameters. Always exactly three entries (MES / MNQ / MGC).
        ''' Defaults from <see cref="DefaultProfiles"/>; tunable in the Ultimate Scalper tab UI.
        ''' </summary>
        Public Property InstrumentProfiles As List(Of UltimateScalperInstrumentRiskProfile) = DefaultProfiles()

        ''' <summary>
        ''' Defaults captured in the FEAT-64 plan: tight for MES, looser for MNQ to absorb
        ''' Nasdaq's higher noise, mid for MGC.
        ''' </summary>
        Public Shared Function DefaultProfiles() As List(Of UltimateScalperInstrumentRiskProfile)
            Return New List(Of UltimateScalperInstrumentRiskProfile) From {
                New UltimateScalperInstrumentRiskProfile With {
                    .Symbol = "MES",
                    .InitialStopDollars = 20D,
                    .BreakevenSnapDollars = 7D,
                    .TrailDistanceDollars = 7D
                },
                New UltimateScalperInstrumentRiskProfile With {
                    .Symbol = "MNQ",
                    .InitialStopDollars = 40D,
                    .BreakevenSnapDollars = 12D,
                    .TrailDistanceDollars = 12D
                },
                New UltimateScalperInstrumentRiskProfile With {
                    .Symbol = "MGC",
                    .InitialStopDollars = 30D,
                    .BreakevenSnapDollars = 10D,
                    .TrailDistanceDollars = 10D
                }
            }
        End Function

        ''' <summary>Returns the profile for the given symbol, or Nothing if not configured.</summary>
        Public Function GetProfile(symbol As String) As UltimateScalperInstrumentRiskProfile
            If String.IsNullOrWhiteSpace(symbol) Then Return Nothing
            Return InstrumentProfiles.FirstOrDefault(
                Function(p) String.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        End Function

    End Class

End Namespace
