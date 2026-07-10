Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Risk
Imports Xunit

Namespace TopStepTrader.Tests.Services.Risk

    ''' <summary>
    ''' FEAT-73 F6: verdict boundaries for the pure combine-rule evaluator.
    ''' Defaults under test (TopStep50k preset): soft −600, hard −750,
    ''' lock trigger +150 / floor +100, max 4 trades, 2 consecutive losers.
    ''' </summary>
    Public Class CombineRuleEvaluatorTests

        Private Shared Function EnabledSettings() As CombineSettings
            Return New CombineSettings With {.Enabled = True}
        End Function

        Private Shared Function Evaluate(settings As CombineSettings,
                                         combined As Decimal,
                                         Optional tradesToday As Integer = 0,
                                         Optional consecutiveLosers As Integer = 0,
                                         Optional armed As Boolean = False,
                                         Optional highWater As Decimal = 0D,
                                         Optional anyOpen As Boolean = True) As CombineVerdict
            ' Split combined across realised/unrealised arbitrarily — the evaluator
            ' only ever consumes the sum.
            Return CombineRuleEvaluator.Evaluate(settings, combined, 0D,
                                                 tradesToday, consecutiveLosers,
                                                 armed, highWater, anyOpen)
        End Function

        ' ── Disabled mode ─────────────────────────────────────────────────────

        <Fact>
        Public Sub Disabled_AlwaysNone_EvenDeepUnderwater()
            Dim verdict = Evaluate(New CombineSettings(), combined:=-5000D, tradesToday:=99, consecutiveLosers:=99)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
            Assert.Equal(RiskHaltReason.None, verdict.Reason)
        End Sub

        <Fact>
        Public Sub NothingSettings_ReturnsNone()
            Dim verdict = Evaluate(Nothing, combined:=-5000D)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        ' ── Soft loss line ────────────────────────────────────────────────────

        <Fact>
        Public Sub SoftLine_ExactBoundary_SoftHalts()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-600D)
            Assert.Equal(CombineVerdictKind.SoftHalt, verdict.Kind)
            Assert.Equal(RiskHaltReason.DailyLossLimit, verdict.Reason)
        End Sub

        <Fact>
        Public Sub JustAboveSoftLine_None()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-599.99D)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        <Fact>
        Public Sub LossRecovery_ClearsSoftHalt_Statelessly()
            ' The evaluator carries no halt state: a recovered P&L simply stops
            ' producing the soft verdict.
            Assert.Equal(CombineVerdictKind.SoftHalt, Evaluate(EnabledSettings(), combined:=-600D).Kind)
            Assert.Equal(CombineVerdictKind.None, Evaluate(EnabledSettings(), combined:=-500D).Kind)
        End Sub

        ' ── Hard loss line ────────────────────────────────────────────────────

        <Fact>
        Public Sub HardLine_ExactBoundary_HardHaltFlatten()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-750D)
            Assert.Equal(CombineVerdictKind.HardHaltFlatten, verdict.Kind)
            Assert.Equal(RiskHaltReason.DailyLossLimit, verdict.Reason)
        End Sub

        <Fact>
        Public Sub BetweenSoftAndHard_SoftOnly()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-749.99D)
            Assert.Equal(CombineVerdictKind.SoftHalt, verdict.Kind)
        End Sub

        <Fact>
        Public Sub HardLine_BeatsProfitLock_WhenBothCouldFire()
            ' Armed with a floor retrace AND under the hard line: the hard line wins.
            Dim verdict = Evaluate(EnabledSettings(), combined:=-750D, armed:=True, highWater:=200D)
            Assert.Equal(CombineVerdictKind.HardHaltFlatten, verdict.Kind)
        End Sub

        ' ── Profit lock: arming + high-water ratchet ──────────────────────────

        <Fact>
        Public Sub LockArms_AtExactTrigger_WithOpenPositions()
            Dim verdict = Evaluate(EnabledSettings(), combined:=150D, anyOpen:=True)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
            Assert.True(verdict.ProfitLockArmed)
            Assert.Equal(150D, verdict.ProfitLockHighWater)
        End Sub

        <Fact>
        Public Sub BelowTrigger_DoesNotArm()
            Dim verdict = Evaluate(EnabledSettings(), combined:=149.99D, anyOpen:=True)
            Assert.False(verdict.ProfitLockArmed)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        <Fact>
        Public Sub HighWater_RatchetsUpOnly()
            Dim up = Evaluate(EnabledSettings(), combined:=220D, armed:=True, highWater:=200D, anyOpen:=True)
            Assert.Equal(220D, up.ProfitLockHighWater)

            Dim down = Evaluate(EnabledSettings(), combined:=180D, armed:=True, highWater:=220D, anyOpen:=True)
            Assert.Equal(220D, down.ProfitLockHighWater)
            Assert.Equal(CombineVerdictKind.None, down.Kind)
        End Sub

        ' ── Profit lock: floor retrace + flat-book banking ────────────────────

        <Fact>
        Public Sub Armed_RetraceToExactFloor_ProfitLockFlatten()
            Dim verdict = Evaluate(EnabledSettings(), combined:=100D, armed:=True, highWater:=200D, anyOpen:=True)
            Assert.Equal(CombineVerdictKind.ProfitLockFlatten, verdict.Kind)
            Assert.Equal(RiskHaltReason.DailyProfitLock, verdict.Reason)
        End Sub

        <Fact>
        Public Sub Armed_JustAboveFloor_KeepsRunning()
            Dim verdict = Evaluate(EnabledSettings(), combined:=100.01D, armed:=True, highWater:=200D, anyOpen:=True)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        <Fact>
        Public Sub FlatBook_AtExactTrigger_BanksImmediately()
            Dim verdict = Evaluate(EnabledSettings(), combined:=150D, anyOpen:=False)
            Assert.Equal(CombineVerdictKind.ProfitLockFlatten, verdict.Kind)
            Assert.Equal(RiskHaltReason.DailyProfitLock, verdict.Reason)
        End Sub

        <Fact>
        Public Sub FlatBook_ArmedButBetweenFloorAndTrigger_KeepsRunning()
            ' Armed earlier, closed at +120: above the floor, below the trigger —
            ' trading may continue.
            Dim verdict = Evaluate(EnabledSettings(), combined:=120D, armed:=True, highWater:=200D, anyOpen:=False)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        ' ── Trade-count and consecutive-loser circuit breakers ────────────────

        <Fact>
        Public Sub MaxTrades_AtLimit_SoftHalts()
            Dim verdict = Evaluate(EnabledSettings(), combined:=40D, tradesToday:=4)
            Assert.Equal(CombineVerdictKind.SoftHalt, verdict.Kind)
            Assert.Equal(RiskHaltReason.MaxTradesPerDay, verdict.Reason)
        End Sub

        <Fact>
        Public Sub MaxTrades_UnderLimit_None()
            Dim verdict = Evaluate(EnabledSettings(), combined:=40D, tradesToday:=3)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        <Fact>
        Public Sub ConsecutiveLosers_AtLimit_SoftHalts()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-100D, tradesToday:=2, consecutiveLosers:=2)
            Assert.Equal(CombineVerdictKind.SoftHalt, verdict.Kind)
            Assert.Equal(RiskHaltReason.ConsecutiveLosses, verdict.Reason)
        End Sub

        <Fact>
        Public Sub ConsecutiveLosers_UnderLimit_None()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-100D, tradesToday:=2, consecutiveLosers:=1)
            Assert.Equal(CombineVerdictKind.None, verdict.Kind)
        End Sub

        <Fact>
        Public Sub SoftLossLine_TakesPrecedence_OverCounterReasons()
            Dim verdict = Evaluate(EnabledSettings(), combined:=-600D, tradesToday:=4, consecutiveLosers:=2)
            Assert.Equal(CombineVerdictKind.SoftHalt, verdict.Kind)
            Assert.Equal(RiskHaltReason.DailyLossLimit, verdict.Reason)
        End Sub

    End Class

End Namespace
