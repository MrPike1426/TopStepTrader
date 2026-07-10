Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Services.Risk

    ''' <summary>FEAT-73 F3: what the guard must do after a combine-rule evaluation.</summary>
    Public Enum CombineVerdictKind
        ''' <summary>Trading allowed.</summary>
        None = 0
        ''' <summary>Block new entries and scale-ins; keep managing open positions.</summary>
        SoftHalt = 1
        ''' <summary>Flatten everything, halt for the day (hard daily-loss line).</summary>
        HardHaltFlatten = 2
        ''' <summary>Flatten (if needed) and halt for the day with the profit banked.</summary>
        ProfitLockFlatten = 3
        ''' <summary>FEAT-74: trailing max-drawdown (MLL) breached — flatten and halt; does NOT auto-release at rollover.</summary>
        MaxDrawdownFlatten = 4
    End Enum

    ''' <summary>
    ''' FEAT-73 F3: verdict plus updated profit-lock state. The evaluator is pure —
    ''' the caller owns persisting <see cref="ProfitLockArmed"/>/<see cref="ProfitLockHighWater"/>
    ''' back into guard state for the next evaluation.
    ''' </summary>
    Public Class CombineVerdict
        Public Property Kind As CombineVerdictKind = CombineVerdictKind.None
        Public Property Reason As RiskHaltReason = RiskHaltReason.None
        Public Property Message As String = String.Empty
        Public Property ProfitLockArmed As Boolean
        Public Property ProfitLockHighWater As Decimal
    End Class

    ''' <summary>
    ''' FEAT-74 F3: outcome of a trailing max-drawdown evaluation. Pure data — the
    ''' caller owns persisting <see cref="PeakEquity"/>/<see cref="MllFloor"/>.
    ''' </summary>
    Public Class TrailVerdict
        ''' <summary>Peak after this evaluation (ratchets up only; unchanged when sampling is deferred).</summary>
        Public Property PeakEquity As Decimal
        ''' <summary>MLL line derived from the (possibly updated) peak.</summary>
        Public Property MllFloor As Decimal
        ''' <summary>True when equity is at or below floor + safety buffer — flatten and halt.</summary>
        Public Property Breached As Boolean
        Public Property Message As String = String.Empty
    End Class

    ''' <summary>
    ''' FEAT-73 F3: pure, timer-free combine-rule evaluation. No I/O, no clock —
    ''' fully unit-testable. Precedence: hard daily-loss line, then profit lock,
    ''' then soft halts (loss line, trade count, consecutive losers).
    ''' FEAT-74 adds <see cref="EvaluateTrail"/> — the trailing MLL check the guard
    ''' runs *before* the daily rules (an MLL breach outranks every daily verdict).
    ''' </summary>
    Public Module CombineRuleEvaluator

        Public Function Evaluate(settings As CombineSettings,
                                 realised As Decimal,
                                 unrealised As Decimal,
                                 tradesToday As Integer,
                                 consecutiveLosers As Integer,
                                 profitLockArmed As Boolean,
                                 profitLockHighWater As Decimal,
                                 anyOpenPositions As Boolean) As CombineVerdict
            Dim verdict As New CombineVerdict With {
                .ProfitLockArmed = profitLockArmed,
                .ProfitLockHighWater = profitLockHighWater
            }
            If settings Is Nothing OrElse Not settings.Enabled Then Return verdict

            Dim combined As Decimal = realised + unrealised

            ' Lock-state ratchet: arm at the trigger, high-water only moves up.
            If combined >= settings.ProfitLockTriggerDollars Then
                verdict.ProfitLockArmed = True
            End If
            If verdict.ProfitLockArmed AndAlso combined > verdict.ProfitLockHighWater Then
                verdict.ProfitLockHighWater = combined
            End If

            ' 1) Hard daily-loss line — flatten everything, done for the day.
            If combined <= settings.DailyLossHardDollars Then
                verdict.Kind = CombineVerdictKind.HardHaltFlatten
                verdict.Reason = RiskHaltReason.DailyLossLimit
                verdict.Message = $"Combine hard loss line breached (combined ${combined:F2} <= ${settings.DailyLossHardDollars:F2}). Force-flattening and halting for the day."
                Return verdict
            End If

            ' 2) Profit lock — bank the green day.
            If verdict.ProfitLockArmed Then
                If combined <= settings.ProfitLockFloorDollars Then
                    verdict.Kind = CombineVerdictKind.ProfitLockFlatten
                    verdict.Reason = RiskHaltReason.DailyProfitLock
                    verdict.Message = $"Profit lock hit: combined ${combined:F2} retraced to floor ${settings.ProfitLockFloorDollars:F2} after arming at ${settings.ProfitLockTriggerDollars:F2}. Banking the day."
                    Return verdict
                End If
                If Not anyOpenPositions AndAlso combined >= settings.ProfitLockTriggerDollars Then
                    verdict.Kind = CombineVerdictKind.ProfitLockFlatten
                    verdict.Reason = RiskHaltReason.DailyProfitLock
                    verdict.Message = $"Daily profit target banked: flat at combined ${combined:F2} >= trigger ${settings.ProfitLockTriggerDollars:F2}. Halting for the day."
                    Return verdict
                End If
            End If

            ' 3) Soft halts — block new entries, keep managing open positions.
            If combined <= settings.DailyLossSoftDollars Then
                verdict.Kind = CombineVerdictKind.SoftHalt
                verdict.Reason = RiskHaltReason.DailyLossLimit
                verdict.Message = $"Combine soft loss line reached (combined ${combined:F2} <= ${settings.DailyLossSoftDollars:F2}). New entries blocked; clears if P&L recovers."
            ElseIf settings.MaxTradesPerDay > 0 AndAlso tradesToday >= settings.MaxTradesPerDay Then
                verdict.Kind = CombineVerdictKind.SoftHalt
                verdict.Reason = RiskHaltReason.MaxTradesPerDay
                verdict.Message = $"Max trades per day reached ({tradesToday}/{settings.MaxTradesPerDay}). New entries blocked for the trading day."
            ElseIf settings.MaxConsecutiveLosers > 0 AndAlso consecutiveLosers >= settings.MaxConsecutiveLosers Then
                verdict.Kind = CombineVerdictKind.SoftHalt
                verdict.Reason = RiskHaltReason.ConsecutiveLosses
                verdict.Message = $"{consecutiveLosers} consecutive losers (limit {settings.MaxConsecutiveLosers}). New entries blocked for the trading day."
            End If

            Return verdict
        End Function

        ''' <summary>
        ''' FEAT-74 F3: trailing max-drawdown (MLL) evaluation.
        ''' Peak ratchets up only (<paramref name="ratchetPeak"/> False defers sampling —
        ''' <c>TrailMode="EndOfDay"</c> samples at day rollover instead of per tick).
        ''' Floor = peak + <c>TrailingMaxDrawdownDollars</c>, capped at the starting
        ''' balance when <c>TrailFreezeAtStartBalance</c> (TopStep's freeze rule).
        ''' Breach at <c>equity &lt;= floor + SafetyBufferDollars</c> so the app halts
        ''' before TopStep's own line.
        ''' </summary>
        Public Function EvaluateTrail(settings As CombineSettings,
                                      equity As Decimal,
                                      priorPeakEquity As Decimal,
                                      ratchetPeak As Boolean) As TrailVerdict
            Dim result As New TrailVerdict With {.PeakEquity = priorPeakEquity}
            If settings Is Nothing OrElse Not settings.Enabled Then Return result

            If ratchetPeak AndAlso equity > result.PeakEquity Then
                result.PeakEquity = equity
            End If

            Dim floor As Decimal = result.PeakEquity + settings.TrailingMaxDrawdownDollars
            If settings.TrailFreezeAtStartBalance Then
                floor = Math.Min(floor, settings.StartingBalance)
            End If
            result.MllFloor = floor

            If equity <= floor + settings.SafetyBufferDollars Then
                result.Breached = True
                result.Message = $"Trailing max-drawdown breached: equity ${equity:F2} <= MLL floor ${floor:F2} + buffer ${settings.SafetyBufferDollars:F2} (peak ${result.PeakEquity:F2}). Force-flattening and halting — combine rule; manual reset required."
            End If

            Return result
        End Function

    End Module

End Namespace
