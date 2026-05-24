Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' ARCH-20: strategy-agnostic exit execution pipeline. Performs the data-side steps
    ''' that previously lived inside <c>SuperTrendPlusViewModel.ReleaseSlotAsync</c>:
    '''
    '''   1. Emit the structured release log line.
    '''   2. Compute the engine-derived exit price from <c>UnrealizedPnl</c> + tick metadata.
    '''   3. Close the <c>LiveTradeRecord</c> row (<c>TradeRecordService.CloseTradeAsync</c>).
    '''   4. Resolve the linked <c>TradeOutcomes</c> row.
    '''   5. Build + persist the <c>TradeLifespanRecord</c> row (FEAT-58).
    '''   6. Flatten the broker position (<c>OrderService.FlattenContractAsync</c>).
    '''   7. Close the slot on the strategy's <c>SlotManager</c>.
    '''
    ''' The service does NOT touch UI bindings, MarketHub unsubscribe state, the
    ''' <c>_releasedThisTick</c> bookkeeping flag, or debug-capture; the caller observes the
    ''' returned <see cref="ExitExecutionResult"/> and wires those up itself.
    ''' </summary>
    Public Interface IExitExecutionService

        Function CloseAsync(slot As PositionSlot,
                            exitReason As String,
                            trigger As String,
                            timeframeMinutes As Integer,
                            ct As CancellationToken) As Task(Of ExitExecutionResult)

    End Interface

End Namespace
