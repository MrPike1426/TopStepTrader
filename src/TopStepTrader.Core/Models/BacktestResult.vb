Namespace TopStepTrader.Core.Models

    Public Class BacktestResult
        Public Property Id As Long
        Public Property RunName As String = String.Empty
        Public Property ContractId As String = String.Empty
        Public Property StartDate As Date
        Public Property EndDate As Date
        Public Property InitialCapital As Decimal
        Public Property FinalCapital As Decimal
        Public Property TotalTrades As Integer
        Public Property WinningTrades As Integer
        Public Property LosingTrades As Integer
        Public Property TotalPnL As Decimal
        Public Property MaxDrawdown As Decimal
        Public Property SharpeRatio As Single?
        Public Property WinRate As Single
        Public Property AveragePnLPerTrade As Decimal
        Public Property EndOfDayCloseCount As Integer
        Public Property RoundTripFeeUsd As Decimal
        ''' <summary>Total commission deducted across all trade legs in this result.</summary>
        Public Property CommissionPaid As Decimal
        Public Property Trades As New List(Of BacktestTrade)
        ''' <summary>Out-of-sample test result when TrainTestSplit &gt; 0; Nothing otherwise.</summary>
        Public Property OutOfSampleResult As BacktestResult = Nothing
    End Class

    Public Class BacktestTrade
        ''' <summary>
        ''' Groups all legs of a single position together.
        ''' The initial entry and any scale-in entries share the same PositionGroupId.
        ''' Metrics (win rate, avg P&amp;L) are computed per group, not per individual row.
        ''' </summary>
        Public Property PositionGroupId As Integer
        Public Property EntryTime As DateTimeOffset
        Public Property ExitTime As DateTimeOffset?
        Public Property Side As String = String.Empty
        Public Property EntryPrice As Decimal
        Public Property ExitPrice As Decimal?
        Public Property Quantity As Integer
        Public Property PnL As Decimal?
        Public Property ExitReason As String = String.Empty
        Public Property SignalConfidence As Single
        ''' <summary>For Sniper pyramid legs: the add index (0 = initial entry, 1+ = scale-ins).</summary>
        Public Property PyramidAddIndex As Integer? = Nothing
        ''' <summary>Average entry price across all legs at the time this leg was added.</summary>
        Public Property AverageEntryAtFill As Decimal? = Nothing
        ''' <summary>Maximum contracts held in the position this leg belongs to.</summary>
        Public Property MaxContractsHeld As Integer? = Nothing
    End Class

End Namespace
