Namespace TopStepTrader.Core.Enums

    ''' <summary>
    ''' Phased stop management progression for an open position slot.
    ''' Phases only advance forward — the stop never retreats.
    ''' </summary>
    Public Enum StopPhase
        ''' <summary>Entry → 1R profit. Stop trails the closed strategy-TF SuperTrend line.</summary>
        Initial
        ''' <summary>Price reached 1R. Stop moved to entry + 0.5R (risk reduced by half).</summary>
        Breakeven
        ''' <summary>Price reached 1.5R. Stop trails via ATR-based formula.</summary>
        ProfitTrail
        ''' <summary>Price reached 2R. Stop locked at entry + 1.5R from entry.</summary>
        Harvest
        ''' <summary>Price reached 3R. Stop locked at entry + 2R. Let it run.</summary>
        FreeRide
    End Enum

End Namespace
