Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Multi-Confluence Engine entry provider.
    ''' Mirrors the MultiConfluence signal block in BacktestEngine (lines 740–822).
    ''' ALL 7 conditions must align for a Long or Short signal:
    '''   1. Close above/below Ichimoku cloud
    '''   2. Close above/below EMA21
    '''   3. Tenkan above/below Kijun
    '''   4. Chikou (close 26 bars ago) above/below current close (momentum)
    '''   5. ADX ≥ MinAdxThreshold and +DI/−DI directional bias
    '''   6. MACD histogram positive/negative AND expanding
    '''   7. StochRSI %K below 0.8 (Long) or above 0.2 (Short)
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
            Dim mcAtrVal  = If(Not Single.IsNaN(indicators.Atr(barIndex)),
                               indicators.Atr(barIndex), 0.0F)
            Dim mcEma21   = indicators.Ema21(barIndex)

            ' Skip if any indicator hasn't finished warming up
            If Single.IsNaN(mcSpanA) OrElse Single.IsNaN(mcSpanB) OrElse
               Single.IsNaN(mcTenkan) OrElse Single.IsNaN(mcKijun) OrElse
               Single.IsNaN(mcAdxVal) OrElse Single.IsNaN(mcHistNow) OrElse
               Single.IsNaN(mcHistPrev) OrElse Single.IsNaN(mcStochK) OrElse
               Single.IsNaN(mcEma21) Then
                Return Nothing
            End If

            ' ── Derived values ───────────────────────────────────────────────────
            Dim mcLastClose  = bar.Close
            Dim mcCloudTop    = SafeD(Math.Max(mcSpanA, mcSpanB))
            Dim mcCloudBottom = SafeD(Math.Min(mcSpanA, mcSpanB))
            Dim mcLagIdx      = barIndex - 26
            Dim mcLagClose    = If(mcLagIdx >= 0, indicators.AllBars(mcLagIdx).Close, Decimal.MinValue)

            ' ── Long: all 7 conditions ────────────────────────────────────────────
            Dim lcl1 = (mcLastClose > mcCloudTop)
            Dim lcl2 = (mcLastClose > SafeD(mcEma21))
            Dim lcl3 = (mcTenkan > mcKijun)
            Dim lcl4 = (mcLagIdx >= 0 AndAlso mcLastClose > mcLagClose)
            Dim lcl5 = ((config.MinAdxThreshold <= 0.0F OrElse mcAdxVal >= config.MinAdxThreshold) AndAlso
                        Not Single.IsNaN(mcPlusDI) AndAlso Not Single.IsNaN(mcMinusDI) AndAlso
                        mcPlusDI > mcMinusDI)
            Dim lcl6 = (mcHistNow > 0 AndAlso mcHistNow > mcHistPrev)
            Dim lcl7 = (mcStochK < 0.8F)

            ' ── Short: all 7 conditions ───────────────────────────────────────────
            Dim scl1 = (mcLastClose < mcCloudBottom)
            Dim scl2 = (mcLastClose < SafeD(mcEma21))
            Dim scl3 = (mcTenkan < mcKijun)
            Dim scl4 = (mcLagIdx >= 0 AndAlso mcLastClose < mcLagClose)
            Dim scl5 = ((config.MinAdxThreshold <= 0.0F OrElse mcAdxVal >= config.MinAdxThreshold) AndAlso
                        Not Single.IsNaN(mcPlusDI) AndAlso Not Single.IsNaN(mcMinusDI) AndAlso
                        mcMinusDI > mcPlusDI)
            Dim scl6 = (mcHistNow < 0 AndAlso mcHistNow < mcHistPrev)
            Dim scl7 = (mcStochK > 0.2F)

            ' ── Determine side and compute SL/TP deltas ──────────────────────────
            Dim mcSide As String = Nothing
            Dim mcSlCand As Decimal = 0D
            Dim mcTpCand As Decimal = 0D

            If lcl1 AndAlso lcl2 AndAlso lcl3 AndAlso lcl4 AndAlso lcl5 AndAlso lcl6 AndAlso lcl7 Then
                mcSide = "Buy"
                ' SL = min(1.5×ATR, cloud bottom); TP = 2:1 R:R from actual SL distance
                Dim mcAtrSlLevel = mcLastClose - SafeD(mcAtrVal * 1.5F)
                mcSlCand = If(mcCloudBottom > mcAtrSlLevel, mcCloudBottom, mcAtrSlLevel)
                mcTpCand = mcLastClose + (mcLastClose - mcSlCand) * 2D

            ElseIf scl1 AndAlso scl2 AndAlso scl3 AndAlso scl4 AndAlso scl5 AndAlso scl6 AndAlso scl7 Then
                mcSide = "Sell"
                ' SL = min(1.5×ATR, cloud top); TP = 2:1 R:R from actual SL distance
                Dim mcAtrSlLevel = mcLastClose + SafeD(mcAtrVal * 1.5F)
                mcSlCand = If(mcCloudTop < mcAtrSlLevel, mcCloudTop, mcAtrSlLevel)
                mcTpCand = mcLastClose - (mcSlCand - mcLastClose) * 2D
            End If

            If mcSide Is Nothing OrElse mcSlCand = 0D Then Return Nothing

            ' StopDelta/TpDelta are relative to signal close; fill block re-anchors to nextBar.Open.
            Return New SignalResult With {
                .Side       = mcSide,
                .Confidence = 1.0F,
                .IsLong     = (mcSide = "Buy"),
                .StopDelta  = Math.Abs(mcLastClose - mcSlCand),
                .TpDelta    = Math.Abs(mcTpCand - mcLastClose)
            }
        End Function

    End Class

End Namespace
