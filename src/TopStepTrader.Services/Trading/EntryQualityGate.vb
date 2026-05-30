Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>UAT-03: outcome from <see cref="EntryQualityGate"/>. Maps 1:1 to the
    ''' three deterministic gates added on top of the existing entry logic.</summary>
    Public Enum EntryGateOutcome
        ''' <summary>All gates passed (or were disabled / inapplicable) — entry may proceed.</summary>
        Allow
        ''' <summary>F2 — last N closes on the wrong side of the 20-period BB median.</summary>
        BlockBbPosition
        ''' <summary>F3 — last K bar-to-bar moves all against the proposed direction
        ''' on a non-flip entry (isFlip = False).</summary>
        BlockMomentumAgainst
        ''' <summary>F6 — flip entry (isFlip = True) where the entry bar failed to confirm
        ''' the new direction.</summary>
        BlockConfirmationCandle
        ''' <summary>STRAT-40 F1 — BB-median slope conflicts with proposed direction and
        ''' the relax-on-flip path did not apply.</summary>
        BlockBbMedianSlope
        ''' <summary>STRAT-40 F2 — lower-TF SuperTrend disagrees with the proposed direction.
        ''' Caller is expected to *defer* (queue) rather than drop the candidate.</summary>
        BlockLowerTfDisagrees
    End Enum

    Public Class EntryGateResult
        Public Property Outcome As EntryGateOutcome
        Public Property Reason As String

        Public ReadOnly Property IsBlocked As Boolean
            Get
                Return Outcome <> EntryGateOutcome.Allow
            End Get
        End Property

        Public Shared ReadOnly Property Allowed As EntryGateResult
            Get
                Return New EntryGateResult With {.Outcome = EntryGateOutcome.Allow, .Reason = String.Empty}
            End Get
        End Property
    End Class

    ''' <summary>UAT-03: deterministic entry-quality gates layered on top of the
    ''' existing SuperTrend/ADX/DI/BB-slope checks. Pure functions on bar series + side
    ''' so they can be unit-tested without the ViewModel + DI graph.</summary>
    Public Module EntryQualityGate

        ''' <summary>UAT-03 F2 — block entry when the last <paramref name="lookbackBars"/>
        ''' closes were all on the wrong side of the BB median for the proposed direction.
        ''' SHORT entry is blocked if every one of those closes was &gt; the BB median at
        ''' the entry bar; LONG is the mirror.</summary>
        ''' <param name="closes">Closing prices of the bar series (oldest first).</param>
        ''' <param name="bbMedianAtEntry">The 20-period BB median value at the entry bar.</param>
        ''' <param name="isLong">True for LONG entries, False for SHORT.</param>
        ''' <param name="lookbackBars">Number of trailing closes that must agree to trigger
        ''' the block. Caller passes <c>Config.BbPositionGateBars</c>.</param>
        Public Function EvaluateBbPosition(closes As IList(Of Single),
                                            bbMedianAtEntry As Single,
                                            isLong As Boolean,
                                            lookbackBars As Integer) As EntryGateResult
            If closes Is Nothing OrElse closes.Count < lookbackBars Then Return EntryGateResult.Allowed
            If lookbackBars <= 0 Then Return EntryGateResult.Allowed
            If Single.IsNaN(bbMedianAtEntry) Then Return EntryGateResult.Allowed

            ' LONG is "blocked" when every recent close is BELOW the median (price stuck under
            ' the channel midline). SHORT is the mirror: block when every recent close is ABOVE
            ' the median. A single close on the correct side is enough to allow the entry.
            Dim n = closes.Count - 1
            For i = n - lookbackBars + 1 To n
                Dim c = closes(i)
                If Single.IsNaN(c) Then Return EntryGateResult.Allowed
                If isLong AndAlso c >= bbMedianAtEntry Then Return EntryGateResult.Allowed
                If Not isLong AndAlso c <= bbMedianAtEntry Then Return EntryGateResult.Allowed
            Next

            Dim sideLabel = If(isLong, "LONG", "SHORT")
            Return New EntryGateResult With {
                .Outcome = EntryGateOutcome.BlockBbPosition,
                .Reason = $"BB position gate — last {lookbackBars} closes all on the wrong side of BB median ({bbMedianAtEntry:F2}) for {sideLabel}"
            }
        End Function

        ''' <summary>UAT-03 F3 — block a non-flip entry when the last <paramref name="lookbackBars"/>
        ''' bar-to-bar moves were all against the proposed direction (e.g. 4 consecutive
        ''' higher closes on a SHORT re-entry). Flips are exempt so genuine reversal entries
        ''' at the start of a new leg still fire.</summary>
        ''' <param name="closes">Closing prices of the bar series (oldest first).</param>
        ''' <param name="isLong">True for LONG entries, False for SHORT.</param>
        ''' <param name="isFlip">True if SuperTrend direction just changed this tick.</param>
        ''' <param name="lookbackBars">Number of trailing bar-to-bar moves that must all
        ''' run against the proposed direction. Caller passes <c>Config.MomentumAgainstGateBars</c>.</param>
        Public Function EvaluateMomentumAgainst(closes As IList(Of Single),
                                                 isLong As Boolean,
                                                 isFlip As Boolean,
                                                 lookbackBars As Integer) As EntryGateResult
            If isFlip Then Return EntryGateResult.Allowed
            If lookbackBars <= 0 Then Return EntryGateResult.Allowed
            ' Need lookbackBars+1 closes to produce lookbackBars bar-to-bar moves.
            If closes Is Nothing OrElse closes.Count < lookbackBars + 1 Then Return EntryGateResult.Allowed

            Dim n = closes.Count - 1
            For i = n - lookbackBars + 1 To n
                Dim prev = closes(i - 1)
                Dim curr = closes(i)
                If Single.IsNaN(prev) OrElse Single.IsNaN(curr) Then Return EntryGateResult.Allowed
                If isLong Then
                    ' "Against" a LONG entry = lower closes. If any move is flat-or-up, allow.
                    If curr >= prev Then Return EntryGateResult.Allowed
                Else
                    ' "Against" a SHORT entry = higher closes. If any move is flat-or-down, allow.
                    If curr <= prev Then Return EntryGateResult.Allowed
                End If
            Next

            Dim sideLabel = If(isLong, "LONG", "SHORT")
            Dim moveLabel = If(isLong, "lower", "higher")
            Return New EntryGateResult With {
                .Outcome = EntryGateOutcome.BlockMomentumAgainst,
                .Reason = $"Momentum-against gate — last {lookbackBars} consecutive {moveLabel} closes against {sideLabel} re-entry (isFlip=False)"
            }
        End Function

        ''' <summary>UAT-03 F6 — for flip entries (isFlip = True), require the entry bar
        ''' itself to confirm the new direction by closing past the prior bar's range.
        ''' For LONG: <c>bars(n).Close &gt; bars(n-1).High</c>. For SHORT:
        ''' <c>bars(n).Close &lt; bars(n-1).Low</c>. Non-flip entries are exempt
        ''' (re-entries into an established trend don't need a fresh confirmation candle).</summary>
        ''' <param name="bars">Bar series (oldest first). The last bar is the entry bar.</param>
        ''' <param name="isLong">True for LONG entries, False for SHORT.</param>
        ''' <param name="isFlip">True if SuperTrend direction just changed this tick.</param>
        Public Function EvaluateConfirmationCandle(bars As IList(Of MarketBar),
                                                    isLong As Boolean,
                                                    isFlip As Boolean) As EntryGateResult
            If Not isFlip Then Return EntryGateResult.Allowed
            If bars Is Nothing OrElse bars.Count < 2 Then Return EntryGateResult.Allowed

            Dim n = bars.Count - 1
            Dim entry = bars(n)
            Dim prior = bars(n - 1)

            If isLong Then
                If entry.Close > prior.High Then Return EntryGateResult.Allowed
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.BlockConfirmationCandle,
                    .Reason = $"Confirmation-candle gate — flip LONG entry but close ({entry.Close:F2}) did not break prior bar high ({prior.High:F2})"
                }
            Else
                If entry.Close < prior.Low Then Return EntryGateResult.Allowed
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.BlockConfirmationCandle,
                    .Reason = $"Confirmation-candle gate — flip SHORT entry but close ({entry.Close:F2}) did not break prior bar low ({prior.Low:F2})"
                }
            End If
        End Function

        ''' <summary>STRAT-40 F1 — replaces the legacy BB-median slope filter with a
        ''' relax-on-flip variant: a fresh SuperTrend flip in a strong-ADX regime bypasses
        ''' the slope check, otherwise the standard "slope must agree with direction" rule
        ''' applies. The 6:1 long bias seen since 2026-05-19 came from up-sloping BB-mid
        ''' blocking every short flip during a multi-week index rally; this lets the
        ''' first bar of a high-conviction reversal through.</summary>
        ''' <param name="closes">Closing prices (oldest first). Used to recompute the
        ''' 20-period BB middle so this function is self-contained.</param>
        ''' <param name="isLong">True for LONG candidates, False for SHORT.</param>
        ''' <param name="isFlip">True if SuperTrend direction just changed this tick.</param>
        ''' <param name="adxValue">Current ADX value at the signal bar.</param>
        ''' <param name="bbLookback">Bars between <c>bbMidNow</c> and <c>bbMidPrev</c>
        ''' for the slope calculation (caller matches the existing VM mapping).</param>
        ''' <param name="strongAdxThreshold">ADX at which a flip is "strong enough" to
        ''' bypass the slope filter. Defaults to 30 — the persona-level Cappuccino entry.</param>
        Public Function EvaluateBbMedianRelaxOnFlip(closes As IList(Of Single),
                                                     isLong As Boolean,
                                                     isFlip As Boolean,
                                                     adxValue As Single,
                                                     bbLookback As Integer,
                                                     Optional strongAdxThreshold As Single = 30.0F) As EntryGateResult
            If closes Is Nothing OrElse closes.Count < 20 + bbLookback OrElse bbLookback <= 0 Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.Allow,
                    .Reason = "BB-median: insufficient bars"
                }
            End If

            ' Reuse the existing TechnicalIndicators math — convert once to Decimal so we
            ' do not diverge from the slope value the legacy block computed.
            Dim closesDec As IList(Of Decimal) = closes.Select(Function(c) CDec(c)).ToList()
            Dim bb = TechnicalIndicators.BollingerBands(closesDec, period:=20, stdDevMultiplier:=2.0)
            Dim n = closesDec.Count - 1
            Dim bbMidNow As Single = bb.Middle(n)
            Dim bbMidPrev As Single = bb.Middle(n - bbLookback)

            If Single.IsNaN(bbMidNow) OrElse Single.IsNaN(bbMidPrev) Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.Allow,
                    .Reason = "BB-median: insufficient bars"
                }
            End If

            ' Relax path: fresh flip + ADX in strong-trend band → bypass the slope wall.
            If isFlip AndAlso Not Single.IsNaN(adxValue) AndAlso adxValue >= strongAdxThreshold Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.Allow,
                    .Reason = $"BB-median bypassed: fresh flip with ADX >= strong (adx={adxValue:F1} >= {strongAdxThreshold:F1})"
                }
            End If

            Dim bbSlope As Single = bbMidNow - bbMidPrev
            If isLong AndAlso bbSlope < 0F Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.BlockBbMedianSlope,
                    .Reason = $"BB-median slope conflicts with direction (slope={bbSlope:F3} LONG)"
                }
            End If
            If Not isLong AndAlso bbSlope > 0F Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.BlockBbMedianSlope,
                    .Reason = $"BB-median slope conflicts with direction (slope={bbSlope:F3} SHORT)"
                }
            End If

            Return EntryGateResult.Allowed
        End Function

        ''' <summary>STRAT-40 F2 — stateless lower-TF agreement check. Computes SuperTrend
        ''' on the supplied bars (caller fetches them) and returns whether the resulting
        ''' direction agrees with the candidate side. On disagreement the caller is expected
        ''' to *defer* the candidate (queue it until the lower TF flips or the strategy-TF
        ''' signal evaporates) — see <c>SuperTrendPlusViewModel._deferredCandidates</c>.</summary>
        ''' <param name="lowerTfBars">Bar series for the lower timeframe (oldest first).
        ''' Caller is responsible for forming-bar strip and timeframe selection.</param>
        ''' <param name="isLong">True for LONG candidates, False for SHORT.</param>
        ''' <param name="stPeriod">SuperTrend period (mirror the strategy-TF value).</param>
        ''' <param name="stMultiplier">SuperTrend ATR multiplier (mirror the strategy-TF value).</param>
        Public Function EvaluateLowerTfAgreement(lowerTfBars As IList(Of MarketBar),
                                                  isLong As Boolean,
                                                  stPeriod As Integer,
                                                  stMultiplier As Double) As EntryGateResult
            If lowerTfBars Is Nothing OrElse lowerTfBars.Count < stPeriod + 1 Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.Allow,
                    .Reason = "Lower-TF: insufficient bars (fail-open)"
                }
            End If

            Dim highs = lowerTfBars.Select(Function(b) b.High).ToList()
            Dim lows = lowerTfBars.Select(Function(b) b.Low).ToList()
            Dim closes = lowerTfBars.Select(Function(b) b.Close).ToList()
            Dim st = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=stPeriod, multiplier:=stMultiplier)
            Dim n = lowerTfBars.Count - 1
            Dim dir As Single = st.Direction(n)

            If Single.IsNaN(dir) OrElse dir = 0.0F Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.Allow,
                    .Reason = "Lower-TF: insufficient bars (fail-open)"
                }
            End If

            Dim agrees As Boolean = If(isLong, dir > 0, dir < 0)
            If agrees Then
                Return New EntryGateResult With {
                    .Outcome = EntryGateOutcome.Allow,
                    .Reason = $"Lower-TF agrees (dir={dir})"
                }
            End If

            Return New EntryGateResult With {
                .Outcome = EntryGateOutcome.BlockLowerTfDisagrees,
                .Reason = $"Lower-TF disagrees — defer (dir={dir} candidate={If(isLong, "LONG", "SHORT")})"
            }
        End Function

    End Module

End Namespace
