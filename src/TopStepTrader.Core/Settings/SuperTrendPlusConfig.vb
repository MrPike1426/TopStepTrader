Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Unified configuration for SuperTrend+ Autopilot.
    ''' Replaces the three per-persona profile settings (Joe/Damian/Lewis).
    ''' </summary>
    Public Class SuperTrendPlusConfig

        ''' <summary>Maximum concurrent position slots per instrument.</summary>
        Public Property MaxSlots As Integer = 3

        ''' <summary>Contracts opened per slot entry.</summary>
        Public Property ContractsPerSlot As Integer = 1

        ''' <summary>Minimum ADX required to open any slot.</summary>
        Public Property AdxWeakThreshold As Single = 25.0F

        ''' <summary>ADX required to open slot 2 (index 1).</summary>
        Public Property AdxModerateThreshold As Single = 40.0F

        ''' <summary>ADX required to open slot 3 (index 2).</summary>
        Public Property AdxStrongThreshold As Single = 60.0F

        ''' <summary>Reward:risk ratio for take-profit calculation.</summary>
        Public Property TpMultiple As Decimal = 2.0D

        ''' <summary>SuperTrend ATR multiplier.</summary>
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
        ''' <summary>
        ''' Score at or above which slot health becomes Warning (Amber) — no new slots opened.
        ''' Max weighted score across all nine signals is E1:8 + E2:3 + E3:2 + E4:2 + E5:4 + E6:2 + E7:1 + E8:2 + E9:3 = 27.
        ''' Default 3.
        ''' </summary>
        Public Property WarningScoreThreshold As Integer = 3

        ''' <summary>
        ''' Score at or above which slot health becomes Exiting (Red) — position closes on next bar
        ''' (or immediately if E1 SuperTrend flip fired).  Default 6.
        ''' </summary>
        Public Property ExitingScoreThreshold As Integer = 6

        ' ── Re-entry cooldown policy (STRAT-33) ──────────────────────────────────
        ' After any slot exit (degradation or SL), re-entry on the same instrument is blocked for
        ' exactly one full bar at the selected timeframe (e.g. 60 min on 1hr, 5 min on 5min).
        '
        ' Three options were evaluated:
        '   1. One-bar cooldown (CHOSEN) — the next bar's entry gate (ST direction + ADX band +
        '      DI alignment) re-validates all conditions from scratch; no extra state needed.
        '      On the 1hr timeframe a 60-minute gap is a meaningful consolidation window.
        '   2. Higher ADX for re-entry after E3 exit — adds per-exit-reason state that must
        '      survive across bar boundaries; complexity without proven benefit.
        '   3. Session-lock — too conservative; misses valid trend resumptions after a brief
        '      pullback that clears the degradation signals.
        '
        ' Enforcement: SuperTrendPlusViewModel._reEntryCooldown records DateTimeOffset.UtcNow on
        ' ReleaseSlotAsync; EvaluateSlotEntriesAsync skips the instrument while
        ' UtcNow < cooldownTimestamp + barMinutes.

    End Class

End Namespace
