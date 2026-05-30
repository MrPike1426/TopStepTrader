Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services.Risk

    ''' <summary>
    ''' FEAT-71: Lambda-backed adapter used by single-position orchestrators
    ''' (SlipStream, BreakAndBounce) to publish their open trade's unrealised PnL
    ''' to the daily-loss guard without leaking the orchestrator's internal locks.
    ''' </summary>
    Friend NotInheritable Class OrchestratorPnlSource
        Implements IOpenSlotPnlSource

        Private ReadOnly _unrealised As Func(Of Decimal)
        Private ReadOnly _isOpen As Func(Of Boolean)

        Public Sub New(unrealised As Func(Of Decimal), isOpen As Func(Of Boolean))
            _unrealised = unrealised
            _isOpen = isOpen
        End Sub

        Public Function GetUnrealisedAggregate() As Decimal _
            Implements IOpenSlotPnlSource.GetUnrealisedAggregate
            If _unrealised Is Nothing Then Return 0D
            Try
                Return _unrealised()
            Catch
                Return 0D
            End Try
        End Function

        Public Function HasOpenSlots() As Boolean _
            Implements IOpenSlotPnlSource.HasOpenSlots
            If _isOpen Is Nothing Then Return False
            Try
                Return _isOpen()
            Catch
                Return False
            End Try
        End Function

    End Class

End Namespace
