Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    <Table("BacktestRuns")>
    Public Class BacktestRunEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        <Required>
        <MaxLength(200)>
        Public Property RunName As String = String.Empty

        <Required>
        <MaxLength(50)>
        Public Property ContractId As String = String.Empty

        <Required>
        Public Property Timeframe As Integer

        <Required>
        Public Property StartDate As Date

        <Required>
        Public Property EndDate As Date

        <Required>
        <Column(TypeName:="decimal(18,2)")>
        Public Property InitialCapital As Decimal

        <MaxLength(50)>
        Public Property ModelVersion As String = String.Empty

        Public Property ParametersJson As String

        Public Property TotalTrades As Integer = 0
        Public Property WinningTrades As Integer = 0
        Public Property LosingTrades As Integer = 0

        <Column(TypeName:="decimal(18,2)")>
        Public Property TotalPnL As Decimal = 0D

        <Column(TypeName:="decimal(18,2)")>
        Public Property FinalCapital As Decimal = 0D

        <Column(TypeName:="decimal(18,2)")>
        Public Property MaxDrawdown As Decimal = 0D

        <Column(TypeName:="decimal(18,2)")>
        Public Property AveragePnLPerTrade As Decimal = 0D

        Public Property SharpeRatio As Single?
        Public Property WinRate As Single?

        ''' <summary>0=Running, 1=Complete, 2=Failed</summary>
        Public Property Status As Byte = 0

        Public Property CompletedAt As DateTimeOffset?
        Public Property CreatedAt As DateTime = DateTime.UtcNow

        Public Property Trades As New List(Of BacktestTradeEntity)
    End Class

    <Table("BacktestTrades")>
    Public Class BacktestTradeEntity
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        <Required>
        <ForeignKey("BacktestRun")>
        Public Property BacktestRunId As Long

        Public Property BacktestRun As BacktestRunEntity

        <Required>
        Public Property EntryTime As DateTimeOffset

        Public Property ExitTime As DateTimeOffset?

        ''' <summary>"Buy" or "Sell"</summary>
        <Required>
        <MaxLength(10)>
        Public Property Side As String = String.Empty

        <Required>
        <Column(TypeName:="decimal(18,6)")>
        Public Property EntryPrice As Decimal

        <Column(TypeName:="decimal(18,6)")>
        Public Property ExitPrice As Decimal?

        Public Property Quantity As Integer = 1

        <Column(TypeName:="decimal(18,2)")>
        Public Property PnL As Decimal?

        <MaxLength(100)>
        Public Property ExitReason As String

        Public Property SignalConfidence As Single?
    End Class

End Namespace
