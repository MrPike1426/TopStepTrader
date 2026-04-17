Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("RiskEvents")>
    Public Class RiskEventEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        <Required>
        Public Property OccurredAt As DateTimeOffset

        <Required>
        <MaxLength(100)>
        Public Property EventType As String = String.Empty  ' e.g. "DailyLossLimit", "MaxDrawdown"

        <Column(TypeName:="decimal(18,2)")>
        Public Property DailyPnLAtEvent As Decimal?

        <Column(TypeName:="decimal(18,2)")>
        Public Property DrawdownAtEvent As Decimal?

        <Column(TypeName:="decimal(18,2)")>
        Public Property RuleValue As Decimal?

        Public Property AccountId As Long?
        Public Property DetailsJson As String
        Public Property Acknowledged As Boolean = False
    End Class

End Namespace
