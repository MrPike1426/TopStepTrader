Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' BUG-94 F1: configuration for the orphan-position safety net run by
    ''' <c>TradeReconciliationWorker</c>. An "orphan" is a broker-reported open position
    ''' that has no matching open <c>LiveTradeRecord</c>; the reconciliation worker can
    ''' (configurably) place a fallback Stop Market on it so an un-managed position never
    ''' runs without protection.
    ''' </summary>
    Public Class SafetyNetSettings
        ''' <summary>
        ''' Master toggle. When False the orphan scan still runs and alarms, but does not
        ''' call EditPositionSlTpAsync.
        ''' </summary>
        Public Property AutoStopLossEnabled As Boolean = False

        ''' <summary>
        ''' Minimum age before a broker position is considered orphan (suppresses
        ''' false-positives during the natural fill → persistence window).
        ''' </summary>
        Public Property OrphanGraceSeconds As Integer = 15

        ''' <summary>Per-symbol fallback stop in dollars. Used only when no entry in PerSymbol matches.</summary>
        Public Property DefaultFallbackStopDollars As Decimal = 50D

        ''' <summary>Per-symbol fallback stop in dollars, keyed by root symbol ("MES", "MNQ", "MGC", ...).</summary>
        Public Property PerSymbol As Dictionary(Of String, Decimal) = New Dictionary(Of String, Decimal)(StringComparer.OrdinalIgnoreCase)
    End Class

End Namespace
