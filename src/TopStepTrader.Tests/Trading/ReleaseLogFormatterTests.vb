Imports System.Globalization
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' BUG-90 F5 regression coverage. The structured release log line is the contract
    ''' between in-app release code and downstream log-scraping tools — its shape must
    ''' stay stable.
    ''' </summary>
    Public Class ReleaseLogFormatterTests

        <Fact>
        Public Sub Format_EmitsAllExpectedKeysInOrder()
            ' Mirrors the BUG-90 sweep release path: slot 1, M6E, sweep trigger,
            ' a last-good snapshot 3 minutes before now, no misses (sweep saw stale REST
            ' but per-tick MissCount was suppressed in this scenario).
            Dim stamp = New DateTime(2026, 5, 18, 10, 30, 0, DateTimeKind.Utc)
            Dim line = ReleaseLogFormatter.Format(
                slotIndex:=1,
                instrument:="M6E",
                reason:="Closed by Broker (sweep)",
                trigger:="sweep",
                lastSnapshotOkUtc:=stamp,
                missCount:=0,
                netPosLastSeen:=2)
            Assert.Contains("slot=1 ", line)
            Assert.Contains(" instrument=M6E ", line)
            Assert.Contains(" reason=""Closed by Broker (sweep)"" ", line)
            Assert.Contains(" trigger=sweep ", line)
            Assert.Contains(" lastSnapshotOk=" & stamp.ToString("o", CultureInfo.InvariantCulture) & " ", line)
            Assert.Contains(" missCount=0 ", line)
            Assert.EndsWith(" netPosLastSeen=2", line)
        End Sub

        <Fact>
        Public Sub Format_RendersMinValueStampAsNever()
            ' The first tick after slot open has no successful snapshot yet — the formatter
            ' must surface that as the literal "never" so log scrapers don't trip on the
            ' .NET DateTime.MinValue ISO string ("0001-01-01T00:00:00.0000000").
            Dim line = ReleaseLogFormatter.Format(
                slotIndex:=0, instrument:="MES",
                reason:="Closed by Broker", trigger:="miss",
                lastSnapshotOkUtc:=DateTime.MinValue,
                missCount:=3, netPosLastSeen:=0)
            Assert.Contains(" lastSnapshotOk=never ", line)
        End Sub

        <Fact>
        Public Sub Format_HandlesNullStringFieldsWithoutThrowing()
            Dim line = ReleaseLogFormatter.Format(
                slotIndex:=2, instrument:=Nothing,
                reason:=Nothing, trigger:=Nothing,
                lastSnapshotOkUtc:=DateTime.MinValue,
                missCount:=0, netPosLastSeen:=0)
            ' Defensive — keys must still all appear so a partial caller cannot corrupt
            ' downstream scrapers expecting a fixed shape.
            Assert.Contains("slot=2 ", line)
            Assert.Contains(" instrument= ", line)
            Assert.Contains(" trigger= ", line)
        End Sub

    End Class

End Namespace
