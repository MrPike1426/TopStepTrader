Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' A lightweight snapshot of one live broker position, populated from the ProjectX positions API.
    ''' Used by the strategy engine to drive API-authoritative trade status and P&amp;L.
    ''' </summary>
    Public Class LivePositionSnapshot
        ''' <summary>Broker positionId.</summary>
        Public Property PositionId As Long
        ''' <summary>Unrealised P&amp;L in USD, as reported directly by the broker.</summary>
        Public Property UnrealizedPnlUsd As Decimal
        ''' <summary>UTC timestamp the position was opened, parsed from the API response.</summary>
        Public Property OpenedAtUtc As DateTimeOffset
        ''' <summary>True if long (buy), False if short (sell).</summary>
        Public Property IsBuy As Boolean
        ''' <summary>Contract count (TopStepX futures) or cash amount invested, as returned by the API.</summary>
        Public Property Amount As Decimal
        ''' <summary>The rate (price) at which the position was opened, as returned by the broker.</summary>
        Public Property OpenRate As Decimal
        ''' <summary>
        ''' Total units across all open positions for this contract, aggregated by OrderService.
        ''' Used by the engine to calculate P&amp;L: (currentPrice − OpenRate) × Units × direction.
        ''' </summary>
        Public Property Units As Decimal
        ''' <summary>Leverage of the representative (first) position.</summary>
        Public Property Leverage As Integer
        ''' <summary>Number of open positions aggregated into this snapshot.</summary>
        Public Property PositionCount As Integer
    End Class

End Namespace
