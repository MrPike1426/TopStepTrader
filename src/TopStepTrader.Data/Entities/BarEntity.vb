Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("Bars")>
    Public Class BarEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        <Required>
        Public Property ContractId As Integer

        ''' <summary>Bar timeframe in minutes (1, 5, 15, 30, 60, 240, 1440)</summary>
        <Required>
        Public Property Timeframe As Integer

        <Required>
        Public Property Timestamp As DateTimeOffset

        <Required>
        <Column(TypeName:="decimal(18,6)")>
        Public Property OpenPrice As Decimal

        <Required>
        <Column(TypeName:="decimal(18,6)")>
        Public Property HighPrice As Decimal

        <Required>
        <Column(TypeName:="decimal(18,6)")>
        Public Property LowPrice As Decimal

        <Required>
        <Column(TypeName:="decimal(18,6)")>
        Public Property ClosePrice As Decimal

        <Required>
        Public Property Volume As Long

        <Column(TypeName:="decimal(18,6)")>
        Public Property VWAP As Decimal?

        Public Property CreatedAt As DateTime = DateTime.UtcNow
    End Class

End Namespace
