Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("Orders")>
    Public Class OrderEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property ExternalOrderId As Long?

        <Required>
        Public Property AccountId As Long

        <Required>
        Public Property ContractId As Integer

        ''' <summary>0=Buy, 1=Sell</summary>
        <Required>
        Public Property Side As Byte

        ''' <summary>2=Market, 1=Limit, 3=StopOrder, 4=StopLimit</summary>
        <Required>
        Public Property OrderType As Byte

        <Required>
        Public Property Quantity As Integer

        <Column(TypeName:="decimal(18,6)")>
        Public Property LimitPrice As Decimal?

        <Column(TypeName:="decimal(18,6)")>
        Public Property StopPrice As Decimal?

        ''' <summary>0=Pending, 1=Working, 2=Filled, 3=PartialFill, 4=Cancelled, 5=Rejected</summary>
        <Required>
        Public Property Status As Byte

        <Required>
        Public Property PlacedAt As DateTimeOffset

        Public Property FilledAt As DateTimeOffset?

        <Column(TypeName:="decimal(18,6)")>
        Public Property FillPrice As Decimal?

        <ForeignKey("SourceSignal")>
        Public Property SourceSignalId As Long?

        Public Property SourceSignal As SignalEntity

        <MaxLength(500)>
        Public Property Notes As String

        Public Property CreatedAt As DateTime = DateTime.UtcNow
    End Class

End Namespace
