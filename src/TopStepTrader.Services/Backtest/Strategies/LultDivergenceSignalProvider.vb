Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' LULT Divergence entry provider (WaveTrend Market Cipher B simulation).
    ''' Mirrors the LultDivergence block in BacktestEngine (lines 1169–1273).
    '''
    ''' 6-step gate for both bull and bear setups:
    '''   1. Anchor extreme: WT1 breaches ±60 (strong momentum exhaustion)
    '''   2. Trigger extreme: shallower than anchor (weakening follow-through)
    '''   3. Price divergence: trigger low &lt; anchor low (bull) or high &gt; anchor high (bear)
    '''   4. Dot signal: WT1 crosses WT2 after trigger
    '''   5. Engulfing candle at current bar (within 6 bars of dot)
    '''
    ''' Time filter: bar UTC hour must be 11–16 (inclusive).
    ''' Stop = |close − trigger extreme|; TP = 2 × stop (fixed 2:1 R:R).
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   WaveTrend1 / WaveTrend2 = WaveTrend(10, 21, 4)
    '''   AllBars = full bar list (for OHLCV at arbitrary indices)
    ''' </summary>
    Public Class LultDivergenceSignalProvider
        Implements IStrategySignalProvider

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            If indicators.WaveTrend1 Is Nothing OrElse indicators.WaveTrend2 Is Nothing OrElse
               indicators.AllBars Is Nothing Then Return Nothing

            ' Require at least 100 bars for the divergence scan lookback
            If barIndex < 100 Then Return Nothing

            ' Time filter: 11–16 UTC (inclusive)
            Dim barUtcHour = bar.Timestamp.UtcDateTime.Hour
            If Not (barUtcHour >= 11 AndAlso barUtcHour < 17) Then Return Nothing

            Dim lultSide As String = Nothing
            Dim triggerExtreme As Decimal = 0D

            For Each isBull In {True, False}
                If lultSide IsNot Nothing Then Exit For

                ' Build list of local WT1 extremes in the scan window
                Dim searchFrom = Math.Max(1, barIndex - 2 - 80)
                Dim extremes As New List(Of (Idx As Integer, Wt1Val As Single, PriceEx As Decimal))
                For ei = searchFrom To barIndex - 2
                    If Single.IsNaN(indicators.WaveTrend1(ei)) OrElse
                       Single.IsNaN(indicators.WaveTrend1(ei - 1)) OrElse
                       Single.IsNaN(indicators.WaveTrend1(ei + 1)) Then Continue For
                    If isBull Then
                        ' Bull: local minimum of WT1
                        If indicators.WaveTrend1(ei) <= indicators.WaveTrend1(ei - 1) AndAlso
                           indicators.WaveTrend1(ei) <= indicators.WaveTrend1(ei + 1) AndAlso
                           (indicators.WaveTrend1(ei) < indicators.WaveTrend1(ei - 1) OrElse
                            indicators.WaveTrend1(ei) < indicators.WaveTrend1(ei + 1)) Then
                            extremes.Add((ei, indicators.WaveTrend1(ei), indicators.AllBars(ei).Low))
                        End If
                    Else
                        ' Bear: local maximum of WT1
                        If indicators.WaveTrend1(ei) >= indicators.WaveTrend1(ei - 1) AndAlso
                           indicators.WaveTrend1(ei) >= indicators.WaveTrend1(ei + 1) AndAlso
                           (indicators.WaveTrend1(ei) > indicators.WaveTrend1(ei - 1) OrElse
                            indicators.WaveTrend1(ei) > indicators.WaveTrend1(ei + 1)) Then
                            extremes.Add((ei, indicators.WaveTrend1(ei), indicators.AllBars(ei).High))
                        End If
                    End If
                Next

                If extremes.Count < 2 Then Continue For

                For ti = extremes.Count - 1 To 1 Step -1
                    Dim trigger = extremes(ti)
                    If barIndex - trigger.Idx < 2 Then Continue For

                    For anchorI = ti - 1 To 0 Step -1
                        Dim anchor = extremes(anchorI)

                        ' Step 1-2: anchor must breach ±60
                        Dim anchorBreached = If(isBull, anchor.Wt1Val < -60.0F, anchor.Wt1Val > 60.0F)
                        If Not anchorBreached Then Continue For

                        ' Step 3: trigger shallower than anchor
                        Dim trigShallower = If(isBull,
                                               trigger.Wt1Val > anchor.Wt1Val,
                                               trigger.Wt1Val < anchor.Wt1Val)
                        If Not trigShallower Then Continue For

                        ' Step 4: price divergence
                        Dim hasDiverg = If(isBull,
                                           trigger.PriceEx < anchor.PriceEx,
                                           trigger.PriceEx > anchor.PriceEx)
                        If Not hasDiverg Then Continue For

                        ' Step 5: dot signal — WT1 crosses WT2 after trigger
                        Dim dotIdx = -1
                        Dim dotEnd = Math.Min(barIndex - 1, trigger.Idx + 15)
                        For di = trigger.Idx + 1 To dotEnd
                            If Single.IsNaN(indicators.WaveTrend1(di)) OrElse
                               Single.IsNaN(indicators.WaveTrend2(di)) OrElse
                               Single.IsNaN(indicators.WaveTrend1(di - 1)) OrElse
                               Single.IsNaN(indicators.WaveTrend2(di - 1)) Then Continue For
                            If isBull Then
                                If indicators.WaveTrend1(di - 1) < indicators.WaveTrend2(di - 1) AndAlso
                                   indicators.WaveTrend1(di) >= indicators.WaveTrend2(di) Then
                                    dotIdx = di : Exit For
                                End If
                            Else
                                If indicators.WaveTrend1(di - 1) > indicators.WaveTrend2(di - 1) AndAlso
                                   indicators.WaveTrend1(di) <= indicators.WaveTrend2(di) Then
                                    dotIdx = di : Exit For
                                End If
                            End If
                        Next
                        If dotIdx < 0 Then Continue For

                        ' Step 6: current bar is an engulfing candle within 6 bars of the dot
                        If barIndex <= dotIdx OrElse barIndex > dotIdx + 6 Then Continue For

                        Dim curO = indicators.AllBars(barIndex).Open
                        Dim curC = bar.Close
                        Dim prvO = indicators.AllBars(barIndex - 1).Open
                        Dim prvC = indicators.AllBars(barIndex - 1).Close
                        Dim prvBodyLo = Math.Min(prvO, prvC)
                        Dim prvBodyHi = Math.Max(prvO, prvC)
                        Dim bodySize  = Math.Abs(curC - curO)
                        If bodySize = 0D Then Continue For

                        Dim engulfOk As Boolean = False
                        If isBull Then
                            If curC > curO AndAlso curO <= prvBodyLo AndAlso curC >= prvBodyHi Then
                                Dim lWick = curO - indicators.AllBars(barIndex).Low
                                engulfOk = (CDbl(lWick) / CDbl(bodySize) <= 0.4)
                            End If
                        Else
                            If curC < curO AndAlso curO >= prvBodyHi AndAlso curC <= prvBodyLo Then
                                Dim uWick = indicators.AllBars(barIndex).High - curO
                                engulfOk = (CDbl(uWick) / CDbl(bodySize) <= 0.4)
                            End If
                        End If
                        If Not engulfOk Then Continue For

                        ' All 6 steps confirmed
                        lultSide = If(isBull, "Buy", "Sell")
                        triggerExtreme = trigger.PriceEx
                        Exit For
                    Next ' anchorI
                    If lultSide IsNot Nothing Then Exit For
                Next ' ti
            Next ' isBull

            If lultSide Is Nothing Then Return Nothing

            ' SL distance from close to trigger extreme; TP = 2R
            Dim lultSlDist = Math.Abs(bar.Close - triggerExtreme)
            If lultSlDist = 0D Then lultSlDist = 1D

            Return New SignalResult With {
                .Side       = lultSide,
                .Confidence = 1.0F,
                .IsLong     = (lultSide = "Buy"),
                .StopDelta  = lultSlDist,
                .TpDelta    = lultSlDist * 2D
            }
        End Function

    End Class

End Namespace
