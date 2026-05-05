Imports TopStepTrader.Core.Models.Debug

Namespace TopStepTrader.Core.Interfaces

    Public Interface IDebugTradeCaptureService
        Property IsEnabled As Boolean
        Sub BeginTrade(header As DebugTradeRecord)
        Sub RecordSnapshot(snap As DebugSnapshotRecord)
        Sub UpdateFill(tradeId As String, fillPrice As Decimal, fillConfirmedTime As DateTime)
        Sub EndTrade(tradeId As String, closedUtc As DateTime, Optional realisedPnl As Nullable(Of Decimal) = Nothing)
    End Interface

End Namespace
