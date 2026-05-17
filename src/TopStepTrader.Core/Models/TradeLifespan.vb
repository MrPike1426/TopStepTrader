Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-58: Lifespan metrics for a closed trade — MAE/MFE, duration, trail counts, R-multiple.
    ''' Core-side POCO that mirrors <c>TopStepTrader.Data.Entities.TradeLifespanRecordEntity</c> so the
    ''' service interface in Core does not have to reference the Data project. Mapped to the entity
    ''' inside <c>TradeRecordService.SaveLifespanRecordAsync</c>.
    ''' </summary>
    Public Class TradeLifespan
        Public Property Id As Integer
        Public Property TradeOutcomeId As Long

        ' Excursion — MAE stored as absolute (positive) dollars/ticks.
        Public Property MaxAdverseExcursionDollars As Decimal = 0D
        Public Property MaxFavorableExcursionDollars As Decimal = 0D
        Public Property MaxAdverseExcursionTicks As Integer = 0
        Public Property MaxFavorableExcursionTicks As Integer = 0

        ' Trail / activation counts
        Public Property SlRatchetCount As Integer = 0
        Public Property TpAdvanceCount As Integer = 0
        Public Property FreeRideActivated As Boolean = False
        Public Property FreeRideActivatedAtMinutes As Single = 0F

        ' Duration
        Public Property DurationMinutes As Single = 0F
        Public Property BarsInTrade As Integer = 0

        ' Session
        Public Property EntrySessionWindow As String = String.Empty
        Public Property ExitSessionWindow As String = String.Empty
        Public Property CrossedSessionBoundary As Boolean = False

        ' R-multiple
        Public Property RMultiple As Single = 0F

        Public Property CreatedAt As DateTimeOffset = DateTimeOffset.UtcNow
        Public Property UpdatedAt As DateTimeOffset = DateTimeOffset.UtcNow
    End Class

End Namespace
