Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    Public Class TradeFilter
        Public Property Symbol As String = String.Empty
        Public Property Strategy As String = String.Empty
        Public Property Persona As String = String.Empty
        Public Property PnLFilter As PnLFilterType = PnLFilterType.All
    End Class

End Namespace
