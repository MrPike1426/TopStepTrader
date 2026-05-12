Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Interface for the Pump-n-Dump execution engine.
    ''' 3-bar price-action entry, P&amp;L-triggered free-ride, dynamic TP tightening on momentum fade.
    ''' </summary>
    Public Interface IPumpNDumpExecutionEngine
        Inherits IDisposable

        ReadOnly Property IsRunning As Boolean
        ReadOnly Property CurrentQty As Integer
        ReadOnly Property AverageEntry As Decimal
        ReadOnly Property FreeRideActive As Boolean
        ReadOnly Property UnrealisedPnl As Decimal

        Event LogMessage As EventHandler(Of String)
        Event ExecutionStopped As EventHandler(Of String)
        Event TradeOpened As EventHandler(Of TradeOpenedEventArgs)
        Event TradeClosed As EventHandler(Of TradeClosedEventArgs)
        Event PositionChanged As EventHandler(Of PumpNDumpPositionEventArgs)
        Event BarCountChanged As EventHandler(Of BarCountEventArgs)

        Sub Start(contractId As String,
                   accountId As Long,
                   takeProfitTicks As Integer,
                   stopLossTicks As Integer,
                   freeRidePnlThreshold As Decimal,
                   scaleInTicks As Integer,
                   maxRiskHeatTicks As Integer,
                   targetTotalSize As Integer,
                   momentumFadeAtrFraction As Double,
                   tightenTicksPerBar As Integer,
                   durationHours As Double,
                   tickSize As Decimal,
                   tickValue As Decimal,
                   brokerType As BrokerType,
                   Optional tradingStartHourUtc As Integer = 6,
                   Optional tradingEndHourUtc As Integer = 21)

        Function StopAsync(Optional reason As String = "Stopped by user") As Task

    End Interface

End Namespace
