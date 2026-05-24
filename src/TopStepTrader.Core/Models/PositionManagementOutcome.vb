Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' ARCH-20: outcome flag returned by <c>IPositionManagementService.UpdateAsync</c>.
    ''' Flat enum (not a discriminated union) — when <see cref="ExitRequested"/> is set,
    ''' the accompanying <c>PositionManagementResult.ExitReason</c> string and
    ''' <c>PositionManagementResult.ExitTrigger</c> tag carry the variance.
    ''' </summary>
    Public Enum PositionManagementOutcome

        ''' <summary>The tick completed; keep monitoring this slot.</summary>
        [Continue] = 0

        ''' <summary>The tick decided this slot must be exited. The VM should call
        ''' <c>IExitExecutionService.CloseAsync</c> with the reason / trigger from the result.</summary>
        ExitRequested = 1

    End Enum

End Namespace
