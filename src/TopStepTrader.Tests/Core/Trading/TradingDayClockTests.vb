Imports TopStepTrader.Core.Trading
Imports Xunit

' Global-anchored so this file does not introduce a "Core" namespace under the
' test root, which would capture fully-qualified TopStepTrader.Core.* references
' in sibling test files (VB nests declared namespaces under RootNamespace).
Namespace Global.TopStepTrader.Tests.Core.Trading

    ''' <summary>
    ''' ARCH-21 F4: trading-day boundary at 17:00 US Central, verified on both DST
    ''' regimes. In July (CDT) 17:00 CT = 22:00 UTC; in January (CST) 17:00 CT = 23:00 UTC.
    ''' </summary>
    Public Class TradingDayClockTests

        ' ── Summer (CDT, UTC-5) ─────────────────────────────────────────────────

        <Fact>
        Public Sub Summer_JustBeforeRollover_StartIsPreviousDay()
            ' 2026-07-15 16:59 CT = 21:59 UTC — still inside the day that began Jul 14 17:00 CT.
            Dim nowUtc As New DateTimeOffset(2026, 7, 15, 21, 59, 0, TimeSpan.Zero)
            Assert.Equal(New DateTimeOffset(2026, 7, 14, 22, 0, 0, TimeSpan.Zero),
                         TradingDayClock.TradingDayStartUtc(nowUtc))
            Assert.Equal("2026-07-15", TradingDayClock.TradingDayKey(nowUtc))
        End Sub

        <Fact>
        Public Sub Summer_AtRollover_StartIsThatInstant()
            ' 2026-07-15 17:00 CT = 22:00 UTC — the new trading day begins exactly here.
            Dim nowUtc As New DateTimeOffset(2026, 7, 15, 22, 0, 0, TimeSpan.Zero)
            Assert.Equal(nowUtc, TradingDayClock.TradingDayStartUtc(nowUtc))
            Assert.Equal("2026-07-16", TradingDayClock.TradingDayKey(nowUtc))
        End Sub

        ' ── Winter (CST, UTC-6) ─────────────────────────────────────────────────

        <Fact>
        Public Sub Winter_JustBeforeRollover_StartIsPreviousDay()
            ' 2026-01-15 16:59 CT = 22:59 UTC.
            Dim nowUtc As New DateTimeOffset(2026, 1, 15, 22, 59, 0, TimeSpan.Zero)
            Assert.Equal(New DateTimeOffset(2026, 1, 14, 23, 0, 0, TimeSpan.Zero),
                         TradingDayClock.TradingDayStartUtc(nowUtc))
            Assert.Equal("2026-01-15", TradingDayClock.TradingDayKey(nowUtc))
        End Sub

        <Fact>
        Public Sub Winter_JustAfterRollover_StartIsSameEvening()
            ' 2026-01-15 17:01 CT = 23:01 UTC.
            Dim nowUtc As New DateTimeOffset(2026, 1, 15, 23, 1, 0, TimeSpan.Zero)
            Assert.Equal(New DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero),
                         TradingDayClock.TradingDayStartUtc(nowUtc))
            Assert.Equal("2026-01-16", TradingDayClock.TradingDayKey(nowUtc))
        End Sub

        ' ── Key stability ───────────────────────────────────────────────────────

        <Fact>
        Public Sub Key_StableWithinDay_ChangesAcrossBoundary()
            ' Evening after open, overnight, and next afternoon all share one key…
            Dim evening As New DateTimeOffset(2026, 7, 14, 22, 30, 0, TimeSpan.Zero)   ' Jul 14 17:30 CT
            Dim overnight As New DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero)   ' Jul 15 03:00 CT
            Dim afternoon As New DateTimeOffset(2026, 7, 15, 21, 30, 0, TimeSpan.Zero) ' Jul 15 16:30 CT
            Assert.Equal(TradingDayClock.TradingDayKey(evening), TradingDayClock.TradingDayKey(overnight))
            Assert.Equal(TradingDayClock.TradingDayKey(overnight), TradingDayClock.TradingDayKey(afternoon))

            ' …and the instant past 17:00 CT keys differently.
            Dim afterRollover As New DateTimeOffset(2026, 7, 15, 22, 0, 0, TimeSpan.Zero)
            Assert.NotEqual(TradingDayClock.TradingDayKey(afternoon), TradingDayClock.TradingDayKey(afterRollover))
        End Sub

    End Class

End Namespace
