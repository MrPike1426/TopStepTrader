Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' FEAT-73: TopStep combine rule parameters, bound to the "Combine" appsettings
    ''' section. Defaults are the "TopStep50k" preset (soft halt −$600, hard
    ''' force-flatten −$750, profit lock arming at +$220 with a +$170 floor,
    ''' max 4 trades/day, halt after 2 consecutive losers).
    '''
    ''' STRAT-46/47: the lock floor sits $20 above TopStep's payout winning-day
    ''' threshold (≥ $150 net P&amp;L) so a lock-banked day still qualifies after
    ''' flatten slippage.
    '''
    ''' When <see cref="Enabled"/> is False the daily-loss guard behaves exactly as
    ''' FEAT-71 shipped it (RiskSettings daily loss, entry-block only, no flatten).
    ''' </summary>
    Public Class CombineSettings
        Public Property Enabled As Boolean = False
        Public Property Tier As String = "TopStep50k"
        Public Property StartingBalance As Decimal = 50000D
        ''' <summary>Combined daily P&amp;L at or below this blocks new entries (negative).</summary>
        Public Property DailyLossSoftDollars As Decimal = -600D
        ''' <summary>Combined daily P&amp;L at or below this force-flattens and halts for the day (negative).
        ''' Default leaves a $250 slippage/fee buffer under TopStep's −$1,000 line.</summary>
        Public Property DailyLossHardDollars As Decimal = -750D
        ''' <summary>Combined daily P&amp;L at or above this arms the trailing profit lock.</summary>
        Public Property ProfitLockTriggerDollars As Decimal = 220D
        ''' <summary>Once armed, a retrace to at or below this flattens and banks the day.
        ''' $20 above TopStep's ≥$150 winning-day payout threshold so flatten
        ''' slippage cannot drop a banked day under the line (STRAT-46/47).</summary>
        Public Property ProfitLockFloorDollars As Decimal = 170D
        ''' <summary>Soft-halt after this many closed trades in the trading day (0 disables).</summary>
        Public Property MaxTradesPerDay As Integer = 4
        ''' <summary>Soft-halt after this many consecutive losing closes (0 disables).</summary>
        Public Property MaxConsecutiveLosers As Integer = 2
        Public Property MaxContracts As Integer = 5
        ''' <summary>TopStep counts commissions+fees in daily P&amp;L; keep True for combine accounts.</summary>
        Public Property IncludeFeesInDailyPnl As Boolean = True
        Public Property MicrosOnly As Boolean = True

        ' ── Strategy profile (STRAT-45) ─────────────────────────────────────────
        ''' <summary>
        ''' SlipStream risk-per-trade ceiling (percent of balance) while combine mode is
        ''' on: 0.4% of $50k ≈ $200. The effective RiskPct is the smaller of this and the
        ''' user's SlipStreamConfig value — sizing can be tightened but never loosened
        ''' past the combine profile.
        ''' </summary>
        Public Property SlipStreamRiskPct As Double = 0.4

        ' ── Trailing max-drawdown (FEAT-74) ─────────────────────────────────────
        Public Property TrailingMaxDrawdownDollars As Decimal = -2000D
        ''' <summary>"IntradayPeak" (peak ratchets every tick) or "EndOfDay" (peak samples only at the 17:00-CT rollover).</summary>
        Public Property TrailMode As String = "IntradayPeak"
        ''' <summary>TopStep's freeze rule: the MLL line never rises above the starting balance.</summary>
        Public Property TrailFreezeAtStartBalance As Boolean = True
        ''' <summary>Halt app-side this many dollars before TopStep's MLL line (equity &lt;= floor + buffer).</summary>
        Public Property SafetyBufferDollars As Decimal = 100D
    End Class

End Namespace
