Imports TopStepTrader.Core.Models.Debug

Namespace TopStepTrader.Core.Interfaces

    Public Interface IDebugTradeCaptureService
        Property IsEnabled As Boolean
        Sub BeginTrade(header As DebugTradeRecord)
        Sub RecordSnapshot(snap As DebugSnapshotRecord)
        Sub EndTrade(tradeId As String, closedUtc As DateTime)
    End Interface

End Namespace
