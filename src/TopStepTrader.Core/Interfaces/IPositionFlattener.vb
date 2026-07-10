Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-73: outcome of a force-flatten sweep. Failures on one contract must not
    ''' abort the others, so the caller can log an incomplete flatten loudly.
    ''' </summary>
    Public Class FlattenAllResult
        ''' <summary>Open broker positions found at sweep start.</summary>
        Public Property AttemptedContracts As Integer
        ''' <summary>Positions closed successfully (broker-confirmed).</summary>
        Public Property FlattenedContracts As Integer
        ''' <summary>Contract ids whose close failed or threw.</summary>
        Public Property FailedContractIds As New List(Of String)()
        ''' <summary>True when the final cancel-all-open-orders sweep succeeded.</summary>
        Public Property OrdersCancelled As Boolean

        ''' <summary>True when every position closed and the resting-order sweep ran.</summary>
        Public ReadOnly Property Complete As Boolean
            Get
                Return FailedContractIds.Count = 0 AndAlso OrdersCancelled
            End Get
        End Property
    End Class

    ''' <summary>
    ''' FEAT-73: flattens every open position for the account and cancels resting
    ''' orders (including pre-staged stop entries). Used by the daily-loss guard's
    ''' hard combine verdicts (hard daily loss, profit lock).
    ''' </summary>
    Public Interface IPositionFlattener
        Function FlattenAllAsync(accountId As Long) As Task(Of FlattenAllResult)
    End Interface

End Namespace
