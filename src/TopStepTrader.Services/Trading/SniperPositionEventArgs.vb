Namespace TopStepTrader.Services.Trading

    ''' <summary>Position state snapshot raised on every qty/avgEntry/freeRide change.</summary>
    Public Class SniperPositionEventArgs
        Inherits EventArgs
        Public ReadOnly Property CurrentQty As Integer
        Public ReadOnly Property AverageEntry As Decimal
        Public ReadOnly Property FreeRideActive As Boolean
        Public ReadOnly Property CurrentHeat As Decimal

        Public Sub New(qty As Integer, avgEntry As Decimal, freeRide As Boolean, currentHeat As Decimal)
            CurrentQty = qty
            AverageEntry = avgEntry
            FreeRideActive = freeRide
            CurrentHeat = currentHeat
        End Sub
    End Class

End Namespace
