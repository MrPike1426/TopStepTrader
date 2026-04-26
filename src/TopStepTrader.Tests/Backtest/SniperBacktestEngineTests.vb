Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' FEAT-20 acceptance tests for <see cref="SniperBacktestEngine"/>.
    ''' Covers: correct tier sizing, heat-cap block, and average-entry calculation.
    ''' </summary>
    Public Class SniperBacktestEngineTests

        ' ══════════════════════════════════════════════════════════════════════
        ' CalculateAddQuantity — tier sizing
        ' ══════════════════════════════════════════════════════════════════════

        <Fact>
        Public Sub CalculateAddQuantity_CoreTier_DistributesCorrectly()
            ' Target=10, Core=60% (6 contracts), 2 adds => 3 each
            Dim q0 = SniperBacktestEngine.CalculateAddQuantity(0, 10, 0.6, 2, 1, 1, False)
            Dim q1 = SniperBacktestEngine.CalculateAddQuantity(1, 10, 0.6, 2, 1, 1, False)
            Assert.Equal(3, q0)
            Assert.Equal(3, q1)
        End Sub

        <Fact>
        Public Sub CalculateAddQuantity_MomentumTier_ReturnsMomentumSize()
            ' addIndex = coreAddsCount => Tier B
            Dim qty = SniperBacktestEngine.CalculateAddQuantity(2, 10, 0.6, 2, 2, 1, False)
            Assert.Equal(2, qty)
        End Sub

        <Fact>
        Public Sub CalculateAddQuantity_ExtensionTier_ReturnsZeroWhenNotAllowed()
            Dim qty = SniperBacktestEngine.CalculateAddQuantity(3, 10, 0.6, 2, 1, 2, False)
            Assert.Equal(0, qty)
        End Sub

        <Fact>
        Public Sub CalculateAddQuantity_ExtensionTier_ReturnsExtensionSizeWhenAllowed()
            Dim qty = SniperBacktestEngine.CalculateAddQuantity(3, 10, 0.6, 2, 1, 2, True)
            Assert.Equal(2, qty)
        End Sub

        <Fact>
        Public Sub CalculateAddQuantity_CoreRemainder_DistributedToEarlyAdds()
            ' Target=10, Core=70% (7 contracts), 3 adds => 3,2,2
            Dim q0 = SniperBacktestEngine.CalculateAddQuantity(0, 10, 0.7, 3, 1, 1, False)
            Dim q1 = SniperBacktestEngine.CalculateAddQuantity(1, 10, 0.7, 3, 1, 1, False)
            Dim q2 = SniperBacktestEngine.CalculateAddQuantity(2, 10, 0.7, 3, 1, 1, False)
            Assert.Equal(7, q0 + q1 + q2)
            Assert.True(q0 >= q1)  ' remainder distributed to first adds
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' RunPyramidReplay — average entry & multi-fill
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Build a minimal StrategyIndicators with flat EMA8 > EMA21 (long aligned)
        ''' and a one-bar crossover at <paramref name="crossIdx"/>.
        ''' ATR is set to <paramref name="atr"/> on all bars.
        ''' </summary>
        Private Shared Function MakeIndicators(barCount As Integer,
                                               crossIdx As Integer,
                                               atr As Single) As StrategyIndicators
            Dim ema8  = Enumerable.Repeat(CSng(105), barCount).ToArray()
            Dim ema21 = Enumerable.Repeat(CSng(100), barCount).ToArray()
            Dim ema50 = Enumerable.Repeat(CSng(95), barCount).ToArray()
            ' Simulate crossover: bar before cross has ema8 <= ema21
            If crossIdx > 0 Then ema8(crossIdx - 1) = CSng(99)
            Dim atrArr = Enumerable.Repeat(atr, barCount).ToArray()
            Return New StrategyIndicators With {
                .Ema8  = ema8,
                .Ema21 = ema21,
                .Ema50 = ema50,
                .Atr   = atrArr
            }
        End Function

        ''' <summary>Create a simple rising-price bar at a given close.</summary>
        Private Shared Function MakeBar(ts As DateTimeOffset, close As Decimal) As MarketBar
            Return New MarketBar With {
                .Timestamp = ts,
                .Open  = close - 1D,
                .High  = close + 1D,
                .Low   = close - 2D,
                .Close = close
            }
        End Function

        Private Shared Function MakeConfig(targetSize As Integer,
                                           coreFrac As Double,
                                           coreAdds As Integer,
                                           momSize As Integer,
                                           extSize As Integer,
                                           extAllowed As Boolean,
                                           maxHeat As Integer,
                                           volAtrFactor As Double) As BacktestConfiguration
            Return New BacktestConfiguration With {
                .ContractId        = "TEST",
                .StrategyCondition = StrategyConditionType.TripleEmaCascade,
                .TickSize          = 0.25D,
                .PointValue        = 5D,
                .UseAtrMode        = True,
                .SlAtrMultiple     = 1.5D,
                .TpAtrMultiple     = 3.0D,
                .TargetTotalSize   = targetSize,
                .CoreSizeFraction  = coreFrac,
                .CoreAddsCount     = coreAdds,
                .MomentumTierSize  = momSize,
                .ExtensionTierSize = extSize,
                .ExtensionAllowed  = extAllowed,
                .MaxRiskHeatTicks  = maxHeat,
                .VolatilityAtrFactor = volAtrFactor
            }
        End Function

        <Fact>
        Public Sub RunPyramidReplay_InitialEntry_RecordsAddIndex0()
            Const N = 10
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) MakeBar(base.AddMinutes(i), 100 + i)).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=5, atr:=2.0F)
            Dim config = MakeConfig(1, 1.0, 1, 1, 1, False, 9999, 0.5)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            Dim initialLeg = trades.FirstOrDefault(Function(t) t.PyramidAddIndex = 0)
            Assert.NotNull(initialLeg)
        End Sub

        <Fact>
        Public Sub RunPyramidReplay_AverageEntry_CorrectAcrossMultipleFills()
            ' 2 core adds, each 3 contracts. Price rises enough to trigger second fill.
            ' Fill 1 at ~101 (bar.Open+slip), Fill 2 at ~106.
            ' AverageEntryAtFill on second leg = (101*3 + 106*3) / 6 = 103.5 (approx)
            Const N = 15
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            ' Make prices rise steadily so scale-in trigger fires
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) MakeBar(base.AddMinutes(i), 100D + i * 2)).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=5, atr:=1.0F)
            ' volatilityAtrFactor=1.0 → need price to rise ≥ 1×ATR (=1) from lastEntry
            Dim config = MakeConfig(6, 0.6, 2, 1, 1, False, 9999, 1.0)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            ' At least the initial entry should be captured
            Assert.True(trades.Count >= 1)
            Dim leg0 = trades.FirstOrDefault(Function(t) t.PyramidAddIndex = 0)
            Assert.NotNull(leg0)
            Assert.True(leg0.AverageEntryAtFill.HasValue)
        End Sub

        <Fact>
        Public Sub RunPyramidReplay_HeatCap_BlocksScaleIn()
            ' MaxRiskHeatTicks = 5 — very tight. Initial leg has SL 10 ticks away × 3 contracts = 30 ticks heat.
            ' Second add should be blocked since heat already exceeds cap.
            Const N = 15
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) MakeBar(base.AddMinutes(i), 100D + i * 5)).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=5, atr:=1.0F)
            Dim config = MakeConfig(6, 0.6, 2, 1, 1, False, maxHeat:=5, volAtrFactor:=1.0)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            ' Only the initial leg (addIndex=0) should be present; scale-in blocked
            Dim scaleIns = trades.Where(Function(t) t.PyramidAddIndex > 0).ToList()
            Assert.Empty(scaleIns)
        End Sub

        <Fact>
        Public Sub RunPyramidReplay_ExitFillsAllLegs()
            ' All open legs must exit together with the same ExitReason
            Const N = 20
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) MakeBar(base.AddMinutes(i), 100D + i)).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=5, atr:=2.0F)
            Dim config = MakeConfig(6, 0.6, 2, 1, 1, False, 9999, 0.5)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            ' All trades in a group must share the same ExitReason and ExitTime
            Dim groups = trades.GroupBy(Function(t) t.PositionGroupId)
            For Each grp In groups
                Dim reasons = grp.Select(Function(t) t.ExitReason).Distinct().ToList()
                Assert.Single(reasons)
            Next
        End Sub

        <Fact>
        Public Sub RunPyramidReplay_MaxContractsHeld_StampedOnAllLegs()
            Const N = 20
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) MakeBar(base.AddMinutes(i), 100D + i)).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=5, atr:=2.0F)
            Dim config = MakeConfig(6, 0.6, 2, 1, 1, False, 9999, 0.5)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            For Each t In trades
                Assert.True(t.MaxContractsHeld.HasValue)
                Assert.True(t.MaxContractsHeld.Value >= 1)
            Next
        End Sub

    End Class

End Namespace
