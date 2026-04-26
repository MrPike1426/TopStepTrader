Namespace TopStepTrader.UI.ViewModels

    Public Class ProTraderSlotStore

        Private _addSlot As Action(Of ProTraderSlotVm)

        Public Sub Register(callback As Action(Of ProTraderSlotVm))
            _addSlot = callback
        End Sub

        Public Sub AddSlot(slot As ProTraderSlotVm)
            _addSlot?.Invoke(slot)
        End Sub

    End Class

End Namespace
