Imports System.Text.RegularExpressions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Parses natural-language strategy descriptions into StrategyDefinition objects
    ''' using regular expressions. Also provides three pre-loaded strategy templates.
    ''' </summary>
    Public Class StrategyParserService

        ' ── Pre-loaded templates ──────────────────────────────────────────────────

        Public ReadOnly Property PreloadedStrategies As IReadOnlyList(Of StrategyDefinition)
            Get
                Return _preloaded
            End Get
        End Property

        Private ReadOnly _preloaded As New List(Of StrategyDefinition) From {
            New StrategyDefinition With {
                .Name = "Bollinger Band Breakout (Mean Reversion)",
                .TimeframeMinutes = 5,
                .DurationHours = 8,
                .Indicator = StrategyIndicatorType.BollingerBands,
                .IndicatorPeriod = 20,
                .IndicatorMultiplier = 2.0,
                .Condition = StrategyConditionType.FullCandleOutsideBands,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "Over the next 8 hours, monitor the charts and wait for a full " &
                                  "5-minute candle to be completely outside the Bollinger Band " &
                                  "(above or below). Place a BUY order if below the bands and a " &
                                  "SELL order if above."
            },
            New StrategyDefinition With {
                .Name = "RSI Reversal",
                .TimeframeMinutes = 5,
                .DurationHours = 4,
                .Indicator = StrategyIndicatorType.RSI,
                .IndicatorPeriod = 14,
                .Condition = StrategyConditionType.RSIOversold,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "Over the next 4 hours, use a 14-period RSI on 5-minute bars. " &
                                  "Place a BUY when RSI drops below 30 (oversold) and a SELL " &
                                  "when RSI rises above 70 (overbought)."
            },
            New StrategyDefinition With {
                .Name = "EMA Crossover",
                .TimeframeMinutes = 5,
                .DurationHours = 6,
                .Indicator = StrategyIndicatorType.EMA,
                .IndicatorPeriod = 9,
                .SecondaryPeriod = 21,
                .Condition = StrategyConditionType.EMACrossAbove,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "Over the next 6 hours, monitor 5-minute bars. When EMA 9 " &
                                  "crosses above EMA 21, place a BUY. When EMA 9 crosses below " &
                                  "EMA 21, place a SELL."
            },
            New StrategyDefinition With {
                .Name = "EMA Smush Zone",
                .TimeframeMinutes = 5,
                .DurationHours = 6.0,
                .Indicator = StrategyIndicatorType.EMA,
                .IndicatorPeriod = 12,
                .SecondaryPeriod = 50,
                .Condition = StrategyConditionType.EMACrossAbove,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "EMA Smush Zone: Price gets squeezed between EMA 12 and EMA 50. " &
                                  "Wait for EMA 12 to cross above EMA 50 to enter long (breakout up), " &
                                  "or EMA 12 to cross below EMA 50 to enter short (breakout down). " &
                                  "Monitor 5-minute bars over 6 hours. Target 50 ticks profit, 20 tick stop."
            },
            New StrategyDefinition With {
                .Name = "EMA Double Tap",
                .TimeframeMinutes = 5,
                .DurationHours = 4.0,
                .Indicator = StrategyIndicatorType.EMA,
                .IndicatorPeriod = 5,
                .SecondaryPeriod = 12,
                .Condition = StrategyConditionType.EMACrossAbove,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "EMA Double Tap: After a big directional move, price pulls back and " &
                                  "bounces between EMA 5 and EMA 12 — the double tap. " &
                                  "Enter long when EMA 5 crosses above EMA 12, short when it crosses below. " &
                                  "Monitor 5-minute bars over 4 hours. Target 30 ticks profit, 12 tick stop."
            },
            New StrategyDefinition With {
                .Name = "EMA Flipperoo",
                .TimeframeMinutes = 5,
                .DurationHours = 6.0,
                .Indicator = StrategyIndicatorType.EMA,
                .IndicatorPeriod = 9,
                .SecondaryPeriod = 20,
                .Condition = StrategyConditionType.EMACrossAbove,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "EMA Flipperoo: Watch for an EMA (9 or 20) that was acting as resistance " &
                                  "to flip and become support (or vice versa for shorts). " &
                                  "Enter long when EMA 9 crosses above EMA 20 and price backtests the level, " &
                                  "or short when EMA 9 crosses below EMA 20. " &
                                  "Monitor 5-minute bars over 6 hours. Target 40 ticks profit, 16 tick stop."
            },
            New StrategyDefinition With {
                .Name = "Double Bottom Reversal",
                .TimeframeMinutes = 5,
                .DurationHours = 4.0,
                .Indicator = StrategyIndicatorType.RSI,
                .IndicatorPeriod = 14,
                .Condition = StrategyConditionType.RSIOversold,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "Double Bottom Reversal: Look for a W-shaped price pattern at a key support level. " &
                                  "When RSI drops below 30 (oversold) a second time at the same support, " &
                                  "enter long expecting a reversal. Enter short when RSI rises above 70 " &
                                  "at the same resistance (double top). " &
                                  "Use 14-period RSI on 5-minute bars over 4 hours. Target 60 ticks profit, 20 tick stop."
            },
            New StrategyDefinition With {
                .Name = "Head & Shoulders Breakout",
                .TimeframeMinutes = 5,
                .DurationHours = 6.0,
                .Indicator = StrategyIndicatorType.BollingerBands,
                .IndicatorPeriod = 20,
                .IndicatorMultiplier = 2.0,
                .Condition = StrategyConditionType.CloseOutsideBands,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialTpAmount = 20D,
                .InitialSlAmount = 10D,
                .RawDescription = "Head & Shoulders Breakout: Identify a Head & Shoulders top pattern (or inverse H&S bottom). " &
                                  "When price closes below the Bollinger lower band (neckline break on H&S top), go short. " &
                                  "When price closes above the Bollinger upper band (neckline break on inverse H&S), go long. " &
                                  "Use 20-period Bollinger Bands on 5-minute bars over 6 hours. Target 60 ticks profit, 25 tick stop."
            },
            New StrategyDefinition With {
                .Name = "Multi-Confluence Engine",
                .TimeframeMinutes = 15,
                .DurationHours = 8.0,
                .Indicator = StrategyIndicatorType.MultiConfluence,
                .IndicatorPeriod = 78,
                .Condition = StrategyConditionType.MultiConfluence,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .RawDescription = "Multi-Confluence Engine: Combines Ichimoku Cloud (9/26/52), EMA 21, EMA 50, " &
                                  "MACD(12/26/9), Stochastic RSI(14), and DMI/ADX(14) on 15-minute commodity bars. " &
                                  "Long when: price above cloud AND EMA21, Tenkan>Kijun, Chikou above price 26 bars ago, " &
                                  "ADX>25 with DI+>DI-, MACD histogram positive and rising, StochRSI<0.8. " &
                                  "Short when all seven conditions are mirrored bearishly. " &
                                  "SL = min(1.5×ATR, Ichimoku cloud edge). TP = 2:1 reward-to-risk. " &
                                  "Minimum 80 bars required for full Ichimoku warm-up."
            }
        }

        ' ── Natural-language parser ───────────────────────────────────────────────

        ''' <summary>
        ''' Parse a free-text strategy description into a StrategyDefinition.
        ''' Returns Nothing if no recognisable indicator is found.
        ''' </summary>
        Public Function Parse(text As String) As StrategyDefinition
            If String.IsNullOrWhiteSpace(text) Then Return Nothing

            Dim lower = text.ToLowerInvariant()
            Dim sd As New StrategyDefinition With {.RawDescription = text.Trim()}

            ' ── Duration ──────────────────────────────────────────────────────────
            Dim durMatch = Regex.Match(lower, "(\d+(?:\.\d+)?)\s*(hour|hr|day)s?")
            If durMatch.Success Then
                Dim val = Double.Parse(durMatch.Groups(1).Value)
                sd.DurationHours = If(durMatch.Groups(2).Value.StartsWith("day"), val * 24, val)
            End If

            ' ── Timeframe ─────────────────────────────────────────────────────────
            Dim tfMatch = Regex.Match(lower, "(\d+)\s*-?\s*(min(?:ute)?|hour|hr)")
            If tfMatch.Success Then
                Dim val = Integer.Parse(tfMatch.Groups(1).Value)
                sd.TimeframeMinutes = If(tfMatch.Groups(2).Value.StartsWith("h"), val * 60, val)
            End If

            ' ── Indicator period override (e.g. "20-period" or "period 14") ───────
            Dim periodMatch = Regex.Match(lower, "(\d+)[- ]period|period[- ](\d+)")
            If periodMatch.Success Then
                Dim pStr = If(periodMatch.Groups(1).Value <> "",
                              periodMatch.Groups(1).Value,
                              periodMatch.Groups(2).Value)
                sd.IndicatorPeriod = Integer.Parse(pStr)
            End If

            ' ── Standard deviation multiplier (e.g. "2.5 standard deviation") ─────
            Dim multMatch = Regex.Match(lower, "(\d+(?:\.\d+)?)\s*(?:std|standard)")
            If multMatch.Success Then
                sd.IndicatorMultiplier = Double.Parse(multMatch.Groups(1).Value)
            End If

            ' ── Indicator type ────────────────────────────────────────────────────
            If lower.Contains("bollinger") Then
                sd.Indicator = StrategyIndicatorType.BollingerBands
                sd.Condition = StrategyConditionType.FullCandleOutsideBands

                ' Detect close-only vs full-candle
                If lower.Contains("close") AndAlso Not lower.Contains("full") Then
                    sd.Condition = StrategyConditionType.CloseOutsideBands
                End If

            ElseIf lower.Contains("rsi") Then
                sd.Indicator = StrategyIndicatorType.RSI
                sd.IndicatorPeriod = If(sd.IndicatorPeriod = 20, 14, sd.IndicatorPeriod) ' default RSI=14
                If lower.Contains("oversold") OrElse Regex.IsMatch(lower, "rsi.{0,20}below.{0,10}30") Then
                    sd.Condition = StrategyConditionType.RSIOversold
                ElseIf lower.Contains("overbought") OrElse Regex.IsMatch(lower, "rsi.{0,20}above.{0,10}70") Then
                    sd.Condition = StrategyConditionType.RSIOverbought
                Else
                    sd.Condition = StrategyConditionType.RSIOversold ' default
                End If

            ElseIf lower.Contains("ema") OrElse lower.Contains("moving average") OrElse lower.Contains("crossover") Then
                sd.Indicator = StrategyIndicatorType.EMA
                sd.IndicatorPeriod = If(sd.IndicatorPeriod = 20, 9, sd.IndicatorPeriod) ' default fast EMA=9
                sd.SecondaryPeriod = 21
                sd.Condition = StrategyConditionType.EMACrossAbove

            ElseIf lower.Contains("macd") Then
                sd.Indicator = StrategyIndicatorType.MACD
                sd.Condition = StrategyConditionType.EMACrossAbove ' reuse for MACD signal cross

            Else
                ' No recognisable indicator — return nothing
                Return Nothing
            End If

            ' ── Entry directions ──────────────────────────────────────────────────
            ' Default: both long and short
            sd.GoLongWhenBelowBands = True
            sd.GoShortWhenAboveBands = True

            If Regex.IsMatch(lower, "buy only|long only|no short|don'?t sell") Then
                sd.GoShortWhenAboveBands = False
            End If
            If Regex.IsMatch(lower, "sell only|short only|no buy|don'?t buy") Then
                sd.GoLongWhenBelowBands = False
            End If

            ' ── Name ──────────────────────────────────────────────────────────────
            sd.Name = $"{sd.Indicator} Strategy ({sd.TimeframeMinutes}-min)"

            Return sd
        End Function

    End Class

End Namespace
