Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' ARCH-20: outcome of one <c>IPositionManagementService.UpdateAsync</c> call. When
    ''' <see cref="Outcome"/> is <c>ExitRequested</c> the caller must invoke
    ''' <c>IExitExecutionService.CloseAsync</c> with <see cref="ExitReason"/> +
    ''' <see cref="ExitTrigger"/>; otherwise carry on monitoring.
    ''' </summary>
    Public Class PositionManagementResult

        ''' <summary>Continue monitoring or fire an exit. See <see cref="PositionManagementOutcome"/>.</summary>
        Public Property Outcome As PositionManagementOutcome = PositionManagementOutcome.Continue

        ''' <summary>Free-text exit reason — surfaced on <c>TradeOutcomes.ExitReasonCode</c> and
        ''' the structured release log. Populated when <see cref="Outcome"/> is
        ''' <c>ExitRequested</c>.</summary>
        Public Property ExitReason As String = String.Empty

        ''' <summary>Structured trigger tag for the release log
        ''' (<c>"miss"</c>, <c>"staleness"</c>, <c>"pnl-guard"</c>, <c>"exit-engine"</c>,
        ''' <c>"bracket-missing"</c>). Mirrors the legacy ReleaseSlotAsync trigger argument.</summary>
        Public Property ExitTrigger As String = String.Empty

        ''' <summary>True when the management tick ratcheted the SL this iteration. The VM
        ''' observes this and refreshes the slot card's stop chip.</summary>
        Public Property StopAdjusted As Boolean = False

        ''' <summary>True when the tick successfully fetched bars + ran the exit engine.
        ''' False when the tick aborted early (snapshot miss, bar fetch failure, etc.) so
        ''' the VM knows whether <see cref="LatestPnl"/> / <see cref="CurrentClose"/> /
        ''' the ADX samples are meaningful.</summary>
        Public Property RanExitEngine As Boolean = False

        ''' <summary>Latest unrealised P&amp;L computed locally from <c>EntryPrice</c> +
        ''' <c>LivePrice</c>. Mirrors <c>slot.UnrealizedPnl</c> after the tick.</summary>
        Public Property LatestPnl As Decimal

        ''' <summary>Freshest price the tick used as "close" — <c>slot.LivePrice</c> when
        ''' the push stream is alive, otherwise the latest strategy-bar close.</summary>
        Public Property CurrentClose As Decimal

        ''' <summary>ADX sample for the trend-strength chart on the slot card.
        ''' <see cref="Single.NaN"/> when the management tick did not reach the indicator
        ''' computation step (early abort).</summary>
        Public Property AdxSample As Single = Single.NaN

        ''' <summary>+DI sample for the trend-strength chart.</summary>
        Public Property PlusDiSample As Single = Single.NaN

        ''' <summary>−DI sample for the trend-strength chart.</summary>
        Public Property MinusDiSample As Single = Single.NaN

        ''' <summary>Absolute distance |close − ST line| for the trend-strength chart.</summary>
        Public Property PriceToStSample As Single = 0F

    End Class

End Namespace
