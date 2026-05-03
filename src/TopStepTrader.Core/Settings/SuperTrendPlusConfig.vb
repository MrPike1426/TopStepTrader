Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Unified configuration for SuperTrend+ Autopilot.
    ''' Persona (Lewis/Damian/Joe) drives entry ADX gate and SuperTrend multiplier.
    ''' Contract sizing is ADX-band driven (Decaff=1, Latte=2, Espresso=3).
    ''' </summary>
    Public Class SuperTrendPlusConfig

        ''' <summary>Maximum concurrent position slots (default 3, reserved for future expansion).</summary>
        Public Property MaxSlots As Integer = 3

        ''' <summary>Active persona: "Lewis", "Damian", or "Joe".</summary>
        Public Property ActivePersona As String = "Damian"

        ''' <summary>Minimum ADX required to open any slot (set from persona).</summary>
        Public Property MinEntryAdx As Single = 30.0F

        ''' <summary>ADX lower band boundary — Decaff starts here (fixed).</summary>
        Public Property AdxWeakThreshold As Single = 25.0F

        ''' <summary>ADX at which the bot places 2 contracts — Latte band (fixed).</summary>
        Public Property AdxModerateThreshold As Single = 40.0F

        ''' <summary>ADX at which the bot places 3 contracts — Espresso band (fixed).</summary>
        Public Property AdxStrongThreshold As Single = 60.0F

        ''' <summary>SuperTrend ATR multiplier (set from persona: Lewis=3.5, Damian=3.0, Joe=2.5).</summary>
        Public Property StMultiplier As Double = 3.0

        ''' <summary>Chart timeframe label shown in the UI (e.g. "5min").</summary>
        Public Property BarTimeframe As String = "5min"

        ' ── Phased stop thresholds (SuperTrend+ only) ────────────────────────────
        ''' <summary>Profit (in R) at which the stop moves to exact entry (breakeven). Default 0.5R.</summary>
        Public Property BreakevenTriggerR As Decimal = 0.5D

        ''' <summary>Profit (in R) at which the stop locks at entry + ProfitLockOffsetR. Default 1R.</summary>
        Public Property ProfitLockTriggerR As Decimal = 1.0D

        ''' <summary>Amount above entry (in R) the stop is locked at the ProfitLock phase. Default 0.3R.</summary>
        Public Property ProfitLockOffsetR As Decimal = 0.3D

        ''' <summary>ATR multiplier for the trailing stop during the ProfitTrail phase. Default 2.0.</summary>
        Public Property TrailAtrMultiple As Decimal = 2.0D

        ''' <summary>Profit (in R) at which the ATR trailing stop activates. Default 1.5R.</summary>
        Public Property ProfitTrailTriggerR As Decimal = 1.5D

        ''' <summary>Profit (in R) at which the stop is locked at entry + HarvestLockR. Default 2R.</summary>
        Public Property HarvestTriggerR As Decimal = 2.0D

        ''' <summary>Amount above entry (in R) the stop is locked at the Harvest phase. Default 1.5R.</summary>
        Public Property HarvestLockR As Decimal = 1.5D

        ''' <summary>Profit (in R) at which the stop is locked at entry + FreeRideLockR. Default 3R.</summary>
        Public Property FreeRideTriggerR As Decimal = 3.0D

        ''' <summary>Amount above entry (in R) the stop is locked at the FreeRide phase. Default 2R.</summary>
        Public Property FreeRideLockR As Decimal = 2.0D

        ' ── Degradation score thresholds ─────────────────────────────────────────
        Public Property WarningScoreThreshold As Integer = 3
        Public Property ExitingScoreThreshold As Integer = 6

        ' ── Re-entry cooldown policy (STRAT-33) ──────────────────────────────────
        ' After any slot exit (degradation or SL), re-entry on the same instrument is blocked for
        ' exactly one full bar at the selected timeframe (e.g. 60 min on 1hr, 5 min on 5min).

        ' ── Monday morning higher-timeframe gate (FEAT-37) ───────────────────────────────────
        ''' <summary>
        ''' When True, entries on Monday before 08:00 UK local time (BST-aware) require the
        ''' 1-hour SuperTrend direction to agree with the signal direction.
        ''' Filters gap-driven phantom trends from the Sunday-open thin-liquidity window.
        ''' </summary>
        Public Property MondayMorningHtfFilterEnabled As Boolean = True

    End Class

End Namespace
