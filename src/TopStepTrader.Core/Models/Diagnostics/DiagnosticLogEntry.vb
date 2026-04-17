Imports System.Text.Json.Serialization

Namespace TopStepTrader.Core.Models.Diagnostics

    ''' <summary>
    ''' Lightweight OHLC snapshot of a single bar used for bar-noise analysis.
    ''' The diagnostic engine captures the previous 3 completed bars so we can
    ''' compare the SL distance against normal market fluctuation ("noise floor").
    ''' </summary>
    Public Class DiagBarSnapshot
        Public Property Timestamp As String = String.Empty
        Public Property Open As Decimal
        Public Property High As Decimal
        Public Property Low As Decimal
        Public Property Close As Decimal
        ''' <summary>High − Low (total bar range including wicks).</summary>
        Public Property Range As Decimal
        ''' <summary>|Close − Open| (body size, excluding wicks).</summary>
        Public Property Body As Decimal
        ''' <summary>True when Close ≥ Open.</summary>
        Public Property IsBullish As Boolean
    End Class

    ''' <summary>
    ''' All indicator values and market micro-structure captured at the exact moment
    ''' of signal evaluation.  Populated for every event type (including NO_SIGNAL)
    ''' so the data set can be used for trend and market-regime analysis.
    ''' </summary>
    Public Class DiagMetricsAtEntry
        ''' <summary>RSI(7) value. Mode A: >50 bull / <50 bear. Mode B: <25 / >75.</summary>
        Public Property Rsi7 As Decimal
        ''' <summary>BB(12,2.0) upper band.</summary>
        Public Property BbUpper As Decimal
        ''' <summary>BB(12,2.0) middle band (SMA12).</summary>
        Public Property BbMiddle As Decimal
        ''' <summary>BB(12,2.0) lower band.</summary>
        Public Property BbLower As Decimal
        ''' <summary>%B: 0=lower band, 1=upper band, &lt;0=below, &gt;1=above.</summary>
        Public Property BbPercentB As Decimal
        ''' <summary>Band Width = (Upper − Lower) / Middle × 100. Squeeze indicator.</summary>
        Public Property BbWidth As Decimal
        ''' <summary>SMA(20) of BBW. Squeeze when BBW &lt; this value.</summary>
        Public Property BbWidthSma20 As Decimal
        ''' <summary>Consecutive bars where BBW &lt; SMA(BBW). ≥3 = active squeeze.</summary>
        Public Property BbSqueezeCount As Integer
        Public Property BbSqueezeActive As Boolean
        ''' <summary>EMA(5) — current bar value.</summary>
        Public Property Ema5Now As Decimal
        ''' <summary>EMA(5) — previous bar value (used to detect slope direction).</summary>
        Public Property Ema5Prev As Decimal
        ''' <summary>True when Ema5Now > Ema5Prev (rising momentum). Required for Mode A long.</summary>
        Public Property Ema5Rising As Boolean
        ''' <summary>ATR(10) value. Drives dynamic SL/TP when >0.</summary>
        Public Property Atr10 As Decimal
        ''' <summary>Bar close used as proxy for entry price. Refined in FinalizeDiagEntry for signal events.</summary>
        Public Property PriceEntry As Decimal
        ''' <summary>Spread in basis points = (Ask − Bid) / MidPrice × 10 000.</summary>
        Public Property SpreadBps As Decimal
        Public Property Bid As Decimal
        Public Property Ask As Decimal
        Public Property BarTimestamp As String = String.Empty
        Public Property BarOpen As Decimal
        Public Property BarHigh As Decimal
        Public Property BarLow As Decimal
        Public Property BarClose As Decimal
        Public Property BarRange As Decimal
        ''' <summary>Lower wick as fraction of total bar range (0.0–1.0). Mode B long filter: ≥0.60.</summary>
        Public Property BarLowerWickPct As Decimal
        ''' <summary>Upper wick as fraction of total bar range (0.0–1.0). Mode B short filter: ≥0.60.</summary>
        Public Property BarUpperWickPct As Decimal
    End Class

    ''' <summary>
    ''' Risk parameters (SL/TP) computed at the moment of signal evaluation.
    ''' Null for NO_SIGNAL entries.
    ''' </summary>
    Public Class DiagSettings
        Public Property InitialSlAmount As Decimal
        Public Property InitialTpAmount As Decimal
        Public Property SlPrice As Decimal
        Public Property TpPrice As Decimal
        ''' <summary>ATR | PCT — which formula determined SL/TP.</summary>
        Public Property SlSource As String = String.Empty
        Public Property RiskRewardRatio As Decimal
        ''' <summary>|Entry − SL| / ATR10. How many ATRs the stop is from entry.</summary>
        Public Property SlDistanceInAtr As Decimal
        ''' <summary>
        ''' The actual fill-price anchor used to compute SL/TP: ASK for BUY orders,
        ''' BID for SELL orders.  SL% and TP% are applied to this price so they
        ''' represent the intended monetary outcome from real entry cost, not mid-price.
        ''' </summary>
        Public Property EffectiveEntryPrice As Decimal
        ''' <summary>
        ''' Bid/ask spread expressed as a percentage of the ask price at signal time
        ''' ((Ask − Bid) / Ask × 100).  This is the immediate P&amp;L cost baked in
        ''' at entry — assets are already this % in loss the moment the order fills.
        ''' A SL% smaller than this value will be triggered immediately at entry.
        ''' </summary>
        Public Property EntryMarginCostPct As Decimal
    End Class

    ''' <summary>
    ''' Bar-noise comparison block.
    ''' The critical flag is IsSlInsideNoise: TRUE means ordinary price fluctuation
    ''' will likely hit the stop before the trade has room to breathe — a "Bad Settings" flag.
    ''' Partially populated for NO_SIGNAL entries (avg range + prev bars only).
    ''' Fully populated for TRADE and REJECT entries after FinalizeDiagEntry runs.
    ''' </summary>
    Public Class DiagNoiseCheck
        ''' <summary>Mean High−Low range of the 3 bars immediately before the signal bar.</summary>
        <JsonPropertyName("prev_3_bar_avg_range")>
        Public Property Prev3BarAvgRange As Decimal
        ''' <summary>TRUE when SL distance (absolute price) &lt; Prev3BarAvgRange.</summary>
        Public Property IsSlInsideNoise As Boolean
        ''' <summary>|Entry − SL| in absolute price units.</summary>
        Public Property SlDistanceAbs As Decimal
        ''' <summary>SlDistanceAbs / Prev3BarAvgRange. &lt;1.0 = SL inside the noise floor.</summary>
        Public Property NoiseRatio As Decimal
        ''' <summary>Spread / SlDistanceAbs. &gt;1.0 = spread alone exceeds stop distance.</summary>
        Public Property EffectiveSlippageRatio As Decimal
        ''' <summary>OHLC snapshots of the 3 bars preceding the signal bar.</summary>
        Public Property PrevBars As New List(Of DiagBarSnapshot)
    End Class

    ''' <summary>
    ''' Trade outcome — null until the position closes.
    ''' TRADE records are held in memory until the position closes, then written once
    ''' as a single complete JSON line with this block fully populated.
    ''' </summary>
    Public Class DiagOutcome
        ''' <summary>SL_HIT | TP_HIT | TRAIL_SL | TRAIL_TP | REVERSAL | NEUTRAL | ENGINE_STOPPED | OPEN</summary>
        Public Property Status As String = "OPEN"
        ''' <summary>Realised P&amp;L as a percentage of position size.</summary>
        Public Property PlPct As Decimal
        ''' <summary>Broker-reported P&amp;L in USD.</summary>
        Public Property PlUsd As Decimal
        ''' <summary>Max Favorable Excursion — best unrealised profit % seen at any 30-second tick.</summary>
        Public Property MaxFavorableExcursion As Decimal
        ''' <summary>Max Adverse Excursion — worst unrealised loss % seen at any 30-second tick.</summary>
        Public Property MaxAdverseExcursion As Decimal
        ''' <summary>Bar close price at which MFE was observed.</summary>
        Public Property MfePrice As Decimal
        ''' <summary>Bar close price at which MAE was observed.</summary>
        Public Property MaePrice As Decimal
        ''' <summary>Duration the position was held, in seconds.</summary>
        Public Property TradeLifetimeSeconds As Long
        ''' <summary>Estimated spread cost / |finalPnl|. High values flag spread-dominated losses.</summary>
        Public Property SpreadCostImpact As Decimal
    End Class

    ''' <summary>
    ''' High-fidelity diagnostic record — one per strategy evaluation tick (every 30 seconds).
    ''' Serialised as a single compact JSON line (JSONL) by DiagnosticLogger.
    '''
    ''' EventType values:
    '''   TRADE      — signal fired AND order sent; record is held in memory and written
    '''                complete with Outcome populated when the position closes.
    '''   REJECT     — signal fired BUT order was blocked; written immediately.
    '''   NO_SIGNAL  — no conditions met this tick; written immediately.
    '''                Settings and Outcome are null; NoiseCheck is partial (noise floor only).
    ''' </summary>
    Public Class DiagnosticLogEntry

        ''' <summary>
        ''' Unique identifier for this trade attempt.
        ''' TRADE records are written once on close with the same TradeId they were assigned
        ''' at signal time, enabling easy correlation in analysis tools.
        ''' </summary>
        Public Property TradeId As String = Guid.NewGuid().ToString("N")

        ''' <summary>TRADE | REJECT | NO_SIGNAL</summary>
        Public Property EventType As String = "NO_SIGNAL"

        ''' <summary>
        ''' ISO-8601 UTC timestamp with millisecond precision.
        ''' For TRADE records this is the CLOSE time (when the record is written).
        ''' </summary>
        Public Property Timestamp As String = DateTimeOffset.UtcNow.ToString("o")

        ''' <summary>8-character uppercase session ID (matches the JSONL filename suffix).</summary>
        Public Property SessionId As String = String.Empty

        ''' <summary>Contract / instrument identifier (e.g. "OIL", "SPX500").</summary>
        Public Property Symbol As String = String.Empty

        ''' <summary>BUY | SELL | NONE</summary>
        Public Property Action As String = String.Empty

        ''' <summary>
        ''' Strategy name, including mode when determined:
        '''   "BB Squeeze Scalper (Mode A)" — squeeze breakout
        '''   "BB Squeeze Scalper (Mode B)" — band bounce
        '''   "BB Squeeze Scalper"           — mode not yet determined (NO_SIGNAL tick)
        ''' </summary>
        Public Property Strategy As String = String.Empty

        ''' <summary>
        ''' Human-readable "internal monologue" — which conditions passed and which failed.
        ''' Example: "Mode A LONG ✓ | squeeze=5bars | Close=5021.3>Upper=5019.8 | EMA5↑ | RSI7=56.2>50"
        ''' </summary>
        Public Property Why As String = String.Empty

        ''' <summary>Why the signal was blocked (REJECT events only).</summary>
        Public Property RejectionReason As String = String.Empty

        ''' <summary>Indicator and market micro-structure snapshot.  Populated for all event types.</summary>
        Public Property MetricsAtEntry As DiagMetricsAtEntry = Nothing

        ''' <summary>SL/TP risk parameters.  Null for NO_SIGNAL.</summary>
        Public Property Settings As DiagSettings = Nothing

        ''' <summary>
        ''' Bar-noise analysis.  Partially populated for all events (avg range + prev bars).
        ''' Fully populated (with SL-distance-derived flags) for TRADE and REJECT only.
        ''' </summary>
        Public Property NoiseCheck As DiagNoiseCheck = Nothing

        ''' <summary>Trade outcome.  Null until the position closes (TRADE events only).</summary>
        Public Property Outcome As DiagOutcome = Nothing

    End Class

End Namespace
