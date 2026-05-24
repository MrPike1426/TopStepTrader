Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    Public Class OpenSlotReleaseSinkRegistryTests

        Private Class StubSink
            Implements IOpenSlotReleaseSink
            Public ReadOnly Property OccupiedSlots As IReadOnlyList(Of PositionSlot) _
                Implements IOpenSlotReleaseSink.OccupiedSlots
                Get
                    Return Array.Empty(Of PositionSlot)()
                End Get
            End Property
            Public Function ForceReleaseAsync(slotIndex As Integer, reason As String, trigger As String) As Task _
                Implements IOpenSlotReleaseSink.ForceReleaseAsync
                Return Task.CompletedTask
            End Function
        End Class

        <Fact>
        Public Sub RegisterAndUnregister_RoundTrip()
            Dim reg As New OpenSlotReleaseSinkRegistry()
            Dim sink As New StubSink()
            Assert.Empty(reg.Snapshot())
            reg.Register(sink)
            Assert.Single(reg.Snapshot())
            ' Idempotent — re-registering the same sink does not duplicate it.
            reg.Register(sink)
            Assert.Single(reg.Snapshot())
            reg.Unregister(sink)
            Assert.Empty(reg.Snapshot())
            ' Unregistering an unknown sink is a no-op.
            reg.Unregister(New StubSink())
            Assert.Empty(reg.Snapshot())
        End Sub

        <Fact>
        Public Sub Register_IgnoresNothing()
            Dim reg As New OpenSlotReleaseSinkRegistry()
            reg.Register(Nothing)
            reg.Unregister(Nothing)
            Assert.Empty(reg.Snapshot())
        End Sub

    End Class

End Namespace
