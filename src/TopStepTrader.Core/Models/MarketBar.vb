Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    Public Class MarketBar
        Public Property Id As Long
        Public Property ContractId As Integer
        Public Property Timestamp As DateTimeOffset
        Public Property Timeframe As BarTimeframe
        Public Property Open As Decimal
        Public Property High As Decimal
        Public Property Low As Decimal
        Public Property Close As Decimal
        Public Property Volume As Long
        Public Property VWAP As Decimal?

        ''' <summary>Convenience: bar range (High - Low)</summary>
        Public ReadOnly Property Range As Decimal
            Get
                Return High - Low
            End Get
        End Property

        ''' <summary>Convenience: body size |Close - Open|</summary>
        Public ReadOnly Property BodySize As Decimal
            Get
                Return Math.Abs(Close - Open)
            End Get
        End Property

        Public ReadOnly Property IsBullish As Boolean
            Get
                Return Close >= Open
            End Get
        End Property
    End Class

End Namespace
