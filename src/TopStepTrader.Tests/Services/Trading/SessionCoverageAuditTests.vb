Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trading

    ''' <summary>
    ''' STRAT-42 F5: regression tests for the 23-hour session coverage audit.
    ''' F5-a: SessionWindowResolver.Resolve labels for hours 0-23.
    ''' F5-b: parity from each former host site (VM / EntryExec / ExitExec).
    ''' F5-c: ContractSessionHours.IsContractTradingNow inside / outside CME Globex.
    ''' </summary>
    Public Class SessionCoverageAuditTests

        ' ── F5-a: SessionWindowResolver label coverage ──────────────────────────

        <Theory>
        <InlineData(0, "Asia")>
        <InlineData(1, "Asia")>
        <InlineData(6, "Asia")>
        <InlineData(7, "London")>
        <InlineData(11, "London")>
        <InlineData(12, "US-Pre")>
        <InlineData(13, "US-Pre")>
        <InlineData(14, "US-RTH")>
        <InlineData(20, "US-RTH")>
        <InlineData(21, "US-Post")>
        <InlineData(23, "US-Post")>
        Public Sub Resolve_ReturnsDocumentedLabel(hour As Integer, expected As String)
            Dim utc = New DateTime(2026, 5, 30, hour, 30, 0, DateTimeKind.Utc)
            Assert.Equal(expected, SessionWindowResolver.Resolve(utc))
        End Sub

        <Fact>
        Public Sub Resolve_AllTwentyFourHoursReturnNonEmpty()
            For h = 0 To 23
                Dim utc = New DateTime(2026, 5, 30, h, 0, 0, DateTimeKind.Utc)
                Dim label = SessionWindowResolver.Resolve(utc)
                Assert.False(String.IsNullOrWhiteSpace(label),
                             $"hour {h} produced an empty session label")
            Next
        End Sub

        ' ── F5-b: per-former-host parity ────────────────────────────────────────
        ' The three former host sites (SuperTrendPlusViewModel, EntryExecutionService,
        ' ExitExecutionService) used to maintain independent copies of ResolveSessionWindow.
        ' After F1 they all go through SessionWindowResolver.Resolve. The tests below
        ' assert per-site parity by invoking the resolver in the same way each call
        ' site does — directly with the current UTC instant.

        <Fact>
        Public Sub Parity_AllThreeFormerHostSitesProduceSameLabel()
            Dim now = New DateTime(2026, 5, 30, 15, 0, 0, DateTimeKind.Utc)
            ' Former: SuperTrendPlusViewModel.ResolveSessionWindow(now)
            Dim viaVm = SessionWindowResolver.Resolve(now)
            ' Former: EntryExecutionService.ResolveSessionWindow(DateTime.UtcNow) at entry time
            Dim viaEntry = SessionWindowResolver.Resolve(now)
            ' Former: ExitExecutionService.ResolveSessionWindow(DateTime.UtcNow) at exit time
            Dim viaExit = SessionWindowResolver.Resolve(now)
            Assert.Equal(viaVm, viaEntry)
            Assert.Equal(viaEntry, viaExit)
            Assert.Equal("US-RTH", viaVm)
        End Sub

        ' ── F5-c: ContractSessionHours per-contract gating ──────────────────────

        <Fact>
        Public Sub IsContractTradingNow_ReturnsTrue_DuringMondayLondonSession()
            ' 2026-06-01 (Monday) 09:00 UTC = 04:00 CDT — CME Globex is open.
            Dim utc = New DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)
            Assert.True(ContractSessionHours.IsContractTradingNow("MES", utc),
                        "MES should be open during the London session on a weekday")
        End Sub

        <Fact>
        Public Sub IsContractTradingNow_ReturnsFalse_OnSaturday()
            ' 2026-05-30 is a Saturday — CME closed all day.
            Dim utc = New DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc)
            Assert.False(ContractSessionHours.IsContractTradingNow("MNQ", utc),
                         "MNQ should be closed on Saturday")
        End Sub

        <Fact>
        Public Sub IsContractTradingNow_ReturnsFalse_SundayBeforeCmeReopen()
            ' 2026-05-31 (Sunday) 10:00 UTC = 05:00 CDT — before 17:00 CT reopen.
            Dim utc = New DateTime(2026, 5, 31, 10, 0, 0, DateTimeKind.Utc)
            Assert.False(ContractSessionHours.IsContractTradingNow("MBT", utc),
                         "MBT (and all TopStepX favourites) should be closed Sunday before 17:00 CT")
        End Sub

        <Fact>
        Public Sub IsContractTradingNow_ReturnsTrue_SundayAfterCmeReopen()
            ' 2026-05-31 (Sunday) 23:00 UTC = 18:00 CDT — CME has reopened.
            Dim utc = New DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc)
            Assert.True(ContractSessionHours.IsContractTradingNow("MES", utc),
                        "MES should be open Sunday evening after CME reopens (17:00 CT)")
        End Sub

        <Fact>
        Public Sub IsContractTradingNow_ReturnsFalse_DuringDailyMaintenance()
            ' 2026-06-02 (Tuesday) 21:30 UTC = 16:30 CDT — daily maintenance 16:00-17:00 CT.
            Dim utc = New DateTime(2026, 6, 2, 21, 30, 0, DateTimeKind.Utc)
            Assert.False(ContractSessionHours.IsContractTradingNow("MES", utc),
                         "MES should be closed during CME daily maintenance 16:00-17:00 CT")
        End Sub

        <Fact>
        Public Sub NextOpenUtc_ReturnsSameInstant_WhenContractAlreadyOpen()
            ' Tuesday mid-session.
            Dim utc = New DateTime(2026, 6, 2, 15, 0, 0, DateTimeKind.Utc)
            Dim next_ = ContractSessionHours.NextOpenUtc("MES", utc)
            Assert.Equal(utc, next_)
        End Sub

        <Fact>
        Public Sub NextOpenUtc_AdvancesPastMaintenance()
            ' During 16:00-17:00 CT maintenance — next open is at 17:00 CT.
            Dim utc = New DateTime(2026, 6, 2, 21, 30, 0, DateTimeKind.Utc)
            Dim next_ = ContractSessionHours.NextOpenUtc("MES", utc)
            Assert.True(next_ > utc,
                        "NextOpenUtc during maintenance must advance the returned instant")
            ' Verify it's actually open at the returned instant.
            Assert.True(ContractSessionHours.IsContractTradingNow("MES", next_),
                        "NextOpenUtc must land inside a trading window")
        End Sub

    End Class

End Namespace
