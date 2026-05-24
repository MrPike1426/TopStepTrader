Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-70: Per-instrument ATR-multiple overrides for the SlipStream strategy.
    ''' Every field is nullable — a null override falls back to the config-wide default,
    ''' so a profile that only tweaks the trail distance (for example) leaves the rest
    ''' of the ATR-multiple stack alone.
    ''' </summary>
    Public Class SlipStreamInstrumentRiskProfile

        ''' <summary>Root symbol: "MES", "MNQ", or "MGC".</summary>
        Public Property Symbol As String = String.Empty

        ''' <summary>Initial stop distance override (ATR multiple). Null = use config default.</summary>
        Public Property AtrSLmultOverride As Double?

        ''' <summary>TP1 distance override (ATR multiple). Null = use config default.</summary>
        Public Property AtrTP1multOverride As Double?

        ''' <summary>Trail distance override (ATR multiple). Null = use config default.</summary>
        Public Property TrailMultOverride As Double?

        ''' <summary>Trail activation offset override (ATR multiple). Null = use config default.</summary>
        Public Property TrailOffsetMultOverride As Double?

    End Class

End Namespace
