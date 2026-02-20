Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("Signals")>
    Public Class SignalEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        <Required>
        Public Property ContractId As Integer

        <Required>
        Public Property GeneratedAt As DateTimeOffset

        ''' <summary>0=Hold, 1=Buy, 2=Sell</summary>
        <Required>
        Public Property SignalType As Byte

        ''' <summary>ML confidence 0.0 to 1.0</summary>
        <Required>
        Public Property Confidence As Single

        <Required>
        <MaxLength(50)>
        Public Property ModelVersion As String = String.Empty

        <Column(TypeName:="decimal(18,6)")>
        Public Property SuggestedEntry As Decimal?

        <Column(TypeName:="decimal(18,6)")>
        Public Property SuggestedStop As Decimal?

        <Column(TypeName:="decimal(18,6)")>
        Public Property SuggestedTarget As Decimal?

        Public Property ReasoningJson As String

        Public Property CreatedAt As DateTime = DateTime.UtcNow
    End Class

End Namespace
