Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Isolated Sniper (TripleEmaCascade) pyramid backtest simulation.
    ''' Mirrors the live <c>SniperExecutionEngine</c> pyramid logic:
    ''' three tiers (Core / Momentum / Extension), ATR-gated scale-ins, and a heat cap.
    '''
    ''' Called by <see cref="BacktestEngine.RunReplay"/> when
    ''' <c>config.StrategyCondition = StrategyConditionType.TripleEmaCascade</c>.
    ''' The generic single-contract loop in <c>BacktestEngine</c> is NOT used.
    ''' </summary>
    Friend Class SniperBacktestEngine

        ' ── Shared helper (mirrors SniperExecutionEngine.CalculateAddQuantity) ──────
        ''' <summary>
        ''' Calculates the number of contracts to add at a given pyramid add-index.
        ''' Mirrors <c>SniperExecutionEngine.CalculateAddQuantity</c> exactly.
        ''' addIndex 0 .. coreAddsCount-1  → Tier A Core (front-loaded)
        ''' addIndex = coreAddsCount        → Tier B Momentum
        ''' addIndex > coreAddsCount        → Tier C Extension (if allowed)
        ''' </summary>
        Friend Shared Function CalculateAddQuantity(addIndex As Integer,
                                                     targetTotalSize As Integer,
                                                     coreSizeFraction As Double,
                                                     coreAddsCount As Integer,
                                                     momentumTierSize As Integer,
                                                     extensionTierSize As Integer,
                                                     extensionAllowed As Boolean) As Integer
            If targetTotalSize <= 0 Then Return 0

            ' Tier A – Core
            If addIndex < coreAddsCount Then
                Dim totalCore = Math.Max(1, CInt(Math.Round(targetTotalSize * coreSizeFraction)))
                Dim baseQty   = totalCore \ coreAddsCount
                Dim remainder = totalCore Mod coreAddsCount
                Dim allocation = baseQty + If(addIndex < remainder, 1, 0)
                Return Math.Max(1, allocation)
            End If

            ' Tier B – Momentum
            If addIndex = coreAddsCount Then Return momentumTierSize

            ' Tier C – Extension
            If addIndex > coreAddsCount Then
                Return If(extensionAllowed, extensionTierSize, 0)
            End If

            Return 0
        End Function

        ''' <summary>
        ''' Runs the full pyramid replay over the pre-loaded bar list.
        ''' Returns the list of all closed <see cref="BacktestTrade"/> legs (one row per fill/add).
        ''' </summary>
        Friend Shared Function RunPyramidReplay(config As BacktestConfiguration,
                                                filteredBars As IReadOnlyList(Of MarketBar),
                                                indicators As StrategyIndicators,
                                                warmUp As Integer) As List(Of BacktestTrade)

            Dim trades As New List(Of BacktestTrade)()

            If indicators.Ema8 Is Nothing OrElse indicators.Ema21 Is Nothing OrElse
               indicators.Ema50 Is Nothing Then Return trades

            ' Position state
            Dim openLegs As New List(Of BacktestTrade)()
            Dim fillPrices As New List(Of Decimal)()    ' tracks all fill prices for average-entry calc
            Dim addIndex As Integer = 0
            Dim posGroupId As Integer = 0
            Dim lastEntryPrice As Decimal = 0D
            Dim positionSide As String = Nothing        ' "Buy" or "Sell"
            Dim slPrice As Decimal = 0D
            Dim tpPrice As Decimal = 0D
            Dim lastEndOfDayBarIndex As Integer = -1000

            ' Pending next-bar fill
            Dim pendingSide As String = Nothing
            Dim pendingIsScaleIn As Boolean = False
            Dim pendingGroupId As Integer = 0
            Dim pendingAddIndex As Integer = 0

            For i = warmUp To filteredBars.Count - 1
                Dim bar = filteredBars(i)

                ' ── End-of-day forced close ──────────────────────────────────────
                If openLegs.Count > 0 AndAlso i > warmUp Then
                    Dim prevBar = filteredBars(i - 1)
                    If bar.Timestamp.Date <> prevBar.Timestamp.Date Then
                        Dim eodPrice = prevBar.Close
                        If config.SlippageTicks > 0 AndAlso config.TickSize > 0D Then
                            Dim slipD = config.SlippageTicks * config.TickSize
                            eodPrice += If(openLegs(0).Side = "Buy", -slipD, slipD)
                        End If
                        CloseAllLegs(openLegs, fillPrices, trades, prevBar.Timestamp, eodPrice, "EndOfDay", config)
                        openLegs.Clear() : fillPrices.Clear()
                        lastEntryPrice = 0D : positionSide = Nothing
                        slPrice = 0D : tpPrice = 0D
                        addIndex = 0
                        lastEndOfDayBarIndex = i
                        pendingSide = Nothing
                        Continue For
                    End If
                End If

                ' ── Fill pending entry/scale-in at bar.Open + slippage ───────────
                If pendingSide IsNot Nothing Then
                    If (i - lastEndOfDayBarIndex) > 1 Then
                        Dim isBuy = (pendingSide = "Buy")
                        Dim fillSlip = If(config.TickSize > 0D, config.TickSize, 0D)
                        Dim spreadCost = If(config.TickSize > 0D, config.SpreadTicks * config.TickSize, 0D)
                        Dim fillPrice = bar.Open + If(isBuy, fillSlip + spreadCost, -(fillSlip + spreadCost))

                        Dim fillQty = CalculateAddQuantity(
                            pendingAddIndex,
                            config.TargetTotalSize,
                            config.CoreSizeFraction,
                            config.CoreAddsCount,
                            config.MomentumTierSize,
                            config.ExtensionTierSize,
                            config.ExtensionAllowed)

                        If fillQty > 0 Then
                            ' Heat-cap check for scale-ins
                            Dim canFill = True
                            If pendingIsScaleIn AndAlso openLegs.Count > 0 Then
                                Dim heatAfter = CalculateHeatAfterAdd(
                                    openLegs, fillQty, fillPrice, slPrice, config.TickSize, pendingSide)
                                If heatAfter > config.MaxRiskHeatTicks Then canFill = False
                            End If

                            If canFill Then
                                fillPrices.Add(fillPrice * fillQty)   ' weighted sum for avg
                                Dim totalQtyNow = openLegs.Sum(Function(l) l.Quantity) + fillQty
                                Dim avgEntry = fillPrices.Sum() / totalQtyNow

                                Dim leg As New BacktestTrade With {
                                    .PositionGroupId    = pendingGroupId,
                                    .EntryTime          = bar.Timestamp,
                                    .EntryPrice         = fillPrice,
                                    .Side               = pendingSide,
                                    .Quantity           = fillQty,
                                    .SignalConfidence    = 1.0F,
                                    .PyramidAddIndex    = pendingAddIndex,
                                    .AverageEntryAtFill = avgEntry
                                }
                                openLegs.Add(leg)
                                lastEntryPrice = fillPrice

                                ' Anchor SL/TP to average entry after each fill (ATR mode)
                                If config.UseAtrMode AndAlso indicators.Atr IsNot Nothing Then
                                    Dim atrVal = indicators.Atr(i)
                                    If Not Single.IsNaN(atrVal) AndAlso atrVal > 0.0F Then
                                        Dim stopDist = CDec(atrVal) * config.SlAtrMultiple
                                        Dim tpDist   = CDec(atrVal) * config.TpAtrMultiple
                                        slPrice = If(isBuy, avgEntry - stopDist, avgEntry + stopDist)
                                        tpPrice = If(isBuy, avgEntry + tpDist,  avgEntry - tpDist)
                                    End If
                                End If

                                addIndex = pendingAddIndex + 1
                            End If
                        End If
                    End If
                    pendingSide = Nothing
                End If

                ' ── Check exit for open position ─────────────────────────────────
                If openLegs.Count > 0 Then
                    Dim exitReason As String = Nothing
                    Dim exitPrice As Decimal = bar.Close

                    If slPrice <> 0D OrElse tpPrice <> 0D Then
                        exitReason = BacktestMetrics.CheckFixedExit(openLegs(0).Side, bar, slPrice, tpPrice)
                        If exitReason IsNot Nothing Then
                            exitPrice = BacktestMetrics.GetExitPrice(openLegs(0), bar, exitReason, slPrice, tpPrice)
                            If exitReason = "StopLoss" AndAlso config.SlippageTicks > 0 AndAlso config.TickSize > 0D Then
                                Dim slipD = config.SlippageTicks * config.TickSize
                                exitPrice += If(openLegs(0).Side = "Buy", -slipD, slipD)
                            End If
                        End If
                    End If

                    If exitReason IsNot Nothing Then
                        ' Stamp MaxContractsHeld on all legs before closing
                        Dim maxContracts = openLegs.Sum(Function(l) l.Quantity)
                        For Each leg In openLegs
                            leg.MaxContractsHeld = maxContracts
                        Next
                        CloseAllLegs(openLegs, fillPrices, trades, bar.Timestamp, exitPrice, exitReason, config)
                        openLegs.Clear() : fillPrices.Clear()
                        lastEntryPrice = 0D : positionSide = Nothing
                        slPrice = 0D : tpPrice = 0D
                        addIndex = 0
                        Continue For
                    End If

                    ' ── Scale-in check (while position is open) ──────────────────
                    Dim totalSize = openLegs.Sum(Function(l) l.Quantity)
                    If totalSize < config.TargetTotalSize AndAlso pendingSide Is Nothing Then
                        Dim nextQty = CalculateAddQuantity(
                            addIndex,
                            config.TargetTotalSize,
                            config.CoreSizeFraction,
                            config.CoreAddsCount,
                            config.MomentumTierSize,
                            config.ExtensionTierSize,
                            config.ExtensionAllowed)

                        If nextQty > 0 Then
                            ' ATR-gated distance trigger
                            Dim scaleTriggered = False
                            If indicators.Atr IsNot Nothing Then
                                Dim atrVal = indicators.Atr(i)
                                If Not Single.IsNaN(atrVal) AndAlso atrVal > 0.0F Then
                                    Dim requiredMove = CDec(atrVal) * CDec(config.VolatilityAtrFactor)
                                    Dim isLong = (openLegs(0).Side = "Buy")
                                    Dim priceOk = If(isLong,
                                        bar.Close >= lastEntryPrice + requiredMove,
                                        bar.Close <= lastEntryPrice - requiredMove)
                                    ' EMA8 still aligned
                                    Dim ema8Now = indicators.Ema8(i)
                                    Dim ema21Now = indicators.Ema21(i)
                                    Dim emaAligned = If(isLong,
                                        Not Single.IsNaN(ema8Now) AndAlso Not Single.IsNaN(ema21Now) AndAlso ema8Now > ema21Now,
                                        Not Single.IsNaN(ema8Now) AndAlso Not Single.IsNaN(ema21Now) AndAlso ema8Now < ema21Now)
                                    scaleTriggered = priceOk AndAlso emaAligned
                                End If
                            Else
                                ' No ATR available: always allow (conservative fallback)
                                scaleTriggered = True
                            End If

                            If scaleTriggered Then
                                pendingSide      = openLegs(0).Side
                                pendingIsScaleIn = True
                                pendingGroupId   = openLegs(0).PositionGroupId
                                pendingAddIndex  = addIndex
                            End If
                        End If
                    End If
                End If

                ' ── Signal evaluation (initial entry when flat) ──────────────────
                If openLegs.Count = 0 AndAlso pendingSide Is Nothing Then
                    If i < 1 Then Continue For

                    Dim ema8Now  = indicators.Ema8(i)
                    Dim ema8Prev = indicators.Ema8(i - 1)
                    Dim ema21Now = indicators.Ema21(i)
                    Dim ema21Prev= indicators.Ema21(i - 1)
                    Dim ema50Now = indicators.Ema50(i)
                    Dim ema50Prev= indicators.Ema50(i - 1)

                    If Single.IsNaN(ema8Now) OrElse Single.IsNaN(ema8Prev) OrElse
                       Single.IsNaN(ema21Now) OrElse Single.IsNaN(ema50Now) OrElse
                       Single.IsNaN(ema50Prev) Then Continue For

                    Dim crossedAbove = (ema8Prev <= ema21Prev AndAlso ema8Now > ema21Now)
                    Dim crossedBelow = (ema8Prev >= ema21Prev AndAlso ema8Now < ema21Now)
                    Dim ema50Rising  = (ema50Now > ema50Prev)
                    Dim ema50Falling = (ema50Now < ema50Prev)

                    Dim signalSide As String = Nothing
                    If crossedAbove AndAlso bar.Close > CDec(ema50Now) AndAlso ema50Rising Then
                        signalSide = "Buy"
                    ElseIf crossedBelow AndAlso bar.Close < CDec(ema50Now) AndAlso ema50Falling Then
                        signalSide = "Sell"
                    End If

                    If signalSide IsNot Nothing Then
                        posGroupId  += 1
                        positionSide = signalSide
                        addIndex     = 0
                        pendingSide      = signalSide
                        pendingIsScaleIn = False
                        pendingGroupId   = posGroupId
                        pendingAddIndex  = 0
                    End If
                End If

            Next ' bar loop

            ' Close any position still open at end of data
            If openLegs.Count > 0 Then
                Dim lastBar = filteredBars.Last()
                Dim maxContracts = openLegs.Sum(Function(l) l.Quantity)
                For Each leg In openLegs
                    leg.MaxContractsHeld = maxContracts
                Next
                CloseAllLegs(openLegs, fillPrices, trades, lastBar.Timestamp, lastBar.Close, "EndOfData", config)
            End If

            Return trades
        End Function

        ' ── Private helpers ──────────────────────────────────────────────────────────

        ''' <summary>Stamp exit on every open leg and move them to the closed trades list.</summary>
        Private Shared Sub CloseAllLegs(openLegs As List(Of BacktestTrade),
                                         fillPrices As List(Of Decimal),
                                         trades As List(Of BacktestTrade),
                                         exitTime As DateTimeOffset,
                                         exitPrice As Decimal,
                                         exitReason As String,
                                         config As BacktestConfiguration)
            For Each leg In openLegs
                leg.ExitTime   = exitTime
                leg.ExitPrice  = exitPrice
                leg.ExitReason = exitReason
                leg.PnL        = BacktestMetrics.CalculatePnL(leg, config)
                trades.Add(leg)
            Next
        End Sub

        ''' <summary>
        ''' Estimates total heat (tick-risk × qty) after adding <paramref name="addQty"/>
        ''' contracts at <paramref name="fillPrice"/> with the same SL as the existing legs.
        ''' </summary>
        Private Shared Function CalculateHeatAfterAdd(openLegs As List(Of BacktestTrade),
                                                       addQty As Integer,
                                                       fillPrice As Decimal,
                                                       slPrice As Decimal,
                                                       tickSize As Decimal,
                                                       side As String) As Decimal
            If tickSize <= 0D Then Return 0D
            Dim heat As Decimal = 0D
            ' Heat from existing legs (using current SL anchored to slPrice)
            For Each leg In openLegs
                Dim priceDist = If(side = "Buy", leg.EntryPrice - slPrice, slPrice - leg.EntryPrice)
                Dim ticksRisk = If(priceDist < 0D, 0D, priceDist / tickSize)
                heat += ticksRisk * leg.Quantity
            Next
            ' Heat from the new leg
            Dim newDist = If(side = "Buy", fillPrice - slPrice, slPrice - fillPrice)
            Dim newTicks = If(newDist < 0D, 0D, newDist / tickSize)
            heat += newTicks * addQty
            Return heat
        End Function

    End Class

End Namespace
