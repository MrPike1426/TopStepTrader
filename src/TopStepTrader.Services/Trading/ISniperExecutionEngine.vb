Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Interface for the Sniper execution engine.
    ''' </summary>
    Public Interface ISniperExecutionEngine
        Inherits IDisposable

        ReadOnly Property IsRunning As Boolean
        ReadOnly Property CurrentQty As Integer
        ReadOnly Property AverageEntry As Decimal
        ReadOnly Property FreeRideActive As Boolean

        Event LogMessage As EventHandler(Of String)
        Event ExecutionStopped As EventHandler(Of String)
        Event TradeOpened As EventHandler(Of TradeOpenedEventArgs)
        Event TradeClosed As EventHandler(Of TradeClosedEventArgs)
        Event PositionChanged As EventHandler(Of SniperPositionEventArgs)

        Sub Start(contractId As String,
                  accountId As Long,
                  takeProfitTicks As Integer,
                  stopLossTicks As Integer,
                  maxRiskHeatTicks As Integer,
                  volatilityAtrFactor As Double,
                  targetTotalSize As Integer,
                  coreSizeFraction As Double,
                  coreAddsCount As Integer,
                  momentumTierSize As Integer,
                  extensionAllowed As Boolean,
                  extensionTierSize As Integer,
                  enableStructureFailExit As Boolean,
                  ema21BreakTicks As Integer,
                  minBarsBeforeExit As Integer,
                  durationHours As Double,
                  tickSize As Decimal,
                  tickValue As Decimal)

        Function StopAsync(Optional reason As String = "Stopped by user") As Task

    End Interface

End Namespace
