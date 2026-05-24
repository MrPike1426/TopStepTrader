Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Trading

    ''' <summary>BUG-88: outcome returned by <see cref="ExitGate.ApplyExitGate"/>. The caller maps each
    ''' value to either a <c>ReleaseSlotAsync</c> call (ImmediateExit / ConsecutiveExit) or a log line
    ''' (WarningIncremented / CounterReset).</summary>
    Public Enum ExitGateAction
        ''' <summary>Throttled — the engine already evaluated this slot on this bar timestamp, or the
        ''' bar produced no actionable transition (e.g. counter already 0 and score below threshold).</summary>
        NoAction
        ''' <summary>E1 SuperTrend flip fired — caller must close the position regardless of the
        ''' two-bar gate.</summary>
        ImmediateExit
        ''' <summary>Second consecutive closed bar at-or-above ExitScoreThreshold — the two-bar gate
        ''' is satisfied and the caller must close the position.</summary>
        ConsecutiveExit
        ''' <summary>First closed bar at-or-above ExitScoreThreshold — counter has been incremented
        ''' (to 1). Caller should emit a warning log line.</summary>
        WarningIncremented
        ''' <summary>A new closed bar arrived with score below threshold and the prior counter was
        ''' > 0 — counter has been reset to 0. Caller should emit a cleared log line.</summary>
        CounterReset
    End Enum

    ''' <summary>BUG-88: the two-consecutive-bars exit gate, extracted from
    ''' <c>SuperTrendPlusViewModel</c> so it can be unit-tested without spinning up the full
    ''' ViewModel + DI graph. Mutates <see cref="PositionSlot.ConsecutiveExitBars"/> and
    ''' <see cref="PositionSlot.LastExitDecisionBarTime"/> on the supplied slot; returns an
    ''' <see cref="ExitGateAction"/> describing what the caller should do next.</summary>
    Public Module ExitGate

        ''' <summary>Applies the per-closed-bar exit-decision gate.</summary>
        ''' <param name="slot">Open position slot — counter and bar-time stamp are mutated in place.</param>
        ''' <param name="eval">Result of <c>ExitSignalEngine.Evaluate</c> for the current tick.</param>
        ''' <param name="currentBarTs">Timestamp of the latest closed strategy-TF bar (i.e.
        ''' <c>bars(n).Timestamp</c>). Used to throttle counter mutations to one per bar.</param>
        ''' <param name="exitScoreThreshold">The score at-or-above which a bar counts toward the
        ''' gate (currently <c>SuperTrendPlusConfig.ExitScoreThreshold = 7</c>).</param>
        Public Function ApplyExitGate(slot As PositionSlot,
                                      eval As ExitEvaluation,
                                      currentBarTs As DateTimeOffset,
                                      exitScoreThreshold As Integer) As ExitGateAction
            If slot Is Nothing OrElse eval Is Nothing Then Return ExitGateAction.NoAction

            ' E1 (SuperTrend flip) bypasses the two-bar gate entirely.
            If eval.ImmediateExit Then
                slot.ConsecutiveExitBars = 0
                Return ExitGateAction.ImmediateExit
            End If

            ' Throttle: the management tick fires several times per minute, but a single closed
            ' bar must produce at most one counter mutation. Without the throttle, three ticks
            ' all seeing the same score-7 bar would set the counter to 3 and exit on bar 1.
            If currentBarTs <= slot.LastExitDecisionBarTime Then
                Return ExitGateAction.NoAction
            End If

            slot.LastExitDecisionBarTime = currentBarTs

            If eval.Score >= exitScoreThreshold Then
                slot.ConsecutiveExitBars += 1
                If slot.ConsecutiveExitBars >= 2 Then
                    Return ExitGateAction.ConsecutiveExit
                End If
                Return ExitGateAction.WarningIncremented
            End If

            ' Score dropped below threshold — reset the counter. Only report CounterReset when
            ' there was actually something to clear, so the caller's log line is gated to the
            ' transition.
            If slot.ConsecutiveExitBars > 0 Then
                slot.ConsecutiveExitBars = 0
                Return ExitGateAction.CounterReset
            End If

            Return ExitGateAction.NoAction
        End Function

    End Module

End Namespace
