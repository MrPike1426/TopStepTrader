Imports TopStepTrader.Core.Enums
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Result returned by <see cref="LultDivergenceStrategy.Evaluate"/>.
    ''' </summary>
    Public Class LultDivergenceResult
        ''' <summary>Trade direction signalled, or Nothing when no signal fires.</summary>
        Public Property Side As OrderSide? = Nothing
        ''' <summary>WaveTrend WT1 value at the confirmed anchor wave extreme.</summary>
        Public Property AnchorWt1 As Single = 0.0F
        ''' <summary>WaveTrend WT1 value at the confirmed trigger wave extreme (shallower than anchor).</summary>
        Public Property TriggerWt1 As Single = 0.0F
        ''' <summary>
        ''' Price at the trigger wave extreme (low for bull, high for bear).
        ''' The engine computes SL = triggerExtreme ± ATR-scaled tick buffer from this value.
        ''' </summary>
        Public Property TriggerWaveExtreme As Decimal = 0D
        ''' <summary>
        ''' Nearest historical swing high (bull) or swing low (bear) between the entry price
        ''' and the approximate 2R target.  When present, take 50 % profit here and move SL
        ''' to breakeven.  Nothing when no qualifying swing exists in the recent lookback window.
        ''' </summary>
        Public Property PartialTpSwingLevel As Decimal? = Nothing
        ''' <summary>Number of LULT setup steps confirmed for a bull entry (0–6).</summary>
        Public Property BullStepsConfirmed As Integer = 0
        ''' <summary>Number of LULT setup steps confirmed for a bear entry (0–6).</summary>
        Public Property BearStepsConfirmed As Integer = 0
        ''' <summary>Bull confidence score 0–100 (= BullStepsConfirmed × 100 / 6, rounded).</summary>
        Public ReadOnly Property BullScore As Integer
            Get
                Return CInt(Math.Round(BullStepsConfirmed / 6.0 * 100))
            End Get
        End Property
        ''' <summary>Bear confidence score 0–100 (= BearStepsConfirmed × 100 / 6, rounded).</summary>
        Public ReadOnly Property BearScore As Integer
            Get
                Return CInt(Math.Round(BearStepsConfirmed / 6.0 * 100))
            End Get
        End Property
        ''' <summary>True when the current UTC time is within the London + NY pre-market window (11:00–17:00 UTC).</summary>
        Public Property IsInTradingWindow As Boolean = True
        ''' <summary>Single-line diagnostic status for the monitoring log.</summary>
        Public Property StatusLine As String = String.Empty
    End Class

    ''' <summary>
    ''' LULT ("LoveULongTime") Divergence Strategy — NQ (Nasdaq 100) 5-minute bars.
    '''
    ''' Simulates the Market Cipher B WaveTrend oscillator (WT1 / WT2) to detect
    ''' Anchor / Trigger wave momentum-price divergence setups.  A trade fires only
    ''' after all six confirmation steps pass in sequence:
    '''
    '''   1. Anchor wave  : WT1 breaches ±60 (overbought / oversold threshold).
    '''   2. Trigger wave : subsequent wave in the same oscillator direction after anchor.
    '''   3. Shallower    : trigger WT1 extreme is less extreme than anchor
    '''                     (higher low for bull, lower high for bear).
    '''                     If trigger OVERSHOOTS the anchor the setup is RESET immediately.
    '''   4. Divergence   : price at trigger is MORE extreme than at anchor
    '''                     (lower price low for bull, higher price high for bear).
    '''   5. Dot signal   : WT1 crosses WT2 after the trigger wave extreme
    '''                     (Green Dot bull, Red Dot bear).
    '''   6. Engulfing    : same-direction engulfing volume candle appears within
    '''                     <see cref="EngulfingWindowBars"/> of the dot signal.
    '''                     Candle body must fully engulf previous bar body; counter-wick
    '''                     must be ≤ 40 % of body size.
    '''
    ''' Entry : market order on the bar following the confirmed engulfing candle (step 6).
    ''' SL    : 2–3 ticks beyond trigger wave extreme (engine adds ATR-scaled buffer).
    ''' TP    : 2R (2 × risk = distance from entry to SL).
    ''' Partial TP: 50 % close at nearest historical swing between entry and 2R target;
    '''            move SL to breakeven after partial fill.
    ''' Time filter: 11:00–17:00 UTC  (≈ 07:00–13:00 EST / EDT — London + NY pre-market).
    ''' </summary>
    Public NotInheritable Class LultDivergenceStrategy

        Private Sub New()
        End Sub

        ' ── WaveTrend oscillator parameters ──────────────────────────────────────
        Private Const WtChannelLength As Integer = 10
        Private Const WtAvgLength As Integer = 21
        Private Const WtSignalSmooth As Integer = 4

        ' ── Threshold levels ─────────────────────────────────────────────────────
        Private Const OverboughtLevel As Single = 60.0F
        Private Const OversoldLevel As Single = -60.0F

        ' ── Setup detection window ────────────────────────────────────────────────
        ''' <summary>Maximum bars to scan backwards for the anchor wave.</summary>
        Private Const AnchorLookbackBars As Integer = 80
        ''' <summary>Maximum bars after trigger wave extreme to search for the dot signal.</summary>
        Private Const DotWindowBars As Integer = 15
        ''' <summary>Maximum bars after the dot to accept the engulfing volume candle.</summary>
        Private Const EngulfingWindowBars As Integer = 6
        ''' <summary>Maximum ratio of counter-direction wick to candle body for the engulfing check.</summary>
        Private Const MaxWickToBodyRatio As Double = 0.4

        ' ── Trading window filter (London + NY pre-market) ───────────────────────
        ' 07:00–11:00 EST (UTC-5) = 12:00–16:00 UTC; with EDT (UTC-4) = 11:00–15:00 UTC.
        ' Using 11:00–17:00 UTC covers both DST states with a safety margin.
        Private Const TradingWindowStartHourUtc As Integer = 11
        Private Const TradingWindowEndHourUtc As Integer = 17

        ''' <summary>
        ''' Minimum bar count for WaveTrend warm-up (≈ 35 bars) plus anchor / trigger
        ''' detection history (up to <see cref="AnchorLookbackBars"/> = 80 bars).
        ''' </summary>
        Public Const MinBarsRequired As Integer = 100

        ' ─────────────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' Evaluates the 6-step LULT divergence setup on the provided OHLC bar series.
        ''' Returns a <see cref="LultDivergenceResult"/> with Side = Nothing when no signal fires.
        ''' </summary>
        Public Shared Function Evaluate(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                opens As IList(Of Decimal)) As LultDivergenceResult

            Dim result As New LultDivergenceResult()
            Dim n = closes.Count

            If n < MinBarsRequired Then
                result.StatusLine = $"LULT warming up — {n}/{MinBarsRequired} bars required"
                Return result
            End If

            ' ── Trading window filter ────────────────────────────────────────────
            Dim utcHour = DateTimeOffset.UtcNow.Hour
            result.IsInTradingWindow = (utcHour >= TradingWindowStartHourUtc AndAlso
                                        utcHour < TradingWindowEndHourUtc)

            ' ── WaveTrend oscillator ─────────────────────────────────────────────
            Dim wt = TechnicalIndicators.WaveTrend(highs, lows, closes,
                                                   WtChannelLength, WtAvgLength, WtSignalSmooth)
            Dim wt1 = wt.Wt1
            Dim wt2 = wt.Wt2

            ' ── Bull and bear setup evaluation ───────────────────────────────────
            Dim bull = EvaluateSetup(highs, lows, closes, opens, wt1, wt2, n, isBull:=True)
            Dim bear = EvaluateSetup(highs, lows, closes, opens, wt1, wt2, n, isBull:=False)

            result.BullStepsConfirmed = bull.Steps
            result.BearStepsConfirmed = bear.Steps

            If bull.Steps = 6 Then
                result.Side = OrderSide.Buy
                result.AnchorWt1 = bull.AnchorWt1
                result.TriggerWt1 = bull.TriggerWt1
                result.TriggerWaveExtreme = bull.TriggerExtreme
                result.PartialTpSwingLevel = bull.PartialTpSwingLevel
            ElseIf bear.Steps = 6 Then
                result.Side = OrderSide.Sell
                result.AnchorWt1 = bear.AnchorWt1
                result.TriggerWt1 = bear.TriggerWt1
                result.TriggerWaveExtreme = bear.TriggerExtreme
                result.PartialTpSwingLevel = bear.PartialTpSwingLevel
            Else
                result.AnchorWt1 = If(bull.AnchorWt1 <> 0.0F, bull.AnchorWt1, bear.AnchorWt1)
                result.TriggerWt1 = If(bull.TriggerWt1 <> 0.0F, bull.TriggerWt1, bear.TriggerWt1)
            End If

            Dim lastWt1 = TechnicalIndicators.LastValid(wt1)
            Dim lastWt2 = TechnicalIndicators.LastValid(wt2)
            Dim windowTag = If(result.IsInTradingWindow, "EST window ✓",
                               $"OUT of window ({utcHour:D2}:xx UTC)")
            result.StatusLine =
                $"WT1={lastWt1:F1} WT2={lastWt2:F1} | " &
                $"Bull={bull.Steps}/6 Bear={bear.Steps}/6 | {windowTag}"

            Return result
        End Function

        ' ── Internal helper structure ─────────────────────────────────────────────

        Private Structure SetupEval
            Public Steps As Integer
            Public AnchorWt1 As Single
            Public TriggerWt1 As Single
            Public TriggerExtreme As Decimal
            Public PartialTpSwingLevel As Decimal?
        End Structure

        ''' <summary>
        ''' Core 6-step LULT evaluation for one direction (bull when isBull=True, bear otherwise).
        ''' Scans the WT1 series for local extrema and checks each step in sequence.
        ''' Returns immediately on the first complete 6-step setup; otherwise returns the
        ''' maximum partial score found across all candidate anchor / trigger pairs.
        ''' </summary>
        Private Shared Function EvaluateSetup(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                opens As IList(Of Decimal),
                wt1 As Single(),
                wt2 As Single(),
                n As Integer,
                isBull As Boolean) As SetupEval

            Dim res As New SetupEval()

            ' Only indices in [1, n-2] can be confirmed local extrema (both neighbours must exist).
            Dim searchFrom = Math.Max(1, n - 2 - AnchorLookbackBars)
            Dim extremes As New List(Of (Idx As Integer, Wt1Val As Single, PriceExtreme As Decimal))

            For i = searchFrom To n - 2
                If Single.IsNaN(wt1(i)) OrElse Single.IsNaN(wt1(i - 1)) OrElse Single.IsNaN(wt1(i + 1)) Then Continue For
                If isBull Then
                    ' Local minimum (strict in at least one direction)
                    If wt1(i) <= wt1(i - 1) AndAlso wt1(i) <= wt1(i + 1) AndAlso
                       (wt1(i) < wt1(i - 1) OrElse wt1(i) < wt1(i + 1)) Then
                        extremes.Add((i, wt1(i), lows(i)))
                    End If
                Else
                    ' Local maximum (strict in at least one direction)
                    If wt1(i) >= wt1(i - 1) AndAlso wt1(i) >= wt1(i + 1) AndAlso
                       (wt1(i) > wt1(i - 1) OrElse wt1(i) > wt1(i + 1)) Then
                        extremes.Add((i, wt1(i), highs(i)))
                    End If
                End If
            Next

            If extremes.Count < 2 Then Return res

            ' Scan from most recent to oldest candidate trigger (needs ≥1 older extreme for anchor).
            For ti = extremes.Count - 1 To 1 Step -1
                Dim trigger = extremes(ti)

                ' Need at least 2 bars after the trigger for dot detection and engulfing candle.
                If n - 1 - trigger.Idx < 2 Then Continue For

                ' Search backward through earlier extremes for a valid anchor.
                For anchorI = ti - 1 To 0 Step -1
                    Dim anchor = extremes(anchorI)

                    ' Step 1+2: anchor must breach the ±60 threshold.
                    Dim anchorBreached = If(isBull, anchor.Wt1Val < OversoldLevel,
                                                    anchor.Wt1Val > OverboughtLevel)
                    If Not anchorBreached Then Continue For
                    res.Steps = Math.Max(res.Steps, 2)

                    ' Step 3: trigger MUST be shallower than anchor.
                    '         If it overshoots, RESET this pair and try the next anchor.
                    Dim triggerShallower = If(isBull, trigger.Wt1Val > anchor.Wt1Val,
                                                      trigger.Wt1Val < anchor.Wt1Val)
                    If Not triggerShallower Then Continue For
                    res.Steps = Math.Max(res.Steps, 3)

                    ' Step 4: price divergence — price extreme at trigger must be MORE extreme.
                    Dim hasDivergence = If(isBull,
                        trigger.PriceExtreme < anchor.PriceExtreme,
                        trigger.PriceExtreme > anchor.PriceExtreme)
                    If Not hasDivergence Then Continue For
                    res.Steps = Math.Max(res.Steps, 4)

                    ' Step 5: dot signal — WT1 crosses WT2 after the trigger wave extreme.
                    Dim dotIdx = FindDotSignal(wt1, wt2, trigger.Idx, n, isBull)
                    If dotIdx < 0 Then Continue For
                    res.Steps = Math.Max(res.Steps, 5)

                    ' Step 6: engulfing volume candle at bars[n-1], within window of dot.
                    If n - 1 <= dotIdx OrElse n - 1 > dotIdx + EngulfingWindowBars Then Continue For
                    If Not IsEngulfingCandle(opens, closes, highs, lows, n - 1, isBull) Then Continue For

                    ' ── All 6 steps confirmed — populate result and return ────────
                    res.Steps = 6
                    res.AnchorWt1 = anchor.Wt1Val
                    res.TriggerWt1 = trigger.Wt1Val
                    res.TriggerExtreme = trigger.PriceExtreme
                    res.PartialTpSwingLevel = FindPartialTpSwing(highs, lows, closes, n,
                                                                 trigger.PriceExtreme, isBull)
                    Return res
                Next ' anchorI

                ' After exhausting all anchors for this trigger, stop searching older triggers
                ' once a high-confidence partial setup (≥ 4 steps) has already been found.
                If res.Steps >= 4 Then Exit For
            Next ' trigger

            Return res
        End Function

        ''' <summary>
        ''' Scans for the first WT1/WT2 crossover (dot signal) after <paramref name="afterIdx"/>.
        ''' Bull (Green Dot): WT1 crosses above WT2.
        ''' Bear (Red Dot): WT1 crosses below WT2.
        ''' Returns the bar index of the crossover, or -1 if none found within <see cref="DotWindowBars"/>.
        ''' </summary>
        Private Shared Function FindDotSignal(wt1 As Single(), wt2 As Single(),
                                              afterIdx As Integer, n As Integer,
                                              isBullCross As Boolean) As Integer
            Dim searchEnd = Math.Min(n - 2, afterIdx + DotWindowBars)
            For di = afterIdx + 1 To searchEnd
                If Single.IsNaN(wt1(di)) OrElse Single.IsNaN(wt2(di)) OrElse
                   Single.IsNaN(wt1(di - 1)) OrElse Single.IsNaN(wt2(di - 1)) Then Continue For
                If isBullCross Then
                    If wt1(di - 1) < wt2(di - 1) AndAlso wt1(di) >= wt2(di) Then Return di
                Else
                    If wt1(di - 1) > wt2(di - 1) AndAlso wt1(di) <= wt2(di) Then Return di
                End If
            Next
            Return -1
        End Function

        ''' <summary>
        ''' Returns True when bars[engulfIdx] is a valid engulfing volume candle in the trade direction.
        ''' Rules applied:
        '''   1. Candle colour matches trade direction (green for bull, red for bear).
        '''   2. Candle body fully engulfs the previous bar's body.
        '''   3. Counter-direction wick ≤ <see cref="MaxWickToBodyRatio"/> × body size (minimal wick).
        ''' </summary>
        Private Shared Function IsEngulfingCandle(opens As IList(Of Decimal),
                                                   closes As IList(Of Decimal),
                                                   highs As IList(Of Decimal),
                                                   lows As IList(Of Decimal),
                                                   engulfIdx As Integer,
                                                   isBull As Boolean) As Boolean
            If engulfIdx < 1 OrElse engulfIdx >= opens.Count Then Return False

            Dim curOpen = opens(engulfIdx)
            Dim curClose = closes(engulfIdx)
            Dim prevOpen = opens(engulfIdx - 1)
            Dim prevClose = closes(engulfIdx - 1)

            Dim prevBodyLow = Math.Min(prevOpen, prevClose)
            Dim prevBodyHigh = Math.Max(prevOpen, prevClose)
            Dim bodySize = Math.Abs(curClose - curOpen)
            If bodySize = 0D Then Return False

            If isBull Then
                If curClose <= curOpen Then Return False     ' must be green
                If curOpen > prevBodyLow Then Return False   ' opens at or below prev body low
                If curClose < prevBodyHigh Then Return False ' closes at or above prev body high
                ' Minimal lower wick (counter-direction for a bull trade)
                Dim lowerWick = curOpen - lows(engulfIdx)
                Return CDbl(lowerWick) / CDbl(bodySize) <= MaxWickToBodyRatio
            Else
                If curClose >= curOpen Then Return False     ' must be red
                If curOpen < prevBodyHigh Then Return False  ' opens at or above prev body high
                If curClose > prevBodyLow Then Return False  ' closes at or below prev body low
                ' Minimal upper wick (counter-direction for a bear trade)
                Dim upperWick = highs(engulfIdx) - curOpen
                Return CDbl(upperWick) / CDbl(bodySize) <= MaxWickToBodyRatio
            End If
        End Function

        ''' <summary>
        ''' Scans the 20 most recent bars for a swing high (bull) or swing low (bear) that
        ''' falls between the current bar's close and the approximate 2R profit target.
        ''' Uses the trigger wave extreme as a proxy for the SL when computing 1R / 2R distances.
        ''' Returns the nearest qualifying level, or Nothing.
        ''' </summary>
        Private Shared Function FindPartialTpSwing(
                highs As IList(Of Decimal),
                lows As IList(Of Decimal),
                closes As IList(Of Decimal),
                n As Integer,
                triggerExtreme As Decimal,
                isBull As Boolean) As Decimal?

            Dim currentClose = closes(n - 1)
            Dim approxR = Math.Abs(currentClose - triggerExtreme)
            If approxR = 0D Then Return Nothing

            Dim target2R = If(isBull, currentClose + approxR * 2D, currentClose - approxR * 2D)
            Dim scanFrom = Math.Max(1, n - 21)
            Dim nearest As Decimal? = Nothing

            For i = scanFrom To n - 2
                If isBull Then
                    Dim h = highs(i)
                    If h > currentClose AndAlso h < target2R Then
                        If nearest Is Nothing OrElse h < nearest.Value Then nearest = h
                    End If
                Else
                    Dim l = lows(i)
                    If l < currentClose AndAlso l > target2R Then
                        If nearest Is Nothing OrElse l > nearest.Value Then nearest = l
                    End If
                End If
            Next
            Return nearest
        End Function

    End Class

End Namespace
