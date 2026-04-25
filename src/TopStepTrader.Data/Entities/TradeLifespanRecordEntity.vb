Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Records the lifespan metrics of a trade: MAE, MFE, duration, trail counts, R-multiple.
    ''' Linked to the TradeOutcomes row via TradeOutcomeId.
    ''' </summary>
    <Table("TradeLifespanRecords")>
    Public Class TradeLifespanRecordEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Integer

        Public Property TradeOutcomeId As Long

        ' ── Excursion ────────────────────────────────────────────────────
        <Column(TypeName:="decimal(18,4)")>
        Public Property MaxAdverseExcursionDollars As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property MaxFavorableExcursionDollars As Decimal = 0D
        Public Property MaxAdverseExcursionTicks As Integer = 0
        Public Property MaxFavorableExcursionTicks As Integer = 0

        ' ── Trail / activation counts ─────────────────────────────────────
        Public Property SlRatchetCount As Integer = 0
        Public Property TpAdvanceCount As Integer = 0
        Public Property FreeRideActivated As Boolean = False
        Public Property FreeRideActivatedAtMinutes As Single = 0F

        ' ── Duration ─────────────────────────────────────────────────────
        Public Property DurationMinutes As Single = 0F
        Public Property BarsInTrade As Integer = 0

        ' ── Session ──────────────────────────────────────────────────────
        <MaxLength(50)>
        Public Property EntrySessionWindow As String = String.Empty
        <MaxLength(50)>
        Public Property ExitSessionWindow As String = String.Empty
        Public Property CrossedSessionBoundary As Boolean = False

        ' ── R-multiple ───────────────────────────────────────────────────
        Public Property RMultiple As Single = 0F

        Public Property CreatedAt As DateTimeOffset = DateTimeOffset.UtcNow
        Public Property UpdatedAt As DateTimeOffset = DateTimeOffset.UtcNow

    End Class

End Namespace
