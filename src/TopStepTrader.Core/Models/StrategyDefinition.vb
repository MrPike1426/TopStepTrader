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
        Public Property CapitalAtRisk As Decimal = 500D
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
        ''' Initial stop-loss in dollars (e.g. 10 = $10 hard stop for Bracket 0).
        ''' Turtle bracket SL only ever advances in the favourable direction; never retreats.
        ''' Engine converts to an absolute price: Entry ± (SlDollarBracket / DollarPerPoint).
        ''' </summary>
        Public Property SlDollarBracket As Decimal = 10D

        ''' <summary>
        ''' Initial take-profit target in dollars (e.g. 20 = $20 triggers first bracket advance).
        ''' Once hit, SL steps to the TP level and a new TP is set at TP + 0.5×N (ATR in $).
        ''' Engine converts to an absolute price: Entry ± (TpDollarBracket / DollarPerPoint).
        ''' Used as ATR-unavailable fallback only when <see cref="TpMultipleOfN"/> is active.
        ''' </summary>
        Public Property TpDollarBracket As Decimal = 20D

        ''' <summary>
        ''' Initial stop-loss as a multiple of N (ATR in dollar terms).
        ''' SL dollars = SlMultipleOfN × ATR × DollarPerPoint.
        ''' When ATR &gt; 0 this overrides <see cref="SlDollarBracket"/>; falls back to that
        ''' fixed amount when ATR is unavailable (e.g. indicator warm-up period).
        ''' Profile defaults — Lewis (Averse): 1.5  Damian (Moderate): 1.0  Joe (Aggressive): 0.75
        ''' </summary>
        Public Property SlMultipleOfN As Decimal = 1.0D

        ''' <summary>
        ''' Wider stop-loss multiple applied automatically when the instrument is a leveraged
        ''' CFD.  Not used for TopStepX exchange-margined futures.
        ''' The execution engine scales position size down by SlMultipleOfN ÷ LeveragedSlMultipleOfN
        ''' so the dollar risk at the SL equals the non-leveraged equivalent.
        ''' 0 = feature disabled (fall back to SlMultipleOfN for all orders).
        ''' Profile defaults — Lewis (Averse): 2.5  Damian (Moderate): 2.0  Joe (Aggressive): 1.5
        ''' </summary>
        Public Property LeveragedSlMultipleOfN As Decimal = 0D

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
        ''' <summary>
        ''' TopStepX only: number of contracts per order (default 1 during testing).
        ''' Ignored for eToro (which uses <see cref="CapitalAtRisk"/> instead).
        ''' </summary>
        Public Property Contracts As Integer = 1

        ''' <summary>
        ''' TopStepX only: initial stop-loss distance in ticks from the entry fill price.
        ''' When set, the engine attaches a stopLossBracket to the entry order and uses this
        ''' value as the SL seed for any trailing logic.
        ''' When Nothing, the engine derives ticks from <see cref="SlDollarBracket"/> / TickValue.
        ''' </summary>
        Public Property InitialStopTicks As Integer?

        ''' <summary>Leverage multiplier sent to eToro (default 1 = no leverage).
        ''' Affects both the effective position size and the minimum cash required:
        ''' minCash = MinNotionalUsd / Leverage.</summary>
        Public Property Leverage As Integer = 1

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
        ''' Default 6 = 06:00 UTC (07:00 BST) — blocks thin CME Globex overnight session.
        ''' Set both TradingStartHourUtc and TradingEndHourUtc to 0 to disable the filter.
        ''' Position management (SL trail, reconciliation) continues outside trading hours.
        ''' </summary>
        Public Property TradingStartHourUtc As Integer = 6

        ''' <summary>
        ''' Latest UTC hour (exclusive) at which new entry orders are permitted (0–23).
        ''' Default 21 = covers through the US equity close.
        ''' Set both TradingStartHourUtc and TradingEndHourUtc to 0 to disable the filter.
        ''' </summary>
        Public Property TradingEndHourUtc As Integer = 21

        ''' <summary>
        ''' Maximum cumulative realised session loss in USD before all new entries are blocked.
        ''' 0 = disabled. E.g. 300 = circuit-breaker fires at -$300 session P&amp;L.
        ''' Position management continues regardless — only new entries are suppressed.
        ''' </summary>
        Public Property MaxDailyLossUsd As Decimal = 0D

        ''' <summary>
        ''' Cash amount per scale-in trade (EmaRsiWeightedScore only). Default $200.
        ''' Set from the Scale-In panel in the UI before the engine starts.
        ''' </summary>
        Public Property ScaleInAmount As Decimal = 200D
        ''' <summary>
        ''' Leverage multiplier applied to each scale-in trade (default 5).
        ''' Set from the Scale-In panel in the UI before the engine starts.
        ''' </summary>
        Public Property ScaleInLeverage As Integer = 5

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

                Dim tp = If(TpMultipleOfN > 0D, $"TP:{TpMultipleOfN:F2}N", If(TpDollarBracket > 0, $"TP:${TpDollarBracket:F0}", "No TP"))
                Dim slBase = If(SlMultipleOfN > 0D, $"SL:{SlMultipleOfN:F2}N", If(SlDollarBracket > 0, $"SL:${SlDollarBracket:F0}", "No SL"))
                Dim sl = If(LeveragedSlMultipleOfN > 0D, $"{slBase} ({LeveragedSlMultipleOfN:F2}N lev)", slBase)

                Return $"{indicator} | {TimeframeMinutes}-min | {DurationHours}hrs | {directions} | {tp} {sl}"
            End Get
        End Property

    End Class

End Namespace
