Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' FEAT-72: strategy-agnostic 0-100 opportunity score per instrument.
    '''
    ''' Components (weighted sum):
    '''   • Volatility (0.40) — current ATR as % of last close, normalised against a
    '''     reasonable upper bound (3% daily move for futures = "saturated").
    '''   • Trend strength (0.30) — last ADX value, normalised against 50 (anything
    '''     above 50 plateaus). Persona's <c>AdxMin</c> acts as a soft floor —
    '''     scores below the floor are halved.
    '''   • Range expansion (0.20) — current ATR / 20-bar ATR average; values above
    '''     1.0 favoured, capped at 2.0.
    '''   • Liquidity (0.10) — 5-bar volume / 20-bar average volume; favoured above 1.0.
    '''
    ''' Returns NaN when insufficient bars to compute (caller should treat as 0 / skip).
    ''' </summary>
    Public Class InstrumentOpportunityScorer

        Public Const VolatilityWeight As Single = 0.4F
        Public Const TrendWeight As Single = 0.3F
        Public Const RangeWeight As Single = 0.2F
        Public Const LiquidityWeight As Single = 0.1F

        ''' <summary>Saturation point for the volatility component (3% daily move).</summary>
        Private Const VolatilityCapPct As Double = 3.0
        Private Const TrendCap As Double = 50.0
        Private Const RangeExpansionCap As Double = 2.0
        Private Const LiquidityCap As Double = 2.0

        ''' <summary>
        ''' Compute the 0-100 opportunity score for <paramref name="contract"/> given a
        ''' chronologically-ordered list of recent bars (oldest first). Pass <paramref name="indicatorLength"/>
        ''' = 14 for the standard Wilder window; <paramref name="personaMinAdx"/> is used as a soft
        ''' floor (scores below halve the trend component).
        ''' </summary>
        Public Function Score(contract As FavouriteContract,
                              bars As IList(Of MarketBar),
                              Optional personaMinAdx As Single = 20.0F,
                              Optional indicatorLength As Integer = 14) As Single
            If bars Is Nothing OrElse bars.Count < Math.Max(indicatorLength * 2 + 1, 21) Then
                Return Single.NaN
            End If

            Dim n = bars.Count
            Dim highs(n - 1) As Double
            Dim lows(n - 1) As Double
            Dim closes(n - 1) As Double
            Dim volumes(n - 1) As Double
            For i = 0 To n - 1
                highs(i) = CDbl(bars(i).High)
                lows(i) = CDbl(bars(i).Low)
                closes(i) = CDbl(bars(i).Close)
                volumes(i) = CDbl(bars(i).Volume)
            Next

            Dim atrSeries = ComputeWilderAtr(highs, lows, closes, indicatorLength)
            Dim adxSeries = ComputeWilderAdx(highs, lows, closes, indicatorLength)

            Dim atrLast As Double = atrSeries(n - 1)
            Dim closeLast As Double = closes(n - 1)
            If atrLast <= 0 OrElse closeLast <= 0 Then Return 0F

            ' ── Volatility: ATR/price as percent, capped at VolatilityCapPct ──────
            Dim atrPct As Double = (atrLast / closeLast) * 100.0
            Dim volScore As Double = Math.Min(atrPct / VolatilityCapPct, 1.0)

            ' ── Trend: last ADX normalised against TrendCap ───────────────────────
            Dim adxLast As Double = adxSeries(n - 1)
            Dim trendScore As Double = 0
            If Not Double.IsNaN(adxLast) Then
                trendScore = Math.Min(adxLast / TrendCap, 1.0)
                If adxLast < personaMinAdx Then trendScore *= 0.5
            End If

            ' ── Range expansion: current ATR vs 20-bar ATR average ────────────────
            Dim rangeScore As Double = 0
            Dim atrStart = Math.Max(0, n - 20)
            Dim atrCount As Integer = 0
            Dim atrSum As Double = 0
            For i = atrStart To n - 1
                If atrSeries(i) > 0 Then
                    atrSum += atrSeries(i)
                    atrCount += 1
                End If
            Next
            If atrCount > 0 Then
                Dim atrAvg = atrSum / atrCount
                If atrAvg > 0 Then
                    Dim ratio = atrLast / atrAvg
                    rangeScore = Math.Min(ratio / RangeExpansionCap, 1.0)
                End If
            End If

            ' ── Liquidity: 5-bar volume vs 20-bar average volume ──────────────────
            Dim liqScore As Double = 0
            If n >= 20 Then
                Dim recentSum As Double = 0
                For i = n - 5 To n - 1
                    recentSum += volumes(i)
                Next
                Dim recentAvg = recentSum / 5.0
                Dim windowSum As Double = 0
                For i = n - 20 To n - 1
                    windowSum += volumes(i)
                Next
                Dim windowAvg = windowSum / 20.0
                If windowAvg > 0 Then
                    Dim ratio = recentAvg / windowAvg
                    liqScore = Math.Min(ratio / LiquidityCap, 1.0)
                End If
            End If

            Dim raw = (volScore * VolatilityWeight) +
                      (trendScore * TrendWeight) +
                      (rangeScore * RangeWeight) +
                      (liqScore * LiquidityWeight)
            Return CSng(Math.Max(0.0, Math.Min(1.0, raw)) * 100.0)
        End Function

        ' ── Wilder ATR / ADX (duplicated from SlipStreamSignalDetector to keep the
        '    scorer dependency-free; see that file for the canonical implementation). ──

        Private Shared Function ComputeWilderAtr(highs As Double(), lows As Double(), closes As Double(), length As Integer) As Double()
            Dim n = closes.Length
            Dim atr(n - 1) As Double
            If n <= length Then Return atr
            Dim trSum As Double = 0
            For i = 1 To length
                trSum += TrueRange(highs(i), lows(i), closes(i - 1))
            Next
            atr(length) = trSum / length
            For i = length + 1 To n - 1
                Dim tr = TrueRange(highs(i), lows(i), closes(i - 1))
                atr(i) = ((atr(i - 1) * (length - 1)) + tr) / length
            Next
            Return atr
        End Function

        Private Shared Function ComputeWilderAdx(highs As Double(), lows As Double(), closes As Double(), length As Integer) As Double()
            Dim n = closes.Length
            Dim adx(n - 1) As Double
            For i = 0 To n - 1
                adx(i) = Double.NaN
            Next
            If n < length * 2 + 1 Then Return adx

            Dim plusDm(n - 1) As Double
            Dim minusDm(n - 1) As Double
            Dim tr(n - 1) As Double
            For i = 1 To n - 1
                Dim upMove = highs(i) - highs(i - 1)
                Dim downMove = lows(i - 1) - lows(i)
                plusDm(i) = If(upMove > downMove AndAlso upMove > 0, upMove, 0.0)
                minusDm(i) = If(downMove > upMove AndAlso downMove > 0, downMove, 0.0)
                tr(i) = TrueRange(highs(i), lows(i), closes(i - 1))
            Next

            Dim sPlus As Double = 0, sMinus As Double = 0, sTr As Double = 0
            For i = 1 To length
                sPlus += plusDm(i)
                sMinus += minusDm(i)
                sTr += tr(i)
            Next
            Dim dxSum As Double = 0
            Dim adxStartIdx = length * 2
            For i = length To n - 1
                If i > length Then
                    sPlus = sPlus - (sPlus / length) + plusDm(i)
                    sMinus = sMinus - (sMinus / length) + minusDm(i)
                    sTr = sTr - (sTr / length) + tr(i)
                End If
                Dim diPlus As Double = If(sTr > 0, 100.0 * sPlus / sTr, 0.0)
                Dim diMinus As Double = If(sTr > 0, 100.0 * sMinus / sTr, 0.0)
                Dim diSum = diPlus + diMinus
                Dim dx As Double = If(diSum > 0, 100.0 * Math.Abs(diPlus - diMinus) / diSum, 0.0)
                If i >= length AndAlso i < adxStartIdx Then
                    dxSum += dx
                    If i = adxStartIdx - 1 Then adx(i) = dxSum / length
                ElseIf i >= adxStartIdx Then
                    adx(i) = ((adx(i - 1) * (length - 1)) + dx) / length
                End If
            Next
            Return adx
        End Function

        Private Shared Function TrueRange(high As Double, low As Double, prevClose As Double) As Double
            Dim a = high - low
            Dim b = Math.Abs(high - prevClose)
            Dim c = Math.Abs(low - prevClose)
            Return Math.Max(a, Math.Max(b, c))
        End Function

    End Class

End Namespace
