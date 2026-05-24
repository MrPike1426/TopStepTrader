Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-70: Configuration for the SlipStream trend-pullback strategy.
    '''
    ''' Confluence (long): trend bias (close &gt; EMA200 AND EMAfast &gt; EMA200, optionally also
    ''' close &gt; HTF EMA), an extended-then-pulled-back pattern (price reached at least
    ''' <see cref="ExtendAtrMult"/> × ATR away from EMAfast in the lookback window then tagged
    ''' it on this bar), momentum (RSI ≥ <see cref="RsiLongMin"/> AND DI+ &gt; DI−), strength
    ''' (ADX ≥ <see cref="AdxMin"/>), and volatility regime (current ATR percentile rank ≥
    ''' <see cref="AtrPercentMin"/>). Short is the mirror.
    '''
    ''' Exits: initial stop at <see cref="AtrSLmult"/> × ATR; partial of <see cref="Tp1Pct"/>%
    ''' at <see cref="AtrTP1mult"/> × ATR; runner trails at <see cref="TrailMult"/> × ATR once
    ''' price has moved <see cref="TrailOffsetMult"/> × ATR in favor. Force-flat window and
    ''' max-bars time stop bound the trade duration.
    ''' </summary>
    Public Class SlipStreamConfig

        ' ── Timeframes ────────────────────────────────────────────────────────

        ''' <summary>Working timeframe for confluence evaluation. Locked to "5min" for v1.</summary>
        Public ReadOnly Property SignalTimeframe As String = "5min"

        ''' <summary>HTF used by the bias filter. Default "60min" (1 hour).</summary>
        Public Property HtfTimeframe As String = "60min"

        ''' <summary>Enable the higher-timeframe bias filter (close vs HTF EMA).</summary>
        Public Property UseHtfFilter As Boolean = True

        ''' <summary>HTF EMA length. 50 per the Pine Script reference.</summary>
        Public Property HtfEmaLength As Integer = 50

        ' ── Trend (fast/slow EMA bias) ────────────────────────────────────────

        ''' <summary>Fast EMA length on the signal timeframe. 21 per the Pine Script reference.</summary>
        Public Property EmaFastLength As Integer = 21

        ''' <summary>Slow EMA length on the signal timeframe. 200 per the Pine Script reference.</summary>
        Public Property EmaSlowLength As Integer = 200

        ' ── Momentum (RSI + DI direction) ─────────────────────────────────────

        ''' <summary>RSI period. 14 per the Pine Script reference.</summary>
        Public Property RsiLength As Integer = 14

        ''' <summary>Minimum RSI for a long signal. 55 per the Pine Script reference.</summary>
        Public Property RsiLongMin As Double = 55.0

        ''' <summary>Maximum RSI for a short signal. 45 per the Pine Script reference.</summary>
        Public Property RsiShortMax As Double = 45.0

        ' ── Strength + volatility regime ──────────────────────────────────────

        ''' <summary>ADX/DI smoothing length. 14 per the Pine Script reference.</summary>
        Public Property AdxLength As Integer = 14

        ''' <summary>Minimum ADX for any signal to fire. Hardened to 22 from the Pine default of 18.</summary>
        Public Property AdxMin As Double = 22.0

        ''' <summary>ATR period. 14 per the Pine Script reference.</summary>
        Public Property AtrLength As Integer = 14

        ''' <summary>Lookback window for the ATR percentile-rank regime filter. Default 100 bars.</summary>
        Public Property AtrPercentLookback As Integer = 100

        ''' <summary>
        ''' Skip signals when the current ATR percentile rank is below this threshold (0–100).
        ''' Filters dead-vol regimes where ATR-based stops are too tight to mean anything.
        ''' Default 30.
        ''' </summary>
        Public Property AtrPercentMin As Double = 30.0

        ' ── Pullback shape ────────────────────────────────────────────────────

        ''' <summary>
        ''' How many bars (excluding the current bar) to look back for the "extension" gate.
        ''' At some point in this window, price must have reached at least
        ''' <see cref="ExtendAtrMult"/> × ATR away from EMAfast for the pullback to qualify.
        ''' Default 3.
        ''' </summary>
        Public Property ExtendBars As Integer = 3

        ''' <summary>Required extension distance from EMAfast (ATR multiple). Default 0.5.</summary>
        Public Property ExtendAtrMult As Double = 0.5

        ' ── Risk / position sizing ────────────────────────────────────────────

        ''' <summary>
        ''' Fraction of account equity to risk per trade, as a percentage (0.5 = 0.5%).
        ''' Quantity is computed as floor((equity × riskPct / 100) / (stopDist × pointValue)),
        ''' clamped to ≥ 1 contract. Default 0.5%.
        ''' </summary>
        Public Property RiskPct As Double = 0.5

        ''' <summary>Initial stop distance from entry, in ATR multiples. Default 1.5.</summary>
        Public Property AtrSLmult As Double = 1.5

        ''' <summary>TP1 distance from entry, in ATR multiples. Default 1.0.</summary>
        Public Property AtrTP1mult As Double = 1.0

        ''' <summary>
        ''' Percentage of the position closed at TP1 (the partial-take leg). Default 50%.
        ''' The remaining percentage is the runner which trails per <see cref="TrailMult"/>.
        ''' </summary>
        Public Property Tp1Pct As Double = 50.0

        ''' <summary>
        ''' Runner trail distance from the peak favorable price, in ATR multiples. Default 1.5.
        ''' Trail is monotonic — SL never retraces. Activates after the trade has moved
        ''' <see cref="TrailOffsetMult"/> × ATR in favor.
        ''' </summary>
        Public Property TrailMult As Double = 1.5

        ''' <summary>
        ''' Trail activation offset — price must move this many ATRs in favor of the trade
        ''' before the runner trail engages. Below this, only the fixed initial stop is live.
        ''' Default 1.0.
        ''' </summary>
        Public Property TrailOffsetMult As Double = 1.0

        ''' <summary>
        ''' Time stop: flatten if this many signal-timeframe bars elapse without exit.
        ''' 0 = disabled. Default 40 (≈ 3h 20min on 5-minute bars).
        ''' </summary>
        Public Property MaxBarsInTrade As Integer = 40

        ' ── Session / control ────────────────────────────────────────────────

        ''' <summary>Master switch for the session filter. When False, signals fire any time of day.</summary>
        Public Property UseSession As Boolean = True

        ''' <summary>
        ''' Trading session window in exchange time, "HHmm-HHmm". Signals only fire inside this
        ''' window. Default "0830-1500" — RTH for US index futures.
        ''' </summary>
        Public Property SessionWindow As String = "0830-1500"

        ''' <summary>
        ''' Force-flat window in exchange time, "HHmm-HHmm". Any open SlipStream position is
        ''' unconditionally closed once this window opens. Default "1450-1500" — last 10 min
        ''' of the RTH session.
        ''' </summary>
        Public Property FlatWindow As String = "1450-1500"

        ''' <summary>Cooldown after an exit, in signal-timeframe bars. Default 3.</summary>
        Public Property CooldownBars As Integer = 3

        ''' <summary>Allow long entries.</summary>
        Public Property EnableLong As Boolean = True

        ''' <summary>Allow short entries.</summary>
        Public Property EnableShort As Boolean = True

        ' ── Broker SL-edit throttle (shared shape with UltimateScalperConfig) ─

        ''' <summary>
        ''' Broker SL-edit throttle: minimum improvement (in instrument ticks) before the
        ''' trail submits a live <c>EditPositionSlTpAsync</c> call. Default 1.
        ''' </summary>
        Public Property MinSlEditStepTicks As Integer = 1

        ''' <summary>
        ''' Broker SL-edit throttle: maximum live SL edits per second per position. Default 5.
        ''' </summary>
        Public Property MaxSlEditsPerSecond As Integer = 5

        ' ── Forward-compat seam ──────────────────────────────────────────────

        ''' <summary>
        ''' Forward-compat seam: maximum concurrent live positions the strategy may hold.
        ''' v1 clamps to 1 regardless of stored value (the orchestrator enforces this with a
        ''' warning log). Matches the Ultimate Scalper invariant.
        ''' </summary>
        Public Property MaxConcurrentPositions As Integer = 1

        ' ── Per-instrument overrides ─────────────────────────────────────────

        ''' <summary>
        ''' Optional per-instrument overrides to the ATR multiples. Always exactly three entries
        ''' for v1 (MES / MNQ / MGC). Every override field is nullable — null falls back to the
        ''' config-wide default.
        ''' </summary>
        Public Property InstrumentProfiles As List(Of SlipStreamInstrumentRiskProfile) = DefaultProfiles()

        ''' <summary>
        ''' Default per-instrument profiles. v1 ships with no overrides — every profile uses the
        ''' config-wide defaults. Profiles exist so a follow-up can tune per-symbol without
        ''' touching the global parameters.
        ''' </summary>
        Public Shared Function DefaultProfiles() As List(Of SlipStreamInstrumentRiskProfile)
            Return New List(Of SlipStreamInstrumentRiskProfile) From {
                New SlipStreamInstrumentRiskProfile With {.Symbol = "MES"},
                New SlipStreamInstrumentRiskProfile With {.Symbol = "MNQ"},
                New SlipStreamInstrumentRiskProfile With {.Symbol = "MGC"}
            }
        End Function

        ''' <summary>Returns the profile for the given symbol, or Nothing if not configured.</summary>
        Public Function GetProfile(symbol As String) As SlipStreamInstrumentRiskProfile
            If String.IsNullOrWhiteSpace(symbol) Then Return Nothing
            Return InstrumentProfiles.FirstOrDefault(
                Function(p) String.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Resolves the effective SL ATR multiple for a symbol (override-or-default).</summary>
        Public Function EffectiveAtrSLmult(symbol As String) As Double
            Dim p = GetProfile(symbol)
            Return If(p?.AtrSLmultOverride, AtrSLmult)
        End Function

        ''' <summary>Resolves the effective TP1 ATR multiple for a symbol (override-or-default).</summary>
        Public Function EffectiveAtrTP1mult(symbol As String) As Double
            Dim p = GetProfile(symbol)
            Return If(p?.AtrTP1multOverride, AtrTP1mult)
        End Function

        ''' <summary>Resolves the effective trail distance ATR multiple for a symbol.</summary>
        Public Function EffectiveTrailMult(symbol As String) As Double
            Dim p = GetProfile(symbol)
            Return If(p?.TrailMultOverride, TrailMult)
        End Function

        ''' <summary>Resolves the effective trail activation offset ATR multiple for a symbol.</summary>
        Public Function EffectiveTrailOffsetMult(symbol As String) As Double
            Dim p = GetProfile(symbol)
            Return If(p?.TrailOffsetMultOverride, TrailOffsetMult)
        End Function

    End Class

End Namespace
