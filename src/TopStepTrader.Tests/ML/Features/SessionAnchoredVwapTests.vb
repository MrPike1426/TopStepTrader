Imports System.Collections.Generic
Imports TopStepTrader.ML.Features
Imports Xunit

Namespace TopStepTrader.Tests.ML.Features

    ''' <summary>
    ''' FEAT-75 F5: Session-anchored VWAP unit tests. The session boundary is the CME
    ''' Globex reopen at 17:00 US Central. All fixtures use January dates so Central
    ''' time is CST (UTC−6) and the boundary sits at a deterministic 23:00 UTC.
    '''
    ''' The reset behaviour is the single biggest risk called out by STRAT-43 §7 — a
    ''' buggy reset (mid-session re-anchor, or no reset at the boundary) silently
    ''' corrupts the signal, so both directions are pinned here.
    ''' </summary>
    Public Class SessionAnchoredVwapTests

        ' Tuesday 2026-01-13. 15:00 UTC = 09:00 CST — inside the session that opened
        ' Monday 17:00 CST, so the whole fixture shares one session key.
        Private Shared ReadOnly MidSessionUtc As New DateTimeOffset(2026, 1, 13, 15, 0, 0, TimeSpan.Zero)

        ' Tuesday 2026-01-13 23:00 UTC = 17:00 CST — the exact reopen instant.
        Private Shared ReadOnly ReopenUtc As New DateTimeOffset(2026, 1, 13, 23, 0, 0, TimeSpan.Zero)

        Private Shared Function Series(ParamArray bars() As (Ts As DateTimeOffset, H As Decimal, L As Decimal, C As Decimal, V As Long)) _
            As (Ts As List(Of DateTimeOffset), H As List(Of Decimal), L As List(Of Decimal), C As List(Of Decimal), V As List(Of Long))
            Return (bars.Select(Function(b) b.Ts).ToList(),
                    bars.Select(Function(b) b.H).ToList(),
                    bars.Select(Function(b) b.L).ToList(),
                    bars.Select(Function(b) b.C).ToList(),
                    bars.Select(Function(b) b.V).ToList())
        End Function

        ' ─── Cumulative correctness ────────────────────────────────────────────

        <Fact>
        Public Sub Vwap_IsVolumeWeightedTypicalPrice_WithinOneSession()
            ' Two bars, hand-computed:
            '   bar0 typical = (101 + 99 + 100)/3 = 100, vol 100
            '   bar1 typical = (105 + 103 + 104)/3 = 104, vol 300
            '   vwap(1) = (100×100 + 104×300)/400 = 103.0
            Dim s = Series(
                (MidSessionUtc, 101D, 99D, 100D, 100L),
                (MidSessionUtc.AddMinutes(5), 105D, 103D, 104D, 300L))

            Dim vwap = TechnicalIndicators.SessionAnchoredVwap(s.Ts, s.H, s.L, s.C, s.V)

            Assert.Equal(100.0F, vwap(0), 3)
            Assert.Equal(103.0F, vwap(1), 3)
        End Sub

        <Fact>
        Public Sub Vwap_NoMidSessionReAnchor_AllBarsContribute()
            ' 12 bars over an hour, all in one session. If any mid-session re-anchor
            ' happened, the final VWAP would drift toward the recent prices (105)
            ' instead of the full-series volume-weighted mean.
            Dim bars As New List(Of (Ts As DateTimeOffset, H As Decimal, L As Decimal, C As Decimal, V As Long))
            For i = 0 To 11
                Dim px As Decimal = If(i < 6, 100D, 105D)
                bars.Add((MidSessionUtc.AddMinutes(i * 5), px, px, px, 100L))
            Next
            Dim s = Series(bars.ToArray())

            Dim vwap = TechnicalIndicators.SessionAnchoredVwap(s.Ts, s.H, s.L, s.C, s.V)

            ' Full-session mean: (6×100 + 6×105)/12 = 102.5. A re-anchor would give 105.
            Assert.Equal(102.5F, vwap(11), 3)
        End Sub

        ' ─── Session-boundary reset ────────────────────────────────────────────

        <Fact>
        Public Sub Vwap_HardResetsAtSessionBoundary()
            ' Two bars before the 17:00 CST reopen at price 100; first bar of the new
            ' session at 120. If the reset works, the new session's VWAP is exactly the
            ' new bar's typical price — the old session's accumulation must not leak.
            Dim s = Series(
                (ReopenUtc.AddMinutes(-130), 100D, 100D, 100D, 1000L),
                (ReopenUtc.AddMinutes(-125), 100D, 100D, 100D, 1000L),
                (ReopenUtc, 121D, 119D, 120D, 10L))

            Dim result = TechnicalIndicators.VwapStandardDeviationBands(s.Ts, s.H, s.L, s.C, s.V)

            Assert.Equal(100.0F, result.Vwap(1), 3)
            Assert.Equal(120.0F, result.Vwap(2), 3)     ' fresh anchor, old volume ignored
            Assert.Equal(0.0F, result.Sd(2), 3)          ' single sample → zero deviation
        End Sub

        <Fact>
        Public Sub Vwap_BarJustBeforeBoundary_StaysInOldSession()
            ' 22:55 UTC (16:55 CST) is still the old session; 23:00 UTC starts the new one.
            Dim before = TechnicalIndicators.CmeSessionKey(ReopenUtc.AddMinutes(-5))
            Dim after = TechnicalIndicators.CmeSessionKey(ReopenUtc)

            Assert.NotEqual(before, after)
            Assert.Equal(New Date(2026, 1, 13), after)   ' session opening Tue 17:00 CST
            Assert.Equal(New Date(2026, 1, 12), before)  ' session that opened Mon 17:00 CST
        End Sub

        ' ─── SD bands ──────────────────────────────────────────────────────────

        <Fact>
        Public Sub Sd_IsPriceDeviationAroundRunningVwap()
            ' Equal-volume typicals 99 and 101 → vwap 100, variance = ((−1)² + 1²)/2 = 1 → sd 1.
            Dim s = Series(
                (MidSessionUtc, 99D, 99D, 99D, 100L),
                (MidSessionUtc.AddMinutes(5), 101D, 101D, 101D, 100L))

            Dim result = TechnicalIndicators.VwapStandardDeviationBands(s.Ts, s.H, s.L, s.C, s.V)

            Assert.Equal(100.0F, result.Vwap(1), 3)
            Assert.Equal(1.0F, result.Sd(1), 3)
        End Sub

        <Fact>
        Public Sub EmptySeries_ReturnsEmptyArrays()
            Dim vwap = TechnicalIndicators.SessionAnchoredVwap(
                New List(Of DateTimeOffset)(), New List(Of Decimal)(),
                New List(Of Decimal)(), New List(Of Decimal)(), New List(Of Long)())
            Assert.Empty(vwap)
        End Sub

    End Class

End Namespace
