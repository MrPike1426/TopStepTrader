Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' FEAT-74: persisted trailing max-drawdown (MLL) state, one row per account.
    ''' Survives app restarts so a mid-combine restart cannot silently reset the
    ''' equity trail and under-protect the account.
    ''' </summary>
    <Table("CombineAccountState")>
    Public Class CombineAccountStateEntity
        ''' <summary>Broker account id — natural key, not auto-generated.</summary>
        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.None)>
        Public Property AccountId As Long

        <Column(TypeName:="decimal(18,2)")>
        Public Property StartingBalance As Decimal

        ''' <summary>Highest modelled equity observed (ratchets up only; sampling per TrailMode).</summary>
        <Column(TypeName:="decimal(18,2)")>
        Public Property PeakEquity As Decimal

        ''' <summary>Current MLL line: Min(PeakEquity + TrailingMaxDrawdownDollars, StartingBalance when frozen).</summary>
        <Column(TypeName:="decimal(18,2)")>
        Public Property MllFloor As Decimal

        ''' <summary>Realised P&amp;L of all *completed* trading days (folded in at day rollover).</summary>
        <Column(TypeName:="decimal(18,2)")>
        Public Property CumulativeRealisedPnl As Decimal

        ''' <summary>Trading day (ARCH-21 key) this row was last rolled to.</summary>
        <MaxLength(10)>
        Public Property TradingDayKey As String = String.Empty

        Public Property UpdatedAtUtc As DateTimeOffset

    End Class

End Namespace
