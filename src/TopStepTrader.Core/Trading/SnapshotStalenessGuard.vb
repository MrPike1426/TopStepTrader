Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' BUG-79 pure decision: is the most recently confirmed live-position snapshot
    ''' old enough that the slot should be force-released even though the per-tick
    ''' <c>MissCount</c> escalation has not yet reached the threshold?
    ''' Lives in Core (no dependencies) so it is reachable from the test project,
    ''' which cannot reference the UI assembly that owns SuperTrendPlusViewModel.
    ''' </summary>
    Public Module SnapshotStalenessGuard

        ''' <summary>
        ''' Returns True when <paramref name="lastSnapshotOkUtc"/> is non-default and the
        ''' elapsed interval to <paramref name="nowUtc"/> exceeds <paramref name="threshold"/>.
        ''' A <c>DateTime.MinValue</c> stamp means no successful snapshot has been recorded
        ''' yet — the slot is in its initial-tick window and the guard intentionally yields
        ''' False so the normal MissCount path drives release.
        ''' </summary>
        Public Function IsStale(lastSnapshotOkUtc As DateTime,
                                nowUtc As DateTime,
                                threshold As TimeSpan) As Boolean
            If lastSnapshotOkUtc = DateTime.MinValue Then Return False
            If threshold <= TimeSpan.Zero Then Return False
            Return (nowUtc - lastSnapshotOkUtc) > threshold
        End Function

    End Module

End Namespace
