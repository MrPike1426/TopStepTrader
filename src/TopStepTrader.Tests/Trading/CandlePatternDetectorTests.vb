Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' FEAT-62: Strict-definition coverage for the four candle patterns used by
    ''' the Break and Bounce entry chain. Tests include happy-path matches, mirror
    ''' rejections, and degenerate-doji rejection.
    ''' </summary>
    Public Class CandlePatternDetectorTests

        Private Shared Function MakeBar(o As Decimal, h As Decimal, l As Decimal, c As Decimal) As MarketBar
            Return New MarketBar With {.Open = o, .High = h, .Low = l, .Close = c}
        End Function

        ' ── Hammer ───────────────────────────────────────────────────────────

        <Fact>
        Public Sub IsHammer_TrueForLongLowerWickSmallUpperWick()
            ' body = 1, lower wick = 3 (>2), upper wick = 0 (<1) → hammer
            Dim bar = MakeBar(o:=10D, h:=10D, l:=6D, c:=9D)
            Assert.True(CandlePatternDetector.IsHammer(bar))
        End Sub

        <Fact>
        Public Sub IsHammer_FalseForSmallLowerWick()
            ' body = 2, lower wick = 1 (≤ 2× body), upper wick = 0 → not hammer
            Dim bar = MakeBar(o:=10D, h:=12D, l:=9D, c:=12D)
            Assert.False(CandlePatternDetector.IsHammer(bar))
        End Sub

        <Fact>
        Public Sub IsHammer_FalseForLargeUpperWick()
            ' body = 1, lower wick = 3, upper wick = 4 (≥ body) → not hammer
            Dim bar = MakeBar(o:=10D, h:=15D, l:=7D, c:=11D)
            Assert.False(CandlePatternDetector.IsHammer(bar))
        End Sub

        <Fact>
        Public Sub IsHammer_FalseForDoji()
            ' body = 0 → degenerate, never qualifies
            Dim bar = MakeBar(o:=10D, h:=11D, l:=6D, c:=10D)
            Assert.False(CandlePatternDetector.IsHammer(bar))
        End Sub

        ' ── Inverted hammer ──────────────────────────────────────────────────

        <Fact>
        Public Sub IsInvertedHammer_TrueForLongUpperWick()
            ' body = 1, upper wick = 3 (>2× body), lower wick = 0 (<1) → inv-hammer
            Dim bar = MakeBar(o:=10D, h:=14D, l:=10D, c:=11D)
            Assert.True(CandlePatternDetector.IsInvertedHammer(bar))
        End Sub

        <Fact>
        Public Sub IsInvertedHammer_FalseForBigLowerWick()
            ' body = 1, upper wick = 3, lower wick = 4 → fails because lower ≥ body
            Dim bar = MakeBar(o:=10D, h:=14D, l:=6D, c:=11D)
            Assert.False(CandlePatternDetector.IsInvertedHammer(bar))
        End Sub

        ' ── Strict bullish engulfing ─────────────────────────────────────────

        <Fact>
        Public Sub IsBullishEngulfing_TrueForOutsideBarBullish()
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=10.5D)
            Dim curr = MakeBar(o:=8.5D, h:=12.5D, l:=8.5D, c:=12D)
            Assert.True(CandlePatternDetector.IsBullishEngulfing(curr, prev))
        End Sub

        <Fact>
        Public Sub IsBullishEngulfing_FalseWhenOpenNotBelowPrevLow()
            ' open = prev.low → not strict outside bar
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=10.5D)
            Dim curr = MakeBar(o:=9D, h:=12.5D, l:=9D, c:=12D)
            Assert.False(CandlePatternDetector.IsBullishEngulfing(curr, prev))
        End Sub

        <Fact>
        Public Sub IsBullishEngulfing_FalseWhenCloseBelowPrevHigh()
            ' close = prev.high — not strictly above
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=10.5D)
            Dim curr = MakeBar(o:=8.5D, h:=11D, l:=8.5D, c:=11D)
            Assert.False(CandlePatternDetector.IsBullishEngulfing(curr, prev))
        End Sub

        <Fact>
        Public Sub IsBullishEngulfing_FalseWhenCurrentIsBearish()
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=10.5D)
            ' Close < Open even though wider range — strict rule fails.
            Dim curr = MakeBar(o:=12D, h:=12.5D, l:=8.5D, c:=8.6D)
            Assert.False(CandlePatternDetector.IsBullishEngulfing(curr, prev))
        End Sub

        ' ── Strict bearish engulfing ─────────────────────────────────────────

        <Fact>
        Public Sub IsBearishEngulfing_TrueForOutsideBarBearish()
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=9.5D)
            Dim curr = MakeBar(o:=12D, h:=12D, l:=8.5D, c:=8.5D)
            Assert.True(CandlePatternDetector.IsBearishEngulfing(curr, prev))
        End Sub

        <Fact>
        Public Sub IsBearishEngulfing_FalseWhenOpenNotAbovePrevHigh()
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=9.5D)
            Dim curr = MakeBar(o:=11D, h:=11D, l:=8.5D, c:=8.5D)
            Assert.False(CandlePatternDetector.IsBearishEngulfing(curr, prev))
        End Sub

        <Fact>
        Public Sub IsBearishEngulfing_FalseForBullishCurrent()
            Dim prev = MakeBar(o:=10D, h:=11D, l:=9D, c:=9.5D)
            Dim curr = MakeBar(o:=12D, h:=12D, l:=8.5D, c:=12D)
            Assert.False(CandlePatternDetector.IsBearishEngulfing(curr, prev))
        End Sub

    End Class

End Namespace
