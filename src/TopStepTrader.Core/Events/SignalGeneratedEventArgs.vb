Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Events

    Public Class SignalGeneratedEventArgs
        Inherits EventArgs
        Public ReadOnly Property Signal As TradeSignal
        Public Sub New(signal As TradeSignal)
            Me.Signal = signal
        End Sub
    End Class

    Public Class QuoteEventArgs
        Inherits EventArgs
        Public ReadOnly Property Quote As Quote
        Public Sub New(quote As Quote)
            Me.Quote = quote
        End Sub
    End Class

    Public Class BarEventArgs
        Inherits EventArgs
        Public ReadOnly Property Bar As MarketBar
        Public Sub New(bar As MarketBar)
            Me.Bar = bar
        End Sub
    End Class

    Public Class OrderFilledEventArgs
        Inherits EventArgs
        Public ReadOnly Property Order As Order
        Public Sub New(order As Order)
            Me.Order = order
        End Sub
    End Class

    Public Class OrderRejectedEventArgs
        Inherits EventArgs
        Public ReadOnly Property Order As Order
        Public ReadOnly Property Reason As String
        Public Sub New(order As Order, reason As String)
            Me.Order = order
            Me.Reason = reason
        End Sub
    End Class

    Public Class RiskHaltEventArgs
        Inherits EventArgs
        Public ReadOnly Property Reason As RiskHaltReason
        Public ReadOnly Property DailyPnL As Decimal
        Public ReadOnly Property Drawdown As Decimal
        Public Sub New(reason As RiskHaltReason, dailyPnL As Decimal, drawdown As Decimal)
            Me.Reason = reason
            Me.DailyPnL = dailyPnL
            Me.Drawdown = drawdown
        End Sub
    End Class

    Public Class BacktestProgressEventArgs
        Inherits EventArgs
        Public ReadOnly Property PercentComplete As Integer
        Public ReadOnly Property CurrentDate As Date
        Public ReadOnly Property TradesExecuted As Integer
        Public Sub New(pct As Integer, currentDate As Date, trades As Integer)
            PercentComplete = pct
            Me.CurrentDate = currentDate
            TradesExecuted = trades
        End Sub
    End Class

End Namespace
