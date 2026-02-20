Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Events

Namespace TopStepTrader.Core.Interfaces

    Public Interface IOrderService
        Event OrderFilled As EventHandler(Of OrderFilledEventArgs)
        Event OrderRejected As EventHandler(Of OrderRejectedEventArgs)
        Function PlaceOrderAsync(order As Order) As Task(Of Order)
        Function CancelOrderAsync(orderId As Long) As Task(Of Boolean)
        Function CancelAllOpenOrdersAsync() As Task
        Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order))
        Function GetOrderHistoryAsync(accountId As Long, from As DateTime, [to] As DateTime) As Task(Of IEnumerable(Of Order))
    End Interface

End Namespace
