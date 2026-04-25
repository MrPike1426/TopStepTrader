Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Context package passed to IClaudeReviewService.PostTradeAnalysisAsync after a
    ''' trade is closed. Used for post-mortem AI review in Phase 5.
    ''' </summary>
    Public Class PostTradeContext
        Public Property ContractId As String = String.Empty
        Public Property ContractDescription As String = String.Empty
        Public Property Side As String = "BUY"
        Public Property EntryPrice As Decimal
        Public Property ExitPrice As Decimal
        Public Property RealizedPnlUsd As Decimal
        Public Property HoldDurationMinutes As Double
        Public Property StrategyName As String = String.Empty
        Public Property PersonaName As String = String.Empty
        Public Property AtrValue As Decimal
        Public Property AdxValue As Single
        Public Property ConfidencePct As Integer
        Public Property SessionPnlUsd As Decimal
        Public Property SessionTradeCount As Integer
        Public Property UtcEnteredAt As DateTimeOffset
        Public Property UtcClosedAt As DateTimeOffset
    End Class

End Namespace
