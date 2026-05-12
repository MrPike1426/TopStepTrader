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

        ''' <summary>ADX lower band boundary — Mellow Birds (L1) starts here (fixed).</summary>
        Public Property AdxWeakThreshold As Single = 25.0F

        ''' <summary>ADX at which the bot places 2 contracts — Latte band (fixed).</summary>
        Public Property AdxModerateThreshold As Single = 40.0F

        ''' <summary>ADX at which the bot places 3 contracts — Espresso band (fixed).</summary>
        Public Property AdxStrongThreshold As Single = 60.0F

        ''' <summary>SuperTrend ATR multiplier (set from persona: Lewis=3.5, Damian=3.0, Joe=2.5).</summary>
        Public Property StMultiplier As Double = 3.0

        ''' <summary>Chart timeframe label shown in the UI (e.g. "5min").</summary>
        Public Property BarTimeframe As String = "5min"

        ' ── Degradation score thresholds ─────────────────────────────────────────
        Public Property WarningScoreThreshold As Integer = 3
        Public Property ExitingScoreThreshold As Integer = 6

        ' ── Monday morning higher-timeframe gate (FEAT-37) ───────────────────────────────────
        ''' <summary>
        ''' When True, entries on Monday before 08:00 UK local time (BST-aware) require the
        ''' 1-hour SuperTrend direction to agree with the signal direction.
        ''' Filters gap-driven phantom trends from the Sunday-open thin-liquidity window.
        ''' </summary>
        Public Property MondayMorningHtfFilterEnabled As Boolean = True

        ' ── FEAT-46: Pre-entry exit-signal gate ──────────────────────────────────
        ''' <summary>
        ''' Minimum ExitSignalEngine score (E1–E9) that blocks a new entry.
        ''' Set to 0 to disable. Default 4 — blocks entries where DI crossover (E5=4),
        ''' or ADX decline + DI compression (E3+E4=4), are already present at signal time.
        ''' </summary>
        Public Property EntryExitScoreBlockThreshold As Integer = 4

        ' ── ARCH-15: Mid-trade exit-signal threshold ─────────────────────────────
        ''' <summary>
        ''' Minimum ExitSignalEngine score (E1–E9) that triggers a discretionary
        ''' exit on an open position (in addition to the always-immediate E1 flip).
        ''' Default 7 — fires on combinations like DI crossover (E5=4) + price
        ''' rejection (E6=2) + ADX decline (E3=2) = 8.
        ''' </summary>
        Public Property ExitScoreThreshold As Integer = 7

    End Class

End Namespace
