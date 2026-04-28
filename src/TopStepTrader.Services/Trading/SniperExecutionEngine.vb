Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Sniper execution engine: 3-EMA Cascade (EMA8/EMA21/EMA50) on 1-minute bars.
    ''' Supports pyramiding scale-in (up to 10 contracts) and auto free-ride SL (breakeven).
    '''
    ''' State machine (every 30-second poll):
    '''   FLAT     → EMA8 crosses EMA21 with EMA50 confirmation → OPEN (1 contract)
    '''   OPEN     → EMA8 still in direction, price moved ≥ ScaleInTriggerTicks → SCALE-IN
    '''   3+ open  → All in profit → free-ride SL moved to AverageEntry
    '''   TP/SL hit → back to FLAT
    '''
    ''' Register as Transient — one instance per sniper session.
    ''' </summary>
    Public Class SniperExecutionEngine
        Implements ISniperExecutionEngine

        ' ── Dependencies ────────────────────────────────────────────────────────
        Private ReadOnly _ingestionService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _logger As ILogger(Of SniperExecutionEngine)

        ' ── Configuration (set by Start()) ──────────────────────────────────────
        Private _contractId As String
        Private _accountId As Long

        ''' <summary>True when running against TopStepX/ProjectX (contract ID starts with "CON.F.").</summary>
        Private ReadOnly Property IsTopStepX As Boolean
            Get
                Return Not String.IsNullOrEmpty(_contractId) AndAlso
                       _contractId.StartsWith("CON.F.", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property
        Private _takeProfitTicks As Integer
        Private _stopLossTicks As Integer
        Private _volatilityAtrFactor As Double = 0.4 ' Default 0.4 for NQ
        Private _maxRiskHeatTicks As Integer = Integer.MaxValue
        Private _targetTotalSize As Integer = 10
        Private _coreSizeFraction As Double = 0.55
        Private _coreAddsCount As Integer = 2
        Private _momentumTierSize As Integer = 1
        Private _extensionAllowed As Boolean = False
        Private _extensionTierSize As Integer = 1
        ' Structure-Fail Exit
        Private _enableStructureFailExit As Boolean = False
        Private _ema21BreakTicks As Integer = 5
        Private _minBarsBeforeExit As Integer = 5
        Private _barsInTrade As Integer = 0
        Private _tickSize As Decimal
        Private _tickValue As Decimal
        Private _expiresAt As DateTimeOffset

        ' ── Position state ───────────────────────────────────────────────────────
        Private _currentQty As Integer = 0              ' 0–10 open contracts
        Private _entryPrices As New List(Of Decimal)    ' fill prices for each contract
        Private _averageEntry As Decimal = 0D
        Private _lastEntryPrice As Decimal = 0D         ' price at last scale-in
        Private _tradeSide As OrderSide = OrderSide.Buy

        ' Pyramiding State
        Private _addIndex As Integer = 0                ' 0=Initial, 1=FirstScaleIn, etc.

        ' Track multiple bracket pairs (one per scale-in/start)
        Private Class BracketPair
            Public Property TpOrderId As Long?
            Public Property SlOrderId As Long?
            Public Property Qty As Integer
            Public Property OcoId As String
            Public Property EntryPrice As Decimal
            Public Property CurrentSlPrice As Decimal
            Public Property CurrentTpPrice As Decimal = 0D
            Public Property AddIndex As Integer
        End Class
        Private _brackets As New List(Of BracketPair)

        Private _freeRideActive As Boolean = False
        Private _lastClose As Decimal = 0D              ' most recent bar close (for emergency P&L estimate)

        ' ── Safety monitor ───────────────────────────────────────────────────────
        ''' <summary>
        ''' Separate CTS for the client-side P&amp;L safety monitor loop so it can be
        ''' cancelled independently of the main engine CTS (which is already cancelled
        ''' during StopAsync before the cleanup path runs).
        ''' </summary>
        Private _safetyMonitorCts As CancellationTokenSource
        ''' <summary>Guard flag: prevents re-entrant forced-flat if the monitor fires twice.</summary>
        Private _safetyFiring As Integer = 0


        ' ── Engine state ─────────────────────────────────────────────────────────
        Private _running As Boolean = False
        Private _disposed As Boolean = False
        Private _timer As System.Threading.Timer
        Private _cts As CancellationTokenSource
        Private _callbackRunning As Integer = 0
        ''' <summary>
        ''' Orders that belong to closed brackets but haven't been confirmed cancelled/filled yet.
        ''' We must poll these and kill them to prevent "Orphan Fills".
        ''' </summary>
        Private _zombieOrders As New List(Of Long)

        ' ── Events ───────────────────────────────────────────────────────────────
        Public Event LogMessage As EventHandler(Of String) Implements ISniperExecutionEngine.LogMessage
        Public Event ExecutionStopped As EventHandler(Of String) Implements ISniperExecutionEngine.ExecutionStopped
        Public Event TradeOpened As EventHandler(Of TradeOpenedEventArgs) Implements ISniperExecutionEngine.TradeOpened
        Public Event TradeClosed As EventHandler(Of TradeClosedEventArgs) Implements ISniperExecutionEngine.TradeClosed
        ''' <summary>Raised whenever position state changes (qty, avgEntry, freeRide).</summary>
        Public Event PositionChanged As EventHandler(Of SniperPositionEventArgs) Implements ISniperExecutionEngine.PositionChanged

        Public Sub New(ingestionService As IBarIngestionService,
                       orderService As IOrderService,
                       logger As ILogger(Of SniperExecutionEngine))
            _ingestionService = ingestionService
            _orderService = orderService
            _logger = logger
        End Sub

        Public ReadOnly Property IsRunning As Boolean Implements ISniperExecutionEngine.IsRunning
            Get
                Return _running
            End Get
        End Property

        Public ReadOnly Property CurrentQty As Integer Implements ISniperExecutionEngine.CurrentQty
            Get
                Return _currentQty
            End Get
        End Property

        Public ReadOnly Property AverageEntry As Decimal Implements ISniperExecutionEngine.AverageEntry
            Get
                Return _averageEntry
            End Get
        End Property

        Public ReadOnly Property FreeRideActive As Boolean Implements ISniperExecutionEngine.FreeRideActive
            Get
                Return _freeRideActive
            End Get
        End Property

        ' ── Public API ──────────────────────────────────────────────────────────

        ''' <summary>
        ''' Start the sniper engine. Polls 1-min bars every 30 seconds.
        ''' </summary>
        Public Sub Start(contractId As String,
                         accountId As Long,
                         takeProfitTicks As Integer,
                         stopLossTicks As Integer,
                         maxRiskHeatTicks As Integer,
                         volatilityAtrFactor As Double,
                         targetTotalSize As Integer,
                         coreSizeFraction As Double,
                         coreAddsCount As Integer,
                         momentumTierSize As Integer,
                         extensionAllowed As Boolean,
                         extensionTierSize As Integer,
                         enableStructureFailExit As Boolean,
                         ema21BreakTicks As Integer,
                         minBarsBeforeExit As Integer,
                         durationHours As Double,
                         tickSize As Decimal,
                         tickValue As Decimal) Implements ISniperExecutionEngine.Start
            If _running Then Return
            _contractId = contractId
            _accountId = accountId
            ' Use the tick values passed in from the ViewModel (already dollar→tick converted)
            _stopLossTicks = Math.Max(1, stopLossTicks)
            _takeProfitTicks = Math.Max(1, takeProfitTicks)
            _volatilityAtrFactor = volatilityAtrFactor
            _maxRiskHeatTicks = maxRiskHeatTicks
            _targetTotalSize = Math.Max(1, targetTotalSize)
            _coreSizeFraction = Math.Min(1.0, Math.Max(0.1, coreSizeFraction))
            _coreAddsCount = Math.Max(1, coreAddsCount)
            _momentumTierSize = Math.Max(1, momentumTierSize)
            _extensionTierSize = Math.Max(1, extensionTierSize)
            _extensionAllowed = extensionAllowed

            _enableStructureFailExit = enableStructureFailExit
            _ema21BreakTicks = Math.Max(0, ema21BreakTicks)
            _minBarsBeforeExit = Math.Max(0, minBarsBeforeExit)

            _tickSize = If(tickSize > 0, tickSize, 0.25D)
            _tickValue = If(tickValue > 0, tickValue, 1.25D)
            _expiresAt = DateTimeOffset.UtcNow.AddHours(durationHours)

            ' Reset position state
            _currentQty = 0
            _entryPrices.Clear()
            _averageEntry = 0D
            _lastEntryPrice = 0D
            _brackets.Clear()
            _freeRideActive = False
            _lastClose = 0D
            _addIndex = 0
            _barsInTrade = 0

            _running = True
            _cts = New CancellationTokenSource()
            Interlocked.Exchange(_callbackRunning, 0)
            Interlocked.Exchange(_safetyFiring, 0)

            AddHandler _orderService.OrderFilled, AddressOf OnOrderFilled

            Log($"🎯 Sniper started — {_contractId} | 3-EMA Cascade | 1-min bars")
            Log($"   TP: {_takeProfitTicks}t  SL: {_stopLossTicks}t  Scale: {_volatilityAtrFactor}x ATR")
            Log($"   MaxHeat: {_maxRiskHeatTicks}t  Core: {_coreSizeFraction:P0} in {_coreAddsCount} adds  MaxQty: {_targetTotalSize}")
            Log($"   Expires: {_expiresAt:HH:mm} UTC")
            Log($"Checking bars every 5 seconds...")

            _timer = New System.Threading.Timer(AddressOf TimerCallback, Nothing,
                                                TimeSpan.Zero, TimeSpan.FromSeconds(5))
        End Sub

        ''' <summary>Stop the engine and cancel any open orders.</summary>
        Public Async Function StopAsync(Optional reason As String = "Stopped by user") As Task Implements ISniperExecutionEngine.StopAsync
            If Not _running Then Return
            _running = False

            RemoveHandler _orderService.OrderFilled, AddressOf OnOrderFilled

            _cts?.Cancel()
            _safetyMonitorCts?.Cancel()
            _timer?.Change(Timeout.Infinite, 0)

            Log($"■ Sniper stopping — {reason}, flattening positions...")

            ' Flatten any open position
            If _currentQty > 0 Then
                ' Use a new cancellation token since _cts is cancelled
                Dim cleanupCts As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Try
                    ' Cancel all working bracket orders first
                    For Each b In _brackets
                        If b.TpOrderId.HasValue Then
                            Try : Await _orderService.CancelOrderAsync(b.TpOrderId.Value) : Catch : End Try
                        End If
                        If b.SlOrderId.HasValue Then
                            Try : Await _orderService.CancelOrderAsync(b.SlOrderId.Value) : Catch : End Try
                        End If
                    Next

                    ' Close overall position
                    Await EmergencyCloseAsync(cleanupCts.Token)
                Catch ex As Exception
                    Log($"⚠️  Error during flattening: {ex.Message}")
                End Try
            End If

            Log($"■ Sniper stopped — {reason}")
            RaiseEvent ExecutionStopped(Me, reason)
        End Function

        ' ── Event Handlers ───────────────────────────────────────────────────────

        Private Sub OnOrderFilled(sender As Object, e As OrderFilledEventArgs)
            If Not _running Then Return

            Dim filledId = e.Order.ExternalOrderId
            If Not filledId.HasValue Then Return

            ' Find which bracket pair this order belongs to
            Dim matchingBracket = _brackets.FirstOrDefault(Function(b) (b.TpOrderId.HasValue AndAlso b.TpOrderId.Value = filledId.Value) OrElse
                                                                       (b.SlOrderId.HasValue AndAlso b.SlOrderId.Value = filledId.Value))

            If matchingBracket IsNot Nothing Then
                Dim isTp = matchingBracket.TpOrderId.HasValue AndAlso matchingBracket.TpOrderId.Value = filledId.Value
                Dim isSl = matchingBracket.SlOrderId.HasValue AndAlso matchingBracket.SlOrderId.Value = filledId.Value

                Dim type = If(isTp, "TP", "SL")

                ' Verify Side Integrity: 
                ' If TP filled, it should be opposite to entry.
                ' If SL filled, it should be opposite to entry.
                ' e.Order.Side comes from API. _tradeSide is our internal view.
                ' If _tradeSide is Buy (Long), TP/SL fills must be Sell.
                Dim expectedFillSide = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)
                If e.Order.Side <> expectedFillSide Then
                    Dim actualSide = e.Order.Side
                    Log($"🚨 CRITICAL: {type} fill side was {actualSide}, expected {expectedFillSide}. Position direction mismatch possible!")
                    ' If we just filled a Buy when we expected a Sell, we might have accidentally ADDED to the position instead of closing it!
                    ' This happens if the order side was wrong.
                End If

                Log($"⚡ Event: {type} filled ({e.Order.Quantity} @ {e.Order.FillPrice}) — cancelling other leg immediately.")

                ' Fire and forget cancellation of the orphan leg
                Task.Run(Async Function()
                             If isTp AndAlso matchingBracket.SlOrderId.HasValue Then
                                 Try
                                     Await _orderService.CancelOrderAsync(matchingBracket.SlOrderId.Value)
                                 Catch
                                     SyncLock _zombieOrders
                                         If Not _zombieOrders.Contains(matchingBracket.SlOrderId.Value) Then _zombieOrders.Add(matchingBracket.SlOrderId.Value)
                                     End SyncLock
                                 End Try
                             End If
                             If isSl AndAlso matchingBracket.TpOrderId.HasValue Then
                                 Try
                                     Await _orderService.CancelOrderAsync(matchingBracket.TpOrderId.Value)
                                 Catch
                                     SyncLock _zombieOrders
                                         If Not _zombieOrders.Contains(matchingBracket.TpOrderId.Value) Then _zombieOrders.Add(matchingBracket.TpOrderId.Value)
                                     End SyncLock
                                 End Try
                             End If

                             ' Trigger an immediate state check to update UI/P&L faster
                             ' Use the lock to ensure we don't overlap with the timer
                             If Interlocked.CompareExchange(_callbackRunning, 1, 0) = 0 Then
                                 Try
                                     Await DoCheckAsync()
                                 Catch ex As Exception
                                     Log($"⚠️  Error during event-triggered check: {ex.Message}")
                                 Finally
                                     Interlocked.Exchange(_callbackRunning, 0)
                                 End Try
                             End If
                         End Function)
            End If
        End Sub

        ' ── Timer callback ───────────────────────────────────────────────────────

        Private Sub TimerCallback(state As Object)
            If Not _running Then Return
            If Interlocked.CompareExchange(_callbackRunning, 1, 0) <> 0 Then
                Log("⏭  Previous bar check still running — skipping this tick")
                Return
            End If

            Task.Run(Async Function() As Task
                         Try
                             Await DoCheckAsync()
                         Catch ex As OperationCanceledException
                             ' Normal: Stop() called while API request in flight
                         Catch ex As Exception
                             _logger.LogError(ex, "SniperExecutionEngine unhandled error")
                             Log($"⚠️  Error during bar check: {ex.Message}")
                         Finally
                             Interlocked.Exchange(_callbackRunning, 0)
                         End Try
                     End Function)
        End Sub

        Private Async Function DoCheckAsync() As Task
            If Not _running Then Return
            Dim ct = If(_cts IsNot Nothing, _cts.Token, CancellationToken.None)

            ' ── Check expiry ─────────────────────────────────────────────────────
            If DateTimeOffset.UtcNow > _expiresAt Then
                Await StopAsync("Sniper session expired")
                Return
            End If

            Dim remaining = _expiresAt - DateTimeOffset.UtcNow
            Dim remStr = $"{CInt(remaining.TotalHours)}h {remaining.Minutes}m remaining"

            ' ── Fetch 1-min bars ─────────────────────────────────────────────────
            Await _ingestionService.IngestAsync(_contractId, BarTimeframe.OneMinute, 70, ct)
            Dim bars = Await _ingestionService.GetBarsForMLAsync(_contractId, BarTimeframe.OneMinute, 70, ct)

            If bars Is Nothing OrElse bars.Count < 55 Then
                Dim cnt = If(bars Is Nothing, 0, bars.Count)
                Log($"Waiting for 1-min bars — have {cnt}/55 needed ({remStr})")
                Return
            End If

            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim ema8Vals = TechnicalIndicators.EMA(closes, 8)
            Dim ema21Vals = TechnicalIndicators.EMA(closes, 21)
            Dim ema50Vals = TechnicalIndicators.EMA(closes, 50)

            Dim ema8Now = TechnicalIndicators.LastValid(ema8Vals)
            Dim ema8Prev = TechnicalIndicators.PreviousValid(ema8Vals)
            Dim ema21Now = TechnicalIndicators.LastValid(ema21Vals)
            Dim ema21Prev = TechnicalIndicators.PreviousValid(ema21Vals)
            Dim ema50Now = TechnicalIndicators.LastValid(ema50Vals)
            Dim ema50Prev = TechnicalIndicators.PreviousValid(ema50Vals)

            Dim lastBar = bars.Last()
            Dim lastClose = lastBar.Close
            _lastClose = lastClose   ' capture for EmergencyCloseAsync P&L estimate

            ' ── Check if open position closed (no Working orders remain) ─────────
            ' If no brackets, we should not have any qty.
            If _currentQty > 0 Then
                Dim liveOrders = Await _orderService.GetLiveWorkingOrdersAsync(_accountId, _contractId, ct)

                ' ── Zombie (Orphan) Cleanup ──────────────────────────────────────────
                If _zombieOrders.Any() Then
                    Dim zombiesToKill As New List(Of Long)
                    SyncLock _zombieOrders
                        zombiesToKill = _zombieOrders.ToList()
                    End SyncLock

                    ' Filter only those that are still live
                    Dim stillLiveZombies = liveOrders.Where(Function(o) o.ExternalOrderId.HasValue AndAlso zombiesToKill.Contains(o.ExternalOrderId.Value)).ToList()
                    If stillLiveZombies.Any() Then
                        Log($"🧟 Found {stillLiveZombies.Count} zombie orders still active. Forcing cancellation...")
                        For Each zombie In stillLiveZombies
                            Try
                                Await _orderService.CancelOrderAsync(zombie.ExternalOrderId.Value)
                            Catch ex As Exception
                                Log($"⚠️  Failed to kill zombie {zombie.ExternalOrderId}: {ex.Message}")
                            End Try
                        Next
                    End If

                    ' Remove zombies that are no longer in liveOrders (successfully killed or filled/gone)
                    ' Note: If filled, we hope it didn't open a position. But if it did, we rely on the user or emergency close?
                    ' We just remove them from tracking so we don't spam cancel attempts forever if they are truly gone.
                    SyncLock _zombieOrders
                        For Each z In zombiesToKill
                            If Not liveOrders.Any(Function(o) o.ExternalOrderId = z) Then
                                _zombieOrders.Remove(z)
                            End If
                        Next
                    End SyncLock
                End If

                Dim bracketsToRemove As New List(Of BracketPair)
                Dim totalClosedPnl As Decimal = 0D
                Dim primaryExitReason As String = "Closed"

                For Each bracket In _brackets.ToList() ' ToList to allow modification if needed, though we track removals separately
                    Dim tpMissing = bracket.TpOrderId.HasValue AndAlso Not liveOrders.Any(Function(o) o.ExternalOrderId = bracket.TpOrderId.Value)
                    Dim slMissing = bracket.SlOrderId.HasValue AndAlso Not liveOrders.Any(Function(o) o.ExternalOrderId = bracket.SlOrderId.Value)

                    If tpMissing OrElse slMissing Then
                        ' Cleanup Orphans
                        If bracket.TpOrderId.HasValue AndAlso Not tpMissing Then
                            Try
                                Await _orderService.CancelOrderAsync(bracket.TpOrderId.Value)
                            Catch
                                ' If cancellation fails, track as Zombie so we retry later
                                SyncLock _zombieOrders
                                    If Not _zombieOrders.Contains(bracket.TpOrderId.Value) Then _zombieOrders.Add(bracket.TpOrderId.Value)
                                End SyncLock
                            End Try
                        End If
                        If bracket.SlOrderId.HasValue AndAlso Not slMissing Then
                            Try
                                Await _orderService.CancelOrderAsync(bracket.SlOrderId.Value)
                            Catch
                                SyncLock _zombieOrders
                                    If Not _zombieOrders.Contains(bracket.SlOrderId.Value) Then _zombieOrders.Add(bracket.SlOrderId.Value)
                                End SyncLock
                            End Try
                        End If

                        ' Determine Reason & P&L for this bracket
                        Dim bracketPnl As Decimal = 0D
                        Dim exitReason As String = "Closed"

                        If tpMissing Then
                            exitReason = "TP"
                            Try
                                Dim tpFill = Await _orderService.TryGetOrderFillPriceAsync(bracket.TpOrderId.Value, _accountId)
                                If tpFill.HasValue Then
                                    Dim priceMove = If(_tradeSide = OrderSide.Buy, tpFill.Value - _averageEntry, _averageEntry - tpFill.Value)
                                    bracketPnl = priceMove / _tickSize * _tickValue * bracket.Qty
                                End If
                            Catch : End Try
                        ElseIf slMissing Then
                            exitReason = "SL"
                            ' Estimate SL fill
                            Dim slPrice = If(_tradeSide = OrderSide.Buy, _averageEntry - _stopLossTicks * _tickSize, _averageEntry + _stopLossTicks * _tickSize)
                            Try
                                Dim slFill = Await _orderService.TryGetOrderFillPriceAsync(bracket.SlOrderId.Value, _accountId)
                                If slFill.HasValue Then slPrice = slFill.Value
                            Catch : End Try
                            Dim priceMove = If(_tradeSide = OrderSide.Buy, slPrice - _averageEntry, _averageEntry - slPrice)
                            bracketPnl = priceMove / _tickSize * _tickValue * bracket.Qty
                        End If

                        totalClosedPnl += bracketPnl
                        primaryExitReason = exitReason ' Last one wins or priority? TP nice to know.
                        bracketsToRemove.Add(bracket)
                    End If
                Next

                If bracketsToRemove.Count > 0 Then
                    For Each b In bracketsToRemove
                        _brackets.Remove(b)
                        _currentQty -= b.Qty
                    Next

                    Log($"✓ Bracket(s) closed ({primaryExitReason}) — removed {bracketsToRemove.Sum(Function(b) b.Qty)} contracts | P&L ≈ ${totalClosedPnl:N0}")
                    RaiseEvent TradeClosed(Me, New TradeClosedEventArgs(primaryExitReason, totalClosedPnl))

                    If _currentQty <= 0 Then
                        _currentQty = 0
                        _entryPrices.Clear()
                        _averageEntry = 0D
                        _lastEntryPrice = 0D
                        _brackets.Clear()
                        _freeRideActive = False
                        _barsInTrade = 0
                        RaisePositionChanged()
                        Return
                    Else
                        RaisePositionChanged()
                    End If
                End If

                _barsInTrade += 1 ' Increment bars in trade

                ' ── Scale-in check (while position open and qty < max) ────────────
                If _currentQty < _targetTotalSize Then
                    Dim momentumHolds As Boolean
                    Dim priceMoved As Boolean

                    ' Calculate Volatility-Normalized Distance (ATR-Fraction)
                    Dim highVals = bars.Select(Function(b) b.High).ToList()
                    Dim lowVals = bars.Select(Function(b) b.Low).ToList()
                    Dim atrVals = TechnicalIndicators.ATR(highVals, lowVals, closes, 14)
                    Dim currentAtr = TechnicalIndicators.LastValid(atrVals)

                    ' Fallback if ATR not yet valid
                    ' We use 14 bars for ATR, so if count > 55 we are fine.
                    ' Min distance = _volatilityAtrFactor * ATR.
                    ' E.g., for NQ (~ATR 20 pts), factor 0.4 => 8 pts distance.
                    Dim scaleDistance = If(currentAtr > 0, currentAtr * CDec(_volatilityAtrFactor), 10D * _tickValue)
                    Dim scaleTicks = scaleDistance / _tickSize

                    If _tradeSide = OrderSide.Buy Then
                        momentumHolds = ema8Now > ema21Now   ' EMA8 still above EMA21
                        priceMoved = lastClose >= (_lastEntryPrice + scaleDistance)
                    Else
                        momentumHolds = ema8Now < ema21Now   ' EMA8 still below EMA21
                        priceMoved = lastClose <= (_lastEntryPrice - scaleDistance)
                    End If

                    If momentumHolds AndAlso priceMoved Then
                        Log($"📈 Scale-in trigger: momentum holds + price moved {scaleDistance:F2} (ATR {currentAtr:F1} * {_volatilityAtrFactor}). Requesting scale-in...")
                        Await ScaleInAsync(lastClose, ct)
                    Else
                        Log($"Position open: {_currentQty}/{_targetTotalSize} contracts | AvgEntry={_averageEntry:F2} | Heat used:{CalculateCurrentHeat():F0}/{_maxRiskHeatTicks} | Next Scale needed:{scaleDistance:F2} away")
                    End If
                Else
                    Log($"Position at max ({_targetTotalSize}/{_targetTotalSize} contracts) | AvgEntry={_averageEntry:F2} | Close={lastClose:F2} | {remStr}")
                End If

                ' ── Free-ride SL check ───────────────────────────────────────────
                If _currentQty > 0 Then ' Manage stops for all positions
                    ' Derive the live mark price from the broker's own UnrealizedPnlUsd
                    ' so the SL tracks the real market rather than a potentially stale bar close.
                    Dim priceForSl = lastClose
                    If IsTopStepX AndAlso _tickSize > 0 AndAlso _tickValue > 0 Then
                        Dim liveSnap = Await _orderService.GetLivePositionSnapshotAsync(
                                           _accountId, _contractId, Nothing, ct)
                        If liveSnap IsNot Nothing AndAlso liveSnap.PositionId <> 0 AndAlso
                           liveSnap.Units > 0 Then
                            Dim dollarPerPoint = _tickValue / _tickSize   ' e.g. MCL $1/$0.01 = $100
                            Dim pnlPerUnit = liveSnap.UnrealizedPnlUsd / (liveSnap.Units * dollarPerPoint)
                            Dim markPrice = If(liveSnap.IsBuy,
                                              liveSnap.OpenRate + pnlPerUnit,
                                              liveSnap.OpenRate - pnlPerUnit)
                            ' Sanity: reject if mark differs from bar close by more than $5
                            If markPrice > 0 AndAlso Math.Abs(markPrice - lastClose) < 5D Then
                                priceForSl = markPrice
                            End If

                            ' ── Self-healing avg entry ────────────────────────────────────
                            ' If the initial position snapshot failed at entry time, _averageEntry
                            ' was set from the bar-close estimate. Correct it now using the
                            ' actual OpenRate from the live position — but only if the discrepancy
                            ' is large enough to have been caused by a bar-close mismatch (≥ 1 tick)
                            ' and small enough to be plausible slippage (< 50 ticks = $0.50 on MCL).
                            If liveSnap.OpenRate > 0 AndAlso
                               Math.Abs(liveSnap.OpenRate - _averageEntry) >= _tickSize AndAlso
                               Math.Abs(liveSnap.OpenRate - _averageEntry) < 50 * _tickSize Then
                                Log($"🔧 Avg entry corrected from bar-close {_averageEntry:F4} → fill {liveSnap.OpenRate:F4} (live snapshot)")
                                _averageEntry = liveSnap.OpenRate
                                _lastEntryPrice = liveSnap.OpenRate
                                ' Update all entry price records proportionally
                                If _entryPrices.Count > 0 Then
                                    For i = 0 To _entryPrices.Count - 1
                                        _entryPrices(i) = liveSnap.OpenRate
                                    Next
                                End If
                            End If
                        End If
                    End If
                    Await ManageTrailingStopsAsync(priceForSl, ct)
                End If

                ' ── Structure-Fail Exit Check ──────────────────────────────────────
                If _currentQty > 0 AndAlso _enableStructureFailExit Then
                    Await CheckStructureFailAsync(lastClose, ema21Now, _barsInTrade, ct)
                End If

                Return
            End If

            ' ── Flat: look for initial EMA8/EMA21 crossover ──────────────────────
            Dim crossedAbove = ema8Prev <= ema21Prev AndAlso ema8Now > ema21Now
            Dim crossedBelow = ema8Prev >= ema21Prev AndAlso ema8Now < ema21Now
            Dim ema50Rising = ema50Now > ema50Prev
            Dim ema50Falling = ema50Now < ema50Prev

            Dim side As OrderSide? = Nothing

            If crossedAbove AndAlso lastClose > CDec(ema50Now) AndAlso ema50Rising Then
                Log($"✅ 3-EMA CASCADE LONG: EMA8 crossed above EMA21 | Price above rising EMA50")
                Log($"   Close={lastClose:F2} EMA8={ema8Now:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2}")
                side = OrderSide.Buy
            ElseIf crossedBelow AndAlso lastClose < CDec(ema50Now) AndAlso ema50Falling Then
                Log($"✅ 3-EMA CASCADE SHORT: EMA8 crossed below EMA21 | Price below falling EMA50")
                Log($"   Close={lastClose:F2} EMA8={ema8Now:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2}")
                side = OrderSide.Sell
            Else
                Log($"Monitoring — Close={lastClose:F2} | EMA8={ema8Now:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2} | {remStr}")
            End If

            If side.HasValue Then
                _tradeSide = side.Value
                Await PlaceInitialEntryAsync(side.Value, lastClose, ct)
            End If
        End Function

        ' ── Order placement ─────────────────────────────────────────────────────

        Private Function CalculateAddQuantity(addIndex As Integer) As Integer
            If _targetTotalSize <= 0 Then Return 0

            ' Tier A: Core (Front-load high quality entry)
            ' e.g. Target=10, Core=0.6, Count=2 => CoreTotal=6.
            ' Add #0: 3 contracts. Add #1: 3 contracts.
            If addIndex < _coreAddsCount Then
                Dim totalCore = Math.Max(1, CInt(Math.Round(_targetTotalSize * _coreSizeFraction)))
                Dim baseQty = totalCore \ _coreAddsCount
                Dim remainder = totalCore Mod _coreAddsCount

                ' Distribute remainder to first adds
                Dim allocation = baseQty + If(addIndex < remainder, 1, 0)
                Return Math.Max(1, allocation)
            End If

            ' Tier B: Momentum (Immediate follow-up)
            ' Traditionally 1 unit (now configurable)
            If addIndex = _coreAddsCount Then
                Return _momentumTierSize
            End If

            ' Tier C: Extension (Late trend)
            If addIndex > _coreAddsCount Then
                If _extensionAllowed Then Return _extensionTierSize Else Return 0
            End If

            Return 0
        End Function

        Private Async Function PlaceInitialEntryAsync(side As OrderSide,
                                                       entryPrice As Decimal,
                                                       ct As CancellationToken) As Task
            _addIndex = 0
            Dim qty = CalculateAddQuantity(_addIndex)
            If qty <= 0 Then
                Log($"🛑 Initial entry blocked: calculated quantity is 0 (Config error?)")
                Return
            End If

            ' Ensure we don't exceed max size
            If qty > _targetTotalSize Then qty = _targetTotalSize

            ' For TopStepX: submit SL bracket with entry (required by ProjectX API), but NO TP bracket.
            ' After the entry fills we poll for the actual fill price and correct the SL bracket via
            ' EditPositionSlTpAsync, so brackets are always anchored to the real fill — not bar-close data.
            ' Removing TP bracket also eliminates the risk of a TP placed at the wrong price firing instantly.
            Dim entryOrder As New Order With {
                .AccountId = _accountId,
                .ContractId = _contractId,
                .Side = side,
                .OrderType = OrderType.Market,
                .Quantity = qty,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"Sniper: 3-EMA Cascade initial entry (Tier 0, Qty={qty})",
                .InitialStopTicks = If(IsTopStepX, _stopLossTicks, CType(Nothing, Integer?)),
                .InitialTakeProfitTicks = Nothing   ' No TP bracket — trailing stop manages profit
            }

            Dim placed As Order
            Try
                placed = Await _orderService.PlaceOrderAsync(entryOrder)
                Log($"Entry {side} order placed — Market qty={qty}" &
                    If(IsTopStepX, $" (SL={_stopLossTicks}t bracket submitted)", ""))
            Catch ex As Exception
                Log($"⚠️  Entry order failed: {ex.Message}")
                Return
            End Try

            ' ── TopStepX: get actual fill price via position snapshot and correct SL bracket ──
            ' TryGetOrderFillPriceAsync is not available via REST for TopStepX (fills arrive on
            ' the SignalR GatewayUserTrade event). Instead poll the positions endpoint with up to
            ' 4 retries (750 ms apart = up to ~3 s) to handle broker-side registration latency.
            Dim actualFill = entryPrice   ' fallback = bar close
            If IsTopStepX Then
                Dim snapshot As LivePositionSnapshot = Nothing
                Dim maxRetries = 4
                For attempt = 1 To maxRetries
                    Await Task.Delay(750, ct)
                    snapshot = Await _orderService.GetLivePositionSnapshotAsync(_accountId, _contractId, Nothing, ct)
                    If snapshot IsNot Nothing AndAlso snapshot.PositionId <> 0 AndAlso snapshot.OpenRate > 0 Then
                        Exit For
                    End If
                    If attempt < maxRetries Then
                        Log($"⏳ Fill not visible yet (attempt {attempt}/{maxRetries}), retrying...")
                    End If
                Next

                If snapshot IsNot Nothing AndAlso snapshot.PositionId <> 0 AndAlso snapshot.OpenRate > 0 Then
                    actualFill = snapshot.OpenRate
                    Log($"📍 Entry fill (from position snapshot): {actualFill:F4}")

                    ' Correct the SL bracket if fill differs materially from bar-close estimate
                    If Math.Abs(actualFill - entryPrice) >= _tickSize Then
                        Dim tick = _tickSize
                        Dim isBuy = (side = OrderSide.Buy)
                        Dim slOffset = _stopLossTicks * tick
                        Dim correctedSl = If(isBuy, actualFill - slOffset, actualFill + slOffset)
                        Dim ok = Await _orderService.EditPositionSlTpAsync(
                                     snapshot.PositionId, correctedSl, Nothing, cancel:=ct)
                        If ok Then
                            Log($"✅ SL corrected: bar-close {entryPrice:F4} → fill-based {correctedSl:F4}")
                        Else
                            Log($"⚠️  SL correction failed — bracket remains at bar-close estimate")
                        End If
                    End If
                Else
                    Log($"⚠️  Position not found after {maxRetries} retries — SL bracket uses bar-close {entryPrice:F4}")
                End If
            End If

            ' Track position using the actual (or best-estimate) fill price
            _currentQty = qty
            _entryPrices.Clear()
            For i = 1 To qty
                _entryPrices.Add(actualFill)
            Next
            _averageEntry = actualFill
            _lastEntryPrice = actualFill
            _addIndex += 1

            RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(side, _contractId, 100,
                                                                DateTimeOffset.UtcNow,
                                                                placed.ExternalOrderId))
            RaisePositionChanged()

            ' Record bracket for heat/SL tracking (TopStepX path: no additional orders placed)
            Await PlaceBracketAsync(side, _averageEntry, qty, actualFill, 0, ct)

            ' Start the client-side safety monitor now that a real position is confirmed.
            StartSafetyMonitor()
        End Function

        Private Async Function ScaleInAsync(currentPrice As Decimal, ct As CancellationToken) As Task
            Dim addQty = CalculateAddQuantity(_addIndex)
            If addQty <= 0 Then
                Log($"🛑 Scale-in blocked: Tier #{_addIndex} allows 0 contracts")
                _lastEntryPrice = currentPrice
                Return
            End If

            ' Cap at remaining capacity
            Dim remaining = _targetTotalSize - _currentQty
            If addQty > remaining Then addQty = remaining

            If addQty <= 0 Then
                Log($"🛑 Scale-in blocked: Size cap reached ({_currentQty}/{_targetTotalSize})")
                _lastEntryPrice = currentPrice
                Return
            End If

            ' ── Heat / Risk Check ───────────────────────────────────────────────
            Dim estAvg = ((_averageEntry * _currentQty) + (currentPrice * addQty)) / (_currentQty + addQty)
            Dim slOffset = _stopLossTicks * _tickSize
            Dim estNewSl = If(_tradeSide = OrderSide.Buy, estAvg - slOffset, estAvg + slOffset)

            ' Risk of new lot = Distance from Entry (CurrentPrice) to Stop (EstNewSl)
            Dim newLotDist = Math.Abs(currentPrice - estNewSl)
            Dim newLotHeat = Math.Max(0D, (newLotDist / _tickSize) * addQty)

            Dim currentHeat = CalculateCurrentHeat()
            Dim projectedHeat = currentHeat + newLotHeat

            If projectedHeat > _maxRiskHeatTicks Then
                Log($"🛑 Scale-in BLOCKED by Risk Heat: Cur={currentHeat:F0} + New={newLotHeat:F0} > Max={_maxRiskHeatTicks}")
                ' Update last entry price to prevent repeated attempts at this price level
                _lastEntryPrice = currentPrice
                Return
            End If

            ' Start immediately with entry - no cancellation of previous brackets needed (incremental scaling)

            ' Place additional entry — no TP bracket (same policy as initial entry)
            Dim entryOrder As New Order With {
                .AccountId = _accountId,
                .ContractId = _contractId,
                .Side = _tradeSide,
                .OrderType = OrderType.Market,
                .Quantity = addQty,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"Sniper: scale-in #{_addIndex} (Tier Qty={addQty})",
                .InitialStopTicks = If(IsTopStepX, _stopLossTicks, CType(Nothing, Integer?)),
                .InitialTakeProfitTicks = Nothing   ' No TP bracket
            }

            ' VB.NET: cannot Await inside Catch — capture flag then Await after.
            Dim entryFailed = False
            Dim scaleInPlaced As Order = Nothing
            Try
                scaleInPlaced = Await _orderService.PlaceOrderAsync(entryOrder)
            Catch ex As Exception
                Log($"⚠️  Scale-in entry failed: {ex.Message}")
                entryFailed = True
            End Try

            If entryFailed Then Return

            ' Get actual scale-in fill price via position snapshot (REST fill price not available on TopStepX)
            ' Retry up to 4 times (750 ms apart) to handle broker-side registration latency.
            Dim actualScaleFill = currentPrice
            If IsTopStepX Then
                Dim scaleSnap As LivePositionSnapshot = Nothing
                Dim maxScaleRetries = 4
                For attempt = 1 To maxScaleRetries
                    Await Task.Delay(750, ct)
                    scaleSnap = Await _orderService.GetLivePositionSnapshotAsync(_accountId, _contractId, Nothing, ct)
                    If scaleSnap IsNot Nothing AndAlso scaleSnap.OpenRate > 0 Then Exit For
                    If attempt < maxScaleRetries Then
                        Log($"⏳ Scale-in fill not visible yet (attempt {attempt}/{maxScaleRetries}), retrying...")
                    End If
                Next
                If scaleSnap IsNot Nothing AndAlso scaleSnap.OpenRate > 0 Then
                    actualScaleFill = scaleSnap.OpenRate
                    Log($"📍 Scale-in fill (position snapshot): {actualScaleFill:F4}")
                End If
            End If

            ' Update position tracking
            _currentQty += addQty
            For i = 1 To addQty
                _entryPrices.Add(actualScaleFill)
            Next
            _averageEntry = _entryPrices.Average()
            _lastEntryPrice = actualScaleFill
            _addIndex += 1

            ' After scale-in, update the position's SL to reflect new average entry
            If IsTopStepX AndAlso actualScaleFill <> currentPrice Then
                Dim isBuy = (_tradeSide = OrderSide.Buy)
                Dim scaleSlOffset = _stopLossTicks * _tickSize
                Dim newSl = If(isBuy, _averageEntry - scaleSlOffset, _averageEntry + scaleSlOffset)
                Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(_accountId, _contractId, Nothing, ct)
                If snapshot IsNot Nothing AndAlso snapshot.PositionId <> 0 Then
                    Await _orderService.EditPositionSlTpAsync(snapshot.PositionId, newSl, Nothing, cancel:=ct)
                End If
            End If

            Log($"✅ Scale-in #{_addIndex} @ {actualScaleFill:F2} | Qty={addQty} | New AvgEntry={_averageEntry:F2} | Heat={projectedHeat:F0}/{_maxRiskHeatTicks}")
            RaisePositionChanged()

            Await PlaceBracketAsync(_tradeSide, _averageEntry, addQty, actualScaleFill, _addIndex - 1, ct)
        End Function

        Private Async Function PlaceBracketAsync(side As OrderSide,
                                                  avgEntry As Decimal,
                                                  qty As Integer,
                                                  actualEntryPrice As Decimal,
                                                  addIndex As Integer,
                                                  ct As CancellationToken) As Task
            Dim tick = _tickSize
            Dim exitSide = If(side = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)

            ' Generate a unique ID for our internal tracking (manual protection)
            Dim ocoId = Guid.NewGuid().ToString("N")
            Dim pair As New BracketPair With {
                .Qty = qty,
                .OcoId = ocoId,
                .EntryPrice = actualEntryPrice,
                .AddIndex = addIndex
            }

            ' ── TopStepX: brackets already submitted with entry order ────────────
            ' ProjectX only supports SL/TP as brackets on the entry order itself.
            ' Standalone Stop or StopLimit orders placed after entry are rejected.
            ' Record the computed SL price for heat tracking; no separate orders needed.
            If IsTopStepX Then
                pair.CurrentSlPrice = If(_stopLossTicks > 0,
                    If(side = OrderSide.Buy,
                       avgEntry - _stopLossTicks * tick,
                       avgEntry + _stopLossTicks * tick),
                    0D)
                pair.CurrentTpPrice = If(_takeProfitTicks > 0,
                    If(side = OrderSide.Buy,
                       avgEntry + _takeProfitTicks * tick,
                       avgEntry - _takeProfitTicks * tick),
                    0D)
                _brackets.Add(pair)
                Return
            End If

            ' ── Take Profit Limit ─────────────────────────────────────────────────
            If _takeProfitTicks > 0 Then
                Dim tpOffset = _takeProfitTicks * tick
                Dim tpPrice = If(side = OrderSide.Buy, avgEntry + tpOffset, avgEntry - tpOffset)
                Dim tpOrder As New Order With {
                    .AccountId = _accountId,
                    .ContractId = _contractId,
                    .Side = exitSide,
                    .OrderType = OrderType.Limit,
                    .Quantity = qty,
                    .LimitPrice = tpPrice,
                    .Status = OrderStatus.Pending,
                    .PlacedAt = DateTimeOffset.UtcNow,
                    .Notes = $"Sniper TP qty={qty}"
                }
                Try
                    Dim placed = Await _orderService.PlaceOrderAsync(tpOrder)
                    pair.TpOrderId = placed.ExternalOrderId
                    Log($"Take Profit Limit qty={qty} @ {tpPrice:F2} (+{_takeProfitTicks}t from avg)")
                Catch ex As Exception
                    Log($"⚠️  TP order failed: {ex.Message}")
                End Try
            End If

            ' ── Stop Loss StopLimit ─────────────────────────────────────────────
            ' UAT-BUG-008: ProjectX rejects StopMarket — use StopLimit with 5-tick slippage buffer.
            If _stopLossTicks > 0 Then
                ' Default initial SL based on individual entry price for clarity in multi-tier
                ' Or avgEntry? Using avgEntry unifies risk but tighter on add-ons. 
                ' Let's use avgEntry as per original design for initial placement.
                Dim basePrice = avgEntry

                Dim slOffset = _stopLossTicks * tick
                Dim slPrice = If(side = OrderSide.Buy, basePrice - slOffset, basePrice + slOffset)
                pair.CurrentSlPrice = slPrice

                Dim slippage = 5 * tick
                Dim slLimit = If(side = OrderSide.Buy, slPrice - slippage, slPrice + slippage)
                Dim slOrder As New Order With {
                    .AccountId = _accountId,
                    .ContractId = _contractId,
                    .Side = exitSide,
                    .OrderType = OrderType.StopLimit,
                    .Quantity = qty,
                    .StopPrice = slPrice,
                    .LimitPrice = slLimit,
                    .Status = OrderStatus.Pending,
                    .PlacedAt = DateTimeOffset.UtcNow,
                    .Notes = $"Sniper SL qty={qty}"
                }
                ' VB.NET: cannot Await inside Catch — use flag pattern.
                Dim slRejected = False
                Try
                    Dim placed = Await _orderService.PlaceOrderAsync(slOrder)
                    pair.SlOrderId = placed.ExternalOrderId

                    ' Verify if the order was actually placed or if it came back as rejected
                    If placed.Status = OrderStatus.Rejected Then
                        ' Use a generic log message since RejectionReason property might be missing or private
                        Log($"🚨 SL order STATUS REJECTED by API (Order ID {placed.ExternalOrderId})")
                        slRejected = True
                    Else
                        Log($"Stop Loss StopLimit qty={qty} @ {slPrice:F2} (-{_stopLossTicks}t from avg)")
                    End If
                Catch ex As Exception
                    Log($"🚨 SL order EXCEPTION during placement — {ex.Message}")
                    slRejected = True
                End Try

                If slRejected Then
                    Log($"🚨 Position UNPROTECTED — emergency closing ALL positions!")
                    ' Cancel the TP (best-effort) so we don't have an orphaned limit order
                    If pair.TpOrderId.HasValue Then
                        Try : Await _orderService.CancelOrderAsync(pair.TpOrderId.Value) : Catch : End Try
                    End If
                    Await EmergencyCloseAsync(ct)
                    Return
                End If
            End If

            _brackets.Add(pair)
        End Function

        Private Function CalculateCurrentHeat() As Decimal
            Dim totalHeat As Decimal = 0D
            For Each b In _brackets
                Dim bracketSl = b.CurrentSlPrice
                If bracketSl = 0D Then Continue For

                Dim priceDist = If(_tradeSide = OrderSide.Buy, b.EntryPrice - bracketSl, bracketSl - b.EntryPrice)
                Dim ticksRisk = priceDist / _tickSize

                If ticksRisk < 0 Then ticksRisk = 0
                totalHeat += (ticksRisk * b.Qty)
            Next
            Return totalHeat
        End Function

        Private Async Function ManageTrailingStopsAsync(currentPrice As Decimal, ct As CancellationToken) As Task
            If _brackets.Count = 0 Then Return

            Dim updatedCount = 0
            Dim tick = _tickSize

            For Each b In _brackets
                Dim isCore = b.AddIndex < _coreAddsCount

                ' Trailing Logic
                ' Core: Loose (2.0x), Add-On: Tight (1.0x)
                Dim trailFactor = If(isCore, 2.0D, 1.0D)
                Dim trailDist = (_stopLossTicks * trailFactor) * tick

                Dim targetSlPrice As Decimal = 0D
                Dim shouldUpdate As Boolean = False
                Dim updateReason As String = ""

                If _tradeSide = OrderSide.Buy Then
                    Dim potentialSl = currentPrice - trailDist

                    ' Breakeven Logic (Add-On only)
                    If Not isCore Then
                        Dim profit = currentPrice - b.EntryPrice
                        If profit > (5 * tick) Then
                            Dim bePrice = b.EntryPrice + tick
                            If potentialSl < bePrice Then potentialSl = bePrice
                        End If
                    End If

                    ' Ensure Monotonicity (Only move UP)
                    If potentialSl > (b.CurrentSlPrice + tick) Then
                        targetSlPrice = potentialSl
                        shouldUpdate = True
                        updateReason = If(isCore, "Core Trail", "Add-On Trail")
                    End If

                Else ' Sell
                    Dim potentialSl = currentPrice + trailDist

                    If Not isCore Then
                        Dim profit = b.EntryPrice - currentPrice
                        If profit > (5 * tick) Then
                            Dim bePrice = b.EntryPrice - tick
                            If potentialSl > bePrice Then potentialSl = bePrice
                        End If
                    End If

                    ' Ensure Monotonicity (Only move DOWN)
                    ' Note: For Sell, CurrentSlPrice starts above Entry. Lower is better.
                    If potentialSl < (b.CurrentSlPrice - tick) Then
                        targetSlPrice = potentialSl
                        shouldUpdate = True
                        updateReason = If(isCore, "Core Trail", "Add-On Trail")
                    End If
                End If

                If shouldUpdate Then
                    If IsTopStepX Then
                        ' TopStepX: modify the resting bracket order in-place via EditPositionSlTpAsync.
                        ' Standalone StopLimit orders placed after entry are rejected by ProjectX.

                        ' Compute trailing TP: ratchets away from price, never back toward it.
                        Dim newTpCandidate As Decimal? = Nothing
                        If b.CurrentTpPrice > 0D AndAlso _takeProfitTicks > 0 Then
                            Dim tpDist = (_takeProfitTicks * trailFactor) * tick
                            If _tradeSide = OrderSide.Buy Then
                                Dim rawTp = CDec(Math.Ceiling(CDbl((currentPrice + tpDist) / tick))) * tick
                                If rawTp > b.CurrentTpPrice Then newTpCandidate = rawTp
                            Else
                                Dim rawTp = CDec(Math.Floor(CDbl((currentPrice - tpDist) / tick))) * tick
                                If rawTp < b.CurrentTpPrice Then newTpCandidate = rawTp
                            End If
                        End If

                        Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                            _accountId, _contractId, Nothing, ct)
                        If snapshot IsNot Nothing AndAlso snapshot.PositionId <> 0 Then
                            Dim ok = Await _orderService.EditPositionSlTpAsync(
                                snapshot.PositionId, targetSlPrice, newTpCandidate, cancel:=ct)
                            If ok Then
                                b.CurrentSlPrice = targetSlPrice
                                If newTpCandidate.HasValue Then b.CurrentTpPrice = newTpCandidate.Value
                                updatedCount += 1
                                Log($"🔒 {updateReason}: SL → {targetSlPrice:F2}" &
                                    If(newTpCandidate.HasValue, $"  TP → {newTpCandidate.Value:F2}", "") &
                                    If(targetSlPrice >= _averageEntry AndAlso _tradeSide = OrderSide.Buy, " [FREE RIDE]", ""))
                            Else
                                Log($"⚠️  Trailing SL: EditPositionSlTp failed for {_contractId}")
                            End If
                        Else
                            Log($"⚠️  Trailing SL: could not resolve open position for {_contractId}")
                        End If
                    Else
                        ' Cancel the old StopLimit bracket and place a replacement.
                        Dim cancelSlSuccess = True
                        If b.SlOrderId.HasValue Then
                            Try
                                Await _orderService.CancelOrderAsync(b.SlOrderId.Value)
                            Catch ex As Exception
                                Log($"⚠️  Trailing SL: could not cancel old SL: {ex.Message}")
                                cancelSlSuccess = False
                            End Try
                        End If

                        If cancelSlSuccess Then
                            Dim slippage = 5 * tick
                            Dim slLimit = If(_tradeSide = OrderSide.Buy, targetSlPrice - slippage, targetSlPrice + slippage)

                            Dim slOrder As New Order With {
                                .AccountId = _accountId,
                                .ContractId = _contractId,
                                .Side = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy),
                                .OrderType = OrderType.StopLimit,
                                .Quantity = b.Qty,
                                .StopPrice = targetSlPrice,
                                .LimitPrice = slLimit,
                                .Status = OrderStatus.Pending,
                                .PlacedAt = DateTimeOffset.UtcNow,
                                .Notes = $"Sniper {updateReason} SL @ {targetSlPrice:F2}"
                            }

                            Try
                                Dim placed = Await _orderService.PlaceOrderAsync(slOrder)
                                b.SlOrderId = placed.ExternalOrderId
                                b.CurrentSlPrice = targetSlPrice
                                updatedCount += 1
                            Catch ex As Exception
                                Log($"⚠️  Trailing SL update failed for bracket: {ex.Message}")
                            End Try
                        End If
                    End If
                End If
            Next

            If updatedCount > 0 Then
                _freeRideActive = True
                RaisePositionChanged()
            End If
        End Function

        ' ── Helpers ─────────────────────────────────────────────────────────────

        ''' <summary>
        ''' Place a market close for the entire open position.
        ''' Called when the SL StopLimit is rejected — never leave an unprotected position.
        ''' Resets internal state and raises TradeClosed so the VM tracks the trade.
        ''' </summary>
        Private Async Function EmergencyCloseAsync(ct As CancellationToken) As Task
            Dim exitSide = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)
            Dim closeOrder As New Order With {
                .AccountId = _accountId,
                .ContractId = _contractId,
                .Side = exitSide,
                .OrderType = OrderType.Market,
                .Quantity = _currentQty,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = "Sniper: emergency close — SL rejected"
            }

            Try
                Await _orderService.PlaceOrderAsync(closeOrder)
                Log("🔴 Emergency close order placed — position closing at market")
            Catch exClose As Exception
                Log($"🚨 CRITICAL: Emergency close ALSO failed: {exClose.Message}")
            End Try

            ' Estimate P&L from last known bar close (best effort — actual fill may differ)
            Dim estimatedPnl As Decimal = 0D
            If _lastClose > 0D AndAlso _averageEntry > 0D Then
                Dim priceMove = If(_tradeSide = OrderSide.Buy,
                                   _lastClose - _averageEntry,
                                   _averageEntry - _lastClose)
                estimatedPnl = priceMove / _tickSize * _tickValue * _currentQty
            End If

            Log($"✓ Position closed (Emergency) — {_currentQty} contracts | P&L ≈ ${estimatedPnl:N0}")
            RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("Emergency Close", estimatedPnl))

            ' Reset position state so the next poll starts looking for a fresh signal
            _currentQty = 0
            _entryPrices.Clear()
            _averageEntry = 0D
            _lastEntryPrice = 0D
            _brackets.Clear()
            _freeRideActive = False
            _addIndex = 0
            _barsInTrade = 0
            _safetyMonitorCts?.Cancel()   ' position is flat — stop the safety monitor
            RaisePositionChanged()
        End Function

        ''' <summary>
        ''' Starts the client-side P&amp;L safety monitor in a background Task.
        ''' Polls the live position snapshot every 3 seconds and force-flattens
        ''' if the broker's reported unrealised loss exceeds the configured SL dollar
        ''' amount, or unrealised profit exceeds the configured TP dollar amount.
        ''' This acts as a backstop against silent broker-side bracket failures.
        ''' </summary>
        Private Sub StartSafetyMonitor()
            _safetyMonitorCts?.Cancel()
            _safetyMonitorCts = New CancellationTokenSource()
            Dim ct = _safetyMonitorCts.Token
            Log("🛡 Safety monitor active — polling every 3s (backstop for bracket failures)")
            Task.Run(Async Function()
                         Try
                             While Not ct.IsCancellationRequested AndAlso _running AndAlso _currentQty > 0
                                 Await Task.Delay(3000, ct)
                                 If ct.IsCancellationRequested OrElse Not _running OrElse _currentQty = 0 Then Exit While

                                 Try
                                     Dim snap = Await _orderService.GetLivePositionSnapshotAsync(
                                                    _accountId, _contractId, Nothing, ct)
                                     If snap Is Nothing OrElse snap.PositionId = 0 Then Continue While

                                     Dim unrealised = snap.UnrealizedPnlUsd

                                     ' Dollar thresholds: ticks × tickSize (price) × tickValue ($/tick) × qty
                                     Dim slDollars = CDec(_stopLossTicks) * _tickSize * _tickValue * _currentQty
                                     Dim tpDollars = CDec(_takeProfitTicks) * _tickSize * _tickValue * _currentQty

                                     Dim triggerReason As String = Nothing
                                     If unrealised <= -Math.Abs(slDollars) Then
                                         triggerReason = $"🛡 Safety monitor: SL dollar limit hit (P&L={unrealised:+$0.00;-$0.00}, limit=-${Math.Abs(slDollars):F2}) — bracket may have failed"
                                     ElseIf tpDollars > 0 AndAlso unrealised >= tpDollars Then
                                         triggerReason = $"🛡 Safety monitor: TP dollar limit hit (P&L={unrealised:+$0.00;-$0.00}, target=${tpDollars:F2}) — bracket may have failed"
                                     End If

                                     If triggerReason IsNot Nothing Then
                                         ' Guard against re-entrant firing
                                         If Interlocked.CompareExchange(_safetyFiring, 1, 0) = 0 Then
                                             Log(triggerReason)
                                             _safetyMonitorCts.Cancel()
                                             Await StopAsync(triggerReason)
                                         End If
                                         Exit While
                                     End If
                                 Catch apiEx As Exception When Not ct.IsCancellationRequested
                                     ' Non-fatal: a single snapshot failure should not stop the monitor
                                     Log($"⚠️  Safety monitor: snapshot error (will retry) — {apiEx.Message}")
                                 End Try
                             End While
                         Catch ex As OperationCanceledException
                             ' Normal cancellation — exit silently
                         Catch ex As Exception
                             Log($"⚠️  Safety monitor exited unexpectedly: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Async Function CheckStructureFailAsync(currentPrice As Decimal,
                                                         ema21 As Double,
                                                         barsHeld As Integer,
                                                         ct As CancellationToken) As Task
            If barsHeld < _minBarsBeforeExit Then Return

            Dim exitTriggered As Boolean = False
            Dim reason As String = "Structure Fail"
            Dim emaVal = CDec(ema21)
            Dim threshold = _ema21BreakTicks * _tickSize

            If _tradeSide = OrderSide.Buy Then
                ' Long: Exit if Price < EMA21 - threshold
                If currentPrice < (emaVal - threshold) Then
                    exitTriggered = True
                    reason = $"Structure Fail (Long): Close {currentPrice:F2} < EMA21 {emaVal:F2} - {threshold:F2}"
                End If
            Else
                ' Short: Exit if Price > EMA21 + threshold
                If currentPrice > (emaVal + threshold) Then
                    exitTriggered = True
                    reason = $"Structure Fail (Short): Close {currentPrice:F2} > EMA21 {emaVal:F2} + {threshold:F2}"
                End If
            End If

            If exitTriggered Then
                Log($"🚨 {reason} — Exiting position ({_currentQty} contracts)")
                Await EmergencyCloseAsync(ct)
            End If
        End Function

        Private Sub RaisePositionChanged()
            RaiseEvent PositionChanged(Me, New SniperPositionEventArgs(
                _currentQty, _averageEntry, _freeRideActive, CalculateCurrentHeat()))
        End Sub

        Private Sub Log(message As String)
            Dim timestamped = $"{DateTime.Now:HH:mm:ss}  {message}"
            _logger.LogInformation("[SniperEngine] {Msg}", message)
            RaiseEvent LogMessage(Me, timestamped)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                ' Fire and forget cleaning up - best effort when disposing
                ' We do NOT dispose _cts here to avoid race condition with StopAsync which uses it.
                Dim unused = Task.Run(Function() StopAsync("Engine disposed"))
                _timer?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

    ''' <summary>Position state snapshot raised on every qty/avgEntry/freeRide change.</summary>
    Public Class SniperPositionEventArgs
        Inherits EventArgs
        Public ReadOnly Property CurrentQty As Integer
        Public ReadOnly Property AverageEntry As Decimal
        Public ReadOnly Property FreeRideActive As Boolean
        Public ReadOnly Property CurrentHeat As Decimal

        Public Sub New(qty As Integer, avgEntry As Decimal, freeRide As Boolean, currentHeat As Decimal)
            CurrentQty = qty
            AverageEntry = avgEntry
            FreeRideActive = freeRide
            CurrentHeat = currentHeat
        End Sub
    End Class

End Namespace
