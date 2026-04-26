Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Pure calculation helpers for the backtest engine.
    ''' Extracted from <see cref="BacktestEngine"/> so they can be unit-tested in isolation.
    '''
    ''' All members are Friend — accessible within TopStepTrader.Services and any assembly
    ''' granted access via InternalsVisibleTo (i.e. TopStepTrader.Tests).
    ''' </summary>
    Friend Module BacktestMetrics

        ''' <summary>
        ''' Calculate the dollar P&amp;L for a closed trade using the contract-specific point value
        ''' from <paramref name="config"/>.
        '''
        ''' Formula: (exitPrice − entryPrice) × quantity × pointValue − commission
        ''' where pointValue is dollars per 1.0 price-unit move (set per-contract in BacktestViewModel).
        ''' Commission = config.CommissionPerSideUsd × 2 (round trip) × trade.Quantity.
        '''
        ''' Correct values: MES = $5/pt, MNQ = $2/pt, MGC = $10/pt, MCL = $10/pt.
        ''' (Old code hardcoded $50/pt which is the full-size ES — 10× too large for MES.)
        '''
        ''' Returns 0 when no exit price has been recorded (open trade guard).
        ''' </summary>
        Friend Function CalculatePnL(trade As BacktestTrade,
                                      config As BacktestConfiguration) As Decimal
            If config.TickSize <= 0D Then
                Throw New InvalidOperationException(
                    $"BacktestConfiguration.TickSize must be > 0 (got {config.TickSize}). " &
                    $"Check FavouriteContracts for contract '{config.ContractId}'.")
            End If
            If Not trade.ExitPrice.HasValue Then Return 0D
            Dim priceDiff = trade.ExitPrice.Value - trade.EntryPrice
            Dim isBuy = trade.Side = "Buy"
            Dim gross = If(isBuy, priceDiff, -priceDiff) * trade.Quantity * config.PointValue
            Dim commission = config.CommissionPerSideUsd * 2D * trade.Quantity  ' entry + exit
            Return gross - commission
        End Function

        ''' <summary>
        ''' Advance dynamic SL/TP levels based on the current bar close.
        ''' Called every bar while a position is open, before CheckExit.
        '''
        ''' <paramref name="stopDelta"/> and <paramref name="tpDelta"/> are price-unit distances
        ''' from entry to the initial SL/TP levels.  These are computed once at trade entry and
        ''' passed unchanged on every subsequent bar so the trailing distance stays constant.
        ''' They are strategy-agnostic — the caller derives them from either dollar-based config
        ''' or ATR price levels:
        '''   Standard strategies: stopDelta = SlDollarBracket / (PointValue × Qty)
        '''   MultiConfluence/SuperTrend: stopDelta = Abs(entryPrice − mcOpenSlPrice)
        '''
        ''' Trailing stop  — advances <paramref name="currentStop"/> toward price, never away.
        '''   Long:  currentStop = Max(currentStop, bar.Close − stopDelta)
        '''   Short: currentStop = Min(currentStop, bar.Close + stopDelta)
        '''
        ''' Break-even      — once bar.Close reaches 50% of the initial TP distance, moves SL
        '''   to entry.  Fires once then becomes idempotent (SL already ≥ entry for longs).
        '''
        ''' Extend TP       — if bar.Close closes beyond the current TP target, shifts TP one
        '''   additional tpDelta further.  Capped at 3× tpDelta from entry.
        '''
        ''' <paramref name="currentStop"/> and <paramref name="currentTp"/> are updated in place.
        ''' </summary>
        Friend Sub UpdateDynamicExits(trade As BacktestTrade,
                                      bar As MarketBar,
                                      config As BacktestConfiguration,
                                      stopDelta As Decimal,
                                      tpDelta As Decimal,
                                      ByRef currentStop As Decimal,
                                      ByRef currentTp As Decimal)
            If stopDelta = 0D Then Return

            Dim isBuy = trade.Side = "Buy"
            Dim entryPrice = trade.EntryPrice

            ' ── Trailing stop ──────────────────────────────────────────────────
            If config.TrailingStopEnabled Then
                If isBuy Then
                    Dim candidate = bar.Close - stopDelta
                    If candidate > currentStop Then currentStop = candidate
                Else
                    Dim candidate = bar.Close + stopDelta
                    If candidate < currentStop Then currentStop = candidate
                End If
            End If

            ' ── Break-even at 50% of TP distance ──────────────────────────────
            If config.BreakEvenOnHalfTpEnabled AndAlso tpDelta > 0D Then
                Dim halfTp = tpDelta * 0.5D
                If isBuy Then
                    If bar.Close >= entryPrice + halfTp AndAlso currentStop < entryPrice Then
                        currentStop = entryPrice
                    End If
                Else
                    If bar.Close <= entryPrice - halfTp AndAlso currentStop > entryPrice Then
                        currentStop = entryPrice
                    End If
                End If
            End If

            ' ── Extend TP on bar close beyond current target ───────────────────
            If config.ExtendTpEnabled AndAlso tpDelta > 0D Then
                Dim maxTp = tpDelta * 3D  ' hard cap: 3× initial TP delta from entry
                If isBuy Then
                    If bar.Close >= currentTp Then
                        Dim extended = currentTp + tpDelta
                        Dim cap = entryPrice + maxTp
                        If extended <= cap Then currentTp = extended
                    End If
                Else
                    If bar.Close <= currentTp Then
                        Dim extended = currentTp - tpDelta
                        Dim cap = entryPrice - maxTp
                        If extended >= cap Then currentTp = extended
                    End If
                End If
            End If
        End Sub

        ''' <summary>
        ''' Determine whether the current bar triggers a stop-loss or take-profit exit.
        ''' Returns "StopLoss", "TakeProfit", or Nothing if neither level is hit.
        '''
        ''' Buy  SL: bar.Low  ≤ entryPrice − slDelta
        ''' Buy  TP: bar.High ≥ entryPrice + tpDelta
        ''' Sell SL: bar.High ≥ entryPrice + slDelta
        ''' Sell TP: bar.Low  ≤ entryPrice − tpDelta
        ''' </summary>
        ''' <overloads>
        ''' Fixed-level overload — uses <paramref name="currentStop"/> and <paramref name="currentTp"/>
        ''' price levels directly (after dynamic adjustment by UpdateDynamicExits).
        ''' <paramref name="currentStop"/> is an absolute price level (e.g. 4 810.25).
        ''' <paramref name="currentTp"/> is an absolute price level (e.g. 4 830.00).
        ''' Returns "StopLoss", "TakeProfit", or Nothing.
        ''' </overloads>
        Friend Function CheckExit(trade As BacktestTrade,
                                   bar As MarketBar,
                                   currentStop As Decimal,
                                   currentTp As Decimal) As String
            Dim isBuy = trade.Side = "Buy"
            If isBuy Then
                If bar.Low <= currentStop Then Return "StopLoss"
                If bar.High >= currentTp Then Return "TakeProfit"
            Else
                If bar.High >= currentStop Then Return "StopLoss"
                If bar.Low <= currentTp Then Return "TakeProfit"
            End If
            Return Nothing
        End Function


''' <summary>
        ''' Returns the exact fill price for a closed trade based on its exit reason.
        '''
        ''' UAT-BUG-006: Using bar.Close as the exit price when SL/TP is triggered on
        ''' bar.High/Low (OHLC detection) produces physically impossible results:
        '''   • If a Sell-side SL is triggered on bar.High but the bar closes below entry
        '''     (bar.Close &lt; entry), the trade would show "StopLoss" with a profit — impossible.
        ''' Fix: when SL or TP fires, fill at the exact level price rather than bar.Close.
        ''' This guarantees StopLoss always produces a loss and TakeProfit always a profit.
        '''
        ''' Rule:
        '''   StopLoss  — Buy:  entry − slDelta   (fill below entry = loss)
        '''   StopLoss  — Sell: entry + slDelta   (fill above entry = loss)
        '''   TakeProfit— Buy:  entry + tpDelta   (fill above entry = profit)
        '''   TakeProfit— Sell: entry − tpDelta   (fill below entry = profit)
        '''   EndOfData — any:  bar.Close         (exit at market; no level was hit)
        ''' </summary>
        ''' <summary>
        ''' Returns the exact fill price using dynamic price levels set by UpdateDynamicExits.
        ''' Pass the same <paramref name="currentStop"/> / <paramref name="currentTp"/> that
        ''' CheckExit used when it returned the exit reason.
        ''' </summary>
        Friend Function GetExitPrice(trade As BacktestTrade,
                                       bar As MarketBar,
                                       exitReason As String,
                                       currentStop As Decimal,
                                       currentTp As Decimal) As Decimal
            If exitReason = "StopLoss" Then
                Return currentStop
            ElseIf exitReason = "TakeProfit" Then
                Return currentTp
            Else
                Return bar.Close   ' EndOfData, NeutralExit, or unknown reason
            End If
        End Function



''' <summary>
        ''' Annualised Sharpe ratio computed from a list of per-position P&amp;L values.
        ''' Returns Nothing when fewer than 2 positions exist or all returns are identical.
        ''' Formula: (avg / stddev) × √252
        ''' </summary>
        Friend Function CalculateSharpeFromReturns(returns As List(Of Decimal)) As Single?
            If returns.Count < 2 Then Return Nothing
            Dim dblReturns = returns.Select(Function(r) CDbl(r)).ToList()
            Dim avg = dblReturns.Average()
            Dim variance = dblReturns.Select(Function(r) (r - avg) * (r - avg)).Average()
            Dim stddev = Math.Sqrt(variance)
            If stddev = 0 Then Return Nothing
            Return CSng(avg / stddev * Math.Sqrt(252))
        End Function

        ''' <summary>
        ''' Annualised Sharpe ratio computed from the list of trade P&amp;Ls.
        ''' Returns Nothing when fewer than 2 trades exist or when all P&amp;Ls are identical
        ''' (standard deviation is zero — Sharpe is undefined).
        ''' Formula: (avg P&amp;L / stddev P&amp;L) × √252
        ''' </summary>
        Friend Function CalculateSharpe(trades As List(Of BacktestTrade)) As Single?
            If trades.Count < 2 Then Return Nothing
            Dim returns = trades.Select(Function(t) CDbl(t.PnL.GetValueOrDefault())).ToList()
            Dim avg = returns.Average()
            Dim variance = returns.Select(Function(r) (r - avg) * (r - avg)).Average()
            Dim stddev = Math.Sqrt(variance)
            If stddev = 0 Then Return Nothing
            Return CSng(avg / stddev * Math.Sqrt(252))  ' Annualised
        End Function

        ''' <summary>
        ''' Aggregate a completed list of trades and run metadata into a <see cref="BacktestResult"/>.
        '''
        ''' Metrics are computed at the POSITION level (grouped by PositionGroupId) so that
        ''' scale-in entries do not inflate the trade count or distort win rate.
        '''   TotalTrades   = number of unique positions (groups), not individual entry rows.
        '''   WinRate       = winning positions / total positions.
        '''   AveragePnL    = total P&amp;L / total positions.
        '''   SharpeRatio   = annualised Sharpe using per-position aggregated returns.
        '''   Trades        = all individual rows (including scale-ins) for display purposes.
        '''
        ''' Win rate is 0 when no trades were taken; Sharpe is Nothing when undefined.
        ''' </summary>
        Friend Function BuildResult(config As BacktestConfiguration,
                                     trades As List(Of BacktestTrade),
                                     finalCapital As Decimal,
                                     maxDrawdown As Decimal) As BacktestResult
            Dim totalPnL = trades.Sum(Function(t) t.PnL.GetValueOrDefault())

            ' Group individual entry/scale-in rows by position for exposure-correct metrics.
            Dim positionPnLs = trades _
                .GroupBy(Function(t) t.PositionGroupId) _
                .Select(Function(g) g.Sum(Function(t) t.PnL.GetValueOrDefault())) _
                .ToList()

            Dim totalPositions = positionPnLs.Count
            Dim winningPositions = positionPnLs.Where(Function(p) p > 0).Count()
            Dim losingPositions = positionPnLs.Where(Function(p) p <= 0).Count()

            Return New BacktestResult With {
                .RunName = config.RunName,
                .ContractId = config.ContractId,
                .StartDate = config.StartDate,
                .EndDate = config.EndDate,
                .InitialCapital = config.InitialCapital,
                .FinalCapital = finalCapital,
                .TotalTrades = totalPositions,
                .WinningTrades = winningPositions,
                .LosingTrades = losingPositions,
                .TotalPnL = totalPnL,
                .MaxDrawdown = maxDrawdown,
                .WinRate = If(totalPositions > 0, CSng(winningPositions) / totalPositions, 0F),
                .AveragePnLPerTrade = If(totalPositions > 0, totalPnL / totalPositions, 0D),
                .SharpeRatio = CalculateSharpeFromReturns(positionPnLs),
                .EndOfDayCloseCount = trades.Where(Function(t) t.ExitReason = "EndOfDay") _
                                            .Select(Function(t) t.PositionGroupId) _
                                            .Distinct().Count(),
                .RoundTripFeeUsd = config.CommissionPerSideUsd * 2D,
                .CommissionPaid = config.CommissionPerSideUsd * 2D * CDec(trades.Sum(Function(t) t.Quantity)),
                .Trades = trades
            }
        End Function

            ''' <summary>
            ''' Check whether a bar triggered a fixed-price stop-loss or take-profit exit.
            '''
            ''' Uses bar.Low / bar.High (OHLC detection), not bar.Close, so that exits fire on the
            ''' correct intra-bar extreme rather than the settlement price.
            '''
            ''' Returns "StopLoss", "TakeProfit", or Nothing (no exit this bar).
            ''' A tp of 0 means no take-profit level is active; only the SL is checked.
            ''' </summary>
            Friend Function CheckFixedExit(side As String, bar As MarketBar, sl As Decimal, tp As Decimal) As String
                If side = "Buy" Then
                    If bar.Low <= sl Then Return "StopLoss"
                    If tp > 0D AndAlso bar.High >= tp Then Return "TakeProfit"
                Else
                    If bar.High >= sl Then Return "StopLoss"
                    If tp > 0D AndAlso bar.Low <= tp Then Return "TakeProfit"
                End If
                Return Nothing
            End Function

        End Module

    End Namespace
