Namespace TopStepTrader.Core.Enums

    ''' <summary>
    ''' Entry condition that must be met before the strategy fires an order.
    ''' </summary>
    Public Enum StrategyConditionType
        ''' <summary>Entire candle (High + Low) is outside the Bollinger Bands.</summary>
        FullCandleOutsideBands = 0
        ''' <summary>Close price crosses outside the Bollinger Bands.</summary>
        CloseOutsideBands = 1
        ''' <summary>RSI drops below 30 (oversold) — triggers a Long.</summary>
        RSIOversold = 2
        ''' <summary>RSI rises above 70 (overbought) — triggers a Short.</summary>
        RSIOverbought = 3
        ''' <summary>Faster EMA crosses above slower EMA — triggers a Long.</summary>
        EMACrossAbove = 4
        ''' <summary>Faster EMA crosses below slower EMA — triggers a Short.</summary>
        EMACrossBelow = 5
        ''' <summary>
        ''' Six-signal weighted score: EMA21/EMA50 crossover (25%), price vs EMA21 (20%),
        ''' price vs EMA50 (15%), RSI gradient (20%), EMA21 momentum (10%), recent candles (10%).
        ''' Buy when score &gt; 60%, Sell when score &lt; 40%.
        ''' </summary>
        EmaRsiWeightedScore = 6
        ''' <summary>
        ''' 3-EMA Cascade (Sniper): EMA8/EMA21/EMA50 on 1-minute bars.
        ''' Long when EMA8 crosses above EMA21 AND price is above rising EMA50.
        ''' Short when EMA8 crosses below EMA21 AND price is below falling EMA50.
        ''' Supports pyramiding scale-in up to 10 contracts.
        ''' </summary>
        TripleEmaCascade = 7
        ''' <summary>
        ''' Multi-Confluence Engine: Ichimoku Cloud (9/26/52) + EMA21/50 + MACD(12/26/9) +
        ''' Stochastic RSI(14) + DMI/ADX(14). Designed for 15-minute commodity bars.
        ''' ALL seven long conditions must align for a Long; all seven short conditions for a Short.
        ''' SL = min(1.5×ATR, Ichimoku cloud edge); TP = 2:1 reward-to-risk.
        ''' </summary>
        MultiConfluence = 8
        ''' <summary>
        ''' LULT Divergence: WaveTrend (Market Cipher B) Anchor/Trigger momentum-price divergence.
        ''' 6-step confirmation gate on 5-minute NQ bars.
        ''' SL = trigger wave extreme ± ATR-scaled tick buffer; TP = 2R.
        ''' Time-filtered to 11:00–17:00 UTC (London + NY pre-market, 07:00–13:00 EST/EDT).
        ''' </summary>
        LultDivergence = 9
        ''' <summary>
        ''' BB Squeeze Scalper: dual-mode Bollinger Band scalping strategy on 1-minute bars.
        ''' Mode A (Squeeze Breakout): BBW &lt; SMA(BBW,20) for ≥3 bars → close breaks band →
        '''   EMA5 slope confirms → RSI7 confirms direction. Momentum trade in breakout direction.
        ''' Mode B (Band Bounce): BBW ≥ SMA(BBW,20) → %B &lt; 0 or &gt; 1 → RSI7 extreme (&lt;25 / &gt;75) →
        '''   rejection wick &gt; 60% of bar range. Mean-reversion fade back toward middle band.
        ''' TP = 0.4%; SL = 0.2% (2:1 R:R). 15-second polling interval. Max leverage.
        ''' </summary>
        BbSqueezeScalper = 10
        ''' <summary>
        ''' VIDYA Cross: price crosses above the Variable Index Dynamic Average → Long;
        ''' price crosses below → Short.  Dynamic alpha = EMA-alpha × |CMO/100| so the
        ''' line reacts fast during trends and barely moves during chop.
        ''' IndicatorPeriod = VIDYA EMA length (default 14).
        ''' SecondaryPeriod  = CMO momentum period (default 9).
        ''' </summary>
        VidyaCross = 15

        ''' <summary>
        ''' Naked Trader: 4-vote consensus from EMA(9/21), MACD(8,17,9), DMI/ADX(14), and VWAP.
        ''' Long when Confidence is Medium/High and Direction is Up.
        ''' Short when Confidence is Medium/High and Direction is Down.
        ''' Uses 5-minute bars; minimum 28 bars required, 40 recommended.
        ''' </summary>
        NakedTrader = 16

        ''' <summary>
        ''' Double Bubble Butt (Double Bollinger Band): Two BB sets over SMA(20) — inner at 1.0 SD,
        ''' outer at 2.0 SD — define three trading zones.
        ''' Buy Zone  (uptrend):   price closes above upper 1.0 SD band → Long entry.
        ''' Sell Zone (downtrend): price closes below lower 1.0 SD band → Short entry.
        ''' Neutral Zone:          price closes back inside the 1.0 SD bands → Exit signal.
        ''' Hard SL = outer 2.0 SD band at entry; TP = 2× ATR(20) from entry.
        ''' </summary>
        DoubleBubbleButt = 17

        ''' <summary>
        ''' Opening Range Breakout (ORB): first 30 minutes of the regular session establish the
        ''' opening range high and low.
        ''' Long  when bar closes above OR high with volume ≥ 1.2× 20-bar average.
        ''' Short when bar closes below OR low  with volume ≥ 1.2× 20-bar average.
        ''' SL = opposite extreme of the opening range; TP = 1.5× OR width (1:1.5 R:R minimum).
        ''' No-trade filters: OR width &gt; 2× ATR(14) (too wide), or past the session midpoint.
        ''' Best instruments: MNQ, MES (NY open momentum), MGC (Gold 08:20–10:30 ET window).
        ''' </summary>
        OpeningRangeBreakout = 18

        ''' <summary>
        ''' Pump-n-Dump: 3 consecutive 1-minute bars all closing in the same direction.
        ''' Long  when the last 3 bars all have Close &gt; Open (3 green bars → momentum pump).
        ''' Short when the last 3 bars all have Close &lt; Open (3 red bars  → momentum dump).
        ''' SL/TP are ATR-based (SlAtrMultiple / TpAtrMultiple on BacktestConfiguration).
        ''' </summary>
        PumpNDump = 19

        ''' <summary>
        ''' VWAP Mean Reversion: institutional midday strategy (10am–2pm ET window).
        ''' Session-anchored VWAP ± 1.5 and 2.0 standard deviations define the reversion zones.
        ''' Long  when close ≤ VWAP − 1.5 SD (oversold vs institutional average cost).
        ''' Short when close ≥ VWAP + 1.5 SD (overbought vs institutional average cost).
        ''' Exit at VWAP (mean reversion target); SL beyond the 2.0 SD band.
        ''' No-trade filter: outside 10am–2pm ET (momentum hours suppressed).
        ''' Best instruments: MES, MNQ (midday chop with high institutional participation).
        ''' Academic win rate: 60–65%; small average winner — tight stops required.
        ''' </summary>
        VwapMeanReversion = 20
    End Enum

End Namespace
