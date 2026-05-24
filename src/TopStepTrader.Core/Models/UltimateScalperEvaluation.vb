Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-64: One per-bar evaluation produced by <c>IUltimateScalperSignalDetector</c>.
    ''' Carries the raw indicator readout plus the confluence decision so the UI can
    ''' explain near-misses ("RSI 35.2 not oversold", "Price below VWAP") and the
    ''' orchestrator can act on <see cref="Signal"/>.
    ''' </summary>
    Public Class UltimateScalperEvaluation

        ''' <summary>Root symbol evaluated, e.g. "MES".</summary>
        Public Property Symbol As String = String.Empty

        ''' <summary>Timestamp of the closed bar whose close drove this evaluation.</summary>
        Public Property AsOf As DateTimeOffset

        ''' <summary>Last closed-bar close on the signal timeframe (5m).</summary>
        Public Property LastClose As Decimal

        ''' <summary>FEAT-69: Last closed-bar high. Used by the stop-entry manager to compute
        ''' a long trigger price as <c>LastBarHigh + EntryTriggerOffsetTicks × tickSize</c>.</summary>
        Public Property LastBarHigh As Decimal

        ''' <summary>FEAT-69: Last closed-bar low. Used by the stop-entry manager to compute
        ''' a short trigger price as <c>LastBarLow − EntryTriggerOffsetTicks × tickSize</c>.</summary>
        Public Property LastBarLow As Decimal

        ''' <summary>200-period MA computed on 5-minute bars. 0 = warmup not satisfied.</summary>
        Public Property Ma200 As Decimal

        ''' <summary>Session VWAP at the evaluation bar's close. 0 = no bars in session yet.</summary>
        Public Property Vwap As Decimal

        ''' <summary>Wilder RSI(14) at the evaluation bar's close. <c>Double.NaN</c> = warmup.</summary>
        Public Property Rsi As Double = Double.NaN

        ''' <summary>Bars since RSI most-recently crossed up through midline (50). <c>Int32.MaxValue</c> = never.</summary>
        Public Property BarsSinceCrossAbove As Integer = Int32.MaxValue

        ''' <summary>Bars since RSI most-recently crossed down through midline (50). <c>Int32.MaxValue</c> = never.</summary>
        Public Property BarsSinceCrossBelow As Integer = Int32.MaxValue

        ''' <summary>The confluence decision for this bar.</summary>
        Public Property Signal As UltimateScalperSignalSide = UltimateScalperSignalSide.None

        ''' <summary>
        ''' FEAT-69: directional intent for pre-staged stop-entry arming. Set when the
        ''' setup is aligned for a direction so the orchestrator's <c>IScalperStopEntryManager</c>
        ''' can rest a stop-entry order at <c>lastBar.High + offset</c> (long) or
        ''' <c>lastBar.Low - offset</c> (short). In v1 this mirrors <see cref="Signal"/>
        ''' exactly — kept as a separate field so a later ticket can loosen "primed" to an
        ''' approaching state (e.g., 3-of-4 conditions) without changing fire semantics.
        ''' </summary>
        Public Property PrimedSide As UltimateScalperSignalSide = UltimateScalperSignalSide.None

        ''' <summary>
        ''' Non-empty when the evaluation could have qualified but missed a single gate.
        ''' Empty when <see cref="Signal"/> &lt;&gt; <c>None</c> or when warmup is the reason.
        ''' </summary>
        Public Property RejectionReason As String = String.Empty

        ''' <summary>True once every input indicator has satisfied its warmup.</summary>
        Public Property IsWarm As Boolean

        ''' <summary>FEAT-65: Count of 5 m bars the detector had available when computing this evaluation.</summary>
        Public Property BarsAvailable As Integer

    End Class

    ''' <summary>FEAT-64: Confluence verdict from the Ultimate Scalper signal detector.</summary>
    Public Enum UltimateScalperSignalSide
        None
        Bullish
        Bearish
    End Enum

End Namespace
