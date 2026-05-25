Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Services.BreakAndBounce

    ''' <summary>
    ''' FEAT-62: Bias side derived from the 15-minute breakout state. <c>None</c>
    ''' indicates no fresh signal this evaluation.
    ''' </summary>
    Public Enum BreakAndBounceSignalSide
        None = 0
        Bullish = 1
        Bearish = 2
    End Enum

    ''' <summary>
    ''' FEAT-62: Per-tick evaluation result for one watchlist instrument.
    ''' </summary>
    Public Class BreakAndBounceEvaluation
        Public Property Symbol As String = String.Empty
        Public Property AsOf As DateTimeOffset
        Public Property Signal As BreakAndBounceSignalSide = BreakAndBounceSignalSide.None
        Public Property RejectionReason As String = String.Empty

        Public Property PrevHigh As Decimal
        Public Property PrevLow As Decimal
        Public Property HasPreviousRange As Boolean

        Public Property Direction As Integer
        Public Property LastFifteenClose As Decimal
        Public Property LastFiveClose As Decimal
        Public Property LastFiveHigh As Decimal
        Public Property LastFiveLow As Decimal

        Public Property RetestTagged As Boolean
        Public Property PatternHit As String = String.Empty

        Public Property Atr As Decimal
        Public Property SuggestedInitialStopPrice As Decimal
        Public Property StopFloorSource As String = String.Empty

        Public Property InEntryWindow As Boolean
        Public Property InFlatWindow As Boolean

        Public Property RetestBarsAvailable As Integer
        Public Property BreakoutBarsAvailable As Integer
    End Class

End Namespace
