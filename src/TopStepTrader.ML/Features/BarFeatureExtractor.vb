Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Models

Namespace TopStepTrader.ML.Features

    ''' <summary>
    ''' Converts a sequence of MarketBars into a BarFeatureVector for ML.NET.
    ''' Requires at least 30 bars for reliable indicator values.
    ''' </summary>
    Public Class BarFeatureExtractor

        Public Const MinBarsRequired As Integer = 30

        ''' <summary>
        ''' Extract features from the most recent bar in the sequence.
        ''' Bars must be in ascending time order.
        ''' </summary>
        Public Function Extract(bars As IList(Of MarketBar),
                                Optional label As Boolean = False) As BarFeatureVector

            If bars Is Nothing OrElse bars.Count < MinBarsRequired Then
                Return New BarFeatureVector()
            End If

            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim highs = bars.Select(Function(b) b.High).ToList()
            Dim lows = bars.Select(Function(b) b.Low).ToList()
            Dim volumes = bars.Select(Function(b) b.Volume).ToList()
            Dim opens = bars.Select(Function(b) b.Open).ToList()
            Dim n = bars.Count
            Dim last = n - 1

            ' ── Calculate all indicators ──────────────────────────────────────
            Dim rsi14 = TechnicalIndicators.RSI(closes, 14)
            Dim rsi7 = TechnicalIndicators.RSI(closes, 7)
            Dim ema9 = TechnicalIndicators.EMA(closes, 9)
            Dim ema21 = TechnicalIndicators.EMA(closes, 21)
            Dim atr14 = TechnicalIndicators.ATR(highs, lows, closes, 14)
            Dim vwap = TechnicalIndicators.VWAP(highs, lows, closes, volumes)
            Dim macd = TechnicalIndicators.MACD(closes)
            Dim bb = TechnicalIndicators.BollingerBands(closes, 20)

            Dim close = CDbl(closes(last))
            Dim high = CDbl(highs(last))
            Dim low = CDbl(lows(last))
            Dim open_ = CDbl(opens(last))
            Dim atr = If(Single.IsNaN(atr14(last)), 1.0F, atr14(last))

            ' Volume ratio = bar volume / 20-bar average volume
            Dim vol20Avg = If(n >= 20,
                              volumes.Skip(n - 20).Average(Function(v) CDbl(v)),
                              volumes.Average(Function(v) CDbl(v)))
            Dim volRatio = If(vol20Avg > 0, CDbl(volumes(last)) / vol20Avg, 1.0)

            ' Price return helpers
            Dim ret1 = If(n >= 2 AndAlso closes(last - 1) <> 0D,
                          (close - CDbl(closes(last - 1))) / CDbl(closes(last - 1)), 0.0)
            Dim ret5 = If(n >= 6 AndAlso closes(last - 5) <> 0D,
                          (close - CDbl(closes(last - 5))) / CDbl(closes(last - 5)), 0.0)
            Dim ret20 = If(n >= 21 AndAlso closes(last - 20) <> 0D,
                           (close - CDbl(closes(last - 20))) / CDbl(closes(last - 20)), 0.0)

            ' MACD histogram change
            Dim histNow = If(Single.IsNaN(macd.Histogram(last)), 0.0F, macd.Histogram(last))
            Dim histPrev = TechnicalIndicators.PreviousValid(macd.Histogram)

            ' Bollinger band width
            Dim bbMiddle = If(Single.IsNaN(bb.Middle(last)), CSng(close), bb.Middle(last))
            Dim bbUpper = If(Single.IsNaN(bb.Upper(last)), bbMiddle, bb.Upper(last))
            Dim bbLower = If(Single.IsNaN(bb.Lower(last)), bbMiddle, bb.Lower(last))
            Dim bbWidth = If(bbMiddle > 0, (CDbl(bbUpper) - CDbl(bbLower)) / CDbl(bbMiddle), 0.0)

            ' EMA comparison (normalised)
            Dim ema9Val = If(Single.IsNaN(ema9(last)), CSng(close), ema9(last))
            Dim ema21Val = If(Single.IsNaN(ema21(last)), CSng(close), ema21(last))
            Dim ema9vs21 = If(ema21Val <> 0, (CDbl(ema9Val) - CDbl(ema21Val)) / CDbl(ema21Val), 0.0)
            Dim priceVsEma21 = If(ema21Val <> 0, (close - CDbl(ema21Val)) / CDbl(ema21Val), 0.0)

            ' Wicks (ATR-normalised)
            Dim bodyHigh = Math.Max(open_, close)
            Dim bodyLow = Math.Min(open_, close)
            Dim upperWick = If(atr > 0, (high - bodyHigh) / CDbl(atr), 0.0)
            Dim lowerWick = If(atr > 0, (bodyLow - low) / CDbl(atr), 0.0)
            Dim barRange = If(atr > 0, (high - low) / CDbl(atr), 0.0)
            Dim bodyRatio = If(high - low > 0, Math.Abs(close - open_) / (high - low), 0.0)

            Return New BarFeatureVector With {
                .Label = label,
                .RSI14 = TechnicalIndicators.LastValid(rsi14),
                .RSI7 = TechnicalIndicators.LastValid(rsi7),
                .EMA9 = ema9Val,
                .EMA21 = ema21Val,
                .EMA9vsEMA21 = CSng(ema9vs21),
                .PriceVsEMA21 = CSng(priceVsEma21),
                .MACDLine = If(Single.IsNaN(macd.Line(last)), 0.0F, macd.Line(last)),
                .MACDSignal = If(Single.IsNaN(macd.Signal(last)), 0.0F, macd.Signal(last)),
                .MACDHistogram = histNow,
                .MACDHistogramChange = histNow - histPrev,
                .ATR14 = atr,
                .BBWidth = CSng(bbWidth),
                .PriceVsBBMiddle = If(bbMiddle <> 0, CSng((close - CDbl(bbMiddle)) / CDbl(bbMiddle)), 0.0F),
                .VolumeRatio = CSng(volRatio),
                .PriceVsVWAP = If(vwap(last) <> 0, CSng((close - CDbl(vwap(last))) / CDbl(vwap(last))), 0.0F),
                .BarRange = CSng(barRange),
                .BodyRatio = CSng(bodyRatio),
                .UpperWick = CSng(upperWick),
                .LowerWick = CSng(lowerWick),
                .IsBullish = If(close >= open_, 1.0F, 0.0F),
                .Return1Bar = CSng(ret1),
                .Return5Bar = CSng(ret5),
                .Return20Bar = CSng(ret20)
            }
        End Function

        ''' <summary>
        ''' Extract features AND label from a bar in the middle of the series.
        ''' Label = True if the close N bars later is higher than close now (buy signal).
        ''' Used for building training data.
        ''' </summary>
        Public Function ExtractWithLabel(allBars As IList(Of MarketBar),
                                         currentIndex As Integer,
                                         Optional lookAheadBars As Integer = 5,
                                         Optional minProfitTicks As Integer = 2) As BarFeatureVector

            If currentIndex < MinBarsRequired OrElse
               currentIndex + lookAheadBars >= allBars.Count Then
                Return Nothing
            End If

            Dim window = allBars.Take(currentIndex + 1).ToList()
            Dim currentClose = allBars(currentIndex).Close
            Dim futureClose = allBars(currentIndex + lookAheadBars).Close

            ' Simplified labelling: was this a profitable buy?
            ' In production, label based on actual tick value and commission
            Dim profitInPoints = CDbl(futureClose - currentClose)
            Dim isProfit = profitInPoints > 0

            Return Extract(window, label:=isProfit)
        End Function

    End Class

End Namespace
