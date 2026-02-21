Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    Public Class Order
        Public Property Id As Long
        Public Property ExternalOrderId As Long?
        Public Property AccountId As Long
        Public Property ContractId As String = String.Empty
        Public Property Side As OrderSide
        Public Property OrderType As OrderType
        Public Property Quantity As Integer
        Public Property LimitPrice As Decimal?
        Public Property StopPrice As Decimal?
        Public Property Status As OrderStatus
        Public Property PlacedAt As DateTimeOffset
        Public Property FilledAt As DateTimeOffset?
        Public Property FillPrice As Decimal?
        Public Property SourceSignalId As Long?
        Public Property Notes As String = String.Empty
    End Class

End Namespace
