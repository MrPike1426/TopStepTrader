Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-64: Immutable snapshot of the live scalper position's trail state emitted
    ''' from <c>UltimateScalperOrchestrator.OnQuoteReceived</c> on every quote tick.
    ''' Consumed by the UI to drive the live position card without exposing the mutable
    ''' <see cref="ScalperTrailState"/> across thread boundaries.
    ''' </summary>
    Public Class ScalperTrailSnapshot
        Public Property Symbol As String = String.Empty
        Public Property Side As OrderSide
        Public Property EntryPrice As Decimal
        Public Property CurrentStopPrice As Decimal
        Public Property LastPrice As Decimal
        Public Property PeakFavorablePrice As Decimal
        Public Property HasBreakevenSnapped As Boolean
        Public Property TickSize As Decimal
        Public Property DollarsPerTick As Decimal
        Public Property EditsThisSecond As Integer
        Public Property CapturedUtc As DateTime
    End Class

End Namespace
