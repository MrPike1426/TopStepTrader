Namespace TopStepTrader.Core.Enums

    ''' <summary>
    ''' Technical indicator used by a trading strategy.
    ''' </summary>
    Public Enum StrategyIndicatorType
        BollingerBands = 0
        RSI = 1
        MACD = 2
        EMA = 3
        ''' <summary>Combined EMA21/EMA50/RSI14 weighted scoring — same signals as Test Trade tab.</summary>
        EmaRsiCombined = 4
        ''' <summary>Triple EMA cascade (EMA8/EMA21/EMA50) on 1-minute bars — Sniper strategy.</summary>
        TripleEma = 5
        ''' <summary>
        ''' Multi-Confluence Engine: Ichimoku Cloud + EMA21/50 + MACD + Stochastic RSI + DMI/ADX.
        ''' All seven conditions must align before an entry signal fires.
        ''' </summary>
        MultiConfluence = 6
        ''' <summary>
        ''' LULT Divergence: WaveTrend (Market Cipher B simulation) Anchor/Trigger wave divergence.
        ''' 6-step gate — Anchor (WT1 ≷ ±60) → Trigger (shallower) → price divergence →
        ''' Green/Red Dot (WT1×WT2 cross) → engulfing volume candle.
        ''' Optimised for NQ 5-minute bars; time-filtered to 11:00–17:00 UTC (London + NY pre-market).
        ''' </summary>
        LultDivergence = 7
        ''' <summary>
        ''' BB Squeeze Scalper: Bollinger Bands (12,2.0) + Band Width + %B + RSI(7) + EMA(5) + ATR(10).
        ''' Dual-mode: Squeeze Breakout (momentum) or Band Bounce (mean-reversion).
        ''' 1-minute bars; 15-second polling; tight 0.4% TP / 0.2% SL scalp targets.
        ''' </summary>
        BbSqueezeScalper = 8
        ''' <summary>
        ''' VIDYA (Variable Index Dynamic Average): CMO-adaptive EMA that speeds up in trending
        ''' conditions and slows in ranging ones.  Volumatic variant adds delta-volume colouring.
        ''' Entry: price crosses above/below the VIDYA line.
        ''' </summary>
        Vidya = 13

        ' ── QuantLab Research Indicators ─────────────────────────────────────

        ''' <summary>
        ''' Connors RSI-2: 2-period RSI + SMA(200) long-term trend filter + SMA(5) exit trigger.
        ''' Mean-reversion system validated at 67–72% win rate on daily equity/commodity bars.
        ''' </summary>
        ConnorsRsi2 = 9

        ''' <summary>
        ''' SuperTrend: ATR(10) × 3.0 multiplier dynamic support/resistance flip indicator.
        ''' Trend-following; validated at 40–52% win rate with positive expectancy from trend runs.
        ''' </summary>
        SuperTrend = 10

        ''' <summary>
        ''' Donchian Channel (20-bar): highest high / lowest low channel breakout.
        ''' Turtle-trading breakout system; validated at 30–40% win rate, large R:R winners.
        ''' </summary>
        DonchianBreakout = 11

        ''' <summary>
        ''' Bollinger Bands (20,2) + RSI(14) dual-confirmation mean reversion.
        ''' Both indicators must confirm the extreme before entry fires.
        ''' Validated at 55–65% win rate on daily charts; Sharpe 0.6–1.2.
        ''' </summary>
        BbRsiMeanReversion = 12

        ''' <summary>
        ''' Naked Trader: 5-minute bar consensus using EMA(9/21) crossover, MACD(8,17,9) histogram,
        ''' DMI/ADX(14) directional strength, and VWAP anchor.
        ''' Fires only when confidence is Medium or High (3+ of 4 votes agree; ADX ≥ 20).
        ''' </summary>
        NakedTrader = 14
    End Enum

End Namespace
