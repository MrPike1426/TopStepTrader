Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    Public Class Order
        Public Property Id As Long

        ''' <summary>Broker-assigned order ID returned by the execution endpoint.</summary>
        Public Property ExternalOrderId As Long?

        ''' <summary>Broker-assigned position ID resolved after the order executes. Required to close a position.</summary>
        Public Property ExternalPositionId As Long?

        Public Property AccountId As Long

        ''' <summary>Ticker symbol or numeric instrument ID as string.</summary>
        Public Property ContractId As String = String.Empty

        ''' <summary>Numeric instrument ID. Resolved from ContractId by ContractMetadataService.</summary>
        Public Property InstrumentId As Integer

        Public Property Side As OrderSide
        Public Property OrderType As OrderType

        ''' <summary>Number of units/contracts to trade.</summary>
        Public Property Quantity As Integer

        ''' <summary>USD dollar amount to invest when set.</summary>
        Public Property Amount As Decimal?

        Public Property LimitPrice As Decimal?
        Public Property StopPrice As Decimal?

        ''' <summary>Stop-loss trigger price level (absolute price, not ticks).</summary>
        Public Property StopLossRate As Decimal?

        ''' <summary>Take-profit trigger price level (absolute price, not ticks).</summary>
        Public Property TakeProfitRate As Decimal?

        ''' <summary>
        ''' When True, native Trailing Stop Loss is enabled on the position.
        ''' </summary>
        Public Property IsTslEnabled As Boolean = False

        Public Property Status As OrderStatus
        Public Property PlacedAt As DateTimeOffset
        Public Property FilledAt As DateTimeOffset?
        Public Property FillPrice As Decimal?
        Public Property SourceSignalId As Long?
        Public Property Notes As String = String.Empty
        Public Property OcoBracketName As String = String.Empty

        ' ── TopStepX / futures-specific ─────────────────────────────────────────────

        ''' <summary>
        ''' TopStepX: initial stop-loss distance in ticks from fill price.
        ''' When set, ProjectXOrderService submits a stopLossBracket with this tick count.
        ''' </summary>
        Public Property InitialStopTicks As Integer?

        ''' <summary>
        ''' TopStepX: approximate entry price used to convert a price-based SL to ticks
        ''' when EditPositionSlTpAsync is called post-open.
        ''' </summary>
        Public Property EstimatedEntryPrice As Decimal?

        ''' <summary>
        ''' TopStepX: initial take-profit distance in ticks from fill price.
        ''' When set, ProjectXOrderService submits a takeProfitBracket with this tick count.
        ''' </summary>
        Public Property InitialTakeProfitTicks As Integer?

        ''' <summary>
        ''' TopStepX: ID of the resting SL order so synthetic-OCO can cancel it on flatten.
        ''' </summary>
        Public Property SlOrderId As Long?

        ''' <summary>Which broker should execute this order. Set from SelectedAccount.Broker.</summary>
        Public Property Broker As BrokerType
    End Class

End Namespace
