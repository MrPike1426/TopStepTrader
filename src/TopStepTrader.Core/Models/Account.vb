Namespace TopStepTrader.Core.Models

    Public Class Account
        Public Property Id As Long
        Public Property Name As String = String.Empty
        Public Property Balance As Decimal
        Public Property CanTrade As Boolean
        Public Property IsVisible As Boolean
        Public Property StartingBalance As Decimal
    End Class

End Namespace
