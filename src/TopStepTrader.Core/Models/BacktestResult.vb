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
        Public Property Trades As New List(Of BacktestTrade)
    End Class

    Public Class BacktestTrade
        Public Property EntryTime As DateTimeOffset
        Public Property ExitTime As DateTimeOffset?
        Public Property Side As String = String.Empty
        Public Property EntryPrice As Decimal
        Public Property ExitPrice As Decimal?
        Public Property Quantity As Integer
        Public Property PnL As Decimal?
        Public Property ExitReason As String = String.Empty
        Public Property SignalConfidence As Single
    End Class

End Namespace
