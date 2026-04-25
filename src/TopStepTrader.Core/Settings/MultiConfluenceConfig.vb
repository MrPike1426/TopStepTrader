Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' All tuneable thresholds for <see cref="TopStepTrader.Services.Trading.MultiConfluenceStrategy"/>.
    ''' Bound from appsettings.json "Strategies:MultiConfluence"; individual properties can be
    ''' overridden per-persona via <see cref="TopStepTrader.Core.Models.PersonaProfile.MultiConfluence"/>.
    ''' Default values reproduce the original hard-coded constants exactly (STRAT-24).
    ''' </summary>
    Public Class MultiConfluenceConfig

        ' ── Ichimoku periods ──────────────────────────────────────────────────────
        Public Property TenkanPeriod As Integer = 9
        Public Property KijunPeriod As Integer = 26
        Public Property SenkouBPeriod As Integer = 52
        Public Property IchimokuDisplacement As Integer = 26

        ' ── MACD periods ──────────────────────────────────────────────────────────
        Public Property MacdFastPeriod As Integer = 8
        Public Property MacdSlowPeriod As Integer = 17
        Public Property MacdSignalPeriod As Integer = 9

        ' ── ADX gate ──────────────────────────────────────────────────────────────
        ''' <summary>Minimum ADX(14) required for the trend-strength gate (condition 5).</summary>
        Public Property AdxThreshold As Single = 20.0F

        ' ── DI spread ─────────────────────────────────────────────────────────────
        ''' <summary>Minimum separation between DI+ and DI− (points). Filters directional chop.</summary>
        Public Property MinDiSpread As Single = 2.0F

        ' ── Chikou gap ────────────────────────────────────────────────────────────
        ''' <summary>
        ''' Minimum Chikou clearance as a fraction of the current close price.
        ''' Default 0.001 = 0.1%. Prevents the lagging-span condition firing during flat consolidation.
        ''' </summary>
        Public Property ChikouMinGapFraction As Double = 0.001

        ' ── MACD magnitude ────────────────────────────────────────────────────────
        ''' <summary>
        ''' MACD histogram must exceed this fraction of ATR(14) in absolute terms.
        ''' Default 0.05. Lewis=0.07; Joe=0.03.
        ''' </summary>
        Public Property MacdHistMinAtrFraction As Double = 0.05

        ' ── StochRSI ──────────────────────────────────────────────────────────────
        ''' <summary>StochRSI K overbought ceiling for Long entries. Default 0.7.</summary>
        Public Property StochRsiOverbought As Single = 0.7F

        ''' <summary>StochRSI K oversold floor for Short entries. Default 0.3.</summary>
        Public Property StochRsiOversold As Single = 0.3F

        ' ── Volume gate ───────────────────────────────────────────────────────────
        ''' <summary>Current bar volume must be at least this multiple of the 20-bar average. Default 1.2.</summary>
        Public Property VolumeMultiple As Double = 1.2

        ''' <summary>
        ''' STRAT-22: When False, condition 8 (volume gate) is bypassed unconditionally.
        ''' Set to False for instruments where the ProjectX bar feed returns 0 volume consistently
        ''' (e.g. M6E EUR/USD micro currency futures).  Default True (gate active).
        ''' Both the live evaluator and the backtest signal provider honour this flag.
        ''' </summary>
        Public Property VolumeGateEnabled As Boolean = True

        ' ── SL / TP sizing ────────────────────────────────────────────────────────
        ''' <summary>Stop-loss as a multiple of ATR(14). Default 1.5.</summary>
        Public Property SlAtrMultiple As Decimal = 1.5D

        ''' <summary>Take-profit as a multiple of the initial risk (R). Default 2.0.</summary>
        Public Property TpRMultiple As Decimal = 2.0D

    End Class

End Namespace
