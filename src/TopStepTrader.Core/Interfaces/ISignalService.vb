Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Core.Interfaces

    Public Interface ISignalService
        Event SignalGenerated As EventHandler(Of SignalGeneratedEventArgs)
        Function GenerateSignalAsync(contractId As Integer, recentBars As IEnumerable(Of MarketBar)) As Task(Of TradeSignal)
        Function GetSignalHistoryAsync(contractId As Integer, from As DateTime, [to] As DateTime) As Task(Of IEnumerable(Of TradeSignal))
        ReadOnly Property LastSignal As TradeSignal
    End Interface

End Namespace
