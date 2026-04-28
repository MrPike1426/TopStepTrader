Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Multi-Confluence Engine for commodities trading via the TopStepX API.
    '''
    ''' Combines nine independent signals on 15-minute bars:
    '''   1. Ichimoku Cloud (9 / 26 / 52 / displacement 26) + cloud twist pre-filter
    '''   2. EMA 21  (primary trend)
    '''   2b. EMA 50  (big-picture trend anchor — active gate condition)
    '''   3. Tenkan/Kijun cross
    '''   4. MACD histogram (8 / 17 / 9 — intraday-tuned)
    '''   5. Stochastic RSI (14 / 14)
    '''   6. DMI / ADX (14) trend-strength gate
    '''   7. Ichimoku Lagging Span (Chikou) confirmation
    '''   8. Volume confirmation (current bar ≥ 1.2× 20-bar average)
    '''
    ''' ALL nine long conditions must align for a Long entry;
    ''' all nine short conditions must align for a Short entry.
    ''' SL = min(1.5×ATR, Ichimoku cloud edge); TP = 2:1 reward-to-risk.
    ''' Minimum <see cref="MinBarsRequired"/> bars required for full warm-up.
    '''
    ''' NOTE (STRAT-23): This class has no time-of-day (TOD) or session awareness.
    ''' The CME daily maintenance window (17:00–18:00 ET), news blackouts, and
    ''' contract-roll windows are enforced upstream in
    ''' <see cref="StrategyExecutionEngine.DoCheckAsync"/> — look for the
    ''' "STRAT-23: TOD gate" comment block in the MultiConfluence Case branch.
    ''' </summary>
    Public NotInheritable Class MultiConfluenceStrategy

        Private Sub New()
        End Sub

        ' ── Ichimoku periods (per specification) ──────────────────────────────────
        Private Const TenkanPeriod As Integer = 9
        Private Const KijunPeriod As Integer = 26
        Private Const SenkouBPeriod As Integer = 52
        Private Const IchimokuDisplacement As Integer = 26        ''' <summary>
        ''' Minimum number of bars required for all indicators to fully warm up.
        ''' Senkou Span B needs senkouBPeriod(52) + displacement(26) = 78 bars minimum;
        ''' an 80-bar buffer provides a small safety margin.
        ''' </summary>
        Public Const MinBarsRequired As Integer = 80

        ''' <summary>
        ''' Evaluates all nine confluence conditions and returns a trade signal.
        ''' Returns a <see cref="MultiConfluenceResult"/> with Side = Nothing when no signal fires.
        ''' </summary>
        ''' <param name="highs">Bar high prices.</param>
        ''' <param name="lows">Bar low prices.</param>
        ''' <param name="closes">Bar close prices.</param>
        ''' <param name="volumes">Bar volumes (used for condition 8 volume gate).</param>
        ''' <param name="config">
        ''' Threshold configuration (STRAT-24). When Nothing the default values reproduce the
        ''' original hard-coded constants exactly, ensuring snapshot-identical behaviour.
        ''' </param>
        ''' <param name="adxThreshold">
        ''' Minimum ADX(14) for condition 5 (trend-strength gate). Defaults to 20 (Damian profile).
        ''' Lewis (Averse) = 25; Joe (Aggressive) = 15.
        ''' Ignored when <paramref name="config"/> is supplied — use config.AdxThreshold instead.
        ''' </param>
        Public Shared Function Evaluate(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                volumes As IList(Of Decimal),
                Optional config As Core.Settings.MultiConfluenceConfig = Nothing,
                Optional adxThreshold As Single = 20.0F,
                Optional macdHistMinAtrFraction As Double = 0.05) As MultiConfluenceResult

            ' Resolve effective config: caller-supplied > legacy optional params > defaults
            Dim cfg As Core.Settings.MultiConfluenceConfig
            If config IsNot Nothing Then
                cfg = config
            Else
                cfg = New Core.Settings.MultiConfluenceConfig() With {
                    .AdxThreshold           = adxThreshold,
                    .MacdHistMinAtrFraction = macdHistMinAtrFraction
                }
            End If

            Dim result As New MultiConfluenceResult()
            Dim n = closes.Count

            If n < MinBarsRequired Then
                result.StatusLine = $"Warming up — {n}/{MinBarsRequired} bars required"
                Return result
            End If

            ' ── Compute all indicators ────────────────────────────────────────────
            Dim ichi    = TechnicalIndicators.IchimokuCloud(highs, lows, closes,
                              cfg.TenkanPeriod, cfg.KijunPeriod, cfg.SenkouBPeriod, cfg.IchimokuDisplacement)
            Dim ema21Arr = TechnicalIndicators.EMA(closes, 21)
            Dim ema50Arr = TechnicalIndicators.EMA(closes, 50)
            Dim dmi     = TechnicalIndicators.DMI(highs, lows, closes)
            Dim macd    = TechnicalIndicators.MACD(closes,
                              fastPeriod:=cfg.MacdFastPeriod, slowPeriod:=cfg.MacdSlowPeriod, signalPeriod:=cfg.MacdSignalPeriod)
            Dim stochRsi = TechnicalIndicators.StochasticRSI(closes)
            Dim atrArr  = TechnicalIndicators.ATR(highs, lows, closes, 14)

            Dim lagIdx = n - 1 - cfg.IchimokuDisplacement

            ' ── Build inputs struct ───────────────────────────────────────────────
            Dim volAvg = If(n >= 21, volumes.Skip(n - 21).Take(20).Average(), 0D)
            Dim inputs As New Core.Trading.MultiConfluenceInputs With {
                .SpanA         = ichi.SpanA(n - 1),
                .SpanB         = ichi.SpanB(n - 1),
                .Tenkan        = ichi.Tenkan(n - 1),
                .Kijun         = ichi.Kijun(n - 1),
                .LagClose      = If(lagIdx >= 0, closes(lagIdx), Decimal.MinValue),
                .LagSpanA      = If(lagIdx >= 0, ichi.SpanA(lagIdx), Single.NaN),
                .LagSpanB      = If(lagIdx >= 0, ichi.SpanB(lagIdx), Single.NaN),
                .Ema21         = TechnicalIndicators.LastValid(ema21Arr),
                .Ema50         = TechnicalIndicators.LastValid(ema50Arr),
                .Adx           = TechnicalIndicators.LastValid(dmi.ADX),
                .PlusDI        = TechnicalIndicators.LastValid(dmi.PlusDI),
                .MinusDI       = TechnicalIndicators.LastValid(dmi.MinusDI),
                .MacdHistNow   = TechnicalIndicators.LastValid(macd.Histogram),
                .MacdHistPrev  = TechnicalIndicators.PreviousValid(macd.Histogram),
                .StochK        = TechnicalIndicators.LastValid(stochRsi.K),
                .StochKPrev    = TechnicalIndicators.PreviousValid(stochRsi.K),
                .Atr           = TechnicalIndicators.LastValid(atrArr),
                .LastClose     = closes(n - 1),
                .VolMa20       = volAvg,
                .CurrentVolume = volumes(n - 1),
                .Config        = cfg
            }

            ' ── Delegate to single shared evaluator (ARCH-04) ────────────────────
            Return s_evaluator.Evaluate(inputs)
        End Function

        Private Shared ReadOnly s_evaluator As New MultiConfluenceEvaluator()

    End Class

End Namespace
