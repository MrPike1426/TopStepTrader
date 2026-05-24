Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>FEAT-63: tests for the $-denominated TP-ladder calculator. Pure-math —
    ''' no DI graph, no broker, no DB. Verifies the rung boundaries from the design
    ''' (TP=$50: $55→1, $105→2, $155→3) and the price-conversion math for both sides.</summary>
    Public Class LadderStopCalculatorTests

        ' ── ComputeRung ──────────────────────────────────────────────────────

        <Fact>
        Public Sub Rung_DisabledWhenTpZero()
            Assert.Equal(0, LadderStopCalculator.ComputeRung(currentPnl:=500D, tp:=0D))
        End Sub

        <Fact>
        Public Sub Rung_ZeroWhenPnlNonPositive()
            Assert.Equal(0, LadderStopCalculator.ComputeRung(currentPnl:=0D, tp:=50D))
            Assert.Equal(0, LadderStopCalculator.ComputeRung(currentPnl:=-12.5D, tp:=50D))
        End Sub

        <Fact>
        Public Sub Rung_BelowFirstTriggerReturnsZero()
            ' Trigger 1 is at $50 + 10% = $55. $54 is below.
            Assert.Equal(0, LadderStopCalculator.ComputeRung(currentPnl:=54D, tp:=50D))
        End Sub

        <Fact>
        Public Sub Rung_FirstTriggerAtBufferAbove()
            Assert.Equal(1, LadderStopCalculator.ComputeRung(currentPnl:=55D, tp:=50D))
        End Sub

        <Fact>
        Public Sub Rung_BelowSecondStaysAtFirst()
            Assert.Equal(1, LadderStopCalculator.ComputeRung(currentPnl:=104D, tp:=50D))
        End Sub

        <Fact>
        Public Sub Rung_SecondTriggerAtBufferAbove()
            Assert.Equal(2, LadderStopCalculator.ComputeRung(currentPnl:=105D, tp:=50D))
        End Sub

        <Fact>
        Public Sub Rung_ThirdTriggerAtBufferAbove()
            Assert.Equal(3, LadderStopCalculator.ComputeRung(currentPnl:=155D, tp:=50D))
        End Sub

        <Fact>
        Public Sub Rung_ScaleInJumpAdvancesPastMultipleRungs()
            ' Scale-in can push total P&L well past several rungs in one instant.
            ' floor(1000/50 - 0.10) = floor(19.9) = 19.
            Assert.Equal(19, LadderStopCalculator.ComputeRung(currentPnl:=1000D, tp:=50D))
        End Sub

        ' ── ComputeLadderStopPrice ───────────────────────────────────────────

        <Fact>
        Public Sub Price_RungZeroReturnsNothing()
            Dim sl = LadderStopCalculator.ComputeLadderStopPrice(
                rung:=0, tp:=50D, entryPrice:=100D,
                isBuy:=True, contracts:=1, tickSize:=0.25D, tickValue:=5D)
            Assert.False(sl.HasValue)
        End Sub

        <Fact>
        Public Sub Price_LongRung2_LocksHundredDollars()
            ' 2 contracts × $5/tick × n ticks = $100  ⇒  n = 10 ticks  ⇒  priceDelta = 10 × 0.25 = $2.50
            Dim sl = LadderStopCalculator.ComputeLadderStopPrice(
                rung:=2, tp:=50D, entryPrice:=100D,
                isBuy:=True, contracts:=2, tickSize:=0.25D, tickValue:=5D)
            Assert.True(sl.HasValue)
            Assert.Equal(102.5D, sl.Value)
        End Sub

        <Fact>
        Public Sub Price_ShortRung2_MirrorsLong()
            ' Same instrument, same rung, short side ⇒ SL is below entry by the same delta.
            Dim sl = LadderStopCalculator.ComputeLadderStopPrice(
                rung:=2, tp:=50D, entryPrice:=100D,
                isBuy:=False, contracts:=2, tickSize:=0.25D, tickValue:=5D)
            Assert.True(sl.HasValue)
            Assert.Equal(97.5D, sl.Value)
        End Sub

        <Fact>
        Public Sub Price_LongRung1_SingleContract()
            ' 1 contract × $5/tick × n ticks = $50  ⇒  n = 10 ticks  ⇒  priceDelta = $2.50
            Dim sl = LadderStopCalculator.ComputeLadderStopPrice(
                rung:=1, tp:=50D, entryPrice:=4500D,
                isBuy:=True, contracts:=1, tickSize:=0.25D, tickValue:=5D)
            Assert.Equal(4502.5D, sl.Value)
        End Sub

        <Fact>
        Public Sub Price_NonPositiveMetadataReturnsNothing()
            ' tickSize 0
            Assert.False(LadderStopCalculator.ComputeLadderStopPrice(
                rung:=1, tp:=50D, entryPrice:=100D,
                isBuy:=True, contracts:=1, tickSize:=0D, tickValue:=5D).HasValue)
            ' tickValue 0
            Assert.False(LadderStopCalculator.ComputeLadderStopPrice(
                rung:=1, tp:=50D, entryPrice:=100D,
                isBuy:=True, contracts:=1, tickSize:=0.25D, tickValue:=0D).HasValue)
            ' contracts 0
            Assert.False(LadderStopCalculator.ComputeLadderStopPrice(
                rung:=1, tp:=50D, entryPrice:=100D,
                isBuy:=True, contracts:=0, tickSize:=0.25D, tickValue:=5D).HasValue)
            ' entryPrice 0
            Assert.False(LadderStopCalculator.ComputeLadderStopPrice(
                rung:=1, tp:=50D, entryPrice:=0D,
                isBuy:=True, contracts:=1, tickSize:=0.25D, tickValue:=5D).HasValue)
            ' tp 0
            Assert.False(LadderStopCalculator.ComputeLadderStopPrice(
                rung:=1, tp:=0D, entryPrice:=100D,
                isBuy:=True, contracts:=1, tickSize:=0.25D, tickValue:=5D).HasValue)
        End Sub

    End Class

End Namespace
