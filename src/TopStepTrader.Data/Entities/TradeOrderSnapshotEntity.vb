Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("TradeOrderSnapshots")>
    Public Class TradeOrderSnapshotEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property LiveTradeRecordId As Long

        Public Property TopStepXOrderId As Long

        <MaxLength(100)>
        Public Property ContractId As String = String.Empty

        <MaxLength(40)>
        Public Property OrderType As String = String.Empty   ' Market | StopMarket | Limit | TrailingStop

        <MaxLength(10)>
        Public Property Side As String = String.Empty        ' Buy | Sell

        <MaxLength(20)>
        Public Property Status As String = String.Empty      ' Filled | Cancelled | Working

        Public Property Size As Integer

        <MaxLength(40)>
        Public Property LimitPrice As String

        <MaxLength(40)>
        Public Property StopPrice As String

        <MaxLength(40)>
        Public Property FilledPrice As String

        <MaxLength(50)>
        Public Property CreatedAt As String = String.Empty   ' ISO-8601 UTC

        <MaxLength(50)>
        Public Property UpdatedAt As String

        Public Property RawJson As String = String.Empty

    End Class

End Namespace
