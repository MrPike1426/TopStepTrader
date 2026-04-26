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
            Dim brackets As New List(Of BracketState)()    ' per-bracket SL tracking (FEAT-21)
            Dim fillPrices As New List(Of Decimal)()       ' tracks all fill prices for average-entry calc
            Dim addIndex As Integer = 0
            Dim posGroupId As Integer = 0
            Dim lastEntryPrice As Decimal = 0D
            Dim positionSide As String = Nothing           ' "Buy" or "Sell"
            Dim tpPrice As Decimal = 0D
            Dim freeRideActive As Boolean = False
            Dim avgEntry As Decimal = 0D
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
                        StampTrailingFields(openLegs, brackets, freeRideActive, eodPrice, config)
                        CloseAllLegs(openLegs, fillPrices, trades, prevBar.Timestamp, eodPrice, "EndOfDay", config)
                        openLegs.Clear() : brackets.Clear() : fillPrices.Clear()
                        lastEntryPrice = 0D : positionSide = Nothing
                        tpPrice = 0D : freeRideActive = False : avgEntry = 0D
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
                            ' Heat-cap check for scale-ins — use first open bracket's SL as reference
                            Dim canFill = True
                            Dim slForHeat = If(brackets.Count > 0, brackets(0).CurrentSlPrice, 0D)
                            If pendingIsScaleIn AndAlso openLegs.Count > 0 Then
                                Dim heatAfter = CalculateHeatAfterAdd(
                                    openLegs, fillQty, fillPrice, slForHeat, config.TickSize, pendingSide)
                                If heatAfter > config.MaxRiskHeatTicks Then canFill = False
                            End If

                            If canFill Then
                                fillPrices.Add(fillPrice * fillQty)   ' weighted sum for avg
                                Dim totalQtyNow = openLegs.Sum(Function(l) l.Quantity) + fillQty
                                avgEntry = fillPrices.Sum() / totalQtyNow

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

                                ' Anchor TP to average entry after each fill (ATR mode)
                                If config.UseAtrMode AndAlso indicators.Atr IsNot Nothing Then
                                    Dim atrVal = indicators.Atr(i)
                                    If Not Single.IsNaN(atrVal) AndAlso atrVal > 0.0F Then
                                        Dim tpDist = CDec(atrVal) * config.TpAtrMultiple
                                        tpPrice = If(isBuy, avgEntry + tpDist, avgEntry - tpDist)
                                    End If
                                End If

                                ' Initialise per-bracket SL at avgEntry ± stopLossTicks×tickSize
                                Dim tick = config.TickSize
                                Dim initialSl As Decimal
                                If config.StopLossTicks > 0 AndAlso tick > 0D Then
                                    Dim slDist = config.StopLossTicks * tick
                                    initialSl = If(isBuy, avgEntry - slDist, avgEntry + slDist)
                                ElseIf config.UseAtrMode AndAlso indicators.Atr IsNot Nothing Then
                                    Dim atrVal = indicators.Atr(i)
                                    Dim slDist = If(Single.IsNaN(atrVal) OrElse atrVal <= 0F,
                                                    0D, CDec(atrVal) * config.SlAtrMultiple)
                                    initialSl = If(isBuy, avgEntry - slDist, avgEntry + slDist)
                                Else
                                    initialSl = 0D
                                End If

                                brackets.Add(New BracketState With {
                                    .AddIndex      = pendingAddIndex,
                                    .EntryPrice    = fillPrice,
                                    .InitialSlPrice = initialSl,
                                    .CurrentSlPrice = initialSl
                                })

                                addIndex = pendingAddIndex + 1
                            End If
                        End If
                    End If
                    pendingSide = Nothing
                End If

                ' ── Check exit for open position ─────────────────────────────────
                If openLegs.Count > 0 Then
                    Dim isBuyPos = (openLegs(0).Side = "Buy")

                    ' ── FEAT-21: Free-ride SL check ───────────────────────────────
                    If Not freeRideActive AndAlso brackets.Count >= 3 Then
                        Dim allProfit = brackets.All(Function(b)
                                                         Return If(isBuyPos,
                                                             bar.Close > b.EntryPrice,
                                                             bar.Close < b.EntryPrice)
                                                     End Function)
                        If allProfit Then
                            freeRideActive = True
                            ' Floor every bracket's SL at avgEntry (breakeven)
                            For Each b In brackets
                                If isBuyPos Then
                                    If b.CurrentSlPrice < avgEntry Then b.CurrentSlPrice = avgEntry
                                Else
                                    If b.CurrentSlPrice > avgEntry Then b.CurrentSlPrice = avgEntry
                                End If
                            Next
                        End If
                    End If

                    ' ── FEAT-21: Per-bracket trailing SL update ───────────────────
                    Dim tick = config.TickSize
                    If tick > 0D AndAlso config.StopLossTicks > 0 Then
                        For k = 0 To brackets.Count - 1
                            Dim b = brackets(k)
                            Dim isCore = b.AddIndex < config.CoreAddsCount
                            Dim trailFactor = If(isCore, 2.0D, 1.0D)
                            Dim trailDist = config.StopLossTicks * trailFactor * tick

                            Dim potentialSl As Decimal
                            If isBuyPos Then
                                potentialSl = bar.Close - trailDist
                                ' Add-on breakeven floor after 5-tick profit
                                If Not isCore Then
                                    Dim profit = bar.Close - b.EntryPrice
                                    If profit > 5D * tick Then
                                        Dim bePrice = b.EntryPrice + tick
                                        If potentialSl < bePrice Then potentialSl = bePrice
                                    End If
                                End If
                                ' Free-ride floor
                                If freeRideActive AndAlso potentialSl < avgEntry Then potentialSl = avgEntry
                                ' Monotonic: only move up
                                If potentialSl > b.CurrentSlPrice Then b.CurrentSlPrice = potentialSl
                            Else
                                potentialSl = bar.Close + trailDist
                                ' Add-on breakeven floor after 5-tick profit
                                If Not isCore Then
                                    Dim profit = b.EntryPrice - bar.Close
                                    If profit > 5D * tick Then
                                        Dim bePrice = b.EntryPrice - tick
                                        If potentialSl > bePrice Then potentialSl = bePrice
                                    End If
                                End If
                                ' Free-ride floor
                                If freeRideActive AndAlso potentialSl > avgEntry Then potentialSl = avgEntry
                                ' Monotonic: only move down
                                If potentialSl < b.CurrentSlPrice Then b.CurrentSlPrice = potentialSl
                            End If
                        Next
                    End If

                    ' ── Exit check: per-bracket SL, then TP ──────────────────────
                    Dim exitReason As String = Nothing
                    Dim exitPrice As Decimal = bar.Close

                    ' Check per-bracket SL (use the worst/best-case bracket that was hit)
                    If brackets.Count > 0 Then
                        For Each b In brackets
                            If b.CurrentSlPrice <> 0D Then
                                Dim slHit = If(isBuyPos,
                                    bar.Low <= b.CurrentSlPrice,
                                    bar.High >= b.CurrentSlPrice)
                                If slHit Then
                                    exitReason = "StopLoss"
                                    ' Exit at SL price (with optional slippage)
                                    exitPrice = b.CurrentSlPrice
                                    If config.SlippageTicks > 0 AndAlso tick > 0D Then
                                        Dim slipD = config.SlippageTicks * tick
                                        exitPrice += If(isBuyPos, -slipD, slipD)
                                    End If
                                    Exit For
                                End If
                            End If
                        Next
                    End If

                    ' Check TP (fixed offset from avgEntry — FEAT-20 behaviour; fires if SL not hit)
                    If exitReason Is Nothing AndAlso tpPrice <> 0D Then
                        Dim tpHit = If(isBuyPos, bar.High >= tpPrice, bar.Low <= tpPrice)
                        If tpHit Then
                            exitReason = "TakeProfit"
                            exitPrice = tpPrice
                        End If
                    End If

                    If exitReason IsNot Nothing Then
                        ' Stamp MaxContractsHeld and FEAT-21 trailing fields on all legs before closing
                        Dim maxContracts = openLegs.Sum(Function(l) l.Quantity)
                        StampTrailingFields(openLegs, brackets, freeRideActive, exitPrice, config)
                        For Each leg In openLegs
                            leg.MaxContractsHeld = maxContracts
                        Next
                        CloseAllLegs(openLegs, fillPrices, trades, bar.Timestamp, exitPrice, exitReason, config)
                        openLegs.Clear() : brackets.Clear() : fillPrices.Clear()
                        lastEntryPrice = 0D : positionSide = Nothing
                        tpPrice = 0D : freeRideActive = False : avgEntry = 0D
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
                StampTrailingFields(openLegs, brackets, freeRideActive, lastBar.Close, config)
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

        ''' <summary>
        ''' FEAT-21: Stamps FreeRideActivated, FinalSlAtExit, and TrailingTicksCaptured onto
        ''' every open leg using the matched bracket for that leg (matched by PyramidAddIndex).
        ''' Called immediately before CloseAllLegs.
        ''' </summary>
        Private Shared Sub StampTrailingFields(openLegs As List(Of BacktestTrade),
                                               brackets As List(Of BracketState),
                                               freeRideActive As Boolean,
                                               exitPrice As Decimal,
                                               config As BacktestConfiguration)
            For Each leg In openLegs
                leg.FreeRideActivated = freeRideActive
                Dim matchedBracket = brackets.FirstOrDefault(
                    Function(b) b.AddIndex = leg.PyramidAddIndex.GetValueOrDefault(-1))
                If matchedBracket IsNot Nothing Then
                    leg.FinalSlAtExit = matchedBracket.CurrentSlPrice
                    If config.TickSize > 0D AndAlso matchedBracket.InitialSlPrice <> 0D Then
                        Dim slMoved = Math.Abs(matchedBracket.CurrentSlPrice - matchedBracket.InitialSlPrice)
                        leg.TrailingTicksCaptured = slMoved / config.TickSize
                    End If
                End If
            Next
        End Sub

        ' ── FEAT-21: Per-bracket SL state ────────────────────────────────────────────

        ''' <summary>
        ''' Tracks the dynamic stop-loss price for a single pyramid bracket.
        ''' One instance is created per fill and updated each bar by the free-ride
        ''' check and per-bracket trailing logic.
        ''' </summary>
        Private Class BracketState
            Public Property AddIndex As Integer
            Public Property EntryPrice As Decimal
            ''' <summary>Initial SL set at fill time (avgEntry ± stopLossTicks × tickSize).</summary>
            Public Property InitialSlPrice As Decimal
            ''' <summary>Current (possibly trailed) SL — the live floor used for exit detection.</summary>
            Public Property CurrentSlPrice As Decimal
        End Class

    End Class

End Namespace
