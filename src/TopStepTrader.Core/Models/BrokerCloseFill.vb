Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' BUG-100: capture of the broker-confirmed closing fill returned by
    ''' <c>IOrderService.FlattenContractWithFillAsync</c>. Used by
    ''' <c>ExitExecutionService.CloseAsync</c> to persist truthful ExitPrice/PnL
    ''' values instead of the decision-time engine estimate that drifted by
    ''' several ticks on fast spikes (postmortem 2026-05-22 MNQ trades #110/#112).
    '''
    ''' <para><see cref="Source"/> values:
    ''' <list type="bullet">
    ''' <item><c>hub</c> — fill arrived on the SignalR <c>GatewayUserOrder</c> push (preferred).</item>
    ''' <item><c>rest-poll</c> — hub timed out; the REST history endpoint returned the fill.</item>
    ''' <item><c>engine-fallback</c> — neither path produced a fill within the timeout; the caller
    ''' will keep the engine-derived value and emit a Warning. (Represented as Fill = Nothing on
    ''' the return tuple; the string lives on <see cref="ExitExecutionResult.CloseFillSource"/>.)</item>
    ''' </list></para>
    ''' </summary>
    Public Class BrokerCloseFill
        Public Property FillPrice As Decimal
        Public Property FillTimeUtc As DateTimeOffset
        Public Property FillSize As Integer
        Public Property OrderId As Long
        Public Property Source As String = String.Empty
    End Class

End Namespace
