Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest.Strategies

    ''' <summary>
    ''' BB Squeeze Scalper entry provider.
    ''' Mirrors the BbSqueezeScalper block in BacktestEngine (lines 1275–1350).
    '''
    ''' Two modes, selected by whether a Bollinger Band squeeze is active:
    '''
    ''' Mode A — Squeeze Breakout (BBW &lt; SMA(BBW) for ≥ 3 consecutive bars):
    '''   Long  when close &gt; upper band × 1.0025 AND EMA5 rising AND RSI7 ≥ 60.
    '''   Short when close &lt; lower band × 0.9975 AND EMA5 falling AND RSI7 ≤ 40.
    '''   Confidence = 0.80.
    '''
    ''' Mode B — Band Bounce (no squeeze):
    '''   Long  when %B ≤ −0.1 AND RSI7 &lt; 25 AND lower wick ≥ 60% of bar range.
    '''   Short when %B ≥  1.1 AND RSI7 &gt; 75 AND upper wick ≥ 60% of bar range.
    '''   Confidence = 0.70.
    '''
    ''' Indicators expected in <see cref="StrategyIndicators"/>:
    '''   BbUpper/BbLower = BB(12, 2.0);  BbWidth = BBW(12, 2.0);  BbPctB = %B(12, 2.0)
    '''   Rsi             = RSI(7)
    '''   Ema5            = EMA(5)
    '''   BbwSma          = SMA(BbWidth, 20)
    '''   Atr             = ATR(10)  [used only when UseAtrMode = True]
    ''' </summary>
    Public Class BbSqueezeSignalProvider
        Implements IStrategySignalProvider

        Private Shared Function SafeD(v As Single) As Decimal
            Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
        End Function

        Public Function Evaluate(bar As MarketBar,
                                 indicators As StrategyIndicators,
                                 config As BacktestConfiguration,
                                 barIndex As Integer) As SignalResult _
            Implements IStrategySignalProvider.Evaluate

            ' Guard: all required series must be populated
            If indicators.BbUpper Is Nothing OrElse indicators.BbLower Is Nothing OrElse
               indicators.BbPctB Is Nothing OrElse indicators.Rsi Is Nothing OrElse
               indicators.Ema5 Is Nothing OrElse indicators.BbWidth Is Nothing OrElse
               indicators.BbwSma Is Nothing Then Return Nothing

            If barIndex < 1 Then Return Nothing

            Dim bUpper   = indicators.BbUpper(barIndex)
            Dim bLower   = indicators.BbLower(barIndex)
            Dim bPctB    = indicators.BbPctB(barIndex)
            Dim bRsi7    = indicators.Rsi(barIndex)
            Dim bEma5Now = indicators.Ema5(barIndex)
            Dim bEma5Pre = indicators.Ema5(barIndex - 1)
            Dim bBbw     = indicators.BbWidth(barIndex)
            Dim bBbwSma  = indicators.BbwSma(barIndex)

            If Single.IsNaN(bUpper) OrElse Single.IsNaN(bLower) OrElse Single.IsNaN(bPctB) OrElse
               Single.IsNaN(bRsi7) OrElse Single.IsNaN(bEma5Now) OrElse Single.IsNaN(bEma5Pre) OrElse
               Single.IsNaN(bBbw) OrElse Single.IsNaN(bBbwSma) Then Return Nothing

            ' Count consecutive squeeze bars (BBW < SMA(BBW)) — up to last 10 bars
            Dim sqzCount As Integer = 0
            For si = barIndex To Math.Max(0, barIndex - 9) Step -1
                If si < indicators.BbWidth.Length AndAlso si < indicators.BbwSma.Length AndAlso
                   Not Single.IsNaN(indicators.BbWidth(si)) AndAlso Not Single.IsNaN(indicators.BbwSma(si)) AndAlso
                   indicators.BbwSma(si) > 0 AndAlso indicators.BbWidth(si) < indicators.BbwSma(si) Then
                    sqzCount += 1
                Else
                    Exit For
                End If
            Next
            Dim sqzActive = (sqzCount >= 3)

            Dim bbSide As String = Nothing
            Dim bbEma5Rising = (bEma5Now > bEma5Pre)

            If sqzActive Then
                ' Mode A: Squeeze Breakout
                If bar.Close > SafeD(bUpper) * 1.0025D AndAlso bbEma5Rising AndAlso bRsi7 >= 60.0F Then
                    bbSide = "Buy"
                ElseIf bar.Close < SafeD(bLower) * 0.9975D AndAlso Not bbEma5Rising AndAlso bRsi7 <= 40.0F Then
                    bbSide = "Sell"
                End If
            Else
                ' Mode B: Band Bounce (mean-reversion)
                Dim bbBarRange = bar.High - bar.Low
                If bbBarRange > 0D Then
                    Dim bbLwPct = CDbl(Math.Min(bar.Open, bar.Close) - bar.Low) / CDbl(bbBarRange)
                    Dim bbUwPct = CDbl(bar.High - Math.Max(bar.Open, bar.Close)) / CDbl(bbBarRange)
                    If bPctB <= -0.1F AndAlso bRsi7 < 25.0F AndAlso bbLwPct >= 0.6 Then
                        bbSide = "Buy"
                    ElseIf bPctB >= 1.1F AndAlso bRsi7 > 75.0F AndAlso bbUwPct >= 0.6 Then
                        bbSide = "Sell"
                    End If
                End If
            End If

            ' Confidence filter before building result
            Dim bbConf = If(sqzActive, 0.8F, 0.7F)
            If bbSide IsNot Nothing AndAlso bbConf < config.MinSignalConfidence Then bbSide = Nothing
            If bbSide Is Nothing Then Return Nothing

            Dim result As New SignalResult With {
                .Side       = bbSide,
                .Confidence = bbConf,
                .IsLong     = (bbSide = "Buy")
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
