Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Unit tests for ExitSignalEngine — composite degradation score, seven signals,
    ''' phased stop management, and SlotHealth state machine.
    ''' </summary>
    Public Class ExitSignalEngineTests

        Private ReadOnly _engine As ExitSignalEngine =
            New ExitSignalEngine(NullLogger(Of ExitSignalEngine).Instance)

        ' ── Helpers ──────────────────────────────────────────────────────────

        Private Shared Function MakeSlot(side As String,
                                         Optional entryAdx As Single = 35.0F,
                                         Optional entryAtr As Decimal = 10D,
                                         Optional entryPrice As Decimal = 100D,
                                         Optional stopPrice As Decimal = 90D) As PositionSlot
            Return New PositionSlot With {
                .SlotIndex   = 0,
                .Instrument  = "MES",
                .Side        = side,
                .IsOpen      = True,
                .EntryAdx    = entryAdx,
                .EntryAtr    = entryAtr,
                .EntryPrice  = entryPrice,
                .StopPrice   = stopPrice,
                .InitialRisk = Math.Abs(entryPrice - stopPrice),
                .StopPhase   = StopPhase.Initial
            }
        End Function

        ''' <summary>Build flat indicator arrays of a given length.</summary>
        Private Shared Function FillArr(length As Integer, value As Single) As Single()
            Dim a(length - 1) As Single
            For i = 0 To length - 1
                a(i) = value
            Next
            Return a
        End Function

        Private Shared Function FillDecArr(length As Integer, value As Decimal) As IList(Of Decimal)
            Return Enumerable.Repeat(value, length).ToList()
        End Function

        ' ── E1: SuperTrend flip ───────────────────────────────────────────────

        <Fact>
        Public Sub E1_LongSlot_Bearish_ST_ImmediateExit()
            Dim slot = MakeSlot("Buy")
            Dim n    = 10
            Dim dirs = FillArr(n, 1.0F)
            dirs(n - 1) = -1.0F          ' flip on last bar
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 25.0F),
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.True(eval.ImmediateExit)
            Assert.Equal(SlotHealth.Exiting, eval.RecommendedHealth)
            Assert.Contains("E1:8", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E1_ShortSlot_Bullish_ST_ImmediateExit()
            Dim slot = MakeSlot("Sell", stopPrice:=110D)
            Dim n    = 10
            Dim dirs = FillArr(n, -1.0F)
            dirs(n - 1) = 1.0F
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 110.0F), dirs,
                FillArr(n, 25.0F), FillArr(n, 30.0F),
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.True(eval.ImmediateExit)
        End Sub

        <Fact>
        Public Sub E1_NoFlip_NotImmediate()
            Dim slot = MakeSlot("Buy")
            Dim n    = 10
            Dim dirs = FillArr(n, 1.0F)    ' stays bullish
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.False(eval.ImmediateExit)
        End Sub

        ' ── E2: Momentum slowing ─────────────────────────────────────────────

        <Fact>
        Public Sub E2_ThreeConsecutiveContracting_HighRate_Fires()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' distances: 30, 20, 10 → contracting, rate = 10, atr = 5 → rate > 0.5×atr
            Dim closes  = New Decimal() {100D, 120D, 110D, 100D, 90D}
            Dim stLines = New Single() {Single.NaN, Single.NaN, 90.0F, 90.0F, 80.0F}
            ' distances at n-2=2: 20, n-1=3: 10, n=4: 10  but make them strictly decreasing
            closes  = New Decimal() {100D, 130D, 120D, 110D, 100D}
            stLines = New Single() {Single.NaN, 90.0F, 100.0F, 101.0F, 92.0F}
            ' d[2]=20, d[3]=9, d[4]=8  → contraction: 8<9<20; rate=|9-8|=1; atr=1.5 → rate > 0.5*atr? 1>0.75 yes
            Dim atrs    = FillArr(n, 1.5F)
            Dim lows    = New Decimal() {99D, 99D, 99D, 99D, 99D}
            Dim highs   = New Decimal() {101D, 131D, 121D, 111D, 101D}
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                stLines, dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), atrs)
            Assert.Contains("E2:3", eval.ContributingSignals)
        End Sub

        ' ── E3: ADX declining ────────────────────────────────────────────────

        <Fact>
        Public Sub E3_FallingAdx_TenBelowEntry_Fires()
            Dim slot = MakeSlot("Buy", entryAdx:=45.0F)
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim adxs = New Single() {Single.NaN, 40.0F, 38.0F, 36.0F, 34.0F}   ' n-2=36,n-1=36? keep strictly: [2]=38,[3]=36,[4]=34
            adxs = New Single() {Single.NaN, 40.0F, 38.0F, 36.0F, 34.0F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                adxs, FillArr(n, 10.0F))
            Assert.Contains("E3:2", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E3_FallingAdxButOnlyFiveBelowEntry_DoesNotFire()
            Dim slot = MakeSlot("Buy", entryAdx:=38.0F)   ' entry 38, current 34, diff=4 < 10
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim adxs = New Single() {Single.NaN, 40.0F, 38.0F, 36.0F, 34.0F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                adxs, FillArr(n, 10.0F))
            Assert.DoesNotContain("E3:2", eval.ContributingSignals)
        End Sub

        ' ── E4: DI compression ───────────────────────────────────────────────

        <Fact>
        Public Sub E4_NarrowingSpreadBelow10_Fires()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' spread at n-1 = 11, at n = 8 → narrowing and < 10
            Dim plusDis  = New Single() {25.0F, 25.0F, 25.0F, 20.0F, 18.0F}
            Dim minusDis = New Single() {10.0F, 10.0F, 10.0F, 9.0F, 10.0F}
            ' spread[3]=11, spread[4]=8 → narrowing and <10
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                plusDis, minusDis,
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.Contains("E4:2", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E4_SpreadAbove10_DoesNotFire()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim plusDis  = FillArr(n, 30.0F)
            Dim minusDis = FillArr(n, 10.0F)   ' spread = 20 always
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                plusDis, minusDis,
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.DoesNotContain("E4:2", eval.ContributingSignals)
        End Sub

        ' ── E5: DI crossover ─────────────────────────────────────────────────

        <Fact>
        Public Sub E5_Long_PlusDiCrossesBelow_Fires()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' n-1: plusDI=30 > minusDI=20; n: plusDI=18 < minusDI=22 → crossed
            Dim plusDis  = New Single() {30F, 30F, 30F, 30F, 18F}
            Dim minusDis = New Single() {10F, 10F, 10F, 20F, 22F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                plusDis, minusDis,
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.Contains("E5:4", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E5_Short_MinusDiCrossesBelow_Fires()
            Dim slot = MakeSlot("Sell", stopPrice:=110D)
            Dim n    = 5
            Dim dirs = FillArr(n, -1.0F)
            ' n-1: minusDI=30>plusDI=10; n: minusDI=8<plusDI=15 → crossed for short
            Dim plusDis  = New Single() {10F, 10F, 10F, 10F, 15F}
            Dim minusDis = New Single() {30F, 30F, 30F, 30F, 8F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 99D), FillDecArr(n, 97D), FillDecArr(n, 98D),
                FillArr(n, 110.0F), dirs,
                plusDis, minusDis,
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.Contains("E5:4", eval.ContributingSignals)
        End Sub

        ' ── E6: Price rejection bar ───────────────────────────────────────────

        <Fact>
        Public Sub E6_Long_UpperWickLargeCloseBelow_Fires()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' high=120, low=90, close=94 (below midpoint=105), body≈0 (open=close=94), upperWick=26 > 2×0
            Dim closes = FillDecArr(n - 1, 100D).ToList()
            closes.Add(94D)
            Dim highs  = FillDecArr(n - 1, 101D).ToList()
            highs.Add(120D)
            Dim lows   = FillDecArr(n - 1, 99D).ToList()
            lows.Add(90D)
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.Contains("E6:2", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E6_Short_LowerWickLargeCloseAbove_Fires()
            Dim slot = MakeSlot("Sell", stopPrice:=110D)
            Dim n    = 5
            Dim dirs = FillArr(n, -1.0F)
            ' high=110, low=80, close=106 (above midpoint=95), body≈0, lowerWick=26 > 0
            Dim closes = FillDecArr(n - 1, 100D).ToList()
            closes.Add(106D)
            Dim highs  = FillDecArr(n - 1, 101D).ToList()
            highs.Add(110D)
            Dim lows   = FillDecArr(n - 1, 99D).ToList()
            lows.Add(80D)
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                FillArr(n, 110.0F), dirs,
                FillArr(n, 20.0F), FillArr(n, 30.0F),
                FillArr(n, 35.0F), FillArr(n, 10.0F))
            Assert.Contains("E6:2", eval.ContributingSignals)
        End Sub

        ' ── E7: ATR contraction ───────────────────────────────────────────────

        <Fact>
        Public Sub E7_AtrFallingAndBelow80Pct_Fires()
            Dim slot = MakeSlot("Buy", entryAtr:=10D)   ' 80% = 8
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' atr[2]=9, [3]=8, [4]=7 → falling AND 7 < 8
            Dim atrs = New Single() {10.0F, 9.5F, 9.0F, 8.0F, 7.0F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), atrs)
            Assert.Contains("E7:1", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E7_AtrFallingButAbove80Pct_DoesNotFire()
            Dim slot = MakeSlot("Buy", entryAtr:=10D)   ' 80% = 8
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim atrs = New Single() {10.0F, 9.5F, 9.2F, 9.0F, 8.5F}   ' all > 8
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), atrs)
            Assert.DoesNotContain("E7:1", eval.ContributingSignals)
        End Sub

        ' ── Score → SlotHealth mapping ────────────────────────────────────────

        <Theory>
        <InlineData(0, SlotHealth.Healthy)>
        <InlineData(2, SlotHealth.Healthy)>
        <InlineData(3, SlotHealth.Warning)>
        <InlineData(5, SlotHealth.Warning)>
        <InlineData(6, SlotHealth.Exiting)>
        <InlineData(10, SlotHealth.Exiting)>
        Public Sub ScoreToHealth_Correct(score As Integer, expected As SlotHealth)
            Dim eval As New ExitEvaluation With {.Score = score}
            Assert.Equal(expected, eval.RecommendedHealth)
        End Sub

        ' ── Phased stop: ratchet-only ────────────────────────────────────────

        <Fact>
        Public Sub PhasedStop_InitialPhase_TrailsStUp()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, stopPrice:=90D)
            slot.InitialRisk = 10D
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' stLine at n = 92 > current stop 90 → ratchets up
            Dim stLines = FillArr(n, 92.0F)
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 100D), FillDecArr(n, 98D), FillDecArr(n, 100D),
                stLines, dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F))
            Assert.Equal(StopPhase.Initial, eval.StopPhase)
            Assert.Equal(92D, eval.PhasedStopPrice)
        End Sub

        <Fact>
        Public Sub PhasedStop_StopNeverRetreats()
            Dim slot = MakeSlot("Buy", entryPrice:=100D, stopPrice:=95D)
            slot.InitialRisk = 10D
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' stLine at n = 91 < current stop 95 → must NOT retreat
            Dim stLines = FillArr(n, 91.0F)
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 100D), FillDecArr(n, 98D), FillDecArr(n, 100D),
                stLines, dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F))
            Assert.True(eval.PhasedStopPrice >= 95D, "Stop retreated — ratchet violated")
        End Sub

        <Fact>
        Public Sub PhasedStop_Breakeven_AdvancesAtOneR()
            ' entryPrice=100, stop=90, R=10 → at 1R profit (close=110) → stop moves to 95
            Dim slot = MakeSlot("Buy", entryPrice:=100D, stopPrice:=90D)
            slot.InitialRisk = 10D
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim closes = FillDecArr(n - 1, 100D).ToList() : closes.Add(110D)  ' exactly 1R profit
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 111D), FillDecArr(n, 98D), closes,
                FillArr(n, 88.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F))
            Assert.Equal(StopPhase.Breakeven, eval.StopPhase)
            Assert.Equal(105D, eval.PhasedStopPrice)  ' entry(100) + 0.5×R(10) = 105
        End Sub

        <Fact>
        Public Sub PhasedStop_FreeRide_LocksAt2R()
            ' entryPrice=100, stop=90, R=10 → at 3R profit (close=130) → stop locked at 120
            Dim slot = MakeSlot("Buy", entryPrice:=100D, stopPrice:=90D)
            slot.InitialRisk = 10D
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim closes = FillDecArr(n - 1, 100D).ToList() : closes.Add(130D)
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 131D), FillDecArr(n, 98D), closes,
                FillArr(n, 88.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F))
            Assert.Equal(StopPhase.FreeRide, eval.StopPhase)
            Assert.Equal(120D, eval.PhasedStopPrice)
        End Sub

        ' ── E8: VWAP cross ───────────────────────────────────────────────────

        <Fact>
        Public Sub E8_Long_CrossesBelowVwap_Fires()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' close[n-1]=101 >= vwap[n-1]=100; close[n]=99 < vwap[n]=100 → crossed below
            Dim closes = New Decimal() {100D, 100D, 100D, 101D, 99D}
            Dim vwap   = New Single()  {100F, 100F, 100F, 100F, 100F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 98D), closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F),
                vwapValues:=vwap)
            Assert.Contains("E8:2", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E8_Long_AlreadyBelowVwap_DoesNotFire()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' already below VWAP on previous bar — no cross
            Dim closes = New Decimal() {100D, 100D, 100D, 98D, 97D}
            Dim vwap   = New Single()  {100F, 100F, 100F, 100F, 100F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 96D), closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F),
                vwapValues:=vwap)
            Assert.DoesNotContain("E8:2", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E8_Short_CrossesAboveVwap_Fires()
            Dim slot = MakeSlot("Sell", stopPrice:=110D)
            Dim n    = 5
            Dim dirs = FillArr(n, -1.0F)
            ' close[n-1]=99 <= vwap[n-1]=100; close[n]=101 > vwap[n]=100 → crossed above
            Dim closes = New Decimal() {100D, 100D, 100D, 99D, 101D}
            Dim vwap   = New Single()  {100F, 100F, 100F, 100F, 100F}
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 102D), FillDecArr(n, 98D), closes,
                FillArr(n, 110.0F), dirs,
                FillArr(n, 20.0F), FillArr(n, 30.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F),
                vwapValues:=vwap)
            Assert.Contains("E8:2", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E8_NoVwapProvided_DoesNotFire()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim closes = New Decimal() {100D, 100D, 100D, 101D, 99D}
            ' vwapValues = Nothing (omitted)
            Dim eval = _engine.Evaluate(slot,
                FillDecArr(n, 101D), FillDecArr(n, 98D), closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F))
            Assert.DoesNotContain("E8:2", eval.ContributingSignals)
        End Sub

        ' ── E9: RSI hidden divergence ────────────────────────────────────────

        <Fact>
        Public Sub E9_Long_HigherHighLowerRsiAbove50_Fires()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' high[n]=105 > high[n-2]=102; RSI[n]=60 < RSI[n-2]=65; RSI[n] > 50
            Dim highs  = New Decimal() {100D, 100D, 102D, 103D, 105D}
            Dim lows   = New Decimal() {99D,  99D,  99D,  99D,  99D}
            Dim closes = New Decimal() {100D, 100D, 100D, 100D, 100D}
            Dim rsi    = New Single()  {Single.NaN, 65F, 65F, 62F, 60F}
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F),
                rsiValues:=rsi)
            Assert.Contains("E9:3", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E9_Long_RsiBelow50_DoesNotFire()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            ' RSI[n] = 45 — not in bull territory
            Dim highs  = New Decimal() {100D, 100D, 102D, 103D, 105D}
            Dim lows   = New Decimal() {99D,  99D,  99D,  99D,  99D}
            Dim closes = New Decimal() {100D, 100D, 100D, 100D, 100D}
            Dim rsi    = New Single()  {Single.NaN, 65F, 65F, 50F, 45F}
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F),
                rsiValues:=rsi)
            Assert.DoesNotContain("E9:3", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E9_Short_LowerLowHigherRsiBelow50_Fires()
            Dim slot = MakeSlot("Sell", stopPrice:=110D)
            Dim n    = 5
            Dim dirs = FillArr(n, -1.0F)
            ' low[n]=85 < low[n-2]=88; RSI[n]=40 > RSI[n-2]=35; RSI[n] < 50
            Dim highs  = New Decimal() {100D, 100D, 100D, 100D, 100D}
            Dim lows   = New Decimal() {99D,  99D,  88D,  87D,  85D}
            Dim closes = New Decimal() {100D, 100D, 100D, 100D, 100D}
            Dim rsi    = New Single()  {Single.NaN, 35F, 35F, 38F, 40F}
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                FillArr(n, 110.0F), dirs,
                FillArr(n, 20.0F), FillArr(n, 30.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F),
                rsiValues:=rsi)
            Assert.Contains("E9:3", eval.ContributingSignals)
        End Sub

        <Fact>
        Public Sub E9_NoRsiProvided_DoesNotFire()
            Dim slot = MakeSlot("Buy")
            Dim n    = 5
            Dim dirs = FillArr(n, 1.0F)
            Dim highs  = New Decimal() {100D, 100D, 102D, 103D, 105D}
            Dim lows   = New Decimal() {99D,  99D,  99D,  99D,  99D}
            Dim closes = New Decimal() {100D, 100D, 100D, 100D, 100D}
            ' rsiValues = Nothing (omitted)
            Dim eval = _engine.Evaluate(slot,
                highs, lows, closes,
                FillArr(n, 90.0F), dirs,
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, 5.0F))
            Assert.DoesNotContain("E9:3", eval.ContributingSignals)
        End Sub

        ' ── FEAT-46: Pre-entry exit-signal gate ─────────────────────────────

        ''' <summary>
        ''' Simulates the FEAT-46 pre-entry check: a minimal slot with only Side/EntryAdx set
        ''' (no open position) is passed to Evaluate. A DI crossover (E5=4) alone should reach
        ''' the default blocking threshold of 4.
        ''' </summary>
        <Fact>
        Public Sub PreEntryGate_DICrossover_ScoreReachesBlockThreshold()
            Dim preSlot = New PositionSlot() With {
                .SlotIndex  = -1,
                .Side       = "Buy",
                .EntryAdx   = 35.0F,
                .EntryAtr   = 0D,
                .EntryPrice = 100D,
                .StopPrice  = 95D
            }
            Dim n = 10
            ' DI crossover: +DI was above -DI, now drops below on bar n (E5 fires for long)
            Dim pdis = FillArr(n, 30.0F)
            Dim mdis = FillArr(n, 20.0F)
            pdis(n - 1) = 18.0F   ' +DI < -DI on last bar
            mdis(n - 1) = 25.0F   ' -DI > +DI on last bar

            Dim nanAtr = FillArr(n, Single.NaN)

            Dim eval = _engine.Evaluate(preSlot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 95.0F), FillArr(n, 1.0F),
                pdis, mdis, FillArr(n, 35.0F), nanAtr)

            Assert.Contains("E5:4", eval.ContributingSignals)
            Assert.True(eval.Score >= 4, $"Expected score >= 4 (blocking threshold), got {eval.Score}")
        End Sub

        <Fact>
        Public Sub PreEntryGate_OnlyADXDeclining_ScoreBelowDefaultThreshold()
            ' E3 fires (ADX declining and 10 below entry), weight=2 — below default threshold 4
            Dim preSlot = New PositionSlot() With {
                .SlotIndex  = -1,
                .Side       = "Buy",
                .EntryAdx   = 45.0F,
                .EntryAtr   = 0D,
                .EntryPrice = 100D,
                .StopPrice  = 95D
            }
            Dim n = 10
            Dim adxArr = FillArr(n, 40.0F)
            adxArr(n - 2) = 34.0F   ' falling ADX that's >10 below entry ADX of 45
            adxArr(n - 1) = 32.0F   ' adx[n] < adx[n-1] < adx[n-2] — three consecutive
            ' Actually need n-2 > n-1 > n, so:
            adxArr(n - 3) = 38.0F
            adxArr(n - 2) = 35.0F
            adxArr(n - 1) = 32.0F   ' all below 45-10=35? No: 38/35/32 — 35 is boundary

            Dim nanAtr = FillArr(n, Single.NaN)

            Dim eval = _engine.Evaluate(preSlot,
                FillDecArr(n, 100D), FillDecArr(n, 98D), FillDecArr(n, 100D),
                FillArr(n, 95.0F), FillArr(n, 1.0F),
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                adxArr, nanAtr)

            Assert.True(eval.Score < 4, $"Expected score < 4 (below threshold), got {eval.Score}")
        End Sub

        <Fact>
        Public Sub PreEntryGate_ZeroThreshold_NeverBlocks()
            ' When EntryExitScoreBlockThreshold = 0 the gate is disabled.
            ' This test verifies that even a high score does not cause blocking
            ' when the caller checks (score >= 0) — every non-negative integer satisfies that,
            ' which is why the caller first checks threshold > 0 before calling Evaluate.
            ' Here we just confirm Evaluate itself returns correct scores; gate logic is in VM.
            Dim preSlot = New PositionSlot() With {
                .SlotIndex  = -1,
                .Side       = "Buy",
                .EntryAdx   = 35.0F,
                .EntryAtr   = 0D,
                .EntryPrice = 100D,
                .StopPrice  = 95D
            }
            ' Healthy bars — no signals fire
            Dim n = 10
            Dim eval = _engine.Evaluate(preSlot,
                FillDecArr(n, 101D), FillDecArr(n, 99D), FillDecArr(n, 100D),
                FillArr(n, 90.0F), FillArr(n, 1.0F),
                FillArr(n, 30.0F), FillArr(n, 20.0F),
                FillArr(n, 35.0F), FillArr(n, Single.NaN))

            Assert.Equal(0, eval.Score)
        End Sub

    End Class

End Namespace
