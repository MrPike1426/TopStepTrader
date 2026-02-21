Imports System.Threading
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Core.Interfaces

    Public Interface IBacktestService
        Event ProgressUpdated As EventHandler(Of BacktestProgressEventArgs)
        Function RunBacktestAsync(config As BacktestConfiguration, cancel As CancellationToken) As Task(Of BacktestResult)
        Function GetBacktestRunsAsync() As Task(Of IEnumerable(Of BacktestResult))
    End Interface

    Public Class BacktestConfiguration
        Public Property RunName As String = String.Empty
        Public Property ContractId As String = String.Empty
        Public Property Timeframe As Integer = 5
        Public Property StartDate As Date
        Public Property EndDate As Date
        Public Property InitialCapital As Decimal = 50000D
        Public Property MinSignalConfidence As Single = 0.65F
        Public Property StopLossTicks As Integer = 10
        Public Property TakeProfitTicks As Integer = 20
    End Class

End Namespace
