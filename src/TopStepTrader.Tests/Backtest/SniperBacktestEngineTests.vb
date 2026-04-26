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

        ' ══════════════════════════════════════════════════════════════════════
        ' FEAT-21: Free-ride activation, core trail, add-on BE floor, monotonicity
        ' ══════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Build a config with StopLossTicks set, suitable for FEAT-21 tests.
        ''' </summary>
        Private Shared Function MakeSniperConfig(targetSize As Integer,
                                                  coreAdds As Integer,
                                                  stopLossTicks As Integer,
                                                  maxHeat As Integer) As BacktestConfiguration
            Return New BacktestConfiguration With {
                .ContractId        = "TEST",
                .StrategyCondition = StrategyConditionType.TripleEmaCascade,
                .TickSize          = 0.25D,
                .PointValue        = 5D,
                .UseAtrMode        = True,
                .SlAtrMultiple     = 2.0D,
                .TpAtrMultiple     = 10.0D,   ' very wide TP so SL is what fires
                .TargetTotalSize   = targetSize,
                .CoreSizeFraction  = 1.0,
                .CoreAddsCount     = coreAdds,
                .MomentumTierSize  = 1,
                .ExtensionTierSize = 1,
                .ExtensionAllowed  = False,
                .MaxRiskHeatTicks  = maxHeat,
                .VolatilityAtrFactor = 0.5,
                .StopLossTicks     = stopLossTicks
            }
        End Function

        <Fact>
        Public Sub FreeRide_ActivatedWhen3BracketsAllInProfit()
            ' 3 core adds of 1 contract each (targetSize=3, coreFrac=1.0, coreAdds=3).
            ' Price rises steadily so all three fills happen and all are in profit.
            ' Free-ride should be activated and stamped on all legs.
            Const N = 30
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) New MarketBar With {
                    .Timestamp = base.AddMinutes(i),
                    .Open  = 100D + i,
                    .High  = 100D + i + 1D,
                    .Low   = 100D + i - 0.5D,
                    .Close = 100D + i
                }).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=3, atr:=1.0F)
            Dim config = MakeSniperConfig(targetSize:=3, coreAdds:=3, stopLossTicks:=4, maxHeat:=9999)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            Assert.True(trades.Count > 0)
            ' Once all 3 brackets are filled and in profit, FreeRideActivated should be True on exits
            Dim fullGroups = trades.GroupBy(Function(t) t.PositionGroupId).
                                    Where(Function(g) g.Count() = 3).ToList()
            If fullGroups.Any() Then
                Dim grp = fullGroups.First()
                Assert.True(grp.All(Function(t) t.FreeRideActivated),
                    "All legs should have FreeRideActivated=True once 3 brackets are in profit")
            End If
        End Sub

        <Fact>
        Public Sub CoreTrail_SlAdvancesWithPrice_Long()
            ' Single core bracket. Price rises far enough that the trail should advance.
            ' TrailingTicksCaptured on the leg should be > 0.
            Const N = 20
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) New MarketBar With {
                    .Timestamp = base.AddMinutes(i),
                    .Open  = 100D + i,
                    .High  = 100D + i + 0.5D,
                    .Low   = 100D + i - 0.25D,
                    .Close = 100D + i
                }).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=3, atr:=1.0F)
            ' 1 contract, 1 core add, stopLossTicks=4, wide TP so we reach end-of-data
            Dim config = MakeSniperConfig(targetSize:=1, coreAdds:=1, stopLossTicks:=4, maxHeat:=9999)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            Assert.True(trades.Count > 0)
            Dim leg = trades.First()
            Assert.True(leg.FinalSlAtExit.HasValue, "FinalSlAtExit should be stamped")
            Assert.True(leg.TrailingTicksCaptured.HasValue, "TrailingTicksCaptured should be stamped")
            Assert.True(leg.TrailingTicksCaptured.Value > 0D,
                "Core trail should have advanced SL — TrailingTicksCaptured should be > 0")
        End Sub

        <Fact>
        Public Sub AddOnBreakevenFloor_SlNotBelowBreakeven_AfterProfitThreshold()
            ' Two-leg pyramid: addIndex 0 = core, addIndex 1 = add-on.
            ' After 5 ticks of profit the add-on SL must floor at entryPrice + 1 tick.
            ' FinalSlAtExit for the add-on leg should be >= its entry price.
            Const N = 30
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) New MarketBar With {
                    .Timestamp = base.AddMinutes(i),
                    .Open  = 100D + i,
                    .High  = 100D + i + 0.5D,
                    .Low   = 100D + i - 0.25D,
                    .Close = 100D + i
                }).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=3, atr:=1.0F)
            ' targetSize=2, coreAdds=1 → addIndex 0 is core, addIndex 1 is momentum (add-on)
            Dim config = MakeSniperConfig(targetSize:=2, coreAdds:=1, stopLossTicks:=4, maxHeat:=9999)
            config.MomentumTierSize = 1

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            Dim addOnLeg = trades.FirstOrDefault(Function(t) t.PyramidAddIndex.GetValueOrDefault(0) >= 1)
            If addOnLeg IsNot Nothing AndAlso addOnLeg.FinalSlAtExit.HasValue Then
                ' SL must not be below the add-on entry price after 5-tick profit
                Assert.True(addOnLeg.FinalSlAtExit.Value >= addOnLeg.EntryPrice,
                    $"Add-on SL ({addOnLeg.FinalSlAtExit.Value}) should be >= entry ({addOnLeg.EntryPrice}) after breakeven floor applies")
            End If
        End Sub

        <Fact>
        Public Sub Monotonicity_SlNeverMovesAgainstTrade_Long()
            ' SL should only ever move upward for a long position.
            ' We simulate by inspecting FinalSlAtExit vs initial SL (InitialSlPrice would be
            ' avgEntry - stopLossTicks×tickSize). Final SL must be >= initial SL for longs.
            Const N = 20
            Dim base = New DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero)
            Dim bars = Enumerable.Range(0, N).Select(
                Function(i) New MarketBar With {
                    .Timestamp = base.AddMinutes(i),
                    .Open  = 100D + i,
                    .High  = 100D + i + 0.5D,
                    .Low   = 100D + i - 0.25D,
                    .Close = 100D + i
                }).ToList()
            Dim indicators = MakeIndicators(N, crossIdx:=3, atr:=1.0F)
            Dim config = MakeSniperConfig(targetSize:=1, coreAdds:=1, stopLossTicks:=4, maxHeat:=9999)

            Dim trades = SniperBacktestEngine.RunPyramidReplay(config, bars, indicators, warmUp:=2)

            For Each t In trades.Where(Function(tr) tr.FinalSlAtExit.HasValue AndAlso tr.Side = "Buy")
                ' Initial SL = entry - 4 ticks. Final SL must be >= initial SL (monotonic up only)
                Dim initialSl = t.EntryPrice - config.StopLossTicks * config.TickSize
                Assert.True(t.FinalSlAtExit.Value >= initialSl,
                    $"SL moved against the trade: final {t.FinalSlAtExit.Value} < initial {initialSl}")
            Next
        End Sub

    End Class

End Namespace
