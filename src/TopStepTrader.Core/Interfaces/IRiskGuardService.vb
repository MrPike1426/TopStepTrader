Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Core.Interfaces

    Public Interface IRiskGuardService
        ReadOnly Property IsHalted As Boolean
        ReadOnly Property HaltReason As RiskHaltReason
        Event TradingHalted As EventHandler(Of RiskHaltEventArgs)
        Event TradingResumed As EventHandler(Of EventArgs)
        Function EvaluateRiskAsync(account As Account) As Task(Of Boolean)
        Function GetCurrentDrawdownAsync() As Task(Of Decimal)
        Function GetDailyPnLAsync() As Task(Of Decimal)
        ''' <summary>Manual override — requires UI confirmation. For testing only.</summary>
        Function ResetHaltAsync(reason As String) As Task
    End Interface

End Namespace
