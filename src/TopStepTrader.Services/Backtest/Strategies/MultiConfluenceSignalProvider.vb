Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Multi-Confluence Engine entry provider.
    ''' Mirrors the MultiConfluence signal block in BacktestEngine (lines 740–822).
    ''' ALL 9 conditions must align for a Long or Short signal:
    '''   1. Close above/below Ichimoku cloud + cloud twist pre-filter (bullish/bearish cloud)
    '''   2. Close above/below EMA21
    '''   2b. Close above/below EMA50 (big-picture trend anchor)
    '''   3. Tenkan above/below Kijun
    '''   4. Chikou (close 26 bars ago) above/below current close (momentum)
    '''   5. ADX ≥ MinAdxThreshold and +DI/−DI directional bias
    '''   6. MACD histogram positive/negative AND expanding (8/17/9 — intraday-tuned)
    '''   7. StochRSI %K below 0.7 (Long) or above 0.3 AND falling (Short)
    '''   8. Volume ≥ 1.2× 20-bar average (participation gate)
    '''
    ''' SL = min(1.5×ATR, Ichimoku cloud edge); TP = 2×R from actual SL distance.
    ''' Returns Nothing if any indicator is still in warm-up (NaN).
    ''' </summary>
    Public Class MultiConfluenceSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' ── Guard: all required series must be populated ─────────────────────
            If indicators.IchiSpanA Is Nothing OrElse indicators.IchiSpanB Is Nothing OrElse
               indicators.IchiTenkan Is Nothing OrElse indicators.IchiKijun Is Nothing OrElse
               indicators.Adx Is Nothing OrElse indicators.MacdHistogram Is Nothing OrElse
               indicators.StochRsiK Is Nothing OrElse indicators.Ema21 Is Nothing OrElse
               indicators.Ema50 Is Nothing OrElse
               indicators.Atr Is Nothing OrElse indicators.AllBars Is Nothing Then
                Return Nothing
            End If

            ' ── Indicator values at barIndex ─────────────────────────────────────
            Dim mcSpanA   = indicators.IchiSpanA(barIndex)
            Dim mcSpanB   = indicators.IchiSpanB(barIndex)
            Dim mcTenkan  = indicators.IchiTenkan(barIndex)
            Dim mcKijun   = indicators.IchiKijun(barIndex)
            Dim mcAdxVal  = indicators.Adx(barIndex)
            Dim mcPlusDI  = indicators.PlusDi(barIndex)
            Dim mcMinusDI = indicators.MinusDi(barIndex)
            Dim mcHistNow = If(Not Single.IsNaN(indicators.MacdHistogram(barIndex)),
                               indicators.MacdHistogram(barIndex), Single.NaN)
            Dim mcHistPrev = If(barIndex > 0 AndAlso Not Single.IsNaN(indicators.MacdHistogram(barIndex - 1)),
                                indicators.MacdHistogram(barIndex - 1), Single.NaN)
            Dim mcStochK  = If(Not Single.IsNaN(indicators.StochRsiK(barIndex)),
                               indicators.StochRsiK(barIndex), Single.NaN)
            Dim mcStochKPrev = If(barIndex > 0 AndAlso Not Single.IsNaN(indicators.StochRsiK(barIndex - 1)),
                                  indicators.StochRsiK(barIndex - 1), Single.NaN)
            Dim mcAtrVal  = If(Not Single.IsNaN(indicators.Atr(barIndex)),
                               indicators.Atr(barIndex), 0.0F)
            Dim mcEma21   = indicators.Ema21(barIndex)
            Dim mcEma50   = indicators.Ema50(barIndex)

            ' Skip if any indicator hasn't finished warming up
            If Single.IsNaN(mcSpanA) OrElse Single.IsNaN(mcSpanB) OrElse
               Single.IsNaN(mcTenkan) OrElse Single.IsNaN(mcKijun) OrElse
               Single.IsNaN(mcAdxVal) OrElse Single.IsNaN(mcHistNow) OrElse
               Single.IsNaN(mcHistPrev) OrElse Single.IsNaN(mcStochK) OrElse
               Single.IsNaN(mcEma21) OrElse Single.IsNaN(mcEma50) Then
                Return Nothing
            End If

            ' ── Derived values ───────────────────────────────────────────────────
            Dim mcLastClose  = bar.Close
            Dim mcCloudTop    = SafeD(Math.Max(mcSpanA, mcSpanB))
            Dim mcCloudBottom = SafeD(Math.Min(mcSpanA, mcSpanB))
            Dim mcBullishCloud = (mcSpanA >= mcSpanB)   ' SpanA ≥ SpanB = green/bullish cloud
            Dim mcLagIdx      = barIndex - 26
            Dim mcLagClose    = If(mcLagIdx >= 0, indicators.AllBars(mcLagIdx).Close, Decimal.MinValue)

            ' Historical cloud at lag position — guard for index < 0 and NaN.
            Dim mcLagSpanAOk = (mcLagIdx >= 0 AndAlso Not Single.IsNaN(indicators.IchiSpanA(mcLagIdx)))
            Dim mcLagSpanBOk = (mcLagIdx >= 0 AndAlso Not Single.IsNaN(indicators.IchiSpanB(mcLagIdx)))
            Dim mcLagCloudTop    = If(mcLagSpanAOk AndAlso mcLagSpanBOk,
                                      SafeD(Math.Max(indicators.IchiSpanA(mcLagIdx), indicators.IchiSpanB(mcLagIdx))), Decimal.MaxValue)
            Dim mcLagCloudBottom = If(mcLagSpanAOk AndAlso mcLagSpanBOk,
                                      SafeD(Math.Min(indicators.IchiSpanA(mcLagIdx), indicators.IchiSpanB(mcLagIdx))), Decimal.MinValue)

            ' BUG-09: chop-rejection thresholds mirroring live MultiConfluenceStrategy
            Const MinDiSpread As Single = 2.0F
            Dim mcChikouMinGap = mcLastClose * 0.001D   ' 0.1% of close
            Dim mcMacdMinMag = mcAtrVal * 0.05F         ' 5% of ATR(14)

            ' ── Long: all 8 conditions ────────────────────────────────────────────
            Dim lcl1 = (mcBullishCloud AndAlso mcLastClose > mcCloudTop)
            Dim lcl2 = (mcLastClose > SafeD(mcEma21))
            Dim lcl2b = (mcLastClose > SafeD(mcEma50))
            Dim lcl3 = (mcTenkan > mcKijun)
            Dim lcl4 = (mcLagIdx >= 0 AndAlso mcLastClose > mcLagClose + mcChikouMinGap AndAlso mcLastClose > mcLagCloudTop)
            Dim lcl5 = ((config.MinAdxThreshold <= 0.0F OrElse mcAdxVal >= config.MinAdxThreshold) AndAlso
                        Not Single.IsNaN(mcPlusDI) AndAlso Not Single.IsNaN(mcMinusDI) AndAlso
                        mcPlusDI - mcMinusDI >= MinDiSpread)
            Dim lcl6 = (mcHistNow >= mcMacdMinMag AndAlso mcHistNow > mcHistPrev)
            Dim lcl7 = (mcStochK < 0.7F)

            ' ── Volume gate: current bar volume ≥ 1.2× 20-bar average ───────────
            ' When VolMa20 is not populated (e.g. in unit tests) the gate is skipped.
            Dim mcVolMa = If(indicators.VolMa20 IsNot Nothing AndAlso Not Single.IsNaN(indicators.VolMa20(barIndex)),
                             CDec(indicators.VolMa20(barIndex)), 0D)
            Dim mcCurVol = indicators.AllBars(barIndex).Volume
            ' STRAT-21: fail-closed when volume data is missing (volMa = 0) — matches live behaviour.
            ' Previously: fail-open (gate passed when VolMa20 unavailable), causing live-vs-backtest divergence.
            Dim lcl8 = (mcVolMa > 0D AndAlso mcCurVol >= mcVolMa * 1.2D)   ' 8. Volume gate (fail-closed: no data = no trade)
            Dim scl8 = lcl8   ' same gate for shorts

            ' ── Short: all 9 conditions ───────────────────────────────────────────
            Dim scl1 = (Not mcBullishCloud AndAlso mcLastClose < mcCloudBottom)
            Dim scl2 = (mcLastClose < SafeD(mcEma21))
            Dim scl2b = (mcLastClose < SafeD(mcEma50))
            Dim scl3 = (mcTenkan < mcKijun)
            Dim scl4 = (mcLagIdx >= 0 AndAlso mcLastClose < mcLagClose - mcChikouMinGap AndAlso mcLastClose < mcLagCloudBottom)
            Dim scl5 = ((config.MinAdxThreshold <= 0.0F OrElse mcAdxVal >= config.MinAdxThreshold) AndAlso
                        Not Single.IsNaN(mcPlusDI) AndAlso Not Single.IsNaN(mcMinusDI) AndAlso
                        mcMinusDI - mcPlusDI >= MinDiSpread)
            Dim scl6 = (mcHistNow <= -mcMacdMinMag AndAlso mcHistNow < mcHistPrev)
            Dim scl7 = (mcStochK > 0.3F AndAlso Not Single.IsNaN(mcStochKPrev) AndAlso mcStochK < mcStochKPrev) ' K not oversold AND falling

            ' ── Determine side and compute SL/TP deltas ──────────────────────────
            Dim mcSide As String = Nothing
            Dim mcSlCand As Decimal = 0D
            Dim mcTpCand As Decimal = 0D

            If lcl1 AndAlso lcl2 AndAlso lcl2b AndAlso lcl3 AndAlso lcl4 AndAlso lcl5 AndAlso lcl6 AndAlso lcl7 AndAlso lcl8 Then
                mcSide = "Buy"
                ' STRAT-20: Intentional tight-SL selection — picks the HIGHER of cloud-bottom or 1.5×ATR floor
                ' (i.e. the SL closest to entry).  Cloud floor = ichimoku invalidation point; 1.5×ATR floor
                ' = maximum acceptable risk distance.  Using the tighter of the two limits max loss per bar.
                ' If you want the safer/wider stop, swap to: If(mcCloudBottom < mcAtrSlLevel, mcCloudBottom, mcAtrSlLevel)
                Dim mcAtrSlLevel = mcLastClose - SafeD(mcAtrVal * 1.5F)
                mcSlCand = If(mcCloudBottom > mcAtrSlLevel, mcCloudBottom, mcAtrSlLevel)
                mcTpCand = mcLastClose + (mcLastClose - mcSlCand) * 2D

            ElseIf scl1 AndAlso scl2 AndAlso scl2b AndAlso scl3 AndAlso scl4 AndAlso scl5 AndAlso scl6 AndAlso scl7 AndAlso scl8 Then
                mcSide = "Sell"
                ' STRAT-20: Intentional tight-SL selection — picks the LOWER of cloud-top or 1.5×ATR ceiling
                ' (i.e. the SL closest to entry). See Buy-side comment above for rationale.
                ' If you want the safer/wider stop, swap to: If(mcCloudTop > mcAtrSlLevel, mcCloudTop, mcAtrSlLevel)
                Dim mcAtrSlLevel = mcLastClose + SafeD(mcAtrVal * 1.5F)
                mcSlCand = If(mcCloudTop < mcAtrSlLevel, mcCloudTop, mcAtrSlLevel)
                mcTpCand = mcLastClose - (mcSlCand - mcLastClose) * 2D
            End If

            ' STRAT-16: 8/9 partial-conviction — one lagging condition missing.
            ' Hard-gate conditions (cloud direction, Chikou-vs-cloud, volume, short StochRSI gate)
            ' must still pass — a partial signal is not valid when these fundamental filters fail.
            Dim mcIsPartial = False
            If mcSide Is Nothing Then
                Dim longHits = {lcl1, lcl2, lcl2b, lcl3, lcl4, lcl5, lcl6, lcl7, lcl8}.Count(Function(c) c)
                Dim shortHits = {scl1, scl2, scl2b, scl3, scl4, scl5, scl6, scl7, scl8}.Count(Function(c) c)
                If longHits = 8 AndAlso lcl1 AndAlso lcl4 AndAlso lcl8 Then
                    mcSide = "Buy"
                    mcIsPartial = True
                    Dim mcAtrSlLevel = mcLastClose - SafeD(mcAtrVal * 1.5F)
                    mcSlCand = If(mcCloudBottom > mcAtrSlLevel, mcCloudBottom, mcAtrSlLevel)
                    mcTpCand = mcLastClose + (mcLastClose - mcSlCand) * 2D
                ElseIf shortHits = 8 AndAlso scl1 AndAlso scl4 AndAlso scl7 AndAlso scl8 Then
                    mcSide = "Sell"
                    mcIsPartial = True
                    Dim mcAtrSlLevel2 = mcLastClose + SafeD(mcAtrVal * 1.5F)
                    mcSlCand = If(mcCloudTop < mcAtrSlLevel2, mcCloudTop, mcAtrSlLevel2)
                    mcTpCand = mcLastClose - (mcSlCand - mcLastClose) * 2D
                End If
            End If

            If mcSide Is Nothing OrElse mcSlCand = 0D Then Return Nothing

            ' ── STRAT-19: Graduated confidence ────────────────────────────────────
            ' Weight: ADX 35%, DI-spread 25%, MACD magnitude 25%, StochRSI distance 15%
            Dim isLongSignal    = (mcSide = "Buy")
            Dim adxThresh       = If(config.MinAdxThreshold > 0.0F, config.MinAdxThreshold, 20.0F)
            Dim adxStrength     = CSng(Math.Min(1.0F, 0.5F + (mcAdxVal - adxThresh) / 60.0F))
            Dim diSpread        = Math.Abs(mcPlusDI - mcMinusDI)
            Dim diStrength      = CSng(Math.Min(1.0F, (diSpread - 2.0F) / 20.0F + 0.5F))
            Dim macdMag         = Math.Abs(mcHistNow)
            Dim macdNorm        = mcAtrVal * 0.5F + 0.001F   ' normalise by ½ ATR; avoid ÷0
            Dim macdStrength    = CSng(Math.Min(1.0F, macdMag / macdNorm))
            Dim stochDist       = If(isLongSignal, 0.7F - mcStochK, mcStochK - 0.3F)
            Dim stochStrength   = CSng(Math.Max(0.0F, Math.Min(1.0F, stochDist / 0.7F)))
            Dim mcConfidence    = CSng(adxStrength * 0.35F + diStrength * 0.25F +
                                       macdStrength * 0.25F + stochStrength * 0.15F)
            mcConfidence = CSng(Math.Max(0.0F, Math.Min(1.0F, mcConfidence)))

            ' StopDelta/TpDelta are relative to signal close; fill block re-anchors to nextBar.Open.
            ' STRAT-26: clamp StopDelta to broker minimum so backtest SLs cannot be tighter than
            ' what the exchange would accept live.  Set config.MinStopDollars from
            ' FavouriteContract.PxMinStopDollars for the instrument under test.
            Dim rawStopDelta = Math.Abs(mcLastClose - mcSlCand)
            Dim clampedStopDelta = If(config.MinStopDollars > 0D AndAlso rawStopDelta < config.MinStopDollars,
                                      config.MinStopDollars, rawStopDelta)
            ' Scale TP proportionally when SL is widened so R:R is preserved
            Dim clampedTpDelta = If(rawStopDelta > 0D AndAlso clampedStopDelta > rawStopDelta,
                                    Math.Abs(mcTpCand - mcLastClose) * (clampedStopDelta / rawStopDelta),
                                    Math.Abs(mcTpCand - mcLastClose))
            Return New SignalResult With {
                .Side            = mcSide,
                .Confidence      = mcConfidence,
                .IsLong          = (mcSide = "Buy"),
                .StopDelta       = clampedStopDelta,
                .TpDelta         = clampedTpDelta,
                .IsPartialSignal = mcIsPartial
            }
        End Function

    End Class

End Namespace
