Imports TopStepTrader.Core.Settings
Imports Xunit

Namespace TopStepTrader.Tests.Settings

    ''' <summary>
    ''' BUG-78: covers the P&amp;L Guard aggregation semantics. The guard now consumes
    ''' the sum of <c>UnrealizedPnl</c> across all open slots on the same instrument
    ''' (computed in <c>SuperTrendPlusViewModel</c>); these tests pin the
    ''' <see cref="PnLGuardSettings.ShouldFlatten"/> contract that the call-site relies on.
    ''' </summary>
    Public Class PnLGuardSettingsTests

        <Fact>
        Public Sub ShouldFlatten_SingleSlot_BelowTakeProfit_DoesNotFire()
            Dim guard As New PnLGuardSettings With {
                .TakeProfitThreshold = PnLGuardThreshold.D50,
                .StopLossThreshold = PnLGuardThreshold.D100
            }
            Assert.False(guard.ShouldFlatten(40D))
        End Sub

        <Fact>
        Public Sub ShouldFlatten_SingleSlot_AtTakeProfit_Fires()
            Dim guard As New PnLGuardSettings With {
                .TakeProfitThreshold = PnLGuardThreshold.D50,
                .StopLossThreshold = PnLGuardThreshold.D100
            }
            Assert.True(guard.ShouldFlatten(50D))
        End Sub

        <Fact>
        Public Sub ShouldFlatten_AggregatedTwoSlots_BreachTakeProfit()
            ' Two scale-in slots on the same instrument: $30 + $25 = $55 ≥ $50 TP.
            ' Neither slot alone would breach; the aggregated total must.
            Dim guard As New PnLGuardSettings With {
                .TakeProfitThreshold = PnLGuardThreshold.D50,
                .StopLossThreshold = PnLGuardThreshold.D100
            }
            Dim slotA As Decimal = 30D
            Dim slotB As Decimal = 25D
            Assert.False(guard.ShouldFlatten(slotA))
            Assert.False(guard.ShouldFlatten(slotB))
            Assert.True(guard.ShouldFlatten(slotA + slotB))
        End Sub

        <Fact>
        Public Sub ShouldFlatten_AggregatedTwoSlots_BreachStopLoss()
            ' Two losing scale-in slots: -$60 + -$50 = -$110 ≤ -$100 SL.
            Dim guard As New PnLGuardSettings With {
                .TakeProfitThreshold = PnLGuardThreshold.D50,
                .StopLossThreshold = PnLGuardThreshold.D100
            }
            Dim slotA As Decimal = -60D
            Dim slotB As Decimal = -50D
            Assert.False(guard.ShouldFlatten(slotA))
            Assert.False(guard.ShouldFlatten(slotB))
            Assert.True(guard.ShouldFlatten(slotA + slotB))
        End Sub

        <Fact>
        Public Sub ShouldFlatten_BothThresholdsOff_NoOp()
            Dim guard As New PnLGuardSettings With {
                .TakeProfitThreshold = PnLGuardThreshold.Off,
                .StopLossThreshold = PnLGuardThreshold.Off
            }
            Assert.False(guard.IsActive)
            Assert.False(guard.ShouldFlatten(9999D))
            Assert.False(guard.ShouldFlatten(-9999D))
        End Sub

    End Class

End Namespace
