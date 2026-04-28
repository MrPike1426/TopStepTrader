Namespace TopStepTrader.ML.Features

    ''' <summary>
    ''' Pure-math technical indicator calculations.
    ''' All methods take a price series and return a Single array.
    ''' No external dependencies — fully unit-testable.
    ''' </summary>
    Public Module TechnicalIndicators

        ' ── EMA ──────────────────────────────────────────────────────────────

        ''' <summary>Exponential Moving Average. Returns array same length as prices (NaN-padded start).</summary>
        Public Function EMA(prices As IList(Of Decimal), period As Integer) As Single()
            Dim result(prices.Count - 1) As Single
            If prices.Count = 0 OrElse period <= 0 Then Return result

            Dim k = 2.0F / (period + 1.0F)
            Dim firstValid = period - 1

            ' Seed with SMA of first 'period' values
            Dim sum As Double = 0
            For i = 0 To period - 1
                If i >= prices.Count Then Return result
                sum += CDbl(prices(i))
                result(i) = Single.NaN
            Next
            result(firstValid) = CSng(sum / period)

            For i = period To prices.Count - 1
                result(i) = CSng(CDbl(prices(i)) * k + CDbl(result(i - 1)) * (1 - k))
            Next
            Return result
        End Function

        ' ── SMA ──────────────────────────────────────────────────────────────

        Public Function SMA(prices As IList(Of Decimal), period As Integer) As Single()
            Dim result(prices.Count - 1) As Single
            For i = 0 To prices.Count - 1
                result(i) = Single.NaN
            Next
            For i = period - 1 To prices.Count - 1
                Dim sum As Double = 0
                For j = i - period + 1 To i
                    sum += CDbl(prices(j))
                Next
                result(i) = CSng(sum / period)
            Next
            Return result
        End Function

        ' ── RSI ──────────────────────────────────────────────────────────────

        ''' <summary>Relative Strength Index (Wilder smoothing).</summary>
        Public Function RSI(closes As IList(Of Decimal), period As Integer) As Single()
            Dim result(closes.Count - 1) As Single
            For i = 0 To closes.Count - 1
                result(i) = Single.NaN
            Next
            If closes.Count < period + 1 Then Return result

            Dim gains As Double = 0
            Dim losses As Double = 0

            For i = 1 To period
                Dim change = CDbl(closes(i)) - CDbl(closes(i - 1))
                If change >= 0 Then gains += change Else losses -= change
            Next
            Dim avgGain = gains / period
            Dim avgLoss = losses / period

            result(period) = If(avgLoss = 0, 100.0F,
                                CSng(100.0 - 100.0 / (1.0 + avgGain / avgLoss)))

            For i = period + 1 To closes.Count - 1
                Dim change = CDbl(closes(i)) - CDbl(closes(i - 1))
                Dim gain = If(change > 0, change, 0.0)
                Dim loss = If(change < 0, -change, 0.0)
                avgGain = (avgGain * (period - 1) + gain) / period
                avgLoss = (avgLoss * (period - 1) + loss) / period
                result(i) = If(avgLoss = 0, 100.0F,
                               CSng(100.0 - 100.0 / (1.0 + avgGain / avgLoss)))
            Next
            Return result
        End Function

        ' ── MACD ─────────────────────────────────────────────────────────────

        ''' <summary>Returns (macdLine, signalLine, histogram) arrays.</summary>
        Public Function MACD(closes As IList(Of Decimal),
                              Optional fastPeriod As Integer = 12,
                              Optional slowPeriod As Integer = 26,
                              Optional signalPeriod As Integer = 9) As (Line As Single(), Signal As Single(), Histogram As Single())

            Dim fastEma = EMA(closes, fastPeriod)
            Dim slowEma = EMA(closes, slowPeriod)

            Dim macdLine(closes.Count - 1) As Single
            For i = 0 To closes.Count - 1
                If Single.IsNaN(fastEma(i)) OrElse Single.IsNaN(slowEma(i)) Then
                    macdLine(i) = Single.NaN
                Else
                    macdLine(i) = fastEma(i) - slowEma(i)
                End If
            Next

            ' Signal = EMA of MACD line
            Dim validMacd = macdLine.Where(Function(v) Not Single.IsNaN(v) AndAlso Not Single.IsInfinity(v)).Select(Function(v) CDec(v)).ToList()
            Dim signalRaw = EMA(validMacd, signalPeriod)

            Dim signalLine(closes.Count - 1) As Single
            Dim histogram(closes.Count - 1) As Single
            Dim validIdx = 0
            For i = 0 To closes.Count - 1
                If Not Single.IsNaN(macdLine(i)) Then
                    If validIdx < signalRaw.Length Then
                        signalLine(i) = signalRaw(validIdx)
                        histogram(i) = If(Single.IsNaN(signalRaw(validIdx)), Single.NaN,
                                          macdLine(i) - signalRaw(validIdx))
                        validIdx += 1
                    End If
                Else
                    signalLine(i) = Single.NaN
                    histogram(i) = Single.NaN
                End If
            Next
            Return (macdLine, signalLine, histogram)
        End Function

        ' ── ATR ──────────────────────────────────────────────────────────────

        ''' <summary>Average True Range (Wilder smoothing).</summary>
        Public Function ATR(highs As IList(Of Decimal),
                             lows As IList(Of Decimal),
                             closes As IList(Of Decimal),
                             period As Integer) As Single()

            Dim n = Math.Min(Math.Min(highs.Count, lows.Count), closes.Count)
            Dim result(n - 1) As Single
            For i = 0 To n - 1
                result(i) = Single.NaN
            Next
            If n < 2 Then Return result
            If n < period Then Return result

            ' True ranges
            Dim trs(n - 1) As Double
            trs(0) = CDbl(highs(0) - lows(0))
            For i = 1 To n - 1
                Dim hl = CDbl(highs(i) - lows(i))
                Dim hc = Math.Abs(CDbl(highs(i)) - CDbl(closes(i - 1)))
                Dim lc = Math.Abs(CDbl(lows(i)) - CDbl(closes(i - 1)))
                trs(i) = Math.Max(hl, Math.Max(hc, lc))
            Next

            ' Seed ATR with SMA of first 'period' TRs
            Dim sum As Double = 0
            For i = 0 To period - 1
                sum += trs(i)
            Next
            result(period - 1) = CSng(sum / period)

            For i = period To n - 1
                result(i) = CSng((CDbl(result(i - 1)) * (period - 1) + trs(i)) / period)
            Next
            Return result
        End Function

        ' ── VWAP ─────────────────────────────────────────────────────────────

        ''' <summary>Volume-Weighted Average Price (cumulative for the series).</summary>
        Public Function VWAP(highs As IList(Of Decimal),
                              lows As IList(Of Decimal),
                              closes As IList(Of Decimal),
                              volumes As IList(Of Long)) As Single()
            Dim n = Math.Min(Math.Min(highs.Count, lows.Count),
                             Math.Min(closes.Count, volumes.Count))
            Dim result(n - 1) As Single
            Dim cumPV As Double = 0
            Dim cumVol As Double = 0
            For i = 0 To n - 1
                Dim typicalPrice = (CDbl(highs(i)) + CDbl(lows(i)) + CDbl(closes(i))) / 3.0
                cumPV += typicalPrice * volumes(i)
                cumVol += volumes(i)
                result(i) = If(cumVol > 0, CSng(cumPV / cumVol), CSng(closes(i)))
            Next
            Return result
        End Function

        ' ── Bollinger Bands ───────────────────────────────────────────────────

        ''' <summary>Returns (upperBand, middleBand, lowerBand) arrays.</summary>
        Public Function BollingerBands(closes As IList(Of Decimal),
                                       period As Integer,
                                       Optional stdDevMultiplier As Double = 2.0) As (Upper As Single(), Middle As Single(), Lower As Single())
            Dim middle = SMA(closes, period)
            Dim upper(closes.Count - 1) As Single
            Dim lower(closes.Count - 1) As Single

            For i = 0 To closes.Count - 1
                upper(i) = Single.NaN
                lower(i) = Single.NaN
            Next

            For i = period - 1 To closes.Count - 1
                If Single.IsNaN(middle(i)) Then Continue For
                Dim variance As Double = 0
                For j = i - period + 1 To i
                    Dim diff = CDbl(closes(j)) - CDbl(middle(i))
                    variance += diff * diff
                Next
                Dim stdDev = Math.Sqrt(variance / period)
                upper(i) = CSng(CDbl(middle(i)) + stdDevMultiplier * stdDev)
                lower(i) = CSng(CDbl(middle(i)) - stdDevMultiplier * stdDev)
            Next
            Return (upper, middle, lower)
        End Function

        ''' <summary>
        ''' Bollinger Band Width — (Upper − Lower) / Middle × 100.
        ''' Returns NaN for bars before the warm-up period.
        ''' A contracting BBW signals a squeeze (low volatility coiling).
        ''' </summary>
        Public Function BollingerBandWidth(closes As IList(Of Decimal),
                                           period As Integer,
                                           Optional stdDevMultiplier As Double = 2.0) As Single()
            Dim bands = BollingerBands(closes, period, stdDevMultiplier)
            Dim n = closes.Count
            Dim result(n - 1) As Single
            For i = 0 To n - 1
                If Single.IsNaN(bands.Upper(i)) OrElse Single.IsNaN(bands.Middle(i)) OrElse
                   bands.Middle(i) = 0.0F Then
                    result(i) = Single.NaN
                Else
                    result(i) = CSng((CDbl(bands.Upper(i)) - CDbl(bands.Lower(i))) /
                                     CDbl(bands.Middle(i)) * 100.0)
                End If
            Next
            Return result
        End Function

        ''' <summary>
        ''' Bollinger %B — where price sits relative to the bands.
        ''' 0 = at lower band, 1 = at upper band, &lt;0 = below lower band, &gt;1 = above upper band.
        ''' Returns NaN for bars before the warm-up period or when band width is zero.
        ''' </summary>
        Public Function BollingerPercentB(closes As IList(Of Decimal),
                                          period As Integer,
                                          Optional stdDevMultiplier As Double = 2.0) As Single()
            Dim bands = BollingerBands(closes, period, stdDevMultiplier)
            Dim n = closes.Count
            Dim result(n - 1) As Single
            For i = 0 To n - 1
                Dim bw = CDbl(bands.Upper(i)) - CDbl(bands.Lower(i))
                If Single.IsNaN(bands.Upper(i)) OrElse bw = 0.0 Then
                    result(i) = Single.NaN
                Else
                    result(i) = CSng((CDbl(closes(i)) - CDbl(bands.Lower(i))) / bw)
                End If
            Next
            Return result
        End Function

        ' ── Helpers ──────────────────────────────────────────────────────────

        ' ── DMI / ADX ────────────────────────────────────────────────────────────

        ''' <summary>
        ''' Directional Movement Index — returns (+DI, -DI, ADX) arrays using
        ''' Wilder smoothing (identical to the ATR and RSI smoothing convention).
        ''' First valid index is 2×period−1 (warm-up matches TradingView default).
        ''' </summary>
        Public Function DMI(highs As IList(Of Decimal),
                            lows As IList(Of Decimal),
                            closes As IList(Of Decimal),
                            Optional period As Integer = 14) As (PlusDI As Single(), MinusDI As Single(), ADX As Single())

            Dim n = Math.Min(Math.Min(highs.Count, lows.Count), closes.Count)
            Dim plusDI(n - 1) As Single
            Dim minusDI(n - 1) As Single
            Dim adxArr(n - 1) As Single
            For i = 0 To n - 1
                plusDI(i) = Single.NaN
                minusDI(i) = Single.NaN
                adxArr(i) = Single.NaN
            Next
            If n < period + 1 Then Return (plusDI, minusDI, adxArr)

            ' ── Raw TR, +DM, -DM ──────────────────────────────────────────────
            Dim trs(n - 1) As Double
            Dim pdms(n - 1) As Double   ' +DM
            Dim mdms(n - 1) As Double   ' -DM
            For i = 1 To n - 1
                Dim hl = CDbl(highs(i) - lows(i))
                Dim hc = Math.Abs(CDbl(highs(i)) - CDbl(closes(i - 1)))
                Dim lc = Math.Abs(CDbl(lows(i)) - CDbl(closes(i - 1)))
                trs(i) = Math.Max(hl, Math.Max(hc, lc))

                Dim upMove = CDbl(highs(i) - highs(i - 1))
                Dim downMove = CDbl(lows(i - 1) - lows(i))
                pdms(i) = If(upMove > downMove AndAlso upMove > 0, upMove, 0.0)
                mdms(i) = If(downMove > upMove AndAlso downMove > 0, downMove, 0.0)
            Next

            ' ── Wilder-smooth TR, +DM, -DM (seed with sum of first `period` values) ──
            Dim smoothTR = trs.Skip(1).Take(period).Sum()
            Dim smoothPDM = pdms.Skip(1).Take(period).Sum()
            Dim smoothMDM = mdms.Skip(1).Take(period).Sum()

            ' ── DX and seed ADX ───────────────────────────────────────────────
            Dim dxArr(n - 1) As Double
            Dim firstValid = period   ' index of first valid DI value (1-based DM array → period index)

            Dim pdi0 = If(smoothTR > 0, CSng(100.0 * smoothPDM / smoothTR), 0.0F)
            Dim mdi0 = If(smoothTR > 0, CSng(100.0 * smoothMDM / smoothTR), 0.0F)
            plusDI(firstValid) = pdi0
            minusDI(firstValid) = mdi0
            Dim diSum0 = CDbl(pdi0) + CDbl(mdi0)
            dxArr(firstValid) = If(diSum0 > 0, 100.0 * Math.Abs(CDbl(pdi0) - CDbl(mdi0)) / diSum0, 0.0)

            ' ── Rolling Wilder smoothing for the rest ──────────────────────────
            Dim adxSeedSum As Double = dxArr(firstValid)
            Dim adxSeedCount As Integer = 1

            For i = firstValid + 1 To n - 1
                smoothTR = smoothTR - smoothTR / period + trs(i)
                smoothPDM = smoothPDM - smoothPDM / period + pdms(i)
                smoothMDM = smoothMDM - smoothMDM / period + mdms(i)

                Dim pdi = If(smoothTR > 0, CSng(100.0 * smoothPDM / smoothTR), 0.0F)
                Dim mdi = If(smoothTR > 0, CSng(100.0 * smoothMDM / smoothTR), 0.0F)
                plusDI(i) = pdi
                minusDI(i) = mdi

                Dim diSum = CDbl(pdi) + CDbl(mdi)
                dxArr(i) = If(diSum > 0, 100.0 * Math.Abs(CDbl(pdi) - CDbl(mdi)) / diSum, 0.0)

                ' Accumulate DX until we have `period` values, then start Wilder ADX
                If adxSeedCount < period Then
                    adxSeedSum += dxArr(i)
                    adxSeedCount += 1
                    If adxSeedCount = period Then
                        ' Seed the ADX with the average of the first `period` DX values
                        adxArr(i) = CSng(adxSeedSum / period)
                    End If
                Else
                    ' Wilder smoothing: ADX(i) = (ADX(i-1)×(period-1) + DX(i)) / period
                    Dim prevAdx = If(Single.IsNaN(adxArr(i - 1)), 0.0F, adxArr(i - 1))
                    adxArr(i) = CSng((CDbl(prevAdx) * (period - 1) + dxArr(i)) / period)
                End If
            Next

            Return (plusDI, minusDI, adxArr)
        End Function

        ' ── Ichimoku Cloud ────────────────────────────────────────────────────────

        ''' <summary>
        ''' Ichimoku Kinkō Hyō — returns Tenkan-sen, Kijun-sen, Senkou Span A and Span B arrays,
        ''' each the same length as the input series.
        ''' SpanA and SpanB are projected <paramref name="displacement"/> bars forward so the
        ''' value at the last index reflects the "current" cloud visible on the chart.
        ''' The Chikou (Lagging) Span is not returned — callers compare
        ''' closes(n-1) vs closes(n-1-displacement) directly.
        ''' </summary>
        Public Function IchimokuCloud(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                Optional tenkanPeriod As Integer = 9,
                Optional kijunPeriod As Integer = 26,
                Optional senkouBPeriod As Integer = 52,
                Optional displacement As Integer = 26) As (Tenkan As Single(), Kijun As Single(), SpanA As Single(), SpanB As Single())

            Dim n = Math.Min(Math.Min(highs.Count, lows.Count), closes.Count)
            Dim tenkan(n - 1) As Single
            Dim kijun(n - 1) As Single
            Dim spanA(n - 1) As Single
            Dim spanB(n - 1) As Single
            For i = 0 To n - 1
                tenkan(i) = Single.NaN
                kijun(i) = Single.NaN
                spanA(i) = Single.NaN
                spanB(i) = Single.NaN
            Next
            If n = 0 Then Return (tenkan, kijun, spanA, spanB)

            ' Compute Tenkan-sen and Kijun-sen (no displacement)
            For i = 0 To n - 1
                If i >= tenkanPeriod - 1 Then
                    Dim hh = CDbl(highs(i))
                    Dim ll = CDbl(lows(i))
                    For j = i - tenkanPeriod + 1 To i
                        If CDbl(highs(j)) > hh Then hh = CDbl(highs(j))
                        If CDbl(lows(j)) < ll Then ll = CDbl(lows(j))
                    Next
                    tenkan(i) = CSng((hh + ll) / 2.0)
                End If

                If i >= kijunPeriod - 1 Then
                    Dim hh = CDbl(highs(i))
                    Dim ll = CDbl(lows(i))
                    For j = i - kijunPeriod + 1 To i
                        If CDbl(highs(j)) > hh Then hh = CDbl(highs(j))
                        If CDbl(lows(j)) < ll Then ll = CDbl(lows(j))
                    Next
                    kijun(i) = CSng((hh + ll) / 2.0)
                End If
            Next

            ' Project SpanA and SpanB forward by `displacement` bars.
            ' SpanA[i + displacement] = (Tenkan[i] + Kijun[i]) / 2
            ' SpanB[i + displacement] = (highest_high(senkouBPeriod, i) + lowest_low(senkouBPeriod, i)) / 2
            For i = 0 To n - 1
                Dim fwd = i + displacement
                If fwd >= n Then Continue For

                If Not Single.IsNaN(tenkan(i)) AndAlso Not Single.IsNaN(kijun(i)) Then
                    spanA(fwd) = CSng((CDbl(tenkan(i)) + CDbl(kijun(i))) / 2.0)
                End If

                If i >= senkouBPeriod - 1 Then
                    Dim hh = CDbl(highs(i))
                    Dim ll = CDbl(lows(i))
                    For j = i - senkouBPeriod + 1 To i
                        If CDbl(highs(j)) > hh Then hh = CDbl(highs(j))
                        If CDbl(lows(j)) < ll Then ll = CDbl(lows(j))
                    Next
                    spanB(fwd) = CSng((hh + ll) / 2.0)
                End If
            Next

            Return (tenkan, kijun, spanA, spanB)
        End Function

        ' ── Stochastic RSI ────────────────────────────────────────────────────────

        ''' <summary>
        ''' Stochastic RSI: applies the Stochastic formula to RSI(rsiPeriod) values.
        ''' Returns (%K, %D) normalised to the range 0.0–1.0.
        ''' %K = (RSI - MinRSI_stochPeriod) / (MaxRSI_stochPeriod - MinRSI_stochPeriod)
        ''' %D = signalPeriod-bar SMA of %K (signal / smoothing line).
        ''' When the RSI range within the stoch window is effectively zero, %K is pinned at 0.5.
        ''' </summary>
        Public Function StochasticRSI(
                closes As IList(Of Decimal),
                Optional rsiPeriod As Integer = 14,
                Optional stochPeriod As Integer = 14,
                Optional signalPeriod As Integer = 3) As (K As Single(), D As Single())

            Dim n = closes.Count
            Dim kArr(n - 1) As Single
            Dim dArr(n - 1) As Single
            For i = 0 To n - 1
                kArr(i) = Single.NaN
                dArr(i) = Single.NaN
            Next
            If n < rsiPeriod + stochPeriod Then Return (kArr, dArr)

            Dim rsiValues = RSI(closes, rsiPeriod)

            ' Compute %K over a rolling stochPeriod window of RSI values
            For i = stochPeriod - 1 To n - 1
                If Single.IsNaN(rsiValues(i)) Then Continue For
                Dim minRsi = Single.MaxValue
                Dim maxRsi = Single.MinValue
                For j = i - stochPeriod + 1 To i
                    If Not Single.IsNaN(rsiValues(j)) Then
                        If rsiValues(j) < minRsi Then minRsi = rsiValues(j)
                        If rsiValues(j) > maxRsi Then maxRsi = rsiValues(j)
                    End If
                Next
                Dim rng = maxRsi - minRsi
                kArr(i) = If(rng < 0.0001F, 0.5F,
                              CSng((CDbl(rsiValues(i)) - CDbl(minRsi)) / CDbl(rng)))
            Next

            ' %D = signalPeriod-period SMA of %K
            For i = signalPeriod - 1 To n - 1
                Dim sum As Double = 0
                Dim cnt = 0
                For j = i - signalPeriod + 1 To i
                    If Not Single.IsNaN(kArr(j)) Then
                        sum += CDbl(kArr(j))
                        cnt += 1
                    End If
                Next
                If cnt = signalPeriod Then
                    dArr(i) = CSng(sum / signalPeriod)
                End If
            Next

            Return (kArr, dArr)
        End Function

        ' ── WaveTrend (Market Cipher B simulation) ───────────────────────────────

        ''' <summary>
        ''' WaveTrend oscillator — simulates the momentum "blue wave" of Market Cipher B.
        ''' WT1 oscillates around zero; ±60 are the standard overbought/oversold thresholds.
        ''' WT2 is a <paramref name="signalSmooth"/>-bar SMA of WT1 (signal / smoothing line).
        ''' A Green Dot fires when WT1 crosses above WT2 near an oversold trough (WT1 &lt; -60).
        ''' A Red Dot fires when WT1 crosses below WT2 near an overbought peak (WT1 &gt; +60).
        ''' Formula:
        '''   HLC3 = (H + L + C) / 3
        '''   ESA  = EMA(HLC3, channelLength)
        '''   D    = EMA(|HLC3 − ESA|, channelLength)
        '''   CI   = (HLC3 − ESA) / (0.015 × D)
        '''   WT1  = EMA(CI, avgLength)
        '''   WT2  = SMA(WT1, signalSmooth)
        ''' </summary>
        Public Function WaveTrend(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                Optional channelLength As Integer = 10,
                Optional avgLength As Integer = 21,
                Optional signalSmooth As Integer = 4) As (Wt1 As Single(), Wt2 As Single())

            Dim n = Math.Min(Math.Min(highs.Count, lows.Count), closes.Count)
            Dim wt1(n - 1) As Single
            Dim wt2(n - 1) As Single
            For i = 0 To n - 1
                wt1(i) = Single.NaN
                wt2(i) = Single.NaN
            Next
            If n < channelLength + avgLength + signalSmooth Then Return (wt1, wt2)

            ' HLC3 — typical price
            Dim hlc3(n - 1) As Decimal
            For i = 0 To n - 1
                hlc3(i) = (highs(i) + lows(i) + closes(i)) / 3D
            Next

            ' ESA = EMA(HLC3, channelLength)
            Dim esaArr = EMA(hlc3, channelLength)

            ' D = EMA(|HLC3 − ESA|, channelLength)
            Dim dInput(n - 1) As Decimal
            For i = 0 To n - 1
                dInput(i) = If(Single.IsNaN(esaArr(i)), 0D, Math.Abs(hlc3(i) - CDec(esaArr(i))))
            Next
            Dim dArr = EMA(dInput, channelLength)

            ' CI = (HLC3 − ESA) / (0.015 × D);  guard against near-zero D to avoid ÷0
            Dim ciArr(n - 1) As Decimal
            For i = 0 To n - 1
                If Single.IsNaN(esaArr(i)) OrElse Single.IsNaN(dArr(i)) OrElse dArr(i) < 0.00001F Then
                    ciArr(i) = 0D
                Else
                    ciArr(i) = (hlc3(i) - CDec(esaArr(i))) / (0.015D * CDec(dArr(i)))
                End If
            Next

            ' WT1 = EMA(CI, avgLength)
            wt1 = EMA(ciArr, avgLength)

            ' WT2 = rolling SMA(WT1, signalSmooth)
            For i = signalSmooth - 1 To n - 1
                If Single.IsNaN(wt1(i)) Then Continue For
                Dim wtSum As Double = 0
                Dim cnt As Integer = 0
                For j = i - signalSmooth + 1 To i
                    If Not Single.IsNaN(wt1(j)) Then
                        wtSum += CDbl(wt1(j))
                        cnt += 1
                    End If
                Next
                If cnt = signalSmooth Then wt2(i) = CSng(wtSum / signalSmooth)
            Next

            Return (wt1, wt2)
        End Function

        ' ── SuperTrend ────────────────────────────────────────────────────────────

        ''' <summary>
        ''' SuperTrend indicator — ATR-based dynamic support/resistance with trend direction.
        '''
        ''' Algorithm:
        '''   HL2          = (High + Low) / 2
        '''   Basic Upper  = HL2 + multiplier × ATR(period)
        '''   Basic Lower  = HL2 − multiplier × ATR(period)
        '''   Final Upper  = min(Basic Upper, prev Final Upper) when prev close &gt; prev Final Upper;
        '''                  otherwise Basic Upper
        '''   Final Lower  = max(Basic Lower, prev Final Lower) when prev close &lt; prev Final Lower;
        '''                  otherwise Basic Lower
        '''   Direction    = +1 (up-trend) when close &gt; prev Final Upper
        '''                = −1 (down-trend) when close &lt; prev Final Lower
        '''                = prev Direction otherwise
        '''   Line         = Final Lower when Direction = +1 (acts as support)
        '''                = Final Upper when Direction = −1 (acts as resistance)
        '''
        ''' Returns:
        '''   Line      — the SuperTrend price line (NaN during ATR warm-up).
        '''   Direction — +1 for up-trend, −1 for down-trend (0 during warm-up).
        ''' </summary>
        Public Function SuperTrend(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                Optional period As Integer = 10,
                Optional multiplier As Double = 3.0) As (Line As Single(), Direction As Single())

            Dim n = Math.Min(Math.Min(highs.Count, lows.Count), closes.Count)
            Dim lineArr(n - 1) As Single
            Dim dirArr(n - 1) As Single
            For i = 0 To n - 1
                lineArr(i) = Single.NaN
                dirArr(i) = 0.0F
            Next
            If n < period + 1 Then Return (lineArr, dirArr)

            Dim atrArr = ATR(highs, lows, closes, period)

            ' State variables
            Dim finalUpper As Double = 0
            Dim finalLower As Double = 0
            Dim direction As Integer = 0   ' 0 = not yet seeded; set on first valid bar

            For i = period To n - 1
                Dim atrVal = atrArr(i)
                If Single.IsNaN(atrVal) Then Continue For

                Dim hl2 = (CDbl(highs(i)) + CDbl(lows(i))) / 2.0
                Dim basicUpper = hl2 + multiplier * CDbl(atrVal)
                Dim basicLower = hl2 - multiplier * CDbl(atrVal)

                ' Adjust Final Upper/Lower bands using previous bar's close
                Dim prevClose = CDbl(closes(i - 1))

                Dim newFinalUpper As Double
                Dim newFinalLower As Double

                If i = period Then
                    ' First valid bar — seed the bands
                    newFinalUpper = basicUpper
                    newFinalLower = basicLower
                Else
                    ' Final Upper = Basic Upper unless previous close > previous Final Upper
                    '   in which case Final Upper can only decrease (locks in support ceiling)
                    newFinalUpper = If(basicUpper < finalUpper OrElse prevClose > finalUpper,
                                      basicUpper, finalUpper)

                    ' Final Lower = Basic Lower unless previous close < previous Final Lower
                    '   in which case Final Lower can only increase (locks in support floor)
                    newFinalLower = If(basicLower > finalLower OrElse prevClose < finalLower,
                                      basicLower, finalLower)
                End If

                ' Determine trend direction by comparing current close against the
                ' PREVIOUS bar's final bands (held in finalUpper / finalLower).
                ' On the very first valid bar we seed direction from the initial bands.
                Dim closeNow = CDbl(closes(i))
                If i = period Then
                    ' Seed: bullish if close is above the lower band, bearish otherwise
                    direction = If(closeNow >= newFinalLower, 1, -1)
                Else
                    If closeNow > finalUpper Then
                        direction = 1
                    ElseIf closeNow < finalLower Then
                        direction = -1
                    End If
                    ' else direction unchanged (carry previous value)
                End If

                lineArr(i) = If(direction = 1, CSng(newFinalLower), CSng(newFinalUpper))
                dirArr(i) = direction

                finalUpper = newFinalUpper
                finalLower = newFinalLower
            Next

            Return (lineArr, dirArr)
        End Function

        ' ── Donchian Channel ──────────────────────────────────────────────────────

        ''' <summary>
        ''' Donchian Channel — rolling highest high / lowest low / midpoint over a given period.
        '''
        ''' Upper[i] = max(High[i−period+1 .. i])
        ''' Lower[i] = min(Low[i−period+1 .. i])
        ''' Middle[i] = (Upper[i] + Lower[i]) / 2
        '''
        ''' All arrays are NaN-padded for the first (period−1) bars.
        ''' </summary>
        Public Function DonchianChannel(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                period As Integer) As (Upper As Single(), Lower As Single(), Middle As Single())

            Dim n = Math.Min(highs.Count, lows.Count)
            Dim upper(n - 1) As Single
            Dim lower(n - 1) As Single
            Dim middle(n - 1) As Single
            For i = 0 To n - 1
                upper(i) = Single.NaN
                lower(i) = Single.NaN
                middle(i) = Single.NaN
            Next
            If n < period OrElse period <= 0 Then Return (upper, lower, middle)

            For i = period - 1 To n - 1
                Dim hi = CDbl(highs(i))
                Dim lo = CDbl(lows(i))
                For j = i - period + 1 To i
                    If CDbl(highs(j)) > hi Then hi = CDbl(highs(j))
                    If CDbl(lows(j)) < lo Then lo = CDbl(lows(j))
                Next
                upper(i) = CSng(hi)
                lower(i) = CSng(lo)
                middle(i) = CSng((hi + lo) / 2.0)
            Next

            Return (upper, lower, middle)
        End Function

        ' ── ConnorsRSI-2 components ───────────────────────────────────────────────

        ''' <summary>
        ''' RSI(2) — convenience wrapper using the generic RSI function with period = 2.
        ''' Used by the Connors RSI-2 strategy where the short 2-period RSI identifies
        ''' overbought/oversold dips against a SMA(200) long-term trend filter.
        ''' Identical to calling RSI(closes, 2); provided here for naming clarity.
        ''' </summary>
        Public Function Rsi2(closes As IList(Of Decimal)) As Single()
            Return RSI(closes, 2)
        End Function

        ' ── Helpers ──────────────────────────────────────────────────────────────

        ''' <summary>Get the last non-NaN value from an indicator array.</summary>
        Public Function LastValid(series As Single()) As Single
            For i = series.Length - 1 To 0 Step -1
                If Not Single.IsNaN(series(i)) Then Return series(i)
            Next
            Return 0.0F
        End Function

        ''' <summary>Get the second-to-last non-NaN value (for momentum/diff calculations).</summary>
        Public Function PreviousValid(series As Single()) As Single
            Dim count = 0
            For i = series.Length - 1 To 0 Step -1
                If Not Single.IsNaN(series(i)) Then
                    count += 1
                    If count = 2 Then Return series(i)
                End If
            Next
            Return 0.0F
        End Function

        ' ── Delta Volume ─────────────────────────────────────────────────────

        ''' <summary>
        ''' Rolling volume delta — measures net buying vs selling pressure over a lookback window.
        ''' Each bar contributes its full volume to the bull bucket (close &gt;= open) or the
        ''' bear bucket (close &lt; open).  The result is normalised to [-1, +1]:
        '''   delta[i] = (sumBull − sumBear) / (sumBull + sumBear)
        ''' +1 = pure buying pressure;  −1 = pure selling pressure;  0 = balanced.
        ''' Used as a volume-confirmation filter for VIDYA crossover signals.
        ''' </summary>
        Public Function DeltaVolume(closes As IList(Of Decimal),
                                    opens As IList(Of Decimal),
                                    volumes As IList(Of Long),
                                    Optional window As Integer = 6) As Single()
            Dim n = Math.Min(Math.Min(closes.Count, opens.Count), volumes.Count)
            Dim result(n - 1) As Single
            For i = 0 To n - 1
                result(i) = Single.NaN
            Next
            If n < window OrElse window <= 0 Then Return result

            For i = window - 1 To n - 1
                Dim bull As Double = 0
                Dim bear As Double = 0
                For j = i - window + 1 To i
                    Dim vol = CDbl(volumes(j))
                    If closes(j) >= opens(j) Then
                        bull += vol
                    Else
                        bear += vol
                    End If
                Next
                Dim total = bull + bear
                If total > 0 Then
                    result(i) = CSng((bull - bear) / total)
                Else
                    ' Volume not available (e.g. index symbols) — fall back to unweighted
                    ' bar-direction delta: count up-bars vs down-bars, normalise to [-1, +1].
                    Dim bullBars As Integer = 0
                    For j = i - window + 1 To i
                        If closes(j) >= opens(j) Then bullBars += 1
                    Next
                    result(i) = CSng((2 * bullBars - window) / CDbl(window))
                End If
            Next
            Return result
        End Function

        ' ── CMO (Chande Momentum Oscillator) ─────────────────────────────────

        ''' <summary>
        ''' Chande Momentum Oscillator — returns values in the range [-1, +1].
        ''' CMO = (sumPos - sumNeg) / (sumPos + sumNeg)
        ''' where sumPos = sum of up-moves, sumNeg = sum of |down-moves| over <paramref name="period"/> bars.
        ''' Normalised to [-1,+1] (not the traditional [-100,+100]) so it can be used
        ''' directly as a VIDYA dynamic-alpha multiplier.
        ''' </summary>
        Public Function CMO(closes As IList(Of Decimal), period As Integer) As Single()
            Dim n = closes.Count
            Dim result(n - 1) As Single
            For i = 0 To n - 1
                result(i) = Single.NaN
            Next
            If n < period + 1 Then Return result

            For i = period To n - 1
                Dim sumPos As Double = 0
                Dim sumNeg As Double = 0
                For j = i - period + 1 To i
                    Dim diff = CDbl(closes(j)) - CDbl(closes(j - 1))
                    If diff > 0 Then sumPos += diff Else sumNeg += Math.Abs(diff)
                Next
                Dim denom = sumPos + sumNeg
                result(i) = If(denom > 0, CSng((sumPos - sumNeg) / denom), 0.0F)
            Next
            Return result
        End Function

        ' ── VIDYA (Variable Index Dynamic Average) ───────────────────────────

        ''' <summary>
        ''' Variable Index Dynamic Average (BigBeluga / Tushar Chande variant).
        ''' Uses the absolute CMO value as a dynamic multiplier for the EMA alpha, so the
        ''' average tracks price tightly during strong trends and barely moves during chop.
        '''
        ''' Formula per bar:
        '''   alpha        = 2 / (<paramref name="vidyaLength"/> + 1)
        '''   dynamicAlpha = alpha × |CMO(<paramref name="cmoLength"/>)|
        '''   VIDYA[i]     = dynamicAlpha × close[i] + (1 − dynamicAlpha) × VIDYA[i-1]
        '''
        ''' Seed: first valid VIDYA value is seeded with the first available close.
        ''' </summary>
        Public Function VIDYA(closes As IList(Of Decimal),
                               vidyaLength As Integer,
                               cmoLength As Integer) As Single()
            Dim n = closes.Count
            Dim result(n - 1) As Single
            For i = 0 To n - 1
                result(i) = Single.NaN
            Next
            If n < cmoLength + 1 Then Return result

            Dim cmoValues As Single() = CMO(closes, cmoLength)
            Dim alpha As Double = 2.0 / (vidyaLength + 1)

            ' Seed the first valid bar
            Dim seedIdx = cmoLength   ' first bar where CMO is valid
            result(seedIdx) = CSng(closes(seedIdx))

            For i = seedIdx + 1 To n - 1
                If Single.IsNaN(cmoValues(i)) Then Continue For
                Dim dynAlpha = alpha * Math.Abs(CDbl(cmoValues(i)))
                result(i) = CSng(dynAlpha * CDbl(closes(i)) + (1.0 - dynAlpha) * CDbl(result(i - 1)))
            Next
            Return result
        End Function

    End Module

End Namespace
