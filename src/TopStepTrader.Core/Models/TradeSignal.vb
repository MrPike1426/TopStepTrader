Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    Public Class TradeSignal
        Public Property Id As Long
        Public Property ContractId As Integer
        Public Property GeneratedAt As DateTimeOffset
        Public Property SignalType As SignalType
        Public Property Confidence As Single          ' 0.0 to 1.0
        Public Property ReasoningTags As New List(Of String)
        Public Property SuggestedEntryPrice As Decimal?
        Public Property SuggestedStopLoss As Decimal?
        Public Property SuggestedTakeProfit As Decimal?
        Public Property ModelVersion As String = String.Empty
    End Class

End Namespace
