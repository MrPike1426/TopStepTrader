Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("TradeStopAdjustments")>
    Public Class TradeStopAdjustmentEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property LiveTradeRecordId As Long

        <MaxLength(50)>
        Public Property Timestamp As String = String.Empty    ' ISO-8601 UTC

        <MaxLength(40)>
        Public Property OldStop As String = String.Empty      ' decimal as string

        <MaxLength(40)>
        Public Property NewStop As String = String.Empty

        <MaxLength(50)>
        Public Property TriggerReason As String = String.Empty ' Initial | Breakeven | AtrRatchet | Manual | other

        Public Property Notes As String

    End Class

End Namespace
