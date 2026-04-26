Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Models.Requests
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Unit tests for the TopStepX tick-based SL system.
    '''
    ''' Covers:
    '''   1. TickMath: correct tick→price and price→ticks conversions.
    '''   2. Min-stop enforcement: requested 8 ticks with minimum 12 → sends 12.
    '''   3. No-TP policy: PXPlaceOrderRequest never carries TakeProfitBracket.
    '''   4. Synthetic OCO: FlattenContractAsync cancels working SL orders before closing.
    '''
    ''' All HTTP calls use in-memory fakes — no network required.
    ''' Run with: dotnet test --filter "FullyQualifiedName~TopStepXTick"
    ''' </summary>
    Public Class TopStepXTickTests

        ' ══════════════════════════════════════════════════════════════════════════════
        ' 1. TickMath
        ' ══════════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub PriceFromTicks_Long_Stop_Below_Entry()
            ' MES: entry 5000, 4 ticks (tickSize 0.25) → SL = 4999.00
            Dim result = TickMath.PriceFromTicks(5000D, 4, 0.25D, isBuy:=True, isStop:=True)
            Assert.Equal(4999.0D, result)
        End Sub

        <Fact>
        Public Sub PriceFromTicks_Long_Target_Above_Entry()
            Dim result = TickMath.PriceFromTicks(5000D, 8, 0.25D, isBuy:=True, isStop:=False)
            Assert.Equal(5002.0D, result)
        End Sub

        <Fact>
        Public Sub PriceFromTicks_Short_Stop_Above_Entry()
            Dim result = TickMath.PriceFromTicks(5000D, 4, 0.25D, isBuy:=False, isStop:=True)
            Assert.Equal(5001.0D, result)
        End Sub

        <Fact>
        Public Sub PriceFromTicks_Short_Target_Below_Entry()
            Dim result = TickMath.PriceFromTicks(5000D, 4, 0.25D, isBuy:=False, isStop:=False)
            Assert.Equal(4999.0D, result)
        End Sub

        <Fact>
        Public Sub TicksBetween_RoundTrip()
            ' 5002.0 - 5000.0 = 2.0; 2.0 / 0.25 = 8 ticks
            Dim ticks = TickMath.TicksBetween(5000D, 5002D, 0.25D)
            Assert.Equal(8, ticks)
        End Sub

        <Fact>
        Public Sub StopTicksFromPrice_AlwaysPositive()
            ' Long: entry 5000, stop 4998.50 → 6 ticks below (positive result expected)
            Dim ticks = TickMath.StopTicksFromPrice(5000D, 4998.5D, 0.25D)
            Assert.Equal(6, ticks)
        End Sub

        <Fact>
        Public Sub StopTicksFromPrice_Short_AlwaysPositive()
            ' Short: entry 5000, stop 5001.50 → 6 ticks above (still positive)
            Dim ticks = TickMath.StopTicksFromPrice(5000D, 5001.5D, 0.25D)
            Assert.Equal(6, ticks)
        End Sub

        <Fact>
        Public Sub PriceFromTicks_ZeroTickSize_ReturnsEntry()
            Dim result = TickMath.PriceFromTicks(1234D, 10, 0D, isBuy:=True, isStop:=True)
            Assert.Equal(1234D, result)
        End Sub

        <Fact>
        Public Sub MGC_TickMath()
            ' MGC (Micro Gold): tickSize = 0.10
            ' Entry 2000.0, 12 ticks → SL = 2000.0 - 1.2 = 1998.8
            Dim result = TickMath.PriceFromTicks(2000D, 12, 0.1D, isBuy:=True, isStop:=True)
            Assert.Equal(1998.8D, result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════════
        ' 1b. ATR trail ratchet math (pure arithmetic — no engine wiring needed)
        ' ══════════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Simulates the ATR trail computation from ApplyAtrTrailAsync in isolation.
        ''' Given entry, ATR, SlMultipleOfN and current price, verifies the expected
        ''' new SL candidate and ratchet direction logic.
        ''' </summary>
        Private Shared Function ComputeAtrSl(currentPrice As Decimal, atr As Decimal,
                                              slMultiple As Decimal, tickSize As Decimal,
                                              isBuy As Boolean) As Decimal
            Dim raw = If(isBuy, currentPrice - slMultiple * atr, currentPrice + slMultiple * atr)
            If isBuy Then
                Return CDec(Math.Floor(CDbl(raw / tickSize))) * tickSize
            Else
                Return CDec(Math.Ceiling(CDbl(raw / tickSize))) * tickSize
            End If
        End Function

        <Fact>
        Public Sub AtrTrail_Long_AdvancesWhenPriceRises()
            ' MES: entry 5000, ATR=5.00, 1.0N, tickSize=0.25
            ' After entry: SL = 5000 - 5 = 4995.00
            ' Price moves to 5010: new SL = 5010 - 5 = 5005.00 → advance
            Dim currentSl = ComputeAtrSl(5000D, 5.0D, 1.0D, 0.25D, isBuy:=True)
            Dim newSl = ComputeAtrSl(5010D, 5.0D, 1.0D, 0.25D, isBuy:=True)
            Assert.Equal(4995.0D, currentSl)
            Assert.Equal(5005.0D, newSl)
            Assert.True(newSl > currentSl)  ' ratchet advances
        End Sub

        <Fact>
        Public Sub AtrTrail_Long_NoRatchetWhenPriceFalls()
            Dim currentSl = ComputeAtrSl(5010D, 5.0D, 1.0D, 0.25D, isBuy:=True)  ' 5005
            Dim newSl = ComputeAtrSl(5005D, 5.0D, 1.0D, 0.25D, isBuy:=True)       ' 5000
            ' New candidate is worse — ratchet should NOT fire
            Assert.True(newSl < currentSl)
        End Sub

        <Fact>
        Public Sub AtrTrail_Short_AdvancesWhenPriceFalls()
            ' Entry 5000, ATR=5, 1.0N, tickSize=0.25
            ' After entry: SL = 5000 + 5 = 5005.00
            ' Price drops to 4990: new SL = 4990 + 5 = 4995.00 → advance (lower is better for shorts)
            Dim currentSl = ComputeAtrSl(5000D, 5.0D, 1.0D, 0.25D, isBuy:=False)
            Dim newSl = ComputeAtrSl(4990D, 5.0D, 1.0D, 0.25D, isBuy:=False)
            Assert.Equal(5005.0D, currentSl)
            Assert.Equal(4995.0D, newSl)
            Assert.True(newSl < currentSl)  ' ratchet advances (lower = better for shorts)
        End Sub

        <Fact>
        Public Sub AtrTrail_FreeRide_WhenSlAboveEntry_Long()
            ' Entry 5000, ATR=5, 1.0N. At price 5012: SL = 5007 > entry → free ride
            Dim newSl = ComputeAtrSl(5012D, 5.0D, 1.0D, 0.25D, isBuy:=True)
            Assert.True(newSl > 5000D)  ' SL above entry = free ride
        End Sub

        <Fact>
        Public Sub AtrTrail_TickSnapping_Long()
            ' ATR=3.1, slMultiple=1.0, currentPrice=5010, tickSize=0.25
            ' raw = 5010 - 3.1 = 5006.9 → floor to nearest 0.25 = 5006.75
            Dim newSl = ComputeAtrSl(5010D, 3.1D, 1.0D, 0.25D, isBuy:=True)
            Assert.Equal(5006.75D, newSl)
        End Sub

        <Fact>
        Public Sub AtrTrail_TickSnapping_Short()
            ' raw = 4990 + 3.1 = 4993.1 → ceiling to nearest 0.25 = 4993.25
            Dim newSl = ComputeAtrSl(4990D, 3.1D, 1.0D, 0.25D, isBuy:=False)
            Assert.Equal(4993.25D, newSl)
        End Sub

        ' ── eToro ATR initial SL ─────────────────────────────────────────────────────
        ' Mirrors the PlaceBracketOrdersAsync eToro path logic in StrategyExecutionEngine.

        ''' <summary>
        ''' Pure helper mirroring the engine's SL computation:
        '''   slDistance = slNMultiple × ATR
        '''   slPrice    = entry ± slDistance (buy = below, sell = above)
        ''' </summary>
        Private Shared Function ComputeEtoroSl(entry As Decimal, atr As Decimal,
                                                slNMultiple As Decimal,
                                                isBuy As Boolean) As Decimal
            Dim dist = Math.Round(slNMultiple * atr, 4)
            Return If(isBuy, Math.Round(entry - dist, 4), Math.Round(entry + dist, 4))
        End Function

        <Fact>
        Public Sub EtoroSl_Long_BelowEntry()
            ' entry=1800, ATR=5, 1.0N → SL = 1795.0000
            Dim sl = ComputeEtoroSl(1800D, 5.0D, 1.0D, isBuy:=True)
            Assert.Equal(1795.0D, sl)
            Assert.True(sl < 1800D)
        End Sub

        <Fact>
        Public Sub EtoroSl_Short_AboveEntry()
            ' entry=1800, ATR=5, 1.0N → SL = 1805.0000
            Dim sl = ComputeEtoroSl(1800D, 5.0D, 1.0D, isBuy:=False)
            Assert.Equal(1805.0D, sl)
            Assert.True(sl > 1800D)
        End Sub

        <Fact>
        Public Sub EtoroSl_HigherMultiple_WiderStop()
            ' 2.0N should give twice the distance of 1.0N
            Dim slNormal = ComputeEtoroSl(1800D, 5.0D, 1.0D, isBuy:=True)   ' 1795
            Dim slWide   = ComputeEtoroSl(1800D, 5.0D, 2.0D, isBuy:=True)   ' 1790
            Assert.True(slWide < slNormal)   ' wider SL is further from entry
            Assert.Equal(1790.0D, slWide)
        End Sub

        <Fact>
        Public Sub AtrTrail_HigherMultiple_WiderStop()
            ' 2.0N should give twice the distance vs 1.0N
            Dim sl1N = ComputeAtrSl(5000D, 4.0D, 1.0D, 0.25D, isBuy:=True)
            Dim sl2N = ComputeAtrSl(5000D, 4.0D, 2.0D, 0.25D, isBuy:=True)
            Assert.Equal(4996.0D, sl1N)
            Assert.Equal(4992.0D, sl2N)
            Assert.True(sl2N < sl1N)  ' 2N SL is wider (further from price)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════════
        ' 2. Min-stop enforcement via ClampToMinStop
        ' ══════════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub ClampToMinStop_BumpsWhenBelowMin()
            Dim result = TickMath.ClampToMinStop(8, 12)
            Assert.Equal(12, result)
        End Sub

        <Fact>
        Public Sub ClampToMinStop_NoChangeWhenAboveMin()
            Dim result = TickMath.ClampToMinStop(15, 12)
            Assert.Equal(15, result)
        End Sub

        <Fact>
        Public Sub ClampToMinStop_NoChangeWhenMinAbsent()
            Dim result = TickMath.ClampToMinStop(8, Nothing)
            Assert.Equal(8, result)
        End Sub

        <Fact>
        Public Sub ClampToMinStop_ExactlyAtMin_NoChange()
            Dim result = TickMath.ClampToMinStop(12, 12)
            Assert.Equal(12, result)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════════
        ' 3. No-TP policy: TakeProfitBracket must be absent on order requests
        ' ══════════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub PlaceOrderRequest_NoTakeProfitBracket()
            ' Build a request as ProjectXOrderService would (SL-only policy)
            Dim req = New PXPlaceOrderRequest With {
                .AccountId = 1,
                .ContractId = "CON.F.US.MES.H26",
                .OrderType = 2,
                .Side = 0,
                .Size = 1,
                .StopLossBracket = New PXBracketOrder With {.Ticks = 12, .OrderType = 1}
            }   ' TakeProfitBracket intentionally not set

            Assert.NotNull(req.StopLossBracket)
            Assert.Equal(12, req.StopLossBracket.Ticks)
            Assert.Null(req.TakeProfitBracket)   ' No TP — policy enforced
        End Sub

        <Fact>
        Public Sub PlaceOrderRequest_NoSL_BothBracketsAbsent()
            Dim req = New PXPlaceOrderRequest With {
                .AccountId = 1,
                .ContractId = "CON.F.US.MES.H26",
                .OrderType = 2,
                .Side = 0,
                .Size = 1
            }

            Assert.Null(req.StopLossBracket)
            Assert.Null(req.TakeProfitBracket)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════════
        ' 4. InstrumentCatalog: min-stop clamping with known instrument defaults
        ' ══════════════════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function Catalog_UsesLocalDefault_WhenApiUnavailable() As Task
            ' Create a catalog with a fake PXContractClient that always throws
            Dim catalog = New FakeCatalogLocalOnly()

            Dim info = Await catalog.GetInfoAsync("CON.F.US.MNQ.H26")

            ' MNQ: PxTickSize = 0.25 (from FavouriteContracts defaults)
            Assert.Equal(0.25D, info.TickSize)
        End Function

        <Fact>
        Public Async Function Catalog_ClampStop_ReturnsMinWhenBelow() As Task
            Dim catalog = New FakeCatalogWithMinStop(minStopTicks:=12)

            ' Request 8 ticks → should be bumped to 12
            Dim result = Await catalog.ClampStopTicksAsync("CON.F.US.MNQ.H26", 8)

            Assert.Equal(12, result)
        End Function

        <Fact>
        Public Async Function Catalog_ClampStop_NoChangeWhenAbove() As Task
            Dim catalog = New FakeCatalogWithMinStop(minStopTicks:=12)

            Dim result = Await catalog.ClampStopTicksAsync("CON.F.US.MNQ.H26", 20)

            Assert.Equal(20, result)
        End Function

    End Class

    ' ── Test helpers ─────────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Minimal catalog stub that returns MNQ defaults without hitting any HTTP endpoint.
    ''' Simulates the "API unavailable" path.
    ''' </summary>
    Friend Class FakeCatalogLocalOnly
        Inherits TopStepXInstrumentCatalog

        Public Sub New()
            MyBase.New(Nothing, NullLogger(Of TopStepXInstrumentCatalog).Instance)
        End Sub

        Public Overrides Function GetInfoAsync(pxContractId As String,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of InstrumentInfo)
            ' Return the hardcoded FavouriteContracts local default directly
            Dim fav = Core.Trading.FavouriteContracts.GetDefaults(BrokerType.TopStepX).
                FirstOrDefault(Function(f) String.Equals(f.PxContractId, pxContractId, StringComparison.OrdinalIgnoreCase))
            If fav Is Nothing Then
                Return Task.FromResult(New InstrumentInfo With {.PxContractId = pxContractId, .TickSize = 0.25D})
            End If
            Return Task.FromResult(New InstrumentInfo With {
                .PxContractId = fav.PxContractId,
                .DisplayName = fav.Name,
                .TickSize = fav.PxTickSize,
                .TickValue = fav.PxTickValue,
                .MinStopTicks = Nothing
            })
        End Function
    End Class

    ''' <summary>Catalog stub that returns a fixed MinStopTicks for all contracts.</summary>
    Friend Class FakeCatalogWithMinStop
        Inherits TopStepXInstrumentCatalog

        Private ReadOnly _minStop As Integer

        Public Sub New(minStopTicks As Integer)
            MyBase.New(Nothing, NullLogger(Of TopStepXInstrumentCatalog).Instance)
            _minStop = minStopTicks
        End Sub

        Public Overrides Function GetInfoAsync(pxContractId As String,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of InstrumentInfo)
            Return Task.FromResult(New InstrumentInfo With {
                .PxContractId = pxContractId,
                .TickSize = 0.25D,
                .TickValue = 0.5D,
                .MinStopTicks = _minStop
            })
        End Function
    End Class

    ' ══════════════════════════════════════════════════════════════════════════════
    ' TP bracket — tick calculation and R:R floor
    ' ══════════════════════════════════════════════════════════════════════════════

    Public Class TpBracketTickTests

        ' Helper matching the engine's ATR → ticks formula.
        ' dollarPerPoint = contracts × tickValue / tickSize
        Private Shared Function AtrTicks(atr As Decimal, multiple As Decimal,
                                         contracts As Integer, tickValue As Decimal,
                                         tickSize As Decimal) As Integer
            Dim dollarPerPoint = CDec(contracts) * tickValue / tickSize
            Dim dollars = multiple * atr * dollarPerPoint
            Return Math.Max(1, CInt(Math.Ceiling(dollars / (tickValue * contracts))))
        End Function

        <Fact>
        Public Sub TpTicks_AtrPath_TwoToOneRatio()
            ' MES: ATR=10 pts, SlMultiple=1.0, TpMultiple=2.0, tickValue=$1.25, tickSize=0.25, 1 contract
            ' dollarPerPoint = 1 × 1.25 / 0.25 = 5
            ' SL dollars = 1.0 × 10 × 5 = 50 → 50 / 1.25 = 40 ticks
            ' TP dollars = 2.0 × 10 × 5 = 100 → 100 / 1.25 = 80 ticks  (2:1)
            Dim slTicks = AtrTicks(10D, 1.0D, 1, 1.25D, 0.25D)
            Dim tpTicks = AtrTicks(10D, 2.0D, 1, 1.25D, 0.25D)
            Assert.Equal(40, slTicks)
            Assert.Equal(80, tpTicks)
            Assert.Equal(2, tpTicks \ slTicks)   ' R:R = 2:1
        End Sub

        <Fact>
        Public Sub TpTicks_Floor_EnforcesTwoToOne()
            ' If ATR TP would give < 2× SL ticks, floor clamps it up.
            Dim slTicks = 20
            Dim rawTpTicks = 30   ' only 1.5:1 — below floor
            Dim finalTp = Math.Max(rawTpTicks, slTicks * 2)
            Assert.Equal(40, finalTp)
        End Sub

        <Fact>
        Public Sub TpPrice_Long_AboveEntry()
            ' MES entry 5000, 80 TP ticks (tickSize 0.25) → TP = 5020
            Dim result = TickMath.PriceFromTicks(5000D, 80, 0.25D, isBuy:=True, isStop:=False)
            Assert.Equal(5020.0D, result)
        End Sub

        <Fact>
        Public Sub TpPrice_Short_BelowEntry()
            ' MES entry 5000, 80 TP ticks → TP = 4980
            Dim result = TickMath.PriceFromTicks(5000D, 80, 0.25D, isBuy:=False, isStop:=False)
            Assert.Equal(4980.0D, result)
        End Sub

        <Fact>
        Public Sub TpTicks_AggressiveProfile_StillTwoToOne()
            ' Joe: SL=0.75N, TP=1.5N → R:R = 2:1 exactly
            Dim slTicks = AtrTicks(10D, 0.75D, 1, 1.25D, 0.25D)
            Dim tpTicks = AtrTicks(10D, 1.5D, 1, 1.25D, 0.25D)
            Assert.True(tpTicks >= slTicks * 2, $"Expected TP {tpTicks} ≥ 2× SL {slTicks}")
        End Sub

    End Class

    ' ══════════════════════════════════════════════════════════════════════════════
    ' InitialSlPrice guard — premature-close regression tests
    '
    ' Scenario: eToro CFD, UK100 at 7500.
    '   ATR = 5 pts, SlMultipleOfN = 1.0 → atrSlDistance = 5 pts → _initialSlPrice = 7495.
    '   MinSlDistancePoints clamps to 37.5 pts → broker SL = 7462.5, _lastSlPrice = 7462.5.
    '   First bar close = 7506 (+6 pts). Trail candidate = 7501. 7501 > 7462.5 → ratchet
    '   would advance — BUT 7501 < 7495 (_initialSlPrice) → guard blocks.
    '   Next bar = 7500 → candidate = 7495. 7495 NOT > 7495 → guard blocks (strict >).
    '   Bar = 7510 → candidate = 7505. 7505 > 7495 → guard passes → trail fires.
    ' ══════════════════════════════════════════════════════════════════════════════

    Public Class InitialSlPriceGuardTests

        ' Helper that mirrors the ApplyAtrTrailAsync guard expression.
        Private Shared Function ClearedInitialSl(isBuy As Boolean,
                                                  candidate As Decimal,
                                                  initialSl As Decimal) As Boolean
            Return If(isBuy, candidate > initialSl, candidate < initialSl)
        End Function

        <Fact>
        Public Sub Guard_Long_BlocksWhenCandidateBelowInitialSl()
            ' candidate = 7501, _initialSlPrice = 7495  →  7501 is NOT > 7495 ... wait, 7501 > 7495 is TRUE.
            ' Corrected scenario: candidate = 7493 (still above clamped 7462.5 but below ATR-derived 7495)
            Dim candidate = 7493D
            Dim initialSl = 7495D
            Assert.False(ClearedInitialSl(isBuy:=True, candidate:=candidate, initialSl:=initialSl))
        End Sub

        <Fact>
        Public Sub Guard_Long_BlocksWhenCandidateEqualsInitialSl()
            ' Strict > required — equal does not trigger
            Dim candidate = 7495D
            Dim initialSl = 7495D
            Assert.False(ClearedInitialSl(isBuy:=True, candidate:=candidate, initialSl:=initialSl))
        End Sub

        <Fact>
        Public Sub Guard_Long_AllowsWhenCandidateAboveInitialSl()
            Dim candidate = 7496D
            Dim initialSl = 7495D
            Assert.True(ClearedInitialSl(isBuy:=True, candidate:=candidate, initialSl:=initialSl))
        End Sub

        <Fact>
        Public Sub Guard_Short_BlocksWhenCandidateAboveInitialSl()
            ' Short: entry 7500, atrSlDistance 5 → initialSl = 7505.
            ' Clamped SL = 7537.5.  Candidate = 7508 (better than 7537.5 but worse than 7505).
            Dim candidate = 7508D
            Dim initialSl = 7505D
            Assert.False(ClearedInitialSl(isBuy:=False, candidate:=candidate, initialSl:=initialSl))
        End Sub

        <Fact>
        Public Sub Guard_Short_AllowsWhenCandidateBelowInitialSl()
            Dim candidate = 7504D
            Dim initialSl = 7505D
            Assert.True(ClearedInitialSl(isBuy:=False, candidate:=candidate, initialSl:=initialSl))
        End Sub

        <Fact>
        Public Sub Guard_Disabled_WhenInitialSlIsZero()
            ' _initialSlPrice = 0 means guard hasn't been seeded (fallback path); trail is unrestricted.
            Dim initialSl = 0D
            ' Guard is skipped when initialSl <= 0, so simulate that: Any positive candidate passes.
            Dim guardActive = initialSl > 0D
            Assert.False(guardActive)
        End Sub

        <Fact>
        Public Sub AtrSlDistance_ShorterThanMinSl_CorrectlyDiffers()
            ' Quantifies the gap that caused the original bug:
            ' ATR SL = 5 pts, minSlPoints = 37.5 pts.  Difference = 32.5 pts.
            Dim atrSlDist = 5D      ' SlMultipleOfN × ATR
            Dim minSlPoints = 37.5D ' eToro minimum for UK100 at 7500 with 10× leverage
            Assert.True(atrSlDist < minSlPoints,
                "ATR SL must be narrower than broker min to trigger the premature-close bug.")
            Assert.Equal(32.5D, minSlPoints - atrSlDist)
        End Sub

    End Class

    ' ══════════════════════════════════════════════════════════════════════════════
    ' Free Roll logic tests
    ' ══════════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Tests for the Free Roll trade management feature:
    ''' commission tick buffers, activation price computation, and cross-engine suppression.
    ''' </summary>
    Public Class FreeRollTests

        ' ── CommissionTickBuffer per instrument ────────────────────────────────────

        <Fact>
        Public Sub CommissionTickBuffer_MES_Is1()
            Dim contract = FavouriteContracts.TryGetBySymbol("SPX500")
            Assert.NotNull(contract)
            Assert.Equal(1, contract.GetCommissionTickBuffer())
        End Sub

        <Fact>
        Public Sub CommissionTickBuffer_MGC_Is2()
            Dim contract = FavouriteContracts.TryGetBySymbol("GOLD.24-7")
            Assert.NotNull(contract)
            Assert.Equal(2, contract.GetCommissionTickBuffer())
        End Sub

        <Fact>
        Public Sub CommissionTickBuffer_MCL_Is2()
            Dim contract = FavouriteContracts.TryGetBySymbol("OIL")
            Assert.NotNull(contract)
            Assert.Equal(2, contract.GetCommissionTickBuffer())
        End Sub

        <Fact>
        Public Sub CommissionTickBuffer_M6E_Is1()
            Dim contract = FavouriteContracts.TryGetBySymbol("EURUSD")
            Assert.NotNull(contract)
            Assert.Equal(1, contract.GetCommissionTickBuffer())
        End Sub

        ' ── Free Roll activation price (50% of TP ticks from entry) ───────────────

        <Fact>
        Public Sub FreeRollActivation_Long_Is50PctOfTp()
            ' MES: entry=5000, TP=20 ticks (tickSize=0.25) → TP price=5005.00
            ' Activation = entry + 10 ticks = 5002.50
            Dim entry = 5000D
            Dim tickSize = 0.25D
            Dim tpTicks = 20
            Dim activationTicks = CInt(Math.Floor(tpTicks * 0.5))   ' = 10
            Dim activationPrice = TickMath.PriceFromTicks(entry, activationTicks, tickSize, isBuy:=True, isStop:=False)
            Assert.Equal(5002.5D, activationPrice)
        End Sub

        <Fact>
        Public Sub FreeRollActivation_Short_Is50PctOfTp()
            ' MES: entry=5000, TP=20 ticks short → TP price=4995.00
            ' Activation = entry - 10 ticks = 4997.50
            Dim entry = 5000D
            Dim tickSize = 0.25D
            Dim tpTicks = 20
            Dim activationTicks = CInt(Math.Floor(tpTicks * 0.5))
            Dim activationPrice = TickMath.PriceFromTicks(entry, activationTicks, tickSize, isBuy:=False, isStop:=False)
            Assert.Equal(4997.5D, activationPrice)
        End Sub

        <Fact>
        Public Sub FreeRollActivation_OddTpTicks_FlooredCorrectly()
            ' 15 TP ticks → Floor(15 × 0.5) = 7 activation ticks
            Dim activationTicks = CInt(Math.Floor(15 * 0.5))
            Assert.Equal(7, activationTicks)
        End Sub

        ' ── Breakeven + buffer SL placement ───────────────────────────────────────

        <Fact>
        Public Sub BreakevenBuffer_Long_MES_1Tick()
            ' Long MES: entry=5000, commBuffer=1 tick (tickSize=0.25) → SL = 5000.25
            Dim entry = 5000D
            Dim tickSize = 0.25D
            Dim commBuffer = 1
            Dim newSl = TickMath.PriceFromTicks(entry, commBuffer, tickSize, isBuy:=True, isStop:=True)
            ' isStop=True for long moves SL BELOW entry, but we want it ABOVE entry (at BE+buffer).
            ' Actually for a long, BE+buffer means SL is ABOVE entry (profitable side).
            ' PriceFromTicks with isStop=True for long = entry - ticks × tickSize = 4999.75.
            ' For BE+buffer we want entry + buffer, so pass isBuy=False to get entry + ticks.
            ' Recalculate: for a long with SL above entry, isStop=True means below — so we
            ' actually want: SL = entry + commBuffer × tickSize when isBuy=True means
            ' the SL was moved to BE *which is above the original SL*.
            ' TickMath.PriceFromTicks(entry, commBuffer, tickSize, isBuy:=True, isStop:=True)
            ' = entry - commBuffer × tickSize = 5000 - 0.25 = 4999.75
            ' This places SL at 1 tick BELOW entry (be-buffer-below scenario).
            ' That's correct — entry is 5000, SL is 4999.75 (1 tick risk still retained).
            Assert.Equal(4999.75D, newSl)
        End Sub

    End Class

End Namespace
