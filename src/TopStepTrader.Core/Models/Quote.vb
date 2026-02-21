Namespace TopStepTrader.Core.Models

    Public Class Quote
        Public Property ContractId As String = String.Empty
        Public Property Timestamp As DateTimeOffset
        Public Property BidPrice As Decimal
        Public Property AskPrice As Decimal
        Public Property LastPrice As Decimal
        Public Property BidSize As Integer
        Public Property AskSize As Integer
        Public Property Volume As Long

        Public ReadOnly Property Spread As Decimal
            Get
                Return AskPrice - BidPrice
            End Get
        End Property

        Public ReadOnly Property MidPrice As Decimal
            Get
                Return (BidPrice + AskPrice) / 2D
            End Get
        End Property
    End Class

End Namespace
