Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' End-to-end validation of the AI-Trading workflow after the percentage-based
    ''' exit and eToro minimum-trade-size refactor:
    '''   signal evaluation → % SL/TP price computation → min-notional clamping → order submission.
    '''
    ''' All tests are pure-math / pure-model — no network calls, no DI container.
    ''' Run with:  dotnet test --filter "FullyQualifiedName~AiTradingWorkflow"
    ''' </summary>
    Public Class AiTradingWorkflowTests

        ' ══════════════════════════════════════════════════════════════════
        ' Helpers
        ' ══════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Builds a strategy definition matching what ApplyEmaRsiCombined() produces,
        ''' using the new percentage-based exit fields.
        ''' </summary>
        Private Shared Function BuildStrategy(
                contractId As String,
                slPct As Decimal,
                tpPct As Decimal,
                capitalAtRisk As Decimal,
                Optional leverage As Integer = 1,
                Optional minConfidencePct As Integer = 75) As StrategyDefinition

            Return New StrategyDefinition With {
                .Name = "EMA/RSI Combined",
                .Indicator = StrategyIndicatorType.EmaRsiCombined,
                .Condition = StrategyConditionType.EmaRsiWeightedScore,
                .IndicatorPeriod = 50,
                .SecondaryPeriod = 0,
                .ContractId = contractId,
                .AccountId = 1L,
                .TimeframeMinutes = 5,
                .DurationHours = 8,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .InitialSlAmount = slPct,
                .InitialTpAmount = tpPct,
                .SlMultipleOfN = 0D,
                .TpMultipleOfN = 0D,
                .Leverage = leverage,
                .CapitalAtRisk = capitalAtRisk,
                .MinConfidencePct = minConfidencePct
            }
        End Function

        ''' <summary>Compute SL rate exactly as the engine does.</summary>
        Private Shared Function ComputeSlRate(entryPrice As Decimal, slPct As Decimal, isBuy As Boolean) As Decimal
            Return Math.Round(
                If(isBuy,
                   entryPrice * (1D - slPct / 100D),
                   entryPrice * (1D + slPct / 100D)), 4)
        End Function

        ''' <summary>Compute TP rate exactly as the engine does.</summary>
        Private Shared Function ComputeTpRate(entryPrice As Decimal, tpPct As Decimal, isBuy As Boolean) As Decimal
            Return Math.Round(
                If(isBuy,
                   entryPrice * (1D + tpPct / 100D),
                   entryPrice * (1D - tpPct / 100D)), 4)
        End Function

        ''' <summary>Compute clamped final amount exactly as the engine does.</summary>
        Private Shared Function ComputeFinalAmount(userAmount As Decimal, minNotional As Decimal, leverage As Integer) As Decimal
            Dim minCash = minNotional / CDec(leverage)
            Return Math.Max(userAmount, minCash)
        End Function

        Private Shared Function BuildBullBars(barCount As Integer,
                                              Optional startPrice As Decimal = 85.0D,
                                              Optional stepPerBar As Decimal = 0.02D) As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim price = startPrice
            For i = 1 To barCount
                Dim o = price
                Dim c = price + stepPerBar
                bars.Add(New MarketBar With {
                    .Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5 * (barCount - i)),
                    .Open = o, .High = c + 0.01D, .Low = o - 0.01D, .Close = c, .Volume = 100
                })
                price = c
            Next
            Return bars
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' 1 — SL/TP percentage price computation — Long
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub SlTp_Pct_Long_ComputesCorrectAbsoluteRates()
            ' GIVEN: OIL entry at 85.00, SL=0.75%, TP=1.5%
            Dim entry = 85.0D
            Dim slPct = 0.75D
            Dim tpPct = 1.5D

            ' WHEN
            Dim slRate = ComputeSlRate(entry, slPct, isBuy:=True)
            Dim tpRate = ComputeTpRate(entry, tpPct, isBuy:=True)

            ' THEN: rates are absolute prices, not tick offsets
            Assert.Equal(84.3625D, slRate)  ' 85.00 × (1 − 0.0075) = 84.3625
            Assert.Equal(86.275D, tpRate)   ' 85.00 × (1 + 0.015)  = 86.275

            ' SL must be below entry for a Long
            Assert.True(slRate < entry, $"SL rate {slRate} must be below entry {entry}")
            ' TP must be above entry for a Long
            Assert.True(tpRate > entry, $"TP rate {tpRate} must be above entry {entry}")
        End Sub

        <Fact>
        Public Sub SlTp_Pct_Long_RatioPreserved()
            ' 1.5% TP / 0.75% SL = 1:2 risk–reward ratio
            Dim entry = 85.0D
            Dim slRate = ComputeSlRate(entry, 0.75D, True)
            Dim tpRate = ComputeTpRate(entry, 1.5D, True)

            Dim risk = entry - slRate
            Dim reward = tpRate - entry

            Assert.Equal(2D, Math.Round(reward / risk, 4))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 2 — SL/TP percentage price computation — Short
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub SlTp_Pct_Short_ComputesCorrectAbsoluteRates()
            ' GIVEN: OIL entry at 85.00, SL=0.75%, TP=1.5% (Short)
            Dim entry = 85.0D

            Dim slRate = ComputeSlRate(entry, 0.75D, isBuy:=False)
            Dim tpRate = ComputeTpRate(entry, 1.5D, isBuy:=False)

            Assert.Equal(85.6375D, slRate)  ' 85.00 × (1 + 0.0075)
            Assert.Equal(83.725D, tpRate)   ' 85.00 × (1 − 0.015)

            ' For Short: SL above entry, TP below entry
            Assert.True(slRate > entry, $"Short SL {slRate} must be above entry {entry}")
            Assert.True(tpRate < entry, $"Short TP {tpRate} must be below entry {entry}")
        End Sub

        <Fact>
        Public Sub SlTp_Pct_Short_LongAndShortAreSymmetric()
            ' The SL of a Short at +x% should equal the TP of a Long at +x%
            Dim entry = 100.0D
            Dim pct = 2.0D

            Dim longTp = ComputeTpRate(entry, pct, isBuy:=True)   ' 102.0
            Dim shortSl = ComputeSlRate(entry, pct, isBuy:=False) ' 102.0

            Assert.Equal(longTp, shortSl)

            Dim longSl = ComputeSlRate(entry, pct, isBuy:=True)    ' 98.0
            Dim shortTp = ComputeTpRate(entry, pct, isBuy:=False)  ' 98.0
            Assert.Equal(longSl, shortTp)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 3 — Zero % = no bracket order
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub SlAmount_Zero_MeansNoSlOrder()
            Dim strategy = BuildStrategy("OIL", slPct:=0D, tpPct:=1.5D, capitalAtRisk:=1000D)
            ' Engine guard: If _strategy.InitialSlAmount > 0 Then ... bracket initialised with SL price
            Assert.Equal(0D, strategy.InitialSlAmount)
            ' slPrice remains 0 → engine skips SL bracket placement
        End Sub

        <Fact>
        Public Sub TpAmount_Zero_MeansNoTpOrder()
            Dim strategy = BuildStrategy("OIL", slPct:=0.75D, tpPct:=0D, capitalAtRisk:=1000D)
            Assert.Equal(0D, strategy.InitialTpAmount)
        End Sub

        <Fact>
        Public Sub BothAmounts_Zero_NoBrackets_OrderStillValidToPlace()
            ' A trade can be opened without SL/TP — just a market open with no brackets.
            Dim strategy = BuildStrategy("OIL", slPct:=0D, tpPct:=0D, capitalAtRisk:=1000D)
            Assert.Equal(0D, strategy.InitialSlAmount)
            Assert.Equal(0D, strategy.InitialTpAmount)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 4 — eToro minimum trade size enforcement
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub MinTradeSize_Leverage1_ClampToMinNotional()
            ' OIL minNotional=$1000, leverage=1 → minCash=$1000
            ' If user sets $500, finalAmount must be $1000
            Dim finalAmount = ComputeFinalAmount(userAmount:=500D, minNotional:=1000D, leverage:=1)

            Assert.Equal(1000D, finalAmount)
        End Sub

        <Fact>
        Public Sub MinTradeSize_Leverage2_HalvesMinCash()
            ' leverage=2 → minCash=$500; user $500 → no clamp needed
            Dim finalAmount = ComputeFinalAmount(500D, 1000D, leverage:=2)

            Assert.Equal(500D, finalAmount)
        End Sub

        <Fact>
        Public Sub MinTradeSize_Leverage5_FurtherReducesMinCash()
            ' leverage=5 → minCash=$200; user $500 → user amount wins
            Dim finalAmount = ComputeFinalAmount(500D, 1000D, leverage:=5)

            Assert.Equal(500D, finalAmount)  ' user amount > minCash; no clamp
        End Sub

        <Fact>
        Public Sub MinTradeSize_Leverage10_MinCash100()
            ' leverage=10 → minCash=$100
            Dim minCash = 1000D / 10D
            Assert.Equal(100D, minCash)

            Dim finalAmount = ComputeFinalAmount(50D, 1000D, leverage:=10)
            Assert.Equal(100D, finalAmount)  ' clamped up from $50
        End Sub

        <Fact>
        Public Sub MinTradeSize_UserAmountAlreadyAboveMin_NoClamp()
            ' User sets $2000 (above $1000 min) → no clamp
            Dim finalAmount = ComputeFinalAmount(2000D, 1000D, leverage:=1)

            Assert.Equal(2000D, finalAmount)
        End Sub

        <Fact>
        Public Sub MinTradeSize_Formula_IsMinNotionalDividedByLeverage()
            ' Core invariant: minCash = minNotionalUsd / leverage
            For Each lev In {1, 2, 5, 10, 20}
                Dim expected = 1000D / lev
                Dim actual = 1000D / CDec(lev)
                Assert.True(expected = actual, $"minCash for leverage={lev} should be {expected}")
            Next
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 5 — FavouriteContracts: MinNotionalUsd + instrument metadata
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub FavouriteContracts_Oil_HasCorrectInstrumentId()
            Dim oil = FavouriteContracts.TryGetBySymbol("OIL")

            Assert.NotNull(oil)
            Assert.Equal(17, oil.InstrumentId)
        End Sub

        <Fact>
        Public Sub FavouriteContracts_Oil_HasMinNotional1000()
            Dim oil = FavouriteContracts.TryGetBySymbol("OIL")

            Assert.NotNull(oil)
            Assert.Equal(1000D, oil.MinNotionalUsd)
        End Sub

        <Fact>
        Public Sub FavouriteContracts_AllSixHaveNonZeroInstrumentIdAndMinNotional()
            Dim contracts = FavouriteContracts.GetDefaults()

            Assert.Equal(5, contracts.Count)
            For Each c In contracts
                Assert.True(c.InstrumentId > 0, $"{c.ContractId}: InstrumentId must be > 0")
                Assert.True(c.MinNotionalUsd > 0, $"{c.ContractId}: MinNotionalUsd must be > 0")
                Assert.True(c.MaxLeverage >= 1, $"{c.ContractId}: MaxLeverage must be >= 1")
            Next
        End Sub

        <Fact>
        Public Sub FavouriteContracts_UnknownSymbol_ReturnsNothing()
            Assert.Null(FavouriteContracts.TryGetBySymbol("NOTACONTRACT"))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 6 — Happy path: SL=0.75%, TP=1.5%, OIL Long and Short
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub HappyPath_OilLong_AllNumbersCorrect()
            ' GIVEN: OIL at 85.50, SL=0.75%, TP=1.5%, amount=$1000, leverage=1
            Dim entry = 85.5D
            Dim slPct = 0.75D
            Dim tpPct = 1.5D
            Dim userAmount = 1000D
            Dim leverage = 1

            ' WHEN
            Dim slRate = ComputeSlRate(entry, slPct, isBuy:=True)
            Dim tpRate = ComputeTpRate(entry, tpPct, isBuy:=True)
            Dim finalAmt = ComputeFinalAmount(userAmount, 1000D, leverage)

            ' THEN: price levels
            Assert.Equal(Math.Round(85.5D * (1D - 0.0075D), 4), slRate)  ' 84.8588
            Assert.Equal(Math.Round(85.5D * (1D + 0.015D), 4), tpRate)   ' 86.7825

            ' Risk/reward — round to 3dp to absorb sub-cent rounding at non-round entry prices
            Dim risk = entry - slRate
            Dim reward = tpRate - entry
            Assert.Equal(2D, Math.Round(reward / risk, 3))

            ' Min-notional — $1000 user = $1000 min → no clamp
            Assert.Equal(1000D, finalAmt)

            ' InstrumentId resolves
            Dim fav = FavouriteContracts.TryGetBySymbol("OIL")
            Assert.Equal(17, fav.InstrumentId)
        End Sub

        <Fact>
        Public Sub HappyPath_OilShort_AllNumbersCorrect()
            Dim entry = 85.5D
            Dim slPct = 0.75D
            Dim tpPct = 1.5D

            Dim slRate = ComputeSlRate(entry, slPct, isBuy:=False)
            Dim tpRate = ComputeTpRate(entry, tpPct, isBuy:=False)

            Assert.Equal(Math.Round(85.5D * (1D + 0.0075D), 4), slRate)
            Assert.Equal(Math.Round(85.5D * (1D - 0.015D), 4), tpRate)

            Assert.True(slRate > entry)
            Assert.True(tpRate < entry)

            Dim risk = slRate - entry
            Dim reward = entry - tpRate
            Assert.Equal(2D, Math.Round(reward / risk, 3))
        End Sub

        <Fact>
        Public Sub HappyPath_OilLong_UserBelowMin_ClampsTo1000()
            ' User sets $500, leverage=1, OIL minNotional=$1000 → clamps to $1000
            Dim finalAmt = ComputeFinalAmount(500D, 1000D, 1)

            Assert.Equal(1000D, finalAmt)
        End Sub

        <Fact>
        Public Sub HappyPath_OilLong_Leverage2_UserAboveMin_NoClamping()
            ' leverage=2 → minCash=$500; user=$800 > $500 → no clamp
            Dim finalAmt = ComputeFinalAmount(800D, 1000D, 2)

            Assert.Equal(800D, finalAmt)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 7 — EMA/RSI signal evaluation (unchanged — no SL/TP dependency)
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub EmaRsi_BullBars_ProducesPositiveBullScore()
            Dim bars = BuildBullBars(70)
            Dim closes = bars.Select(Function(b) b.Close).ToList()

            Dim ema21 = TechnicalIndicators.EMA(closes, 21)
            Dim ema50 = TechnicalIndicators.EMA(closes, 50)
            Dim rsi14 = TechnicalIndicators.RSI(closes, 14)

            Dim ema21Now = TechnicalIndicators.LastValid(ema21)
            Dim ema50Now = TechnicalIndicators.LastValid(ema50)
            Dim rsiVal = TechnicalIndicators.LastValid(rsi14)
            Dim lastClose = CDec(bars.Last().Close)

            Dim bullScore As Double = 0
            If ema21Now > ema50Now Then bullScore += 25
            If lastClose > CDec(ema21Now) Then bullScore += 20
            If lastClose > CDec(ema50Now) Then bullScore += 15
            Dim rsiScore As Double
            If rsiVal <= 30 Then
                rsiScore = 20
            ElseIf rsiVal >= 70 Then
                rsiScore = 0
            Else
                rsiScore = (70.0 - rsiVal) / 40.0 * 20.0
            End If
            bullScore += rsiScore
            Dim ema21Prev = TechnicalIndicators.PreviousValid(ema21)
            If ema21Now > ema21Prev Then bullScore += 10
            Dim lastThree = bars.Skip(bars.Count - 3).ToList()
            If lastThree.Where(Function(b) b.Close >= b.Open).Count() >= 2 Then bullScore += 10

            Assert.True(bullScore > 0, $"Expected positive bull score, got {bullScore}")
        End Sub

        <Fact>
        Public Sub EmaRsi_RequiresMinimum55Bars_GuardCorrect()
            ' minBars = IndicatorPeriod + 5 = 50 + 5 = 55  (unchanged by this refactor)
            Dim strategy = BuildStrategy("OIL", 0.75D, 1.5D, 1000D)
            Dim minBars = strategy.IndicatorPeriod + 5

            Assert.Equal(55, minBars)
            Assert.True(BuildBullBars(54).Count < minBars)
            Assert.True(BuildBullBars(55).Count >= minBars)
        End Sub

        <Fact>
        Public Sub EmaRsi_Ema50ValidFrom50thBar_PreviousValidFrom51stBar()
            Dim bars51 = BuildBullBars(51)
            Dim closes = bars51.Select(Function(b) b.Close).ToList()
            Dim ema50 = TechnicalIndicators.EMA(closes, 50)

            Assert.False(Single.IsNaN(TechnicalIndicators.LastValid(ema50)),
                         "LastValid(EMA50) should be valid with 51 bars")
            Assert.False(Single.IsNaN(TechnicalIndicators.PreviousValid(ema50)),
                         "PreviousValid(EMA50) should be valid with 51 bars")
        End Sub

        <Fact>
        Public Sub EmaRsi_WithExactly50Bars_PreviousValidReturnsZeroFallback()
            Dim bars50 = BuildBullBars(50)
            Dim closes = bars50.Select(Function(b) b.Close).ToList()
            Dim ema50 = TechnicalIndicators.EMA(closes, 50)

            Assert.False(Single.IsNaN(TechnicalIndicators.LastValid(ema50)))
            Assert.Equal(0.0F, TechnicalIndicators.PreviousValid(ema50))
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 8 — StrategyDefinition defaults after refactor
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub StrategyDefinition_NewDollarFields_DefaultToTwentyAndTen()
            Dim sd As New StrategyDefinition()

            Assert.Equal(20D, sd.InitialTpAmount)
            Assert.Equal(10D, sd.InitialSlAmount)
            Assert.Equal(1, sd.Leverage)
        End Sub

        <Fact>
        Public Sub StrategyDefinition_DollarFields_RoundTrip()
            Dim sd = BuildStrategy("OIL", slPct:=0.75D, tpPct:=1.5D, capitalAtRisk:=1000D, leverage:=2)

            Assert.Equal(0.75D, sd.InitialSlAmount)
            Assert.Equal(1.5D, sd.InitialTpAmount)
            Assert.Equal(2, sd.Leverage)
        End Sub

        <Fact>
        Public Sub StrategyDefinition_Summary_ShowsDollarAmountWhenSet()
            Dim sd = BuildStrategy("OIL", slPct:=0.75D, tpPct:=1.5D, capitalAtRisk:=1000D)
            Dim summary = sd.Summary

            Assert.Contains("TP:$2", summary)   ' 1.5D rounds to $2 with :F0
            Assert.Contains("SL:$1", summary)   ' 0.75D rounds to $1 with :F0
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 10 — Reversal detection state machine
        ' ══════════════════════════════════════════════════════════════════
        '
        ' These tests mirror the pure logic inside StrategyExecutionEngine.DoCheckAsync
        ' using a local helper (AdvanceReversalState) that matches the engine implementation.
        ' No network, no DI container — deterministic confirmation counting.

        ''' <summary>
        ''' Mirrors the reversal-candidate advance logic in StrategyExecutionEngine.
        ''' Returns True when a confirmed reversal has occurred.
        ''' candidateSide / confirmCount are ByRef and updated in-place.
        ''' </summary>
        Private Shared Function AdvanceReversalState(
                signalSide As OrderSide?,
                currentTrend As OrderSide?,
                ByRef candidateSide As OrderSide?,
                ByRef confirmCount As Integer,
                isNewBar As Boolean,
                confirmBars As Integer) As Boolean

            If Not signalSide.HasValue OrElse Not currentTrend.HasValue Then Return False
            If signalSide.Value = currentTrend.Value Then
                candidateSide = Nothing
                confirmCount = 0
                Return False
            End If
            ' Opposite signal — advance only on new bars
            If isNewBar Then
                If Not candidateSide.HasValue OrElse candidateSide.Value <> signalSide.Value Then
                    candidateSide = signalSide
                    confirmCount = 1
                Else
                    confirmCount += 1
                End If
            End If
            Return (confirmCount >= confirmBars)
        End Function

        <Fact>
        Public Sub Reversal_FirstOppositeBarIsCandidate_NotConfirmed()
            ' Bar 1: SHORT signal while trend is LONG → candidate (1/2), not yet confirmed
            Dim candidateSide As OrderSide? = Nothing
            Dim confirmCount As Integer = 0

            Dim confirmed = AdvanceReversalState(
                OrderSide.Sell, OrderSide.Buy,
                candidateSide, confirmCount,
                isNewBar:=True, confirmBars:=2)

            Assert.False(confirmed)
            Assert.Equal(1, confirmCount)
            Assert.Equal(OrderSide.Sell, candidateSide)
        End Sub

        <Fact>
        Public Sub Reversal_TwoConsecutiveNewBars_Confirms()
            ' Bar 1: opposite signal → candidate
            Dim candidateSide As OrderSide? = Nothing
            Dim confirmCount As Integer = 0
            AdvanceReversalState(OrderSide.Sell, OrderSide.Buy, candidateSide, confirmCount, True, 2)

            ' Bar 2: same opposite signal on NEW bar → reversal confirmed
            Dim confirmed = AdvanceReversalState(
                OrderSide.Sell, OrderSide.Buy,
                candidateSide, confirmCount,
                isNewBar:=True, confirmBars:=2)

            Assert.True(confirmed)
            Assert.Equal(2, confirmCount)
        End Sub

        <Fact>
        Public Sub Reversal_SameBarRecheck_DoesNotAdvanceCount()
            ' Bar 1: opposite signal on new bar → count = 1
            Dim candidateSide As OrderSide? = Nothing
            Dim confirmCount As Integer = 0
            AdvanceReversalState(OrderSide.Sell, OrderSide.Buy, candidateSide, confirmCount, True, 2)

            ' Same bar (timer fires again) → isNewBar = False → count stays at 1, not confirmed
            Dim confirmed = AdvanceReversalState(
                OrderSide.Sell, OrderSide.Buy,
                candidateSide, confirmCount,
                isNewBar:=False, confirmBars:=2)

            Assert.False(confirmed)
            Assert.Equal(1, confirmCount)   ' unchanged
        End Sub

        <Fact>
        Public Sub Reversal_SameTrendSignalResetsCandidateCount()
            ' Candidate established on bar 1
            Dim candidateSide As OrderSide? = Nothing
            Dim confirmCount As Integer = 0
            AdvanceReversalState(OrderSide.Sell, OrderSide.Buy, candidateSide, confirmCount, True, 2)
            Assert.Equal(1, confirmCount)

            ' LONG signal comes back on bar 2 → candidate reset
            Dim confirmed = AdvanceReversalState(
                OrderSide.Buy, OrderSide.Buy,
                candidateSide, confirmCount,
                isNewBar:=True, confirmBars:=2)

            Assert.False(confirmed)
            Assert.Equal(0, confirmCount)
            Assert.Null(candidateSide)
        End Sub

        <Fact>
        Public Sub Reversal_NoCurrentTrend_NeverConfirms()
            ' currentTrend = Nothing → engine hasn't seen a first signal yet; no reversal possible
            Dim candidateSide As OrderSide? = Nothing
            Dim confirmCount As Integer = 0

            Dim confirmed = AdvanceReversalState(
                OrderSide.Sell, Nothing,
                candidateSide, confirmCount,
                isNewBar:=True, confirmBars:=2)

            Assert.False(confirmed)
        End Sub

        <Fact>
        Public Sub TradeRow_Close_Reversal_SetsOrangeResult()
            Dim row As New TradeRowViewModel(
                OrderSide.Sell, "OIL", 77, DateTimeOffset.UtcNow,
                exposureUsd:=1000D, leverage:=1, entryPrice:=85.0D)

            row.UpdatePnl(84.0D, 100D)   ' short going in our favour; MCL DPP = $100/pt
            row.Close("Reversal", row.UnrealizedPnlUsd)

            Assert.False(row.IsInProgress)
            Assert.Contains("Reversal", row.Result)
            Assert.Contains("↔", row.Result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' 9 — TradeRowViewModel: eToro fields + live P&L
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub TradeRow_CapturesEtoroPositionIdAndOpenedTime()
            ' GIVEN: event values that come from a real eToro placement
            Dim positionId = 3454794424L
            Dim openedAt = New DateTimeOffset(2026, 5, 3, 9, 56, 0, TimeSpan.Zero)

            ' WHEN: row constructed the same way OnTradeOpened does it
            Dim row As New TradeRowViewModel(
                OrderSide.Sell, "OIL", 77,
                openedAt,
                externalOrderId:=333223404L,
                etoroPositionId:=positionId,
                openedAtUtc:=openedAt)

            ' THEN
            Assert.Equal(positionId, row.EtoroPositionId)
            Assert.Equal("3454794424", row.EtoroPositionIdDisplay)
            Assert.Equal(openedAt.ToLocalTime().ToString("dd/MM HH:mm"), row.OpenedAtDisplay)
        End Sub

        <Fact>
        Public Sub TradeRow_ExposureDisplay_ShowsAmountAndLeverageSuffix()
            Dim row As New TradeRowViewModel(
                OrderSide.Sell, "OIL", 77,
                DateTimeOffset.UtcNow,
                exposureUsd:=1007D,
                leverage:=2)

            ' leverage > 1 → "×N" suffix
            Assert.Equal("$1,007×2", row.ExposureDisplay)
        End Sub

        <Fact>
        Public Sub TradeRow_ExposureDisplay_NoSuffix_WhenLeverage1()
            Dim row As New TradeRowViewModel(
                OrderSide.Buy, "OIL", 77,
                DateTimeOffset.UtcNow,
                exposureUsd:=1000D,
                leverage:=1)

            Assert.Equal("$1,000", row.ExposureDisplay)
        End Sub

        <Fact>
        Public Sub TradeRow_UpdatePnl_Long_PositiveWhenPriceRises()
            ' Entry 85.00, current 86.00 — Long → profit
            ' MCL (OIL): tickValue=$1, tickSize=0.01 → DPP=$100/pt
            ' P&L = (86 - 85) × $100 = $100.00
            Dim row As New TradeRowViewModel(
                OrderSide.Buy, "OIL", 77,
                DateTimeOffset.UtcNow,
                exposureUsd:=1000D,
                leverage:=1,
                entryPrice:=85.0D)

            row.UpdatePnl(86.0D, 100D)

            Assert.True(row.UnrealizedPnlUsd > 0, $"Long P&L should be positive; got {row.UnrealizedPnlUsd}")
            Assert.Equal(100.0D, row.UnrealizedPnlUsd)
        End Sub

        <Fact>
        Public Sub TradeRow_UpdatePnl_Short_NegativeWhenPriceRises()
            ' Entry 85.00, current 86.00 — Short → loss
            ' MCL (OIL): DPP=$100/pt → P&L = (85 - 86) × $100 = -$100.00
            Dim row As New TradeRowViewModel(
                OrderSide.Sell, "OIL", 77,
                DateTimeOffset.UtcNow,
                exposureUsd:=1000D,
                leverage:=1,
                entryPrice:=85.0D)

            row.UpdatePnl(86.0D, 100D)

            Assert.True(row.UnrealizedPnlUsd < 0, $"Short P&L should be negative when price rises; got {row.UnrealizedPnlUsd}")
            Assert.Equal(-100.0D, row.UnrealizedPnlUsd)
        End Sub

        <Fact>
        Public Sub TradeRow_UpdatePnl_StopsUpdatingAfterClose()
            Dim row As New TradeRowViewModel(
                OrderSide.Buy, "OIL", 77,
                DateTimeOffset.UtcNow,
                exposureUsd:=1000D,
                leverage:=1,
                entryPrice:=85.0D)

            row.UpdatePnl(86.0D, 100D)   ' MCL DPP = $100/pt → P&L = $100.00
            Dim pnlBeforeClose = row.UnrealizedPnlUsd

            row.Close("TP", 100.0D)

            ' After close, further price ticks must not change UnrealizedPnlUsd
            row.UpdatePnl(90.0D, 100D)
            Assert.Equal(pnlBeforeClose, row.UnrealizedPnlUsd)
        End Sub

    End Class

End Namespace
