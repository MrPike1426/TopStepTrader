Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Domain model for a completed or in-progress live trade recorded by the Orders tab.
    ''' Populated by ITradeRecordService when a position slot opens and closes.
    ''' </summary>
    Public Class LiveTradeRecord

        Public Property Id As Long

        ''' <summary>TopStepX order ID for the market entry order (from PXPlaceOrderResponse.OrderId).</summary>
        Public Property EntryOrderId As Long

        ''' <summary>TopStepX Trade ID shown on the Trades panel (entry fill ID from /api/Trade/search). Resolved async after entry.</summary>
        Public Property TopStepXTradeId As Long?

        ''' <summary>TopStepX order ID for the exit fill. 0 when not available (e.g. FlattenContractAsync path).</summary>
        Public Property ExitOrderId As Long

        Public Property ContractId As String = String.Empty   ' full PX contract ID
        Public Property Symbol As String = String.Empty       ' display root, e.g. "/M6E"
        Public Property Direction As String = String.Empty    ' "Long" or "Short"
        Public Property Sizes As Integer                      ' contracts traded
        Public Property MaxScaleIns As Integer                ' persona slot limit (1/2/3)
        Public Property StrategyName As String = String.Empty
        Public Property Persona As String = String.Empty
        Public Property Timeframe As String = String.Empty

        Public Property EntryTime As DateTimeOffset
        Public Property ExitTime As DateTimeOffset?
        Public Property EntryPrice As Decimal
        Public Property ExitPrice As Decimal?
        Public Property PnL As Decimal?

        ''' <summary>TopStepX broker commission: $0.50 per contract round-trip.</summary>
        Public Property CommissionUsd As Decimal

        ''' <summary>Exchange/NFA fees from FavouriteContracts.RoundTripFee per contract.</summary>
        Public Property FeesUsd As Decimal

        Public Property ExitReason As String = String.Empty
        Public Property IsOpen As Boolean = True
        Public Property IsRecoveredFromCrash As Boolean = False

        ''' <summary>Stop price at the moment the trade was opened. Used to log the Initial stop adjustment row.</summary>
        Public Property InitialStopPrice As Decimal

    End Class

End Namespace
