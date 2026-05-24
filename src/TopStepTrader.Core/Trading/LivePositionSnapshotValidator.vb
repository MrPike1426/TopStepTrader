Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' BUG-90 F2/F3: tests whether a <see cref="LivePositionSnapshot"/> returned by the
    ''' broker confirms a position is still open. A snapshot that arrives with a zero or
    ''' negative quantity is treated as a degenerate "snapshot success but position closed"
    ''' shape — F3 requires this to fall through to the miss / release path rather than
    ''' advancing <c>LastSnapshotOkUtc</c>, which would otherwise starve the staleness guard.
    ''' Lives in Core (no dependencies) so it is reachable from the test project.
    ''' </summary>
    Public Module LivePositionSnapshotValidator

        ''' <summary>
        ''' Returns True iff the snapshot is non-null and reports both a positive
        ''' <see cref="LivePositionSnapshot.Units"/> and a positive
        ''' <see cref="LivePositionSnapshot.Amount"/>. Either zero or negative is
        ''' treated as "broker reports flat" — see BUG-90 H2.
        ''' </summary>
        Public Function IsConfirmedOpen(snapshot As LivePositionSnapshot) As Boolean
            If snapshot Is Nothing Then Return False
            If snapshot.Units <= 0D Then Return False
            If snapshot.Amount <= 0D Then Return False
            Return True
        End Function

    End Module

End Namespace
