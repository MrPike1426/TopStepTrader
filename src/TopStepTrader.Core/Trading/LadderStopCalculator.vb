Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' FEAT-63: pure-math helpers for the $-denominated TP-ladder stop. Given a
    ''' configured TP increment (in dollars of total unrealised P&amp;L) and the
    ''' current open P&amp;L, decides which "rung" the trade has cleared and where
    ''' the protective stop should sit to lock in that rung.
    '''
    ''' Rung N triggers when <c>currentPnl ≥ (N + 0.10) × tp</c>. When rung N is
    ''' the highest reached, the stop is placed at the price equivalent of
    ''' <c>N × tp</c> in total open P&amp;L. The 10% buffer above each rung gives
    ''' price action room to wobble before the SL is rolled up.
    '''
    ''' Stateless w.r.t. prior rungs: the highest reached rung is recomputed each
    ''' tick from current P&amp;L, so scale-ins that bump P&amp;L past several rungs
    ''' in one instant advance the SL to the highest cleared rung naturally.
    ''' </summary>
    Public Module LadderStopCalculator

        ''' <summary>Buffer applied above each rung before its SL ratchet fires (10% of TP).</summary>
        Public Const RungBufferFraction As Decimal = 0.10D

        ''' <summary>
        ''' Highest rung N (≥ 1) the trade has cleared, or 0 if not yet at rung 1.
        ''' Rung N is cleared when <c>currentPnl ≥ (N + 0.10) × tp</c>.
        ''' </summary>
        ''' <param name="currentPnl">Total open unrealised P&amp;L in dollars.</param>
        ''' <param name="tp">Configured TP increment (one rung) in dollars. ≤ 0 disables.</param>
        Public Function ComputeRung(currentPnl As Decimal, tp As Decimal) As Integer
            If tp <= 0D Then Return 0
            If currentPnl <= 0D Then Return 0
            ' maxN = floor(currentPnl / tp - 0.10)
            Dim ratio As Decimal = currentPnl / tp - RungBufferFraction
            If ratio < 1D Then Return 0
            Return CInt(Math.Floor(ratio))
        End Function

        ''' <summary>
        ''' Price equivalent of the ladder SL at the given rung, or <c>Nothing</c> when
        ''' the rung is below 1 or any contract-metadata input is non-positive. Caller
        ''' merges this with the phased-stop price via Max (long) / Min (short).
        ''' </summary>
        ''' <param name="rung">Rung number from <see cref="ComputeRung"/> — must be ≥ 1.</param>
        ''' <param name="tp">Configured TP increment (one rung) in dollars.</param>
        ''' <param name="entryPrice">Trade entry price.</param>
        ''' <param name="isBuy">True for a long position, False for short.</param>
        ''' <param name="contracts">Open contract count (must be ≥ 1).</param>
        ''' <param name="tickSize">Instrument tick size in price units.</param>
        ''' <param name="tickValue">Dollar value of one tick for one contract.</param>
        Public Function ComputeLadderStopPrice(rung As Integer,
                                               tp As Decimal,
                                               entryPrice As Decimal,
                                               isBuy As Boolean,
                                               contracts As Integer,
                                               tickSize As Decimal,
                                               tickValue As Decimal) As Decimal?
            If rung < 1 Then Return Nothing
            If tp <= 0D Then Return Nothing
            If entryPrice <= 0D Then Return Nothing
            If contracts < 1 Then Return Nothing
            If tickSize <= 0D OrElse tickValue <= 0D Then Return Nothing

            Dim targetPnl As Decimal = rung * tp
            ' priceDelta locks targetPnl total P&L:
            '   $ = (priceDelta / tickSize) × tickValue × contracts
            ' ⇒ priceDelta = targetPnl × tickSize / (tickValue × contracts)
            Dim priceDelta As Decimal = targetPnl * tickSize / (tickValue * contracts)
            Return If(isBuy, entryPrice + priceDelta, entryPrice - priceDelta)
        End Function

    End Module

End Namespace
