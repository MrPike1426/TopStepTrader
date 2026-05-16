Namespace TopStepTrader.Core.Models.Debug

    Public Class DebugTradeRecord
        Public Property TradeId As String = String.Empty
        Public Property SlotIndex As Integer
        Public Property Persona As String = String.Empty
        Public Property Instrument As String = String.Empty
        Public Property TimeFrame As String = String.Empty
        Public Property EntryMode As String = String.Empty
        Public Property Direction As String = String.Empty
        Public Property EntryPrice As Decimal
        Public Property EntryTime As String = String.Empty
        Public Property InitialSL As Decimal
        Public Property InitialTP As Decimal
        Public Property ContractCount As Integer
        Public Property SuperTrendConfigJson As String = String.Empty
        Public Property AiCheckResult As String
        Public Property AiCheckReason As String
        Public Property ActualFillPrice As Nullable(Of Decimal)
        Public Property FillConfirmedTime As String
        Public Property RealisedPnLDollars As Nullable(Of Decimal)
        Public Property ClosedAt As String
        Public Property CreatedAt As String = String.Empty

        ' ── FEAT-56: reconciliation + post-mortem metadata ─────────────────────
        ''' <summary>TopStepX account that owned the trade (set at BeginTrade).</summary>
        Public Property AccountId As Long
        ''' <summary>Fill price of the order that flattened the position (post-mortem reconciliation).</summary>
        Public Property ExitPrice As Nullable(Of Decimal)
        ''' <summary>Why the trade closed: "SL Hit" / "TP Hit" / "Manual / Engine Flatten" / "Unknown".</summary>
        Public Property ExitReason As String
        ''' <summary>One of: Reconciled, StillOpen, Unreconciled, Failed. Null = never reconciled.</summary>
        Public Property ReconciliationStatus As String
        ''' <summary>UTC ISO-8601 timestamp of the last reconciliation pass.</summary>
        Public Property ReconciledAt As String
    End Class

    ''' <summary>FEAT-56: authoritative action entry recorded for a debug trade.</summary>
    Public Class DebugTradeAction
        Public Property Id As Integer
        Public Property TradeId As String = String.Empty
        Public Property TimestampUtc As String = String.Empty
        ''' <summary>One of: OrderPlaced, EntryFilled, StopLossPlaced, StopLossModified,
        ''' TakeProfitPlaced, TakeProfitModified, OrderCancelled, PartialExit, Closed.</summary>
        Public Property ActionType As String = String.Empty
        Public Property OldValue As Nullable(Of Decimal)
        Public Property NewValue As Nullable(Of Decimal)
        Public Property Price As Nullable(Of Decimal)
        Public Property Quantity As Nullable(Of Integer)
        Public Property OrderId As Nullable(Of Long)
        Public Property Reason As String
        ''' <summary>"Local" (emitted by the engine) or "Api" (synthesised by reconciliation).</summary>
        Public Property Source As String = "Local"
        Public Property RawPayloadJson As String
    End Class

End Namespace
