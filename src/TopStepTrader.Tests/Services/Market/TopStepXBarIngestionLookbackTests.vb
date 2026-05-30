Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Services.Market

    ''' <summary>
    ''' BUG-97 regression: pins the per-bar minute weights used by
    ''' <see cref="TopStepXBarIngestionService.GetLiveBarsAsync"/> when computing how
    ''' far back to ask the broker. The helper had no explicit arm for
    ''' <see cref="BarTimeframe.Daily"/>, which made it fall through Case Else (5 min)
    ''' and gave DailyRangeService only ~3 days of lookback — short enough to drop
    ''' Friday's daily bar on Tuesday mornings, especially after a holiday Monday.
    ''' </summary>
    Public Class TopStepXBarIngestionLookbackTests

        <Fact>
        Public Sub Daily_Returns_1440_Minutes()
            Assert.Equal(1440, TopStepXBarIngestionService._strategy_TimeframeMinutesForLiveBar(BarTimeframe.Daily))
        End Sub

        <Theory>
        <InlineData(BarTimeframe.OneMinute, 1)>
        <InlineData(BarTimeframe.ThreeMinute, 3)>
        <InlineData(BarTimeframe.FiveMinute, 5)>
        <InlineData(BarTimeframe.FifteenMinute, 15)>
        <InlineData(BarTimeframe.ThirtyMinute, 30)>
        <InlineData(BarTimeframe.OneHour, 60)>
        Public Sub Intraday_Cases_Unchanged(tf As BarTimeframe, expectedMinutes As Integer)
            Assert.Equal(expectedMinutes, TopStepXBarIngestionService._strategy_TimeframeMinutesForLiveBar(tf))
        End Sub

        <Fact>
        Public Sub Unknown_Falls_Back_To_FiveMinutes()
            ' Documents the Case Else safety net as intentional: any future enum value
            ' that gets added without an explicit arm still produces a sane (if
            ' conservative) per-bar weight rather than zero.
            Assert.Equal(5, TopStepXBarIngestionService._strategy_TimeframeMinutesForLiveBar(CType(9999, BarTimeframe)))
        End Sub

    End Class

End Namespace
