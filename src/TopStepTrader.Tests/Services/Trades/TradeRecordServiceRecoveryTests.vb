Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Services.Trades
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trades

    ''' <summary>
    ''' BUG-86 F1: unit tests for the dynamic crash-recovery lookback computation.
    ''' Friend Shared helper isolates the windowing logic from the PX REST + EF Core
    ''' code paths so we can drive every age scenario without a DB or a broker mock.
    ''' </summary>
    Public Class TradeRecordServiceRecoveryTests

        Private Shared Function Rec(id As Long, entry As DateTimeOffset) As LiveTradeRecordEntity
            Return New LiveTradeRecordEntity With {
                .Id = id,
                .Symbol = "MNQ",
                .ContractId = "CON.F.US.MNQ.U26",
                .Direction = "Long",
                .EntryTime = entry,
                .EntryPrice = 21000D,
                .Sizes = 1,
                .IsOpen = True
            }
        End Function

        <Fact>
        Public Sub ComputeLookback_EmptyInput_ReturnsZero()
            Dim now = New DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
            Dim skipped As List(Of LiveTradeRecordEntity) = Nothing
            Dim result = TradeRecordService.ComputeLookbackSinceMs(
                New List(Of LiveTradeRecordEntity)(), now, skipped)
            Assert.Equal(0L, result.sinceMs)
            Assert.Empty(result.eligible)
            Assert.Empty(skipped)
        End Sub

        <Fact>
        Public Sub ComputeLookback_AllRecent_WindowMatchesOldestMinusBuffer()
            Dim now = New DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
            Dim records = New List(Of LiveTradeRecordEntity) From {
                Rec(1, now.AddHours(-3)),
                Rec(2, now.AddHours(-26)),
                Rec(3, now.AddHours(-72))
            }
            Dim skipped As List(Of LiveTradeRecordEntity) = Nothing
            Dim result = TradeRecordService.ComputeLookbackSinceMs(records, now, skipped)
            Assert.Equal(3, result.eligible.Count)
            Assert.Empty(skipped)
            ' Oldest is rec 3 (now - 72h); since = entry - 5 minutes
            Dim expected = now.AddHours(-72).AddMinutes(-5).ToUnixTimeMilliseconds()
            Assert.Equal(expected, result.sinceMs)
        End Sub

        <Fact>
        Public Sub ComputeLookback_RecordOlderThanCap_SkippedNotIncludedInWindow()
            Dim now = New DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
            Dim records = New List(Of LiveTradeRecordEntity) From {
                Rec(1, now.AddDays(-2)),
                Rec(2, now.AddDays(-45))  ' Beyond the 30-day cap.
            }
            Dim skipped As List(Of LiveTradeRecordEntity) = Nothing
            Dim result = TradeRecordService.ComputeLookbackSinceMs(records, now, skipped)
            Assert.Single(result.eligible)
            Assert.Equal(1L, result.eligible(0).Id)
            Assert.Single(skipped)
            Assert.Equal(2L, skipped(0).Id)
            ' Window pivots on the only eligible record (2d), not the 45d outlier.
            Dim expected = now.AddDays(-2).AddMinutes(-5).ToUnixTimeMilliseconds()
            Assert.Equal(expected, result.sinceMs)
        End Sub

        <Fact>
        Public Sub ComputeLookback_AllOlderThanCap_SinceIsZeroAndEligibleEmpty()
            Dim now = New DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
            Dim records = New List(Of LiveTradeRecordEntity) From {
                Rec(1, now.AddDays(-31)),
                Rec(2, now.AddDays(-90))
            }
            Dim skipped As List(Of LiveTradeRecordEntity) = Nothing
            Dim result = TradeRecordService.ComputeLookbackSinceMs(records, now, skipped)
            Assert.Equal(0L, result.sinceMs)
            Assert.Empty(result.eligible)
            Assert.Equal(2, skipped.Count)
        End Sub

        <Fact>
        Public Sub ComputeLookback_DoesNotMutateInputOrder()
            Dim now = New DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
            Dim records = New List(Of LiveTradeRecordEntity) From {
                Rec(1, now.AddHours(-1)),
                Rec(2, now.AddHours(-100))
            }
            Dim originalOrder = records.Select(Function(r) r.Id).ToList()
            Dim skipped As List(Of LiveTradeRecordEntity) = Nothing
            TradeRecordService.ComputeLookbackSinceMs(records, now, skipped)
            Assert.Equal(originalOrder, records.Select(Function(r) r.Id).ToList())
        End Sub

    End Class

End Namespace
