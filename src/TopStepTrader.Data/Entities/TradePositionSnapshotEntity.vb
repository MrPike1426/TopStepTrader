Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("TradePositionSnapshots")>
    Public Class TradePositionSnapshotEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property LiveTradeRecordId As Long

        Public Property TopStepXPositionId As Long

        <MaxLength(100)>
        Public Property ContractId As String = String.Empty

        <MaxLength(10)>
        Public Property Side As String = String.Empty

        Public Property Size As Integer

        <MaxLength(40)>
        Public Property AvgEntryPrice As String = String.Empty

        <MaxLength(40)>
        Public Property RealisedPnL As String

        <MaxLength(50)>
        Public Property OpenedAt As String = String.Empty

        <MaxLength(50)>
        Public Property ClosedAt As String

        Public Property RawJson As String = String.Empty

    End Class

End Namespace
