Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' BUG-79 regression coverage for the stale-snapshot force-release path and the
    ''' new <c>PositionSlot.LastSnapshotOkUtc</c> field that gates it.
    ''' </summary>
    Public Class SlotStalenessTests

        <Fact>
        Public Sub PositionSlot_LastSnapshotOkUtc_DefaultsToMinValue()
            ' A freshly constructed slot has no confirmed snapshot — the staleness guard
            ' must yield False while in this state so the normal MissCount path drives
            ' release during the first ticks after slot open (e.g. order placed but the
            ' first SearchOpenPositionsAsync round-trip has not landed yet).
            Dim slot As New PositionSlot()
            Assert.Equal(DateTime.MinValue, slot.LastSnapshotOkUtc)
        End Sub

        <Fact>
        Public Sub StalenessGuard_ReturnsFalse_WhenStampIsMinValue()
            ' First-tick window: no successful snapshot has been recorded yet. The guard
            ' must not force-release in this state — that would defeat the purpose of
            ' the per-slot MissCount escalation which exists for exactly this scenario.
            Dim isStale = SnapshotStalenessGuard.IsStale(
                lastSnapshotOkUtc:=DateTime.MinValue,
                nowUtc:=DateTime.UtcNow,
                threshold:=TimeSpan.FromMinutes(5))
            Assert.False(isStale)
        End Sub

        <Fact>
        Public Sub StalenessGuard_ReturnsFalse_WhenWithinThreshold()
            Dim now = New DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc)
            Dim isStale = SnapshotStalenessGuard.IsStale(
                lastSnapshotOkUtc:=now.AddMinutes(-2),
                nowUtc:=now,
                threshold:=TimeSpan.FromMinutes(5))
            Assert.False(isStale)
        End Sub

        <Fact>
        Public Sub StalenessGuard_ReturnsTrue_WhenBeyondThreshold()
            ' Mirrors the MES UAT scenario: last good snapshot is ~57 minutes old while the
            ' 5-minute threshold is in force → force-release fires regardless of MissCount.
            Dim now = New DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc)
            Dim isStale = SnapshotStalenessGuard.IsStale(
                lastSnapshotOkUtc:=now.AddMinutes(-57),
                nowUtc:=now,
                threshold:=TimeSpan.FromMinutes(5))
            Assert.True(isStale)
        End Sub

        <Fact>
        Public Sub StalenessGuard_ReturnsFalse_WhenThresholdIsZeroOrNegative()
            ' Defensive: a misconfigured zero / negative threshold must NOT cause every
            ' tick to force-release. The guard returns False so the MissCount path stays
            ' in control.
            Dim now = New DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc)
            Assert.False(SnapshotStalenessGuard.IsStale(now.AddHours(-1), now, TimeSpan.Zero))
            Assert.False(SnapshotStalenessGuard.IsStale(now.AddHours(-1), now, TimeSpan.FromSeconds(-1)))
        End Sub

    End Class

End Namespace
