Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Per-instrument trail risk parameters for the Ultimate Scalper strategy (FEAT-64).
    ''' One profile per traded micro futures symbol (MES / MNQ / MGC).
    ''' </summary>
    Public Class UltimateScalperInstrumentRiskProfile

        ''' <summary>Root symbol: "MES", "MNQ", or "MGC".</summary>
        Public Property Symbol As String = String.Empty

        ''' <summary>Initial risk per contract in USD. SL placed entry ∓ this many dollars on entry.</summary>
        Public Property InitialStopDollars As Decimal

        ''' <summary>
        ''' Favorable excursion (USD per contract) at which the SL snaps to entry (break-even).
        ''' Phase transition: Risk → Trail.
        ''' </summary>
        Public Property BreakevenSnapDollars As Decimal

        ''' <summary>
        ''' Distance (USD per contract) the trailing SL stays behind peak favorable price
        ''' after the break-even snap. Trail is monotonic — SL never retraces.
        ''' </summary>
        Public Property TrailDistanceDollars As Decimal

    End Class

End Namespace
