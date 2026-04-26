Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' VWAP Mean Reversion entry provider — institutional midday strategy.
    '''
    ''' Uses a session-anchored VWAP with rolling standard-deviation bands to identify
    ''' price extremes where institutional order flow is likely to push price back to its
    ''' average cost.  Only fires during the 10am–2pm ET window when momentum has faded
    ''' and the market is anchored to institutional VWAP.
    '''
    ''' Entry:
    '''   Long  when close ≤ VWAP − 1.5 × stdDev (oversold vs institutional average).
    '''   Short when close ≥ VWAP + 1.5 × stdDev (overbought vs institutional average).
    '''
    ''' Exit anchor: VWAP itself (mean-reversion target, stored in IndicatorExitLevel).
    ''' Stop-loss:   Beyond the 2.0 × stdDev band in the adverse direction.
    ''' Time filter: 10:00–14:00 ET (14:00–18:00 UTC); bars outside this window return Nothing.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   Vwap    = session-anchored VWAP series
    '''   BbUpper = VWAP + 2.0 SD band  (used for short SL)
    '''   BbLower = VWAP − 2.0 SD band  (used for long  SL)
    '''   BbMiddle= VWAP + 1.5 SD upper entry threshold
    '''   BbWidth = VWAP − 1.5 SD lower entry threshold  (repurposed field)
    '''   Atr     = ATR(14) for fallback SL sizing
    ''' </summary>
    Public Class VwapMeanReversionSignalProvider
        Implements IStrategySignalProvider

        ' 10:00 ET = 15:00 UTC (EST, no DST adjustment in backtest)
        Private Shared ReadOnly WindowStartUtc As New TimeOnly(15, 0)
        ' 14:00 ET = 19:00 UTC
        Private Shared ReadOnly WindowEndUtc As New TimeOnly(19, 0)

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' ── Time-of-day gate: 10am–2pm ET only ───────────────────────────
            Dim barTime = TimeOnly.FromTimeSpan(bar.Timestamp.TimeOfDay)
            If barTime < WindowStartUtc OrElse barTime >= WindowEndUtc Then Return Nothing

            ' ── Guard: all required indicator series must be present ──────────
            If indicators.Vwap Is Nothing OrElse
               indicators.BbUpper Is Nothing OrElse
               indicators.BbLower Is Nothing OrElse
               indicators.BbMiddle Is Nothing OrElse
               indicators.BbWidth Is Nothing Then Return Nothing

            Dim vwapVal    = indicators.Vwap(barIndex)
            Dim upper2Sd   = indicators.BbUpper(barIndex)   ' VWAP + 2.0 SD
            Dim lower2Sd   = indicators.BbLower(barIndex)   ' VWAP − 2.0 SD
            Dim upper15Sd  = indicators.BbMiddle(barIndex)  ' VWAP + 1.5 SD entry threshold
            Dim lower15Sd  = indicators.BbWidth(barIndex)   ' VWAP − 1.5 SD entry threshold

            If Single.IsNaN(vwapVal) OrElse Single.IsNaN(upper2Sd) OrElse Single.IsNaN(lower2Sd) OrElse
               Single.IsNaN(upper15Sd) OrElse Single.IsNaN(lower15Sd) Then Return Nothing

            Dim close     = bar.Close
            Dim vwapDec   = SafeD(vwapVal)
            Dim up2Dec    = SafeD(upper2Sd)
            Dim lo2Dec    = SafeD(lower2Sd)
            Dim up15Dec   = SafeD(upper15Sd)
            Dim lo15Dec   = SafeD(lower15Sd)

            If vwapDec = 0D OrElse up2Dec = 0D OrElse lo2Dec = 0D Then Return Nothing

            ' ── Entry signals ─────────────────────────────────────────────────
            Dim mrSide As String = Nothing
            Dim slPrice As Decimal = 0D
            If close <= lo15Dec Then
                ' Oversold: long reversion to VWAP; SL below the 2.0 SD lower band
                mrSide  = "Buy"
                slPrice = lo2Dec
            ElseIf close >= up15Dec Then
                ' Overbought: short reversion to VWAP; SL above the 2.0 SD upper band
                mrSide  = "Sell"
                slPrice = up2Dec
            End If

            If mrSide Is Nothing Then Return Nothing

            ' ── Size stop and TP deltas ───────────────────────────────────────
            Dim stopDelta As Decimal = Math.Abs(close - slPrice)
            Dim tpDelta   As Decimal = Math.Abs(close - vwapDec)   ' target = VWAP

            ' Clamp to broker minimum stop when configured
            If config.MinStopDollars > 0D AndAlso stopDelta < config.MinStopDollars Then
                stopDelta = config.MinStopDollars
            End If

            ' Fall back to ATR-based sizing if the band spread is degenerate
            If stopDelta = 0D AndAlso indicators.Atr IsNot Nothing Then
                Dim atrVal = indicators.Atr(barIndex)
                If Not Single.IsNaN(atrVal) AndAlso atrVal > 0F Then
                    stopDelta = CDec(atrVal) * 0.75D   ' tight: 0.75 ATR for mean reversion
                    tpDelta   = CDec(atrVal) * 1.0D
                End If
            End If

            If stopDelta = 0D Then Return Nothing

            Return New SignalResult With {
                .Side               = mrSide,
                .Confidence         = 0.75F,
                .IsLong             = (mrSide = "Buy"),
                .StopDelta          = stopDelta,
                .TpDelta            = tpDelta,
                .IndicatorExitLevel = vwapDec   ' mean-reversion exit at VWAP
            }
        End Function

    End Class

End Namespace
