Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Naked Trader — deterministic 5-minute trend snapshot.
    '''
    ''' Accepts an oldest-first OHLCV bar list and produces a Direction + Confidence result
    ''' using four indicator votes: EMA(9/21), MACD(8,17,9), DMI/ADX(14), and VWAP.
    '''
    ''' ADX uses Wilder's smoothing (RMA) as implemented in <see cref="TechnicalIndicators.DMI"/>.
    ''' Minimum bars: 28 (ADX(14) seeds at index 2×14−1 = 27).
    ''' Recommended fetch: 40 bars (multiple valid ADX readings for stability).
    ''' </summary>
    Public Module NakedTraderAnalyser

        ''' <summary>Absolute minimum bars: ADX(14) first valid at index 27 requires 28 bars.</summary>
        Public Const MIN_BARS As Integer = 28

        ''' <summary>Recommended fetch count — comfortable ADX warm-up margin.</summary>
        Public Const RECOMMENDED_BARS As Integer = 40

        ''' <summary>
        ''' Compute direction and confidence from an oldest-first OHLCV bar list.
        ''' </summary>
        Public Function Analyse(bars As IList(Of MarketBar)) As TrendSnapshotResult
            Dim result As New TrendSnapshotResult With {
                .BarsAnalysed = If(bars IsNot Nothing, bars.Count, 0),
                .AnalysedAt = DateTimeOffset.UtcNow
            }

            If bars Is Nothing OrElse bars.Count < MIN_BARS Then
                result.Summary = $"Insufficient data — need {MIN_BARS}+ bars, got {If(bars IsNot Nothing, bars.Count, 0)}."
                result.Confidence = TrendConfidence.Low
                result.Direction = TrendDirection.Up
                Return result
            End If

            ' ── Extract price series ─────────────────────────────────────────
            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim highs = bars.Select(Function(b) b.High).ToList()
            Dim lows = bars.Select(Function(b) b.Low).ToList()
            Dim volumes = bars.Select(Function(b) b.Volume).ToList()

            ' ── EMA(9) and EMA(21) ───────────────────────────────────────────
            Dim ema9Arr = TechnicalIndicators.EMA(closes, 9)
            Dim ema21Arr = TechnicalIndicators.EMA(closes, 21)
            Dim ema9 = TechnicalIndicators.LastValid(ema9Arr)
            Dim ema21 = TechnicalIndicators.LastValid(ema21Arr)

            ' ── MACD(8, 17, 9) ──────────────────────────────────────────────
            Dim macd = TechnicalIndicators.MACD(closes, 8, 17, 9)
            Dim macdHist = TechnicalIndicators.LastValid(macd.Histogram)
            Dim macdLineVal = TechnicalIndicators.LastValid(macd.Line)

            ' ── DMI / ADX(14) — Wilder RMA smoothing ────────────────────────
            Dim dmi = TechnicalIndicators.DMI(highs, lows, closes, 14)
            Dim plusDI = TechnicalIndicators.LastValid(dmi.PlusDI)
            Dim minusDI = TechnicalIndicators.LastValid(dmi.MinusDI)
            Dim adx = TechnicalIndicators.LastValid(dmi.ADX)

            ' ── Rolling VWAP over the full window ───────────────────────────
            ' Volume=0 on all bars signals an absent volume feed (e.g. some crypto).
            Dim hasVolume = volumes.Any(Function(v) v > 0)
            Dim vwapValue As Single = Single.NaN
            Dim vwapAvailable As Boolean = False
            If hasVolume Then
                Dim vwapArr = TechnicalIndicators.VWAP(highs, lows, closes, volumes)
                vwapValue = TechnicalIndicators.LastValid(vwapArr)
                vwapAvailable = Not Single.IsNaN(vwapValue)
            End If

            ' ── VolumeMA(20) for volume confirmation ─────────────────────────
            Dim volumeOk As Boolean? = Nothing
            If hasVolume Then
                Dim volDecimals = volumes.Select(Function(v) CDec(v)).ToList()
                Dim volMa20 = TechnicalIndicators.SMA(volDecimals, 20)
                Dim volMaVal = TechnicalIndicators.LastValid(volMa20)
                If Not Single.IsNaN(volMaVal) AndAlso volMaVal > 0 Then
                    volumeOk = CDec(volumes.Last()) > CDec(volMaVal)
                End If
            End If

            Dim lastClose = closes.Last()

            ' ── Four directional votes ───────────────────────────────────────
            ' All votes require a minimum magnitude to prevent single-tick noise fires.
            Dim upVotes As Integer = 0
            Dim downVotes As Integer = 0
            Dim totalVotes As Integer = 0

            ' Vote 1: EMA — EMA(9) vs EMA(21) with ≥0.1% separation (prevents 1-tick crossovers)
            If Not Single.IsNaN(ema9) AndAlso Not Single.IsNaN(ema21) Then
                Const EmaGapPct As Single = 0.001F  ' 0.1%
                Dim ema9Bull = ema9 > ema21 * (1.0F + EmaGapPct)
                Dim ema9Bear = ema9 < ema21 * (1.0F - EmaGapPct)
                If ema9Bull OrElse ema9Bear Then
                    totalVotes += 1
                    If ema9Bull Then upVotes += 1 Else downVotes += 1
                End If
                ' If gap is < 0.1% the vote is abstained — prevents whipsaw during convergence
            End If

            ' Vote 2: MACD — prefer histogram; if histogram is too small (near-zero during trend convergence),
            ' fall back to MACD line. Require ≥0.001 magnitude on whichever value is used — filters
            ' truly degenerate readings (0.00001) without suppressing converged-but-genuine trends.
            Const MacdMinMag As Single = 0.001F
            Dim macdVote As Single = Single.NaN
            If Not Single.IsNaN(macdHist) AndAlso Math.Abs(macdHist) >= MacdMinMag Then
                macdVote = macdHist                             ' histogram has meaningful magnitude
            ElseIf Not Single.IsNaN(macdLineVal) AndAlso Math.Abs(macdLineVal) >= MacdMinMag Then
                macdVote = macdLineVal                          ' histogram converged — use MACD line
            End If
            If Not Single.IsNaN(macdVote) Then
                totalVotes += 1
                If macdVote > 0 Then upVotes += 1 Else downVotes += 1
            End If

            ' Vote 3: DMI — +DI vs -DI with ≥1.0 pt spread (prevents noise in low-conviction markets)
            Const DiMinSpread As Single = 1.0F
            If Not Single.IsNaN(plusDI) AndAlso Not Single.IsNaN(minusDI) Then
                Dim diSpread = Math.Abs(plusDI - minusDI)
                If diSpread >= DiMinSpread Then
                    totalVotes += 1
                    If plusDI > minusDI Then upVotes += 1 Else downVotes += 1
                End If
                ' DI spread < 1 pt = near-equal directional pressure; abstain
            End If

            ' Vote 4: VWAP — Close vs VWAP with ≥0.1% gap (absent when volume feed unavailable)
            If vwapAvailable Then
                Const VwapGapPct As Double = 0.001  ' 0.1%
                Dim closeDbl = CDbl(lastClose)
                Dim vwapDbl = CDbl(vwapValue)
                Dim vwapBull = closeDbl > vwapDbl * (1.0 + VwapGapPct)
                Dim vwapBear = closeDbl < vwapDbl * (1.0 - VwapGapPct)
                If vwapBull OrElse vwapBear Then
                    totalVotes += 1
                    If vwapBull Then upVotes += 1 Else downVotes += 1
                End If
                ' Within 0.1% of VWAP = neutral; abstain
            End If

            ' ── Direction ────────────────────────────────────────────────────
            Dim direction As TrendDirection
            Dim isTie As Boolean = False
            If upVotes > downVotes Then
                direction = TrendDirection.Up
            ElseIf downVotes > upVotes Then
                direction = TrendDirection.Down
            Else
                ' Tie (including 0/0 when all votes abstained) → always LOW confidence — no trade fires
                isTie = True
                direction = TrendDirection.Up   ' direction irrelevant; confidence = Low → no signal
            End If

            ' ── Confidence ───────────────────────────────────────────────────
            Dim confidence As TrendConfidence
            If Single.IsNaN(adx) OrElse adx < 20.0F OrElse isTie Then
                ' ADX below tradeability threshold, unavailable, or tied votes
                confidence = TrendConfidence.Low
            ElseIf adx >= 25.0F Then
                Dim aligned = Math.Max(upVotes, downVotes)
                Dim volAvailable = volumeOk.HasValue
                Dim volOkBool = volumeOk.GetValueOrDefault()

                If aligned = totalVotes Then
                    ' All available votes agree — check volume gate for HIGH
                    Dim highConfOk As Boolean
                    If volAvailable Then
                        highConfOk = volOkBool                            ' volume present: current bar > MA(20)
                    Else
                        highConfOk = adx >= 30.0F                         ' no volume: stricter ADX floor
                    End If
                    confidence = If(highConfOk, TrendConfidence.High, TrendConfidence.Medium)
                ElseIf aligned >= totalVotes - 1 Then
                    confidence = TrendConfidence.Medium                    ' 3/4 or 2/3 aligned
                Else
                    confidence = TrendConfidence.Low
                End If
            Else
                ' 20 ≤ ADX < 25 — trend developing but not yet strong
                confidence = TrendConfidence.Low
            End If

            ' ── Build result ─────────────────────────────────────────────────
            result.Direction = direction
            result.Confidence = confidence
            result.Adx = adx
            result.PlusDI = plusDI
            result.MinusDI = minusDI
            result.Ema9 = ema9
            result.Ema21 = ema21
            result.MacdHistogram = macdHist
            result.MacdLine = macdLineVal
            result.Vwap = vwapValue
            result.LastClose = lastClose
            result.UpVotes = upVotes
            result.DownVotes = downVotes
            result.TotalVotes = totalVotes
            result.IsVolumeOk = volumeOk

            Dim adxStr = If(Single.IsNaN(adx), "N/A", $"{adx:F1}")
            Dim tieTag = If(isTie, " (tie→EMA)", String.Empty)
            result.Summary = $"{direction.ToString().ToUpper()}{tieTag} | {confidence.ToString().ToUpper()} | " &
                             $"ADX={adxStr} +DI={plusDI:F1} -DI={minusDI:F1} | " &
                             $"{upVotes}↑ {downVotes}↓ / {totalVotes} votes"
            Return result
        End Function

    End Module

End Namespace
