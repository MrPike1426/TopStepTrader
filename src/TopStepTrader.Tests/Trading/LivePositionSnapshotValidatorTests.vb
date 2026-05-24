Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' BUG-90 F2/F3 regression coverage.
    '''
    ''' Covers the H3 hypothesis: a successful snapshot call whose payload reports zero
    ''' quantity is treated as "broker confirms flat" — it must fall through to the
    ''' miss / release path rather than stamping <c>LastSnapshotOkUtc</c> and starving
    ''' the <see cref="SnapshotStalenessGuard"/>.
    ''' </summary>
    Public Class LivePositionSnapshotValidatorTests

        <Fact>
        Public Sub IsConfirmedOpen_ReturnsFalse_WhenSnapshotIsNothing()
            ' H1 hypothesis — broker REST returned no row at all (or threw and the catch
            ' nulled the snapshot). The slot must fall through to the miss path.
            Assert.False(LivePositionSnapshotValidator.IsConfirmedOpen(Nothing))
        End Sub

        <Fact>
        Public Sub IsConfirmedOpen_ReturnsFalse_WhenUnitsAreZero()
            ' H3 — degenerate "successful but flat" shape. Must NOT advance LastSnapshotOkUtc.
            Dim snapshot As New LivePositionSnapshot With {
                .Units = 0D,
                .Amount = 1D,
                .OpenRate = 1234D
            }
            Assert.False(LivePositionSnapshotValidator.IsConfirmedOpen(snapshot))
        End Sub

        <Fact>
        Public Sub IsConfirmedOpen_ReturnsFalse_WhenAmountIsZero()
            Dim snapshot As New LivePositionSnapshot With {
                .Units = 2D,
                .Amount = 0D,
                .OpenRate = 1234D
            }
            Assert.False(LivePositionSnapshotValidator.IsConfirmedOpen(snapshot))
        End Sub

        <Fact>
        Public Sub IsConfirmedOpen_ReturnsFalse_WhenUnitsNegative()
            ' Defensive — a negative-quantity snapshot is malformed and must not be
            ' treated as "still open" even though the contract usually carries unsigned units.
            Dim snapshot As New LivePositionSnapshot With {
                .Units = -1D,
                .Amount = 1D
            }
            Assert.False(LivePositionSnapshotValidator.IsConfirmedOpen(snapshot))
        End Sub

        <Fact>
        Public Sub IsConfirmedOpen_ReturnsTrue_WhenBothUnitsAndAmountPositive()
            Dim snapshot As New LivePositionSnapshot With {
                .Units = 2D,
                .Amount = 2D,
                .OpenRate = 1234D
            }
            Assert.True(LivePositionSnapshotValidator.IsConfirmedOpen(snapshot))
        End Sub

    End Class

End Namespace
