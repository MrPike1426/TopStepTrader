Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' Naked Trader entry provider (4-vote consensus).
    ''' Mirrors the NakedTrader block in BacktestEngine (lines 1073–1167).
    '''
    ''' Four independent votes, each optional (only counted when magnitude threshold is met):
    '''   1. EMA(9) vs EMA(21) — 0.1% gap required
    '''   2. MACD(8,17,9) histogram or line — ≥ 0.001 magnitude
    '''   3. DMI(14) +DI vs −DI — ≥ 1.0 pt spread
    '''   4. VWAP — close 0.1% above/below
    '''
    ''' Signal fires with ADX gate:
    '''   High confidence (0.90 with volume, 0.60 without):
    '''     All votes aligned + total ≥ 3 + ADX ≥ 25
    '''   Medium confidence (0.60):
    '''     All but one vote aligned + total ≥ 3 + ADX ≥ MinAdxThreshold (default 20)
    '''
    ''' Both paths require ntConf ≥ MinSignalConfidence before firing.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   Ema9 / Ema21           = EMA(9) / EMA(21)
    '''   MacdHistogram / MacdLine = MACD(8, 17, 9) histogram and line
    '''   PlusDi / MinusDi / Adx  = DMI(14)
    '''   Vwap                   = cumulative VWAP
    '''   VolMa20                = SMA(20) of volume   [optional; skips vol gate if Nothing]
    '''   AllBars                = full bar list        [for volume at barIndex]
    '''   Atr                    = ATR(14) [used only when UseAtrMode = True]
    ''' </summary>
    Public Class NakedTraderSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Guard: minimum required series
            If indicators.Ema9 Is Nothing OrElse indicators.Ema21 Is Nothing OrElse
               indicators.MacdHistogram Is Nothing OrElse indicators.MacdLine Is Nothing OrElse
               indicators.PlusDi Is Nothing OrElse indicators.MinusDi Is Nothing OrElse
               indicators.Adx Is Nothing Then Return Nothing

            Dim nEma9  = indicators.Ema9(barIndex)
            Dim nEma21 = indicators.Ema21(barIndex)
            Dim nMacdH = indicators.MacdHistogram(barIndex)
            Dim nMacdL = indicators.MacdLine(barIndex)
            Dim nPdi   = indicators.PlusDi(barIndex)
            Dim nMdi   = indicators.MinusDi(barIndex)
            Dim nAdx   = indicators.Adx(barIndex)

            ' Required warm-up check
            If Single.IsNaN(nEma9) OrElse Single.IsNaN(nEma21) OrElse Single.IsNaN(nAdx) Then
                Return Nothing
            End If

            Dim ntUp As Integer = 0
            Dim ntDown As Integer = 0
            Dim ntTotal As Integer = 0

            ' Vote 1: EMA — 0.1% gap filter
            Const EmaGapPct As Single = 0.001F
            If nEma9 > nEma21 * (1.0F + EmaGapPct) Then
                ntTotal += 1 : ntUp += 1
            ElseIf nEma9 < nEma21 * (1.0F - EmaGapPct) Then
                ntTotal += 1 : ntDown += 1
            End If

            ' Vote 2: MACD — histogram preferred; line as fallback; ≥ 0.001 magnitude
            Const MacdMinMag As Single = 0.001F
            Dim macdVote As Single = Single.NaN
            If Not Single.IsNaN(nMacdH) AndAlso Math.Abs(nMacdH) >= MacdMinMag Then
                macdVote = nMacdH
            ElseIf Not Single.IsNaN(nMacdL) AndAlso Math.Abs(nMacdL) >= MacdMinMag Then
                macdVote = nMacdL
            End If
            If Not Single.IsNaN(macdVote) Then
                ntTotal += 1
                If macdVote > 0 Then ntUp += 1 Else ntDown += 1
            End If

            ' Vote 3: DMI — ≥ 1.0 pt spread
            Const DiMinSpread As Single = 1.0F
            If Not Single.IsNaN(nPdi) AndAlso Not Single.IsNaN(nMdi) Then
                If Math.Abs(nPdi - nMdi) >= DiMinSpread Then
                    ntTotal += 1
                    If nPdi > nMdi Then ntUp += 1 Else ntDown += 1
                End If
            End If

            ' Vote 4: VWAP — 0.1% gap
            If indicators.Vwap IsNot Nothing Then
                Dim nVwap = indicators.Vwap(barIndex)
                If Not Single.IsNaN(nVwap) Then
                    Const VwapGapPct As Single = 0.001F
                    Dim closeDbl = CDbl(bar.Close)
                    Dim vwapDbl  = CDbl(nVwap)
                    If closeDbl > vwapDbl * (1.0 + VwapGapPct) Then
                        ntTotal += 1 : ntUp += 1
                    ElseIf closeDbl < vwapDbl * (1.0 - VwapGapPct) Then
                        ntTotal += 1 : ntDown += 1
                    End If
                End If
            End If

            ' Direction, tie-break, ADX gate
            Dim ntAligned  = Math.Max(ntUp, ntDown)
            Dim ntIsBull   = (ntUp > ntDown)
            Dim ntIsTie    = (ntUp = ntDown)
            Dim ntConf     As Single = 0.0F
            Dim ntFireable = False
            Dim ntAdxGate  = CSng(If(config.MinAdxThreshold > 0, config.MinAdxThreshold, 20.0))

            If Not ntIsTie AndAlso nAdx >= ntAdxGate Then
                If nAdx >= 25.0F AndAlso ntAligned = ntTotal AndAlso ntTotal >= 3 Then
                    ' High confidence: all votes aligned + ADX ≥ 25
                    Dim volOk = False
                    If indicators.VolMa20 IsNot Nothing AndAlso indicators.AllBars IsNot Nothing AndAlso
                       barIndex < indicators.AllBars.Count Then
                        Dim volMaVal = indicators.VolMa20(barIndex)
                        If Not Single.IsNaN(volMaVal) Then
                            volOk = (indicators.AllBars(barIndex).Volume > SafeD(volMaVal))
                        End If
                    End If
                    ntConf = If(volOk, 0.9F, 0.6F)
                    ntFireable = True
                ElseIf ntAligned >= ntTotal - 1 AndAlso ntTotal >= 3 Then
                    ' Medium confidence: 3/4 votes + ADX ≥ ntAdxGate
                    ntConf = 0.6F
                    ntFireable = True
                End If
            End If

            If Not ntFireable OrElse ntConf < config.MinSignalConfidence Then Return Nothing

            Dim ntSide = If(ntIsBull, "Buy", "Sell")
            Dim result As New SignalResult With {
                .Side       = ntSide,
                .Confidence = ntConf,
                .IsLong     = ntIsBull
            }

            If config.UseAtrMode AndAlso indicators.Atr IsNot Nothing Then
                Dim atrVal = indicators.Atr(barIndex)
                If Not Single.IsNaN(atrVal) AndAlso atrVal > 0.0F Then
                    result.StopDelta = CDec(atrVal) * config.SlAtrMultiple
                    result.TpDelta   = CDec(atrVal) * config.TpAtrMultiple
                End If
            End If

            Return result
        End Function

    End Class

End Namespace
