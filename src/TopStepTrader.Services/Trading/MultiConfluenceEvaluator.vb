Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Single canonical implementation of the nine Multi-Confluence conditions (ARCH-04).
    ''' Consumes pre-computed scalar indicator values via <see cref="MultiConfluenceInputs"/>
    ''' and returns a <see cref="MultiConfluenceResult"/>.
    '''
    ''' The live path (<see cref="MultiConfluenceStrategy"/>) builds inputs from raw bar arrays.
    ''' </summary>
    Public NotInheritable Class MultiConfluenceEvaluator
        Implements IMultiConfluenceEvaluator

        Public Function Evaluate(inputs As MultiConfluenceInputs) As MultiConfluenceResult _
            Implements IMultiConfluenceEvaluator.Evaluate

            Dim result As New MultiConfluenceResult()
            Dim cfg = inputs.Config

            ' ── Guard: warm-up check ─────────────────────────────────────────────
            If Single.IsNaN(inputs.SpanA) OrElse Single.IsNaN(inputs.SpanB) OrElse
               Single.IsNaN(inputs.Tenkan) OrElse Single.IsNaN(inputs.Kijun) OrElse
               Single.IsNaN(inputs.Adx) OrElse Single.IsNaN(inputs.PlusDI) OrElse
               Single.IsNaN(inputs.MinusDI) OrElse Single.IsNaN(inputs.MacdHistNow) OrElse
               Single.IsNaN(inputs.MacdHistPrev) OrElse Single.IsNaN(inputs.StochK) OrElse
               Single.IsNaN(inputs.Ema21) OrElse Single.IsNaN(inputs.Ema50) Then
                result.StatusLine = "Warming up — one or more indicators not yet available"
                Return result
            End If

            ' ── Derived cloud values ─────────────────────────────────────────────
            Dim cloudTop    = CDec(Math.Max(inputs.SpanA, inputs.SpanB))
            Dim cloudBottom = CDec(Math.Min(inputs.SpanA, inputs.SpanB))
            Dim bullishCloud = (inputs.SpanA >= inputs.SpanB)

            ' ── Historical cloud at Chikou lag position ──────────────────────────
            Dim lagCloudTop    As Decimal = Decimal.MaxValue
            Dim lagCloudBottom As Decimal = Decimal.MinValue
            If Not Single.IsNaN(inputs.LagSpanA) AndAlso Not Single.IsNaN(inputs.LagSpanB) Then
                lagCloudTop    = CDec(Math.Max(inputs.LagSpanA, inputs.LagSpanB))
                lagCloudBottom = CDec(Math.Min(inputs.LagSpanA, inputs.LagSpanB))
            End If

            Dim lastClose = inputs.LastClose

            ' ── Derived thresholds ───────────────────────────────────────────────
            Dim minDiSpread      = cfg.MinDiSpread
            Dim chikouMinGap     = lastClose * CDec(cfg.ChikouMinGapFraction)
            Dim macdMinMag       = CSng(CDec(inputs.Atr) * CDec(cfg.MacdHistMinAtrFraction))
            Dim adxThresholdEff  = cfg.AdxThreshold
            Dim lagAvailable     = (inputs.LagClose <> Decimal.MinValue)

            ' ── ATR for result ───────────────────────────────────────────────────
            result.AtrValue = CDec(inputs.Atr)

            ' ── Long conditions ──────────────────────────────────────────────────
            Dim lc1  = (bullishCloud AndAlso lastClose > cloudTop)
            Dim lc2  = (lastClose > CDec(inputs.Ema21))
            Dim lc2b = (lastClose > CDec(inputs.Ema50))
            Dim lc3  = (inputs.Tenkan > inputs.Kijun)
            Dim lc4  = (lagAvailable AndAlso lastClose > inputs.LagClose + chikouMinGap AndAlso
                        lastClose > lagCloudTop)
            Dim lc5  = (inputs.Adx >= adxThresholdEff AndAlso
                        inputs.PlusDI - inputs.MinusDI >= minDiSpread)
            Dim lc6  = (inputs.MacdHistNow >= macdMinMag AndAlso
                        inputs.MacdHistNow > inputs.MacdHistPrev)
            Dim lc7  = (inputs.StochK < cfg.StochRsiOverbought)

            ' ── Volume gate ──────────────────────────────────────────────────────
            Dim lc8 As Boolean
            If cfg.VolumeGateEnabled Then
                lc8 = (inputs.VolMa20 > 0D AndAlso
                       inputs.CurrentVolume >= inputs.VolMa20 * CDec(cfg.VolumeMultiple))
            Else
                lc8 = True
            End If
            Dim sc8 = lc8

            ' ── Short conditions ─────────────────────────────────────────────────
            Dim sc1  = (Not bullishCloud AndAlso lastClose < cloudBottom)
            Dim sc2  = (lastClose < CDec(inputs.Ema21))
            Dim sc2b = (lastClose < CDec(inputs.Ema50))
            Dim sc3  = (inputs.Tenkan < inputs.Kijun)
            Dim sc4  = (lagAvailable AndAlso lastClose < inputs.LagClose - chikouMinGap AndAlso
                        lastClose < lagCloudBottom)
            Dim sc5  = (inputs.Adx >= adxThresholdEff AndAlso
                        inputs.MinusDI - inputs.PlusDI >= minDiSpread)
            Dim sc6  = (inputs.MacdHistNow <= -macdMinMag AndAlso
                        inputs.MacdHistNow < inputs.MacdHistPrev)
            Dim sc7  = (inputs.StochK > cfg.StochRsiOversold AndAlso
                        Not Single.IsNaN(inputs.StochKPrev) AndAlso
                        inputs.StochK < inputs.StochKPrev)

            ' ── Counts ───────────────────────────────────────────────────────────
            Dim longFlags  = {lc1, lc2, lc2b, lc3, lc4, lc5, lc6, lc7, lc8}
            Dim shortFlags = {sc1, sc2, sc2b, sc3, sc4, sc5, sc6, sc7, sc8}
            Dim longCount  = longFlags.Count(Function(c) c)
            Dim shortCount = shortFlags.Count(Function(c) c)

            ' ── Populate result snapshot ─────────────────────────────────────────
            result.BullScore  = CInt(longCount / 9 * 100)
            result.BearScore  = CInt(shortCount / 9 * 100)
            result.AdxValue   = inputs.Adx
            result.Cloud1     = cloudTop
            result.Cloud2     = cloudBottom
            result.Tenkan     = CDec(inputs.Tenkan)
            result.Kijun      = CDec(inputs.Kijun)
            result.Ema21      = CDec(inputs.Ema21)
            result.Ema50      = CDec(inputs.Ema50)
            result.PlusDI     = inputs.PlusDI
            result.MinusDI    = inputs.MinusDI
            result.MacdHist   = inputs.MacdHistNow
            result.MacdHistPrev = If(Single.IsNaN(inputs.MacdHistPrev), 0F, inputs.MacdHistPrev)
            result.StochRsiK  = If(Single.IsNaN(inputs.StochK), 0F, inputs.StochK)
            result.LongCount  = longCount
            result.ShortCount = shortCount

            ' ── Failed condition names (dominant direction) ───────────────────────
            Dim condLabels = {"Ichimoku", "EMA21", "EMA50", "TenkanKijun", "Chikou",
                              "ADX/DMI", "MACD", "StochRSI", "Volume"}
            Dim reportLong = (longCount >= shortCount)
            Dim evalFlags  = If(reportLong, longFlags, shortFlags)
            For i = 0 To condLabels.Length - 1
                If Not evalFlags(i) Then result.FailedConditions.Add(condLabels(i))
            Next

            ' ── Signal: all 9 conditions met ─────────────────────────────────────
            If longCount = 9 Then
                result.Side         = OrderSide.Buy
                result.CloudEdgeSl  = cloudBottom
            ElseIf shortCount = 9 Then
                result.Side         = OrderSide.Sell
                result.CloudEdgeSl  = cloudTop
            ElseIf longCount = 8 AndAlso lc1 AndAlso lc4 AndAlso lc8 Then
                result.Side         = OrderSide.Buy
                result.IsPartialSignal = True
                result.CloudEdgeSl  = cloudBottom
            ElseIf shortCount = 8 AndAlso sc1 AndAlso sc4 AndAlso sc7 AndAlso sc8 Then
                result.Side         = OrderSide.Sell
                result.IsPartialSignal = True
                result.CloudEdgeSl  = cloudTop
            End If

            ' ── STRAT-19: Graduated confidence ───────────────────────────────────
            If result.Side IsNot Nothing Then
                Dim isLong      = (result.Side = OrderSide.Buy)
                Dim confAdx     = CSng(Math.Min(1.0F, 0.5F + (inputs.Adx - adxThresholdEff) / 60.0F))
                Dim diSpreadVal = If(isLong, inputs.PlusDI - inputs.MinusDI,
                                             inputs.MinusDI - inputs.PlusDI)
                Dim confDi      = CSng(Math.Min(1.0F, (diSpreadVal - minDiSpread) / 20.0F + 0.5F))
                Dim macdMagVal  = Math.Abs(inputs.MacdHistNow)
                Dim macdNormVal = CSng(result.AtrValue) * 0.5F + 0.001F
                Dim confMacd    = CSng(Math.Min(1.0F, macdMagVal / macdNormVal))
                Dim stochDist   = If(isLong,
                                     cfg.StochRsiOverbought - inputs.StochK,
                                     inputs.StochK - cfg.StochRsiOversold)
                Dim confStoch   = CSng(Math.Max(0.0F, Math.Min(1.0F, stochDist / 0.7F)))
                result.Confidence = CSng(Math.Max(0.0F, Math.Min(1.0F,
                                    confAdx * 0.35F + confDi * 0.25F +
                                    confMacd * 0.25F + confStoch * 0.15F)))

                result.StatusLine =
                    $"Close={lastClose:F4} | Cloud=[{cloudBottom:F4}–{cloudTop:F4}] | " &
                    $"T={CDec(inputs.Tenkan):F4} K={CDec(inputs.Kijun):F4} | " &
                    $"EMA21={CDec(inputs.Ema21):F4} EMA50={CDec(inputs.Ema50):F4} | " &
                    $"ADX={inputs.Adx:F1} DI+={inputs.PlusDI:F1} DI-={inputs.MinusDI:F1} | " &
                    $"MACD-H={inputs.MacdHistNow:F4}(prev={inputs.MacdHistPrev:F4}) | " &
                    $"StochRSI={inputs.StochK:F3} | " &
                    $"VolGate={If(cfg.VolumeGateEnabled, lc8.ToString(), "SKIP(PX-no-vol)")} | " &
                    $"Long={longCount}/9 Short={shortCount}/9 | " &
                    $"Conf=adx{confAdx:F2}/di{confDi:F2}/macd{confMacd:F2}/stoch{confStoch:F2}"
            End If

            Return result
        End Function

    End Class

End Namespace
