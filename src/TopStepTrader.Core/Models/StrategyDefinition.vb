Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Fully-parsed trading strategy ready for execution by StrategyExecutionEngine.
    ''' Created either from a pre-loaded template or from natural-language parsing.
    ''' </summary>
    Public Class StrategyDefinition

        ' ── Instrument & account ──────────────────────────────────────────
        Public Property Name As String = "Custom Strategy"
        Public Property ContractId As String = String.Empty
        Public Property AccountId As Long
        Public Property Quantity As Integer = 1

        ' ── Time parameters ───────────────────────────────────────────────
        Public Property TimeframeMinutes As Integer = 5
        Public Property DurationHours As Double = 8.0

        ''' <summary>
        ''' Minute-precise UTC window start (inclusive). When both fields are set, entry orders are
        ''' suppressed outside this window. Nothing = no restriction. Complements TradingStartHourUtc
        ''' with minute-level granularity (e.g. New TimeOnly(8, 0) for 08:00 UTC).
        ''' </summary>
        Public Property TradingWindowUtcStart As TimeOnly? = Nothing

        ''' <summary>
        ''' Minute-precise UTC window end (inclusive). Nothing = no restriction.
        ''' </summary>
        Public Property TradingWindowUtcEnd As TimeOnly? = Nothing

        ' ── Indicator ─────────────────────────────────────────────────────
        Public Property Indicator As StrategyIndicatorType = StrategyIndicatorType.BollingerBands
        Public Property IndicatorPeriod As Integer = 20
        Public Property IndicatorMultiplier As Double = 2.0
        ''' <summary>Secondary EMA period (used for EMA Crossover strategies).</summary>
        Public Property SecondaryPeriod As Integer = 21

        ' ── Entry condition ───────────────────────────────────────────────
        Public Property Condition As StrategyConditionType = StrategyConditionType.FullCandleOutsideBands
        Public Property GoLongWhenBelowBands As Boolean = True
        Public Property GoShortWhenAboveBands As Boolean = True

        ' ── Exit strategy — Turtle Bracket (all execution paths) ─────────────────
        ''' <summary>
        ''' Initial stop-loss as a multiple of N (ATR in dollar terms).
        ''' SL dollars = SlMultipleOfN × ATR × DollarPerPoint.
        ''' Profile defaults — Lewis (Averse): 1.5  Damian (Moderate): 1.0  Joe (Aggressive): 0.75
        ''' </summary>
        Public Property SlMultipleOfN As Decimal = 1.0D

        ''' <summary>
        ''' Initial take-profit as a multiple of N (ATR in dollar terms).
        ''' TP dollars = TpMultipleOfN × ATR × DollarPerPoint.
        ''' Subsequent bracket steps always advance by +0.5 × N (fixed Turtle increment),
        ''' regardless of which profile is active.
        ''' Profile defaults — Lewis (Averse): 3.0  Damian (Moderate): 2.0  Joe (Aggressive): 1.5
        ''' </summary>
        Public Property TpMultipleOfN As Decimal = 2.0D

        ''' <summary>Minimum price increment for the selected contract (e.g. 0.25 for MES/MNQ).</summary>
        Public Property TickSize As Decimal = 1D

        ''' <summary>Dollar value of one tick move (e.g. MES = $1.25, MGC = $1.00). Used for P&amp;L display.</summary>
        Public Property TickValue As Decimal = 1D

        ' ── TopStepX futures ──────────────────────────────────────────────
        ''' <summary>TopStepX only: number of contracts per order (default 1 during testing).</summary>
        Public Property Contracts As Integer = 1

        ''' <summary>TopStepX only: initial stop-loss distance in ticks from the entry fill price.
        ''' When set, the engine attaches a stopLossBracket to the entry order and uses this
        ''' value as the SL seed for any trailing logic.
        ''' </summary>
        Public Property InitialStopTicks As Integer?

        ' ── Signal filtering ──────────────────────────────────────────────
        ''' <summary>
        ''' Minimum weighted-score confidence required to fire a trade signal (0–100, default 75).
        ''' The EMA/RSI engine computes upPct/downPct in the range 0–100.
        ''' A trade is only placed when upPct >= MinConfidencePct (Long) or
        ''' downPct >= MinConfidencePct (Short). Set from UI by the user.
        ''' </summary>
        Public Property MinConfidencePct As Integer = 85

        ''' <summary>
        ''' Minimum ADX(14) value required for the trend-strength gate to pass.
        ''' EMA/RSI path: suppresses entry when ADX &lt; AdxThreshold (ranging market).
        ''' Multi-Confluence path: conditions lc5/sc5 require ADX ≥ AdxThreshold.
        ''' Profile defaults — Lewis (Averse): 25  Damian (Moderate): 20  Joe (Aggressive): 15
        ''' </summary>
        Public Property AdxThreshold As Single = 20.0F

        ''' <summary>
        ''' STRAT-27: Minimum MACD histogram magnitude as a fraction of ATR(14).
        ''' Passed to MultiConfluenceStrategy.Evaluate. Default 0.05 (Damian baseline).
        ''' Lewis = 0.07 (higher bar for entry); Joe = 0.03 (more signals allowed).
        ''' </summary>
        Public Property MacdHistMinAtrFraction As Double = 0.05

        ''' <summary>
        ''' Maximum number of additional scale-in positions after the initial entry.
        ''' Profile defaults — Lewis (Averse): 1  Damian (Moderate): 2  Joe (Aggressive): 3
        ''' </summary>
        Public Property MaxScaleIns As Integer = 2

        ''' <summary>
        ''' Earliest UTC hour at which new entry orders are permitted (0–23).
        ''' Default 0 = no lower bound — the TopStepX pre-close blackout (19:50–22:00 UTC) is
        ''' enforced centrally in StrategyExecutionEngine regardless of this value.
        ''' Set both TradingStartHourUtc and TradingEndHourUtc to 0 to disable the filter.
        ''' Position management (SL trail, reconciliation) continues outside trading hours.
        ''' </summary>
        Public Property TradingStartHourUtc As Integer = 0

        ''' <summary>
        ''' Latest UTC hour (exclusive) at which new entry orders are permitted (0–23).
        ''' Default 0 = no upper bound — the TopStepX pre-close blackout (19:50–22:00 UTC) is
        ''' enforced centrally in StrategyExecutionEngine regardless of this value.
        ''' Set both TradingStartHourUtc and TradingEndHourUtc to 0 to disable the filter.
        ''' </summary>
        Public Property TradingEndHourUtc As Integer = 0

        ''' <summary>
        ''' Maximum cumulative realised session loss in USD before all new entries are blocked.
        ''' 0 = disabled. E.g. 300 = circuit-breaker fires at -$300 session P&amp;L.
        ''' Position management continues regardless — only new entries are suppressed.
        ''' </summary>
        Public Property MaxDailyLossUsd As Decimal = 0D

        ' ── Runtime state (set when engine starts) ────────────────────────
        Public Property ExpiresAt As DateTimeOffset

        ' ── Provenance ────────────────────────────────────────────────────
        ''' <summary>Original natural-language description (empty for pre-loaded templates).</summary>
        Public Property RawDescription As String = String.Empty

        ''' <summary>
        ''' When True, the live engine advances the TP by one TpMultipleOfN × ATR unit each time
        ''' a bar closes at or beyond the current TP price (up to 3 advances per trade).
        ''' Mirrors the backtest Extend-TP-on-close feature; lets winning trades run further.
        ''' Backtest winner config: Multi-Confluence · Damian · OIL · 5-min — this was ON.
        ''' </summary>
        Public Property ExtendTpOnClose As Boolean = False

        ' ── STRAT-30: Regime overrides ───────────────────────────────────────────
        ''' <summary>
        ''' STRAT-30: When both overrides are set, the engine classifies the market regime each bar
        ''' (Trending = ATR ≥ its 20-bar SMA AND ADX ≥ AdxThreshold) and substitutes this strategy
        ''' in trending conditions.  Nothing = feature disabled (base Condition is used as-is).
        ''' </summary>
        Public Property TrendingStrategyOverride As StrategyConditionType? = Nothing

        ''' <summary>
        ''' STRAT-30: Strategy to use when regime = Ranging (ATR contracting or ADX &lt; threshold).
        ''' Only active when <see cref="TrendingStrategyOverride"/> is also set.
        ''' Nothing = feature disabled.
        ''' </summary>
        Public Property RangingStrategyOverride As StrategyConditionType? = Nothing

        ' ── STRAT-38: Phased ATR trail multipliers ───────────────────────────────
        ''' <summary>
        ''' Phase 2: Free Roll activation threshold as a multiple of N (ATR).
        ''' Favourable price movement >= BreakevenTriggerMultipleOfN x ATR arms the Free Roll.
        ''' The per-contract PhasedTrailBreakevenMinTicks supplies a hard floor.
        ''' Article: 1.0-1.5; default 1.2.
        ''' Setting 0 = use the legacy "50% of TP tick distance" gate.
        ''' </summary>
        Public Property BreakevenTriggerMultipleOfN As Decimal = 1.2D

        ''' <summary>
        ''' Phase 3: Chandelier trail distance as a multiple of N (ATR).
        ''' After Free Roll arms, the SL is held at MAX(highestHigh_since_entry) -
        ''' TrailingStopMultipleOfN x ATR (for longs; mirrored for shorts).
        ''' Watermark is updated from bar OHLC each bar-check tick.
        ''' Article: 2.0; default 2.0.
        ''' </summary>
        Public Property TrailingStopMultipleOfN As Decimal = 2.0D

        ''' <summary>
        ''' UTC hour of day at which all open positions are flattened and the engine refuses
        ''' new entries for the rest of the session.
        ''' Default 20 = 20:00 UTC = 15:00 CT, 10 min before TopStepX 20:10 UTC maintenance.
        ''' Set to 0 with EndOfDayFlattenMinuteUtc=0 to disable the EOD flatten.
        ''' Once fired, the engine stays flat until restart (no automatic daily re-arm).
        ''' </summary>
        Public Property EndOfDayFlattenHourUtc As Integer = 20

        ''' <summary>UTC minute for the EOD flatten. Default 0.</summary>
        Public Property EndOfDayFlattenMinuteUtc As Integer = 0

        ''' <summary>
        ''' When True, the engine calls Claude Haiku for a pre-trade macro/session sanity check
        ''' immediately before placing an entry order. A VETO response suppresses the trade and
        ''' logs the AI rationale. On API failure the engine defaults to PROCEED so a connectivity
        ''' issue never silently blocks all trading. Adds ~$0.01 per signal. Default True.
        ''' </summary>
        Public Property UsePreTradeAiCheck As Boolean = True

        ''' <summary>
        ''' Alias for <see cref="UsePreTradeAiCheck"/>. When False, the pre-trade Haiku call is
        ''' skipped entirely — no log line, no API call. Set False for sub-second strategies
        ''' (PumpNDump, Sniper) where the gate latency is unacceptable.
        ''' </summary>
        Public Property UseAiPreTradeGate As Boolean = True

        ''' <summary>
        ''' Active persona name applied to this strategy ("Lewis", "Damian", "Joe", or empty).
        ''' Passed to the pre-trade AI check for richer context. Set by ViewModels when
        ''' ApplyRiskProfile / BuildStrategyDefinition creates the definition.
        ''' </summary>
        Public Property PersonaName As String = String.Empty

        ''' <summary>
        ''' Returns Name so WPF ComboBoxes display the strategy name as a fallback
        ''' when no DataTemplate / DisplayMemberPath is active.
        ''' </summary>
        Public Overrides Function ToString() As String
            Return Name
        End Function

        ''' <summary>Human-readable one-line summary for display in the parsed-parameters panel.</summary>
        Public ReadOnly Property Summary As String
            Get
                Dim indicator As String
                Select Case Me.Indicator
                    Case StrategyIndicatorType.BollingerBands : indicator = $"BB({IndicatorPeriod},{IndicatorMultiplier})"
                    Case StrategyIndicatorType.RSI : indicator = $"RSI({IndicatorPeriod})"
                    Case StrategyIndicatorType.EMA : indicator = $"EMA({IndicatorPeriod}/{SecondaryPeriod})"
                    Case Else : indicator = Me.Indicator.ToString()
                End Select

                Dim directions As String
                If GoLongWhenBelowBands AndAlso GoShortWhenAboveBands Then
                    directions = "Long+Short"
                ElseIf GoLongWhenBelowBands Then
                    directions = "Long only"
                Else
                    directions = "Short only"
                End If

                Dim tp = If(TpMultipleOfN > 0D, $"TP:{TpMultipleOfN:F2}N", "No TP")
                Dim sl = If(SlMultipleOfN > 0D, $"SL:{SlMultipleOfN:F2}N", "No SL")

                Return $"{indicator} | {TimeframeMinutes}-min | {DurationHours}hrs | {directions} | {tp} {sl}"
            End Get
        End Property

    End Class

End Namespace
