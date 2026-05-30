Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("LiveTradeRecords")>
    Public Class LiveTradeRecordEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property EntryOrderId As Long
        Public Property TopStepXTradeId As Long?
        Public Property ExitOrderId As Long

        <MaxLength(100)>
        Public Property ContractId As String = String.Empty
        <MaxLength(20)>
        Public Property Symbol As String = String.Empty
        <MaxLength(10)>
        Public Property Direction As String = String.Empty
        Public Property Sizes As Integer
        Public Property MaxScaleIns As Integer
        <MaxLength(100)>
        Public Property StrategyName As String = String.Empty
        <MaxLength(20)>
        Public Property Persona As String = String.Empty
        <MaxLength(10)>
        Public Property Timeframe As String = String.Empty

        Public Property EntryTime As DateTimeOffset
        Public Property ExitTime As DateTimeOffset?

        <Column(TypeName:="decimal(18,6)")>
        Public Property EntryPrice As Decimal
        <Column(TypeName:="decimal(18,6)")>
        Public Property ExitPrice As Decimal?
        <Column(TypeName:="decimal(18,4)")>
        Public Property PnL As Decimal?
        <Column(TypeName:="decimal(10,2)")>
        Public Property CommissionUsd As Decimal
        <Column(TypeName:="decimal(10,2)")>
        Public Property FeesUsd As Decimal

        <MaxLength(50)>
        Public Property ExitReason As String = String.Empty
        Public Property IsOpen As Boolean = True
        Public Property IsRecoveredFromCrash As Boolean = False

        ''' <summary>
        ''' BUG-100: provenance of <see cref="ExitPrice"/> / <see cref="PnL"/> at close time:
        ''' <c>"hub"</c> (SignalR fill push, preferred), <c>"rest-poll"</c> (REST history
        ''' fallback), or <c>"engine-fallback"</c> (no broker fill captured within the
        ''' timeout — values are the engine estimate). NULL on rows written before the
        ''' column shipped (no backfill — see ticket Out of Scope).
        ''' </summary>
        <MaxLength(16)>
        Public Property CloseFillSource As String

        Public Property CreatedAt As DateTimeOffset = DateTimeOffset.UtcNow
        Public Property UpdatedAt As DateTimeOffset = DateTimeOffset.UtcNow

    End Class

End Namespace
