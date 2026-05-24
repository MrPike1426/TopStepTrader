Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-70: One per-bar evaluation produced by <c>ISlipStreamSignalDetector</c>.
    ''' Carries the raw indicator readout plus the confluence decision so the UI can
    ''' explain near-misses ("ADX 18.4 below 22", "Not extended before pullback") and
    ''' the orchestrator can act on <see cref="Signal"/>.
    ''' </summary>
    Public Class SlipStreamEvaluation

        ''' <summary>Root symbol evaluated, e.g. "MES".</summary>
        Public Property Symbol As String = String.Empty

        ''' <summary>Timestamp of the closed bar whose close drove this evaluation.</summary>
        Public Property AsOf As DateTimeOffset

        ''' <summary>Last closed-bar close on the signal timeframe.</summary>
        Public Property LastClose As Decimal

        ''' <summary>Last closed-bar high. Useful for UI display + potential stop-entry triggers.</summary>
        Public Property LastBarHigh As Decimal

        ''' <summary>Last closed-bar low. Useful for UI display + potential stop-entry triggers.</summary>
        Public Property LastBarLow As Decimal

        ''' <summary>EMA(fastLen) on the signal timeframe at the evaluation bar's close.</summary>
        Public Property EmaFast As Decimal

        ''' <summary>EMA(slowLen) on the signal timeframe at the evaluation bar's close.</summary>
        Public Property EmaSlow As Decimal

        ''' <summary>HTF EMA at the most recent closed HTF bar. 0 when HTF filter disabled or warmup not satisfied.</summary>
        Public Property HtfEma As Decimal

        ''' <summary>Wilder RSI at the evaluation bar's close. <c>Double.NaN</c> = warmup.</summary>
        Public Property Rsi As Double = Double.NaN

        ''' <summary>Wilder ATR at the evaluation bar's close. 0 = warmup.</summary>
        Public Property Atr As Decimal

        ''' <summary>
        ''' Percentile rank of the current ATR within the last
        ''' <c>AtrPercentLookback</c> readings. 0–100. -1 when warmup is not satisfied.
        ''' </summary>
        Public Property AtrPercentRank As Double = -1.0

        ''' <summary>Wilder ADX at the evaluation bar's close. <c>Double.NaN</c> = warmup.</summary>
        Public Property Adx As Double = Double.NaN

        ''' <summary>Wilder +DI at the evaluation bar's close. <c>Double.NaN</c> = warmup.</summary>
        Public Property DiPlus As Double = Double.NaN

        ''' <summary>Wilder −DI at the evaluation bar's close. <c>Double.NaN</c> = warmup.</summary>
        Public Property DiMinus As Double = Double.NaN

        ''' <summary>
        ''' True when the extended-then-pullback gate fired bullish: in the lookback window
        ''' (excluding the current bar), at least one bar's low was ≥ ExtendAtrMult × ATR
        ''' above EMAfast.
        ''' </summary>
        Public Property WasExtendedUp As Boolean

        ''' <summary>
        ''' True when the extended-then-pullback gate fired bearish: in the lookback window
        ''' (excluding the current bar), at least one bar's high was ≤ ExtendAtrMult × ATR
        ''' below EMAfast.
        ''' </summary>
        Public Property WasExtendedDown As Boolean

        ''' <summary>True when the current bar's timestamp falls inside the configured session window.</summary>
        Public Property InSession As Boolean

        ''' <summary>True when the current bar's timestamp falls inside the configured force-flat window.</summary>
        Public Property InFlatWindow As Boolean

        ''' <summary>The confluence decision for this bar.</summary>
        Public Property Signal As SlipStreamSignalSide = SlipStreamSignalSide.None

        ''' <summary>
        ''' Non-empty when the evaluation could have qualified but missed a single gate.
        ''' Empty when <see cref="Signal"/> &lt;&gt; <c>None</c> or when warmup is the reason.
        ''' </summary>
        Public Property RejectionReason As String = String.Empty

        ''' <summary>True once every input indicator has satisfied its warmup.</summary>
        Public Property IsWarm As Boolean

        ''' <summary>Number of signal-timeframe bars the detector had available when computing this evaluation.</summary>
        Public Property BarsAvailable As Integer

        ''' <summary>Number of HTF bars the detector had available. 0 when HTF filter disabled.</summary>
        Public Property HtfBarsAvailable As Integer

    End Class

    ''' <summary>FEAT-70: Confluence verdict from the SlipStream signal detector.</summary>
    Public Enum SlipStreamSignalSide
        None
        Bullish
        Bearish
    End Enum

End Namespace
