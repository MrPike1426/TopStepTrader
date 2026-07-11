Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-75: Configuration for the VWAP Mean-Reversion strategy (STRAT-43 Candidate B).
    '''
    ''' The strategy fades ±<see cref="SdEntryThreshold"/> SD excursions from the
    ''' session-anchored VWAP in non-trending sessions:
    '''   • Long: 5-min close ≥ SdEntryThreshold SDs below VWAP + 1-min reversal candle.
    '''   • Veto: 15-min ADX(14) ≥ <see cref="AdxVetoThreshold"/> (trend regime — do not fade).
    '''   • Veto: excursion beyond <see cref="SdTooFarThreshold"/> SDs (breakout, not a fade).
    '''   • Stop: beyond the entry 5-min bar's swing, min <see cref="MinStopAtrMult"/> × ATR(14).
    '''   • T1 = VWAP (half close); T2 = opposite 1 SD band or
    '''     <see cref="Tp2StopMultiple"/> × stop distance, whichever is closer; T2 trails by
    '''     <see cref="TrailAtrMult"/> × ATR after T1 fills.
    '''   • No entries within <see cref="EntryCutoffMinutesBeforeClose"/> min of the
    '''     21:10 UTC daily close.
    ''' </summary>
    Public Class VwapMeanReversionConfig

        ' ── Timeframes (locked for v1) ────────────────────────────────────────

        ''' <summary>Signal timeframe — SD-excursion detection. Locked to "5min" for v1.</summary>
        Public ReadOnly Property SignalTimeframe As String = "5min"

        ''' <summary>Confirmation timeframe — reversal candle. Locked to "1min" for v1.</summary>
        Public ReadOnly Property ConfirmTimeframe As String = "1min"

        ''' <summary>Trend-veto timeframe — ADX regime gate. Locked to "15min" for v1.</summary>
        Public ReadOnly Property AdxTimeframe As String = "15min"

        ' ── Persona ───────────────────────────────────────────────────────────

        ''' <summary>Active persona: "Lewis", "Damian", or "Joe".</summary>
        Public Property ActivePersona As String = "Damian"

        ' ── Entry bands ───────────────────────────────────────────────────────

        ''' <summary>SD distance from VWAP the 5-min close must reach for a fade entry. Default 2.0.</summary>
        Public Property SdEntryThreshold As Double = 2.0

        ''' <summary>
        ''' Too-far-gone veto: beyond this SD distance the excursion is treated as a breakout,
        ''' not a fade, and the entry is blocked. Default 5.0.
        ''' </summary>
        Public Property SdTooFarThreshold As Double = 5.0

        ' ── Trend veto ────────────────────────────────────────────────────────

        ''' <summary>ADX/DI smoothing length on the veto timeframe. Default 14.</summary>
        Public Property AdxLength As Integer = 14

        ''' <summary>
        ''' Entries are blocked while 15-min ADX(14) ≥ this threshold (trend regime).
        ''' Persona defaults: Lewis 30 / Damian 25 / Joe 20 (lower for Joe per FEAT-75 F1).
        ''' </summary>
        Public Property AdxVetoThreshold As Double = 25.0

        ' ── Risk / exits ──────────────────────────────────────────────────────

        ''' <summary>ATR period on the signal timeframe (stop floor + T2 trail). Default 14.</summary>
        Public Property AtrLength As Integer = 14

        ''' <summary>
        ''' Stop-distance floor as an ATR(14) multiple on the 5-min. The stop is placed beyond
        ''' the entry bar's swing low/high, but never closer than this many ATRs. The final
        ''' tick distance is clamped by <c>TopStepXInstrumentCatalog.ClampStopTicksAsync</c>
        ''' inside the order pipeline. Default 1.0.
        ''' </summary>
        Public Property MinStopAtrMult As Double = 1.0

        ''' <summary>
        ''' T2 cap as a multiple of the initial stop distance. T2 is the opposite 1 SD band or
        ''' entry ± this multiple × stop distance, whichever is closer to entry. Default 2.0.
        ''' </summary>
        Public Property Tp2StopMultiple As Double = 2.0

        ''' <summary>Runner trail distance (ATR multiple) applied to T2 after T1 fills. Default 1.0.</summary>
        Public Property TrailAtrMult As Double = 1.0

        ''' <summary>
        ''' Fraction of account equity risked per trade, as a percentage (0.5 = 0.5%).
        ''' Quantity = floor((equity × riskPct / 100) / (stopDist × pointValue)), clamped ≥ 1.
        ''' Default 0.5%.
        ''' </summary>
        Public Property RiskPct As Double = 0.5

        ' ── Session / control ─────────────────────────────────────────────────

        ''' <summary>
        ''' No new entries within this many minutes of the 21:10 UTC daily close
        ''' (STRAT-43 §3 time cutoff). Default 90 (= no entries after 19:40 UTC).
        ''' </summary>
        Public Property EntryCutoffMinutesBeforeClose As Integer = 90

        ''' <summary>
        ''' Force-flat time, "HHmm" UTC. Any open VWAP-MR position is unconditionally closed
        ''' at/after this time so a fade never carries into the 21:10 UTC close. Default "2105".
        ''' </summary>
        Public Property ForceFlatUtc As String = "2105"

        ''' <summary>Cooldown after an exit, in signal-timeframe bars. Default 3.</summary>
        Public Property CooldownBars As Integer = 3

        ''' <summary>Allow long entries.</summary>
        Public Property EnableLong As Boolean = True

        ''' <summary>Allow short entries.</summary>
        Public Property EnableShort As Boolean = True

        ' ── Broker SL-edit throttle (shared shape with SlipStream / UltimateScalper) ─

        ''' <summary>Minimum SL improvement (ticks) before a live edit is submitted. Default 1.</summary>
        Public Property MinSlEditStepTicks As Integer = 1

        ''' <summary>Maximum live SL edits per second per position. Default 5.</summary>
        Public Property MaxSlEditsPerSecond As Integer = 5

        ' ── Forward-compat seam ───────────────────────────────────────────────

        ''' <summary>
        ''' Forward-compat seam: v1 clamps to 1 regardless of stored value (the orchestrator
        ''' enforces this with a warning log). Matches SlipStream / UltimateScalper.
        ''' </summary>
        Public Property MaxConcurrentPositions As Integer = 1

        ' ── Persona defaults ──────────────────────────────────────────────────

        ''' <summary>
        ''' ADX trend-veto threshold for a persona. Mirrors the descending Lewis/Damian/Joe
        ''' scheme used by SuperTrend+ (Lewis strictest ADX figure, Joe loosest): for the
        ''' fade engine the ticket fixes Damian at 25 with "lower for Joe" — Lewis 30 /
        ''' Damian 25 / Joe 20.
        ''' </summary>
        Public Shared Function PersonaAdxVetoThreshold(persona As String) As Double
            Select Case persona
                Case "Lewis" : Return 30.0
                Case "Joe" : Return 20.0
                Case Else : Return 25.0   ' Damian default
            End Select
        End Function

        ''' <summary>Applies the persona's default thresholds to this config instance.</summary>
        Public Sub ApplyPersona(persona As String)
            If String.IsNullOrWhiteSpace(persona) Then persona = "Damian"
            ActivePersona = persona
            AdxVetoThreshold = PersonaAdxVetoThreshold(persona)
        End Sub

        ' ── Entry-cutoff helper ───────────────────────────────────────────────

        ''' <summary>Daily close used by the entry cutoff, as a UTC time-of-day (21:10 UTC).</summary>
        Public Shared ReadOnly SessionCloseUtc As New TimeSpan(21, 10, 0)

        ''' <summary>Globex reopen after the daily maintenance window, as a UTC time-of-day.</summary>
        Public Shared ReadOnly SessionReopenUtc As New TimeSpan(22, 0, 0)

        ''' <summary>
        ''' True when new entries are blocked at <paramref name="utcNow"/>: inside the
        ''' <see cref="EntryCutoffMinutesBeforeClose"/> window before the 21:10 UTC close,
        ''' or inside the dead window (21:10–22:00 UTC) itself.
        ''' </summary>
        Public Function IsInsideEntryCutoff(utcNow As DateTime) As Boolean
            Dim tod = utcNow.TimeOfDay
            Dim cutoffStart = SessionCloseUtc - TimeSpan.FromMinutes(Math.Max(0, EntryCutoffMinutesBeforeClose))
            Return tod >= cutoffStart AndAlso tod < SessionReopenUtc
        End Function

    End Class

End Namespace
