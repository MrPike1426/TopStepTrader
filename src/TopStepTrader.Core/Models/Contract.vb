Namespace TopStepTrader.Core.Models

    Public Class Contract
        Public Property Id As Integer
        Public Property Name As String = String.Empty
        Public Property Description As String = String.Empty
        Public Property TickSize As Decimal
        Public Property TickValue As Decimal
        Public Property IsActive As Boolean
        Public Property ExpiryDate As DateTimeOffset?
    End Class

End Namespace
