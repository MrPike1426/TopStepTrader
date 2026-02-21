Namespace TopStepTrader.Core.Settings

    Public Class TradingSettings
        Public Property DefaultOrderQuantity As Integer = 1
        Public Property DefaultTimeframe As Integer = 5
        Public Property ActiveContractIds As New List(Of String)
        Public Property PrimaryAccountId As Long = 0
    End Class

End Namespace
