Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Trading

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Multi-Confluence backtest entry provider.
    ''' Slices indicator arrays from <see cref="StrategyIndicators"/> at the current bar index,
    ''' builds a <see cref="MultiConfluenceInputs"/> struct, and delegates to the single shared
    ''' <see cref="MultiConfluenceEvaluator"/> (ARCH-04 — no duplicated condition logic).
    ''' </summary>
    Public Class MultiConfluenceSignalProvider
        Implements IStrategySignalProvider

        Private Shared ReadOnly s_evaluator As New MultiConfluenceEvaluator()

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' -- Guard: all required series must be populated ---------------------
            If indicators.IchiSpanA Is Nothing OrElse indicators.IchiSpanB Is Nothing OrElse
               indicators.IchiTenkan Is Nothing OrElse indicators.IchiKijun Is Nothing OrElse
               indicators.Adx Is Nothing OrElse indicators.MacdHistogram Is Nothing OrElse
               indicators.StochRsiK Is Nothing OrElse indicators.Ema21 Is Nothing OrElse
               indicators.Ema50 Is Nothing OrElse
               indicators.Atr Is Nothing OrElse indicators.AllBars Is Nothing Then
                Return Nothing
            End If

            ' -- Build MultiConfluenceConfig from BacktestConfiguration -----------
            Dim mcCfg As New Core.Settings.MultiConfluenceConfig With {
                .AdxThreshold      = If(config.MinAdxThreshold > 0.0F, config.MinAdxThreshold, 20.0F),
                .VolumeGateEnabled = config.McVolumeGateEnabled
            }

            ' -- Slice indicator scalars at barIndex ------------------------------
            Dim lagIdx = barIndex - mcCfg.IchimokuDisplacement
            Dim mcVolMa    = If(indicators.VolMa20 IsNot Nothing AndAlso Not Single.IsNaN(indicators.VolMa20(barIndex)),
                                CDec(indicators.VolMa20(barIndex)), 0D)
            Dim mcCurVol   = indicators.AllBars(barIndex).Volume

            Dim inputs As New MultiConfluenceInputs With {
                .SpanA         = indicators.IchiSpanA(barIndex),
                .SpanB         = indicators.IchiSpanB(barIndex),
                .Tenkan        = indicators.IchiTenkan(barIndex),
                .Kijun         = indicators.IchiKijun(barIndex),
                .LagClose      = If(lagIdx >= 0, indicators.AllBars(lagIdx).Close, Decimal.MinValue),
                .LagSpanA      = If(lagIdx >= 0, indicators.IchiSpanA(lagIdx), Single.NaN),
                .LagSpanB      = If(lagIdx >= 0, indicators.IchiSpanB(lagIdx), Single.NaN),
                .Ema21         = indicators.Ema21(barIndex),
                .Ema50         = indicators.Ema50(barIndex),
                .Adx           = indicators.Adx(barIndex),
                .PlusDI        = indicators.PlusDi(barIndex),
                .MinusDI       = indicators.MinusDi(barIndex),
                .MacdHistNow   = If(Not Single.IsNaN(indicators.MacdHistogram(barIndex)),
                                    indicators.MacdHistogram(barIndex), Single.NaN),
                .MacdHistPrev  = If(barIndex > 0 AndAlso Not Single.IsNaN(indicators.MacdHistogram(barIndex - 1)),
                                    indicators.MacdHistogram(barIndex - 1), Single.NaN),
                .StochK        = If(Not Single.IsNaN(indicators.StochRsiK(barIndex)),
                                    indicators.StochRsiK(barIndex), Single.NaN),
                .StochKPrev    = If(barIndex > 0 AndAlso Not Single.IsNaN(indicators.StochRsiK(barIndex - 1)),
                                    indicators.StochRsiK(barIndex - 1), Single.NaN),
                .Atr           = If(Not Single.IsNaN(indicators.Atr(barIndex)),
                                    indicators.Atr(barIndex), 0.0F),
                .LastClose     = bar.Close,
                .VolMa20       = mcVolMa,
                .CurrentVolume = mcCurVol,
                .Config        = mcCfg
            }

            ' -- Delegate to shared evaluator (ARCH-04) ---------------------------
            Dim mcResult = s_evaluator.Evaluate(inputs)

            If mcResult.Side Is Nothing Then Return Nothing

            ' -- Compute SL/TP candidates matching original backtest logic ---------
            Dim mcAtrVal   = CSng(inputs.Atr)
            Dim mcLastClose = bar.Close
            Dim cloudTop    = mcResult.Cloud1
            Dim cloudBottom = mcResult.Cloud2

            Dim mcSide   As String = If(mcResult.Side = Core.Enums.OrderSide.Buy, "Buy", "Sell")
            Dim mcSlCand As Decimal = 0D
            Dim mcTpCand As Decimal = 0D

            If mcSide = "Buy" Then
                Dim mcAtrSlLevel = mcLastClose - SafeD(mcAtrVal * 1.5F)
                mcSlCand = If(cloudBottom > mcAtrSlLevel, cloudBottom, mcAtrSlLevel)
                mcTpCand = mcLastClose + (mcLastClose - mcSlCand) * 2D
            Else
                Dim mcAtrSlLevel = mcLastClose + SafeD(mcAtrVal * 1.5F)
                mcSlCand = If(cloudTop < mcAtrSlLevel, cloudTop, mcAtrSlLevel)
                mcTpCand = mcLastClose - (mcSlCand - mcLastClose) * 2D
            End If

            ' -- STRAT-26: clamp StopDelta to broker minimum -----------------------
            Dim rawStopDelta     = Math.Abs(mcLastClose - mcSlCand)
            Dim clampedStopDelta = If(config.MinStopDollars > 0D AndAlso rawStopDelta < config.MinStopDollars,
                                      config.MinStopDollars, rawStopDelta)
            Dim clampedTpDelta   = If(rawStopDelta > 0D AndAlso clampedStopDelta > rawStopDelta,
                                      Math.Abs(mcTpCand - mcLastClose) * (clampedStopDelta / rawStopDelta),
                                      Math.Abs(mcTpCand - mcLastClose))

            Return New SignalResult With {
                .Side            = mcSide,
                .Confidence      = mcResult.Confidence,
                .IsLong          = (mcSide = "Buy"),
                .StopDelta       = clampedStopDelta,
                .TpDelta         = clampedTpDelta,
                .IsPartialSignal = mcResult.IsPartialSignal
            }
        End Function

    End Class

End Namespace
