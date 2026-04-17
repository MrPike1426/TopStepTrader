Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("BalanceHistory")>
    Public Class BalanceHistoryEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        <Required>
        Public Property AccountId As Long

        <Required>
        <MaxLength(256)>
        Public Property AccountName As String = String.Empty

        <Required>
        <Column(TypeName:="decimal(18,2)")>
        Public Property Balance As Decimal

        <Required>
        Public Property RecordedDate As DateTime

        <Required>
        Public Property CreatedAt As DateTime = DateTime.UtcNow

    End Class

End Namespace
