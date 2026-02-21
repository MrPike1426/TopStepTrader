Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Persists the real-world outcome of a signal-driven trade.
    ''' IsOpen = True means the outcome has not yet been resolved.
    ''' IsWinner is null until the position is measured at exit.
    ''' </summary>
    <Table("TradeOutcomes")>
    Public Class TradeOutcomeEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id              As Long

        Public Property SignalId        As Long
        Public Property OrderId         As Long?
        <MaxLength(50)>
        Public Property ContractId      As String = String.Empty
        Public Property Timeframe       As Integer

        <MaxLength(10)>
        Public Property SignalType      As String = String.Empty

        Public Property SignalConfidence As Single

        <MaxLength(50)>
        Public Property ModelVersion    As String = String.Empty

        Public Property EntryTime       As DateTimeOffset
        <Column(TypeName:="decimal(18,4)")>
        Public Property EntryPrice      As Decimal

        Public Property ExitTime        As DateTimeOffset?
        <Column(TypeName:="decimal(18,4)")>
        Public Property ExitPrice       As Decimal?

        <Column(TypeName:="decimal(18,4)")>
        Public Property PnL             As Decimal?

        Public Property IsWinner        As Boolean?

        <MaxLength(50)>
        Public Property ExitReason      As String = String.Empty

        Public Property IsOpen          As Boolean = True
        Public Property CreatedAt       As DateTimeOffset = DateTimeOffset.UtcNow

    End Class

End Namespace
