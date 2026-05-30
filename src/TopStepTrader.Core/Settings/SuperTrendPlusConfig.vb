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

        ''' <summary>
        ''' Multiplier applied to the ADX-band contract count for both initial entries
        ''' and mid-trade scale-ins. 1 = unchanged; 2 = double; 3 = triple. Resets to 1
        ''' on every app start (not persisted) so leverage is never silently active
        ''' after a restart or crash.
        ''' </summary>
        Public Property LeverageMultiplier As Integer = 1

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

        ' ── BUG-81: Early-mode grace cap ─────────────────────────────────────────
        ''' <summary>
        ''' Maximum age (in minutes) that <see cref="PositionSlot.IsEarlyModeEntry"/>
        ''' may remain True without explicit ST-direction confirmation. After this
        ''' window elapses the flag is auto-cleared so a stuck early-mode flag can
        ''' never suppress downstream exit logic indefinitely. Default 30.
        ''' </summary>
        Public Property EarlyModeMaxAgeMinutes As Integer = 30

        ' ── UAT-03 F2: BB position entry gate ────────────────────────────────────
        ''' <summary>
        ''' When True, block a SHORT entry if the last <see cref="BbPositionGateBars"/>
        ''' closes were all above the 20-period BB median (mirror for LONG). Catches the
        ''' "shorting above the median" / "longing below the median" case where price
        ''' has pulled back inside the channel against the proposed direction.
        ''' </summary>
        Public Property BbPositionGateEnabled As Boolean = True

        ''' <summary>
        ''' Number of trailing closes that must all be on the wrong side of the BB median
        ''' to trigger the <see cref="BbPositionGateEnabled"/> block. Default 2.
        ''' </summary>
        Public Property BbPositionGateBars As Integer = 2

        ' ── UAT-03 F3: Consecutive-bar momentum-against entry gate ───────────────
        ''' <summary>
        ''' When True, block an entry where <c>isFlip = False</c> if the last
        ''' <see cref="MomentumAgainstGateBars"/> closes all moved against the proposed
        ''' direction (e.g. 4 consecutive higher closes on a SHORT re-entry). Genuine
        ''' SuperTrend flips (<c>isFlip = True</c>) are not affected so reversal entries
        ''' at the start of a new leg still fire.
        ''' </summary>
        Public Property MomentumAgainstGateEnabled As Boolean = True

        ''' <summary>
        ''' Number of trailing bar-to-bar moves that must all be against the proposed
        ''' direction to trigger the <see cref="MomentumAgainstGateEnabled"/> block.
        ''' Default 4.
        ''' </summary>
        Public Property MomentumAgainstGateBars As Integer = 4

        ' ── FEAT-63: $-denominated TP ladder ─────────────────────────────────────
        ''' <summary>
        ''' Global dollar TP increment for the laddered profit-protection stop. When
        ''' &gt; 0, ladder mode is active: once total unrealised P&amp;L crosses
        ''' <c>(N + 0.10) × LadderTpDollars</c> for some <c>N ≥ 1</c>, the broker
        ''' stop ratchets up to the price equivalent of <c>N × LadderTpDollars</c>
        ''' total P&amp;L. <c>ExitSignalEngine</c>'s E1–E9 force-close path is
        ''' suppressed for the entire trade while ladder mode is on; phased stops
        ''' (Initial / Breakeven / ProfitTrail / Harvest / FreeRide) keep ratcheting
        ''' in parallel and whichever stop is more conservative wins. Set to 0 to
        ''' fall back to the existing exit-gate + phased-stop behavior. Persisted
        ''' across restarts.
        ''' </summary>
        Public Property LadderTpDollars As Decimal = 0D

        ' ── STRAT-41: Pullback-gated scale-in (replaces STRAT-31 ADX-band path) ──
        ''' <summary>
        ''' When True, the position-management tick adds size only when price has
        ''' retraced to within <see cref="PullbackAtrFactor"/> × ATR of the SuperTrend
        ''' line AND DI still favours the side, and only once per slot lifetime. The
        ''' STRAT-31 ADX-band ratchet that this replaces was adding contracts at the
        ''' worst price (trend peaks).
        ''' </summary>
        Public Property PullbackScaleInEnabled As Boolean = True

        ''' <summary>STRAT-41 pullback distance from the SuperTrend line, in ATR units.
        ''' Default 0.5 — "within half an ATR" of the ST line.</summary>
        Public Property PullbackAtrFactor As Decimal = 0.5D

        ''' <summary>STRAT-41 contracts added per pullback scale-in. Default 1 —
        ''' deliberately conservative compared to the STRAT-31 design.</summary>
        Public Property PullbackScaleInContracts As Integer = 1

        ''' <summary>STRAT-41 soft cap on slot size after pullback scale-in. VM clamps
        ''' further by leverage. Default 2 — matches the BUG-99 + STRAT-41
        ''' "shrink to recover" risk posture.</summary>
        Public Property MaxContractsAfterScaleIn As Integer = 2

        ' ── STRAT-40: Multi-TF entry confirmation + BB-median relax-on-flip ──────
        ''' <summary>
        ''' When True, every survived strategy-TF candidate must additionally agree with
        ''' a lower-TF SuperTrend before firing. On disagreement the candidate is queued
        ''' (deferred) until the lower TF flips, the strategy-TF signal evaporates, or
        ''' <see cref="MultiTfDeferMaxAgeMinutes"/> elapses. Disable in dev when the
        ''' extra <c>GetLiveBarsAsync</c> per evaluated candidate is rate-budget sensitive.
        ''' </summary>
        Public Property MultiTfConfirmationEnabled As Boolean = True

        ''' <summary>
        ''' Lower-timeframe label resolved by <see cref="EvaluateSlotEntriesAsync"/> when
        ''' STRAT-40 multi-TF confirmation is active. Default 3min mirrors the user's
        ''' framing of "does the 3m timeframe agree with the 15m timeframe". Overridable
        ''' per-persona via the config persistence layer.
        ''' </summary>
        Public Property MultiTfLowerTimeframe As String = "3min"

        ''' <summary>
        ''' How long a deferred candidate may sit in the <c>_deferredCandidates</c> queue
        ''' before it is dropped (the strategy-TF signal is assumed stale by then).
        ''' </summary>
        Public Property MultiTfDeferMaxAgeMinutes As Integer = 10

        ''' <summary>
        ''' ADX threshold above which a fresh SuperTrend flip bypasses the BB-median
        ''' slope filter (STRAT-40 F1). Matches the persona-level "Cappuccino" strong-
        ''' trend entry so high-conviction reversals are not blocked by a multi-week
        ''' up-sloping BB-mid (the 6:1 long-bias trigger observed since 2026-05-19).
        ''' </summary>
        Public Property BbMedianRelaxAdxThreshold As Single = 30.0F

        ' ── UAT-03 F6: Entry-bar confirmation-candle gate ────────────────────────
        ''' <summary>
        ''' When True, block an entry where <c>isFlip = True</c> unless the entry bar
        ''' itself confirms the new direction (close past prior bar's high for LONG,
        ''' below prior bar's low for SHORT). Forces "wait for the rejection candle"
        ''' on flips while leaving established-trend re-entries untouched.
        ''' </summary>
        Public Property ConfirmationCandleGateEnabled As Boolean = True

    End Class

End Namespace
