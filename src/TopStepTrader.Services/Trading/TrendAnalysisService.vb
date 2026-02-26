Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Analyses the most recent bars using a combined EMA + RSI strategy
    ''' to produce an Up/Down probability for trend direction.
    '''
    ''' Strategy is based on the AIT Technical Analysis Approach documents:
    '''   - EMA 21 (primary trend spotter) and EMA 50 (bigger picture guide)
    '''   - RSI 14 for momentum / overbought / oversold conditions
    '''   - EMA crossover direction for trend confirmation
    '''   - Price position relative to EMAs
    '''
    ''' The service combines multiple indicator signals into a weighted
    ''' probability score, where each signal contributes a bullish or
    ''' bearish vote with a configurable weight.
    ''' </summary>
    Public Class TrendAnalysisService

        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _logger As ILogger(Of TrendAnalysisService)

        ' ── Weights for each indicator signal ────────────────────────────────
        Private Const EMA_CROSSOVER_WEIGHT   As Double = 25.0  ' EMA 21 vs EMA 50 crossover
        Private Const PRICE_VS_EMA21_WEIGHT  As Double = 20.0  ' Price above/below EMA 21
        Private Const PRICE_VS_EMA50_WEIGHT  As Double = 15.0  ' Price above/below EMA 50
        Private Const RSI_TREND_WEIGHT       As Double = 20.0  ' RSI position (overbought/oversold/neutral)
        Private Const EMA_MOMENTUM_WEIGHT    As Double = 10.0  ' EMA 21 slope (rising/falling)
        Private Const CANDLE_PATTERN_WEIGHT  As Double = 10.0  ' Recent candle pattern (bullish/bearish)

        Public Sub New(barRepository As BarRepository,
                       logger As ILogger(Of TrendAnalysisService))
            _barRepository = barRepository
            _logger = logger
        End Sub

        ''' <summary>
        ''' Analyse the past N bars for a contract and return Up/Down probabilities.
        ''' Uses 1-hour bars by default (24 bars = 24 hours of data).
        ''' </summary>
        ''' <param name="contractId">The contract to analyse.</param>
        ''' <param name="barCount">Number of recent bars to fetch (default 24).</param>
        ''' <param name="timeframe">Bar timeframe (default OneHour).</param>
        Public Async Function AnalyseTrendAsync(contractId As String,
                                                 Optional barCount As Integer = 24,
                                                 Optional timeframe As BarTimeframe = BarTimeframe.OneHour,
                                                 Optional cancel As System.Threading.CancellationToken = Nothing) As Task(Of TrendAnalysisResult)

            ' We need extra bars beyond the requested count so the EMA 50 has enough
            ' history to produce valid (non-NaN) values.  Fetch barCount + 50 bars.
            Dim fetchCount = barCount + 50
            Dim bars = Await _barRepository.GetRecentBarsAsync(contractId, timeframe, fetchCount, cancel)

            Dim result As New TrendAnalysisResult With {
                .BarsAnalysed = bars.Count,
                .AnalysedAt = DateTimeOffset.UtcNow
            }

            If bars.Count < 25 Then
                result.Summary = $"Insufficient data: only {bars.Count} bars available (need at least 25)."
                result.UpProbability = 50.0
                result.DownProbability = 50.0
                Return result
            End If

            ' ── Extract close prices ─────────────────────────────────────────
            Dim closes = bars.Select(Function(b) b.Close).ToList()

            ' ── Calculate indicators ─────────────────────────────────────────
            Dim ema21Arr = TechnicalIndicators.EMA(closes, 21)
            Dim ema50Arr = TechnicalIndicators.EMA(closes, 50)
            Dim rsi14Arr = TechnicalIndicators.RSI(closes, 14)

            Dim currentEma21 = TechnicalIndicators.LastValid(ema21Arr)
            Dim currentEma50 = TechnicalIndicators.LastValid(ema50Arr)
            Dim currentRsi   = TechnicalIndicators.LastValid(rsi14Arr)
            Dim prevEma21    = TechnicalIndicators.PreviousValid(ema21Arr)
            Dim lastClose    = closes.Last()

            result.EMA21 = currentEma21
            result.EMA50 = currentEma50
            result.RSI14 = currentRsi
            result.LastClose = lastClose

            ' ── Score each signal ────────────────────────────────────────────
            Dim bullishScore As Double = 0.0
            Dim bearishScore As Double = 0.0

            ' 1. EMA Crossover: EMA 21 above EMA 50 = bullish trend
            If currentEma21 > currentEma50 Then
                bullishScore += EMA_CROSSOVER_WEIGHT
                result.Signals.Add($"EMA Crossover: BULLISH (EMA21 {currentEma21:F2} > EMA50 {currentEma50:F2})")
            ElseIf currentEma21 < currentEma50 Then
                bearishScore += EMA_CROSSOVER_WEIGHT
                result.Signals.Add($"EMA Crossover: BEARISH (EMA21 {currentEma21:F2} < EMA50 {currentEma50:F2})")
            Else
                ' Neutral — split the weight
                bullishScore += EMA_CROSSOVER_WEIGHT / 2.0
                bearishScore += EMA_CROSSOVER_WEIGHT / 2.0
                result.Signals.Add($"EMA Crossover: NEUTRAL (EMA21 ≈ EMA50 at {currentEma21:F2})")
            End If

            ' 2. Price vs EMA 21: Price above EMA 21 = bullish
            If CDbl(lastClose) > CDbl(currentEma21) Then
                bullishScore += PRICE_VS_EMA21_WEIGHT
                result.Signals.Add($"Price vs EMA21: BULLISH (Close {lastClose:F2} > EMA21 {currentEma21:F2})")
            Else
                bearishScore += PRICE_VS_EMA21_WEIGHT
                result.Signals.Add($"Price vs EMA21: BEARISH (Close {lastClose:F2} < EMA21 {currentEma21:F2})")
            End If

            ' 3. Price vs EMA 50: Price above EMA 50 = bullish
            If CDbl(lastClose) > CDbl(currentEma50) Then
                bullishScore += PRICE_VS_EMA50_WEIGHT
                result.Signals.Add($"Price vs EMA50: BULLISH (Close {lastClose:F2} > EMA50 {currentEma50:F2})")
            Else
                bearishScore += PRICE_VS_EMA50_WEIGHT
                result.Signals.Add($"Price vs EMA50: BEARISH (Close {lastClose:F2} < EMA50 {currentEma50:F2})")
            End If

            ' 4. RSI Trend:
            '    RSI > 70 = overbought → bearish bias (likely to pull back)
            '    RSI < 30 = oversold  → bullish bias (likely to bounce)
            '    RSI 50-70 = bullish momentum
            '    RSI 30-50 = bearish momentum
            If currentRsi > 70 Then
                bearishScore += RSI_TREND_WEIGHT * 0.7
                bullishScore += RSI_TREND_WEIGHT * 0.3
                result.Signals.Add($"RSI: OVERBOUGHT ({currentRsi:F1}) — bearish reversal bias")
            ElseIf currentRsi < 30 Then
                bullishScore += RSI_TREND_WEIGHT * 0.7
                bearishScore += RSI_TREND_WEIGHT * 0.3
                result.Signals.Add($"RSI: OVERSOLD ({currentRsi:F1}) — bullish reversal bias")
            ElseIf currentRsi >= 50 Then
                Dim strength = (currentRsi - 50) / 20.0  ' 0.0 at 50, 1.0 at 70
                bullishScore += RSI_TREND_WEIGHT * (0.5 + strength * 0.3)
                bearishScore += RSI_TREND_WEIGHT * (0.5 - strength * 0.3)
                result.Signals.Add($"RSI: BULLISH MOMENTUM ({currentRsi:F1})")
            Else
                Dim strength = (50 - currentRsi) / 20.0  ' 0.0 at 50, 1.0 at 30
                bearishScore += RSI_TREND_WEIGHT * (0.5 + strength * 0.3)
                bullishScore += RSI_TREND_WEIGHT * (0.5 - strength * 0.3)
                result.Signals.Add($"RSI: BEARISH MOMENTUM ({currentRsi:F1})")
            End If

            ' 5. EMA 21 Momentum: Is EMA 21 rising or falling?
            If currentEma21 > prevEma21 Then
                bullishScore += EMA_MOMENTUM_WEIGHT
                result.Signals.Add($"EMA21 Slope: RISING ({prevEma21:F2} → {currentEma21:F2})")
            ElseIf currentEma21 < prevEma21 Then
                bearishScore += EMA_MOMENTUM_WEIGHT
                result.Signals.Add($"EMA21 Slope: FALLING ({prevEma21:F2} → {currentEma21:F2})")
            Else
                bullishScore += EMA_MOMENTUM_WEIGHT / 2.0
                bearishScore += EMA_MOMENTUM_WEIGHT / 2.0
                result.Signals.Add($"EMA21 Slope: FLAT ({currentEma21:F2})")
            End If

            ' 6. Recent Candle Pattern: Last 3 bars — majority bullish or bearish?
            Dim recentBars = bars.Skip(bars.Count - Math.Min(3, bars.Count)).ToList()
            Dim bullishCandles = recentBars.Count(Function(b) b.IsBullish)
            Dim bearishCandles = recentBars.Count - bullishCandles
            If bullishCandles > bearishCandles Then
                bullishScore += CANDLE_PATTERN_WEIGHT
                result.Signals.Add($"Recent Candles: BULLISH ({bullishCandles}/{recentBars.Count} bullish)")
            ElseIf bearishCandles > bullishCandles Then
                bearishScore += CANDLE_PATTERN_WEIGHT
                result.Signals.Add($"Recent Candles: BEARISH ({bearishCandles}/{recentBars.Count} bearish)")
            Else
                bullishScore += CANDLE_PATTERN_WEIGHT / 2.0
                bearishScore += CANDLE_PATTERN_WEIGHT / 2.0
                result.Signals.Add($"Recent Candles: MIXED ({bullishCandles}/{recentBars.Count} bullish)")
            End If

            ' ── Normalise to percentages ─────────────────────────────────────
            Dim total = bullishScore + bearishScore
            If total > 0 Then
                result.UpProbability = Math.Round(bullishScore / total * 100.0, 1)
                result.DownProbability = Math.Round(bearishScore / total * 100.0, 1)
            Else
                result.UpProbability = 50.0
                result.DownProbability = 50.0
            End If

            ' ── Build summary ────────────────────────────────────────────────
            Dim direction = If(result.UpProbability > result.DownProbability, "BULLISH", "BEARISH")
            Dim confidence = Math.Max(result.UpProbability, result.DownProbability)
            result.Summary = $"Trend: {direction} | Up: {result.UpProbability:F1}% | Down: {result.DownProbability:F1}% | " &
                             $"EMA21: {currentEma21:F2} | EMA50: {currentEma50:F2} | RSI: {currentRsi:F1} | " &
                             $"Bars: {bars.Count} | Analysed: {result.AnalysedAt:HH:mm:ss}"

            _logger.LogInformation("Trend analysis for {Contract}: {Summary}", contractId, result.Summary)
            Return result
        End Function

    End Class

End Namespace
