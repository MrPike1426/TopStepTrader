Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-59: per-closed-bar tick snapshot for a live trade — indicator state, stop ladder
    ''' state, and unrealised P&amp;L / MAE / MFE at the close of one strategy-TF bar.
    ''' Core-side POCO that mirrors <c>TopStepTrader.Data.Entities.TradeTickSnapshotEntity</c>
    ''' so the service interface in Core does not have to reference the Data project. Mapped
    ''' to the entity inside <c>TradeRecordService.LogTickSnapshotAsync</c>.
    ''' </summary>
    Public Class TradeTickSnapshot
        Public Property LiveTradeRecordId As Long

        ''' <summary>Close-of-bar UTC timestamp.</summary>
        Public Property BarTimestamp As DateTimeOffset

        Public Property BarOpen As Decimal
        Public Property BarHigh As Decimal
        Public Property BarLow As Decimal
        Public Property BarClose As Decimal
        Public Property BarVolume As Long

        Public Property SuperTrendLine As Decimal
        ''' <summary>+1 = long bias, -1 = short bias.</summary>
        Public Property SuperTrendDirection As Integer

        Public Property Atr As Single
        Public Property Adx As Single
        Public Property PlusDi As Single
        Public Property MinusDi As Single

        Public Property CurrentStopPrice As Decimal
        Public Property CurrentTakeProfitPrice As Decimal

        Public Property UnrealisedPnlDollars As Decimal
        Public Property MaxAdverseExcursionDollars As Decimal
        Public Property MaxFavorableExcursionDollars As Decimal

        Public Property StopPhase As String = String.Empty
        Public Property ExitScore As Integer
    End Class

End Namespace
