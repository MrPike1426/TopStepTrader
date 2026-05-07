Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Domain model representing a single stop-loss adjustment on a live trade.
    ''' Populated by ITradeRecordService.GetStopAdjustmentsAsync.
    ''' </summary>
    Public Class TradeStopAdjustment

        Public Property Id As Long
        Public Property LiveTradeRecordId As Long
        Public Property Timestamp As DateTimeOffset
        Public Property OldStop As Decimal
        Public Property NewStop As Decimal
        ''' <summary>Initial | Breakeven | AtrRatchet | Manual | other</summary>
        Public Property TriggerReason As String = String.Empty
        Public Property Notes As String

    End Class

End Namespace
