Namespace TopStepTrader.Core.Models.Debug

    Public Class DebugTradeRecord
        Public Property TradeId As String = String.Empty
        Public Property SlotIndex As Integer
        Public Property Persona As String = String.Empty
        Public Property Instrument As String = String.Empty
        Public Property TimeFrame As String = String.Empty
        Public Property EntryMode As String = String.Empty
        Public Property Direction As String = String.Empty
        Public Property EntryPrice As Decimal
        Public Property EntryTime As String = String.Empty
        Public Property InitialSL As Decimal
        Public Property InitialTP As Decimal
        Public Property ContractCount As Integer
        Public Property SuperTrendConfigJson As String = String.Empty
        Public Property AiCheckResult As String
        Public Property AiCheckReason As String
        Public Property ActualFillPrice As Nullable(Of Decimal)
        Public Property FillConfirmedTime As String
        Public Property RealisedPnLDollars As Nullable(Of Decimal)
        Public Property ClosedAt As String
        Public Property CreatedAt As String = String.Empty
    End Class

End Namespace
