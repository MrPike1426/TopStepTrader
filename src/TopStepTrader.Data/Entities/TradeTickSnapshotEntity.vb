Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' FEAT-59: per-closed-bar snapshot of indicators, stop state, and excursion for a live
    ''' trade. Written unconditionally (no Debug Capture toggle) so every closed trade can be
    ''' replayed without depending on debug_trades.db. One row per unique strategy-TF bar
    ''' timestamp during the trade's lifetime.
    ''' </summary>
    <Table("TradeTickSnapshots")>
    Public Class TradeTickSnapshotEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property LiveTradeRecordId As Long

        ''' <summary>Close-of-bar UTC timestamp.</summary>
        Public Property BarTimestamp As DateTimeOffset

        <Column(TypeName:="decimal(18,4)")>
        Public Property BarOpen As Decimal
        <Column(TypeName:="decimal(18,4)")>
        Public Property BarHigh As Decimal
        <Column(TypeName:="decimal(18,4)")>
        Public Property BarLow As Decimal
        <Column(TypeName:="decimal(18,4)")>
        Public Property BarClose As Decimal
        Public Property BarVolume As Long

        <Column(TypeName:="decimal(18,4)")>
        Public Property SuperTrendLine As Decimal
        ''' <summary>+1 = long bias, -1 = short bias.</summary>
        Public Property SuperTrendDirection As Integer

        Public Property Atr As Single
        Public Property Adx As Single
        Public Property PlusDi As Single
        Public Property MinusDi As Single

        <Column(TypeName:="decimal(18,4)")>
        Public Property CurrentStopPrice As Decimal
        <Column(TypeName:="decimal(18,4)")>
        Public Property CurrentTakeProfitPrice As Decimal

        <Column(TypeName:="decimal(18,4)")>
        Public Property UnrealisedPnlDollars As Decimal

        <Column(TypeName:="decimal(18,4)")>
        Public Property MaxAdverseExcursionDollars As Decimal
        <Column(TypeName:="decimal(18,4)")>
        Public Property MaxFavorableExcursionDollars As Decimal

        <MaxLength(20)>
        Public Property StopPhase As String = String.Empty

        Public Property ExitScore As Integer

    End Class

End Namespace
