Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-71: Adapter exposing a <see cref="SlotManager"/>'s open-slot unrealised PnL
    ''' to the daily-loss guard. Snapshot-style read — sums <c>slot.UnrealizedPnl</c>
    ''' across open slots on each call. Safe to call from any thread.
    ''' </summary>
    Friend NotInheritable Class SlotManagerPnlSource
        Implements IOpenSlotPnlSource

        Private ReadOnly _slotManager As SlotManager

        Public Sub New(slotManager As SlotManager)
            _slotManager = slotManager
        End Sub

        Public Function GetUnrealisedAggregate() As Decimal _
            Implements IOpenSlotPnlSource.GetUnrealisedAggregate
            If _slotManager Is Nothing Then Return 0D
            Dim total As Decimal = 0D
            For Each slot In _slotManager.Slots
                If slot IsNot Nothing AndAlso slot.IsOpen Then
                    total += slot.UnrealizedPnl
                End If
            Next
            Return total
        End Function

        Public Function HasOpenSlots() As Boolean _
            Implements IOpenSlotPnlSource.HasOpenSlots
            If _slotManager Is Nothing Then Return False
            For Each slot In _slotManager.Slots
                If slot IsNot Nothing AndAlso slot.IsOpen Then Return True
            Next
            Return False
        End Function

    End Class

End Namespace
