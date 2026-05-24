Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.AI
Imports Xunit

Namespace TopStepTrader.Tests.Services.AI

    ''' <summary>UAT-03 F1: asserts the Pre-trade check now receives the same bar window
    ''' as the Mid-trade check, so the two prompts can no longer disagree on the same data
    ''' (Q1 resolution: "upgrade Pre-trade to bar-history + price-action review").</summary>
    Public Class PreTradeCheckBarHistoryTests

        ' ──────────────────────────────────────────────────────────────────────
        '  Bar series synthesized from the 2026-05-18 MBT 10:15:27 timeline
        '  (Concern A in UAT-03). Real bar capture was OFF for that session;
        '  we replay the human-described pattern: ADX-strong SHORT signal fires
        '  with price ~76900 after 4 prior bars that printed higher closes off
        '  a session low — exactly the conditions the mid-trade check flagged
        '  RED 6 minutes later.
        ' ──────────────────────────────────────────────────────────────────────
        Private Shared Function MakeMbt20260518Bars() As IReadOnlyList(Of MarketBar)
            Dim t0 = New DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc)
            Dim bars As New List(Of MarketBar)
            Dim opens() As Single = {76840.0F, 76860.0F, 76870.0F, 76885.0F, 76900.0F}
            Dim highs() As Single = {76870.0F, 76900.0F, 76920.0F, 76925.0F, 76940.0F}
            Dim lows() As Single = {76820.0F, 76850.0F, 76865.0F, 76880.0F, 76890.0F}
            Dim closes() As Single = {76860.0F, 76875.0F, 76890.0F, 76905.0F, 76915.0F}
            For i = 0 To opens.Length - 1
                bars.Add(New MarketBar With {
                    .Timestamp = t0.AddMinutes(i * 3),
                    .Open = opens(i),
                    .High = highs(i),
                    .Low = lows(i),
                    .Close = closes(i),
                    .Volume = 200
                })
            Next
            Return bars
        End Function

        Private Shared Function MakeMbtContext(bars As IReadOnlyList(Of MarketBar)) As PreTradeContext
            Return New PreTradeContext With {
                .ContractId = "MBT",
                .ContractDescription = "Micro Bitcoin",
                .Side = "SELL",
                .Price = 76915D,
                .AdxValue = 50.7F,
                .AdxThreshold = 30.0F,
                .ConfidencePct = 80,
                .MinConfidencePct = 70,
                .TimeframeMinutes = 15,
                .StrategyName = "SuperTrend+ Autopilot",
                .UtcNow = New DateTimeOffset(2026, 5, 18, 9, 15, 27, TimeSpan.Zero),
                .SessionPnlUsd = -9D,
                .SessionTradeCount = 1,
                .ExitStrategyDescription = "SuperTrend+ flip-only exit; SL at SuperTrend line.",
                .RecentBars = bars
            }
        End Function

        <Fact>
        Public Sub PreTradeMessage_WithBars_IncludesBarTable()
            Dim bars = MakeMbt20260518Bars()
            Dim ctx = MakeMbtContext(bars)

            Dim msg = ClaudeReviewService.BuildPreTradeUserMessage(ctx)

            ' The header is the contract; the bar table proves price-action review is in scope.
            Assert.Contains("BAR HISTORY", msg)
            Assert.Contains("Time (UTC)", msg)
            ' Confirm the entry-bar close appears in the table.
            Assert.Contains("76915.0000", msg)
            ' Confirm the macro / session block survives the upgrade.
            Assert.Contains("SESSION PERFORMANCE", msg)
        End Sub

        <Fact>
        Public Sub PreTradeMessage_WithoutBars_OmitsBarTableAndRemainsBackwardsCompatible()
            ' RecentBars unset (or empty) must still produce a valid macro-only prompt so
            ' deployments where bar capture is off do not break the entry path.
            Dim ctx = MakeMbtContext(bars:=Nothing)

            Dim msg = ClaudeReviewService.BuildPreTradeUserMessage(ctx)

            Assert.DoesNotContain("BAR HISTORY", msg)
            Assert.Contains("SESSION PERFORMANCE", msg)
            Assert.Contains("PROCEED or be VETOED", msg)
        End Sub

        <Fact>
        Public Sub PreTradeAndMidTrade_SameBars_EmitIdenticalBarTable()
            ' Q1 reconciliation, expressed as a regression test: both prompts must format
            ' the same bar window identically. If a future change drifts one formatter,
            ' the two checks can disagree on the same data again — this test catches that.
            Dim bars = MakeMbt20260518Bars()

            Dim preMsg = ClaudeReviewService.BuildPreTradeUserMessage(MakeMbtContext(bars))
            Dim midMsg = ClaudeReviewService.BuildMidTradeMessage(
                "MBT", "SELL", adxVal:=50.7F, plusDi:=12.0F, minusDi:=24.0F,
                stopPhaseLabel:="Initial", unrealizedPnl:=0D, bars:=bars)

            Dim preTable = ExtractBarTable(preMsg)
            Dim midTable = ExtractBarTable(midMsg)

            Assert.False(String.IsNullOrEmpty(preTable), "Pre-trade prompt should contain a bar table when bars are supplied")
            Assert.False(String.IsNullOrEmpty(midTable), "Mid-trade prompt should always contain a bar table")
            Assert.Equal(midTable, preTable)
        End Sub

        Private Shared Function ExtractBarTable(prompt As String) As String
            Const header = "BAR HISTORY"
            Dim start = prompt.IndexOf(header, StringComparison.Ordinal)
            If start < 0 Then Return String.Empty
            ' Both formatters terminate the table with a blank line. Tolerate \r\n or \n.
            Dim tail = prompt.Substring(start)
            Dim normalised = tail.Replace(vbCrLf, vbLf)
            Dim blank = normalised.IndexOf(vbLf & vbLf, StringComparison.Ordinal)
            If blank > 0 Then Return normalised.Substring(0, blank)
            Return normalised
        End Function

    End Class

End Namespace
