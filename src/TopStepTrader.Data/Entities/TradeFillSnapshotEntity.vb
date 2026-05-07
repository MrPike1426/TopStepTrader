Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("TradeFillSnapshots")>
    Public Class TradeFillSnapshotEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property LiveTradeRecordId As Long

        Public Property TopStepXTradeId As Long

        Public Property TopStepXOrderId As Long

        <MaxLength(100)>
        Public Property ContractId As String = String.Empty

        <MaxLength(10)>
        Public Property Side As String = String.Empty

        Public Property Size As Integer

        <MaxLength(40)>
        Public Property Price As String = String.Empty

        <MaxLength(50)>
        Public Property Timestamp As String = String.Empty

        Public Property RawJson As String = String.Empty

    End Class

End Namespace
