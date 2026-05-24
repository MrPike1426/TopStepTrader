Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>BUG-88: tests for the two-consecutive-bars exit gate. Drives
    ''' <see cref="ExitGate.ApplyExitGate"/> directly with hand-built
    ''' <see cref="PositionSlot"/> + <see cref="ExitEvaluation"/> fixtures — no ViewModel,
    ''' no DI graph.</summary>
    Public Class ExitGateTests

        Private Const ExitScoreThreshold As Integer = 7

        Private Shared Function MakeOpenSlot() As PositionSlot
            Return New PositionSlot With {
                .Side = "Buy",
                .IsOpen = True,
                .ConsecutiveExitBars = 0,
                .LastExitDecisionBarTime = DateTimeOffset.MinValue
            }
        End Function

        Private Shared Function MakeEval(score As Integer, Optional immediateExit As Boolean = False) As ExitEvaluation
            Return New ExitEvaluation With {
                .Score = score,
                .ImmediateExit = immediateExit
            }
        End Function

        <Fact>
        Public Sub SingleBarAboveThresholdIncrementsAndDoesNotExit()
            Dim slot = MakeOpenSlot()
            Dim eval = MakeEval(score:=7)
            Dim t0 As New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)

            Dim action = ExitGate.ApplyExitGate(slot, eval, t0, ExitScoreThreshold)

            Assert.Equal(ExitGateAction.WarningIncremented, action)
            Assert.Equal(1, slot.ConsecutiveExitBars)
            Assert.Equal(t0, slot.LastExitDecisionBarTime)
        End Sub

        <Fact>
        Public Sub TwoConsecutiveBarsAboveThresholdExits()
            Dim slot = MakeOpenSlot()
            Dim t0 As New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)
            Dim t1 = t0.AddMinutes(15)

            Dim a0 = ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t0, ExitScoreThreshold)
            Assert.Equal(ExitGateAction.WarningIncremented, a0)
            Assert.Equal(1, slot.ConsecutiveExitBars)

            Dim a1 = ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t1, ExitScoreThreshold)
            Assert.Equal(ExitGateAction.ConsecutiveExit, a1)
            Assert.Equal(2, slot.ConsecutiveExitBars)
            Assert.Equal(t1, slot.LastExitDecisionBarTime)
        End Sub

        <Fact>
        Public Sub SecondBarBelowThresholdResetsCounter()
            Dim slot = MakeOpenSlot()
            Dim t0 As New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)
            Dim t1 = t0.AddMinutes(15)

            ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t0, ExitScoreThreshold)
            Assert.Equal(1, slot.ConsecutiveExitBars)

            Dim a1 = ExitGate.ApplyExitGate(slot, MakeEval(score:=5), t1, ExitScoreThreshold)

            Assert.Equal(ExitGateAction.CounterReset, a1)
            Assert.Equal(0, slot.ConsecutiveExitBars)
        End Sub

        <Fact>
        Public Sub SameBarTimestampDoesNotDoubleIncrement()
            Dim slot = MakeOpenSlot()
            Dim t0 As New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)

            Dim a0 = ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t0, ExitScoreThreshold)
            Assert.Equal(ExitGateAction.WarningIncremented, a0)
            Assert.Equal(1, slot.ConsecutiveExitBars)

            ' Second tick on the SAME bar — throttle must suppress the increment.
            Dim a1 = ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t0, ExitScoreThreshold)

            Assert.Equal(ExitGateAction.NoAction, a1)
            Assert.Equal(1, slot.ConsecutiveExitBars)
        End Sub

        <Fact>
        Public Sub ImmediateExitBypassesCounter()
            Dim slot = MakeOpenSlot()
            slot.ConsecutiveExitBars = 1   ' pre-existing warning state
            Dim t0 As New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)
            Dim eval = MakeEval(score:=4, immediateExit:=True)

            Dim action = ExitGate.ApplyExitGate(slot, eval, t0, ExitScoreThreshold)

            Assert.Equal(ExitGateAction.ImmediateExit, action)
            Assert.Equal(0, slot.ConsecutiveExitBars)   ' reset for cleanliness
        End Sub

        <Fact>
        Public Sub CounterPersistsAcrossSubThresholdBarThatStillFiresExit()
            ' Codifies the "must be consecutive" contract: a sub-threshold bar between two
            ' above-threshold bars resets the counter so the third bar must start from 1, not 2.
            Dim slot = MakeOpenSlot()
            Dim t0 As New DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero)
            Dim t1 = t0.AddMinutes(15)
            Dim t2 = t0.AddMinutes(30)

            ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t0, ExitScoreThreshold)
            Assert.Equal(1, slot.ConsecutiveExitBars)

            Dim a1 = ExitGate.ApplyExitGate(slot, MakeEval(score:=4), t1, ExitScoreThreshold)
            Assert.Equal(ExitGateAction.CounterReset, a1)
            Assert.Equal(0, slot.ConsecutiveExitBars)

            Dim a2 = ExitGate.ApplyExitGate(slot, MakeEval(score:=7), t2, ExitScoreThreshold)
            Assert.Equal(ExitGateAction.WarningIncremented, a2)
            Assert.Equal(1, slot.ConsecutiveExitBars)   ' must not be 2 — no false exit
        End Sub

    End Class

End Namespace
