Namespace TopStepTrader.Core.Events

    Public Class PositionUpdateEventArgs
        Inherits EventArgs

        Public ReadOnly Property ContractId As String
        Public ReadOnly Property NetPosition As Integer
        Public ReadOnly Property AveragePrice As Decimal
        ''' <summary>Live unrealised P&amp;L in USD, as pushed by the SignalR UserHub (GatewayUserPosition).</summary>
        Public ReadOnly Property OpenPnL As Decimal

        Public Sub New(contractId As String, netPos As Integer, avgPrice As Decimal,
                       Optional openPnL As Decimal = 0D)
            Me.ContractId = contractId
            Me.NetPosition = netPos
            Me.AveragePrice = avgPrice
            Me.OpenPnL = openPnL
        End Sub
    End Class

End Namespace
