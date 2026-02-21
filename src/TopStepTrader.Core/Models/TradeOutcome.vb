Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Represents the real-world outcome of a trade signal — did it actually make money?
    ''' Used to provide ground-truth feedback labels for ML retraining.
    ''' </summary>
    Public Class TradeOutcome
        Public Property Id              As Long
        Public Property SignalId        As Long
        Public Property OrderId         As Long?
        Public Property ContractId      As String = String.Empty
        Public Property Timeframe       As Integer       ' bars (e.g., 5 = 5-minute)
        Public Property SignalType      As String = String.Empty  ' "Buy" or "Sell"
        Public Property SignalConfidence As Single
        Public Property ModelVersion    As String = String.Empty

        Public Property EntryTime       As DateTimeOffset
        Public Property EntryPrice      As Decimal
        Public Property ExitTime        As DateTimeOffset?
        Public Property ExitPrice       As Decimal?
        Public Property PnL             As Decimal?

        ''' <summary>True = profitable trade, False = loss, Nothing = still open.</summary>
        Public Property IsWinner        As Boolean?
        Public Property ExitReason      As String = String.Empty
        Public Property IsOpen          As Boolean = True
    End Class

End Namespace
