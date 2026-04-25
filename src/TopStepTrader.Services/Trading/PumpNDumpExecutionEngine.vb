Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Models.Diagnostics
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Diagnostics
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Pump-n-Dump execution engine: 3-bar price-action entry on 1-minute bars.
    ''' Poll every 15 seconds.
    '''
    ''' State machine:
    '''   FLAT   → 3 consecutive 1-min bars all closing in same direction → OPEN (1 contract)
    '''   OPEN   → unrealisedPnl ≥ freeRidePnlThreshold → move all SLs to avg entry (free-ride)
    '''   OPEN   → price moved ≥ scaleInTicks → scale in 1 more contract (up to targetTotalSize)
    '''   OPEN   → avg last-3-bar range < ATR × momentumFadeAtrFraction → tighten TP by N ticks/poll
    '''   TP/SL hit → back to FLAT
    '''
    ''' Register as Transient — one instance per session.
    ''' </summary>
    Public Class PumpNDumpExecutionEngine
        Implements IPumpNDumpExecutionEngine

        ' ── Dependencies ────────────────────────────────────────────────────────
        Private ReadOnly _ingestionService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _logger As ILogger(Of PumpNDumpExecutionEngine)
        Private ReadOnly _diagLogger As DiagnosticLogger

        ' ── Configuration (set by Start()) ──────────────────────────────────────
        Private _contractId As String
        Private _accountId As Long
        Private _takeProfitTicks As Integer
        Private _stopLossTicks As Integer
        Private _freeRidePnlThreshold As Decimal   ' dollar P&L to trigger free-ride
        Private _scaleInTicks As Integer           ' ticks price must move for next scale-in
        Private _maxRiskHeatTicks As Integer = Integer.MaxValue
        Private _targetTotalSize As Integer = 5
        Private _momentumFadeAtrFraction As Double = 0.5
        Private _tightenTicksPerBar As Integer = 2
        Private _tickSize As Decimal
        Private _tickValue As Decimal
        Private _expiresAt As DateTimeOffset
        Private _tradingStartHour As Integer = 6
        Private _tradingEndHour As Integer = 21

        ' ── Position state ───────────────────────────────────────────────────────
        Private _currentQty As Integer = 0
        Private ReadOnly _entryPrices As New List(Of Decimal)
        Private _averageEntry As Decimal = 0D
        Private _lastEntryPrice As Decimal = 0D
        Private _tradeSide As OrderSide = OrderSide.Buy
        Private _lastClose As Decimal = 0D

        ' Track multiple bracket pairs (one per entry/scale-in)
        Private Class BracketPair
            Public Property TpOrderId As Long?
            Public Property SlOrderId As Long?
            Public Property Qty As Integer
            Public Property EntryPrice As Decimal
            Public Property CurrentSlPrice As Decimal
            Public Property CurrentTpPrice As Decimal
            Public Property MissCount As Integer = 0
        End Class
        Private ReadOnly _brackets As New List(Of BracketPair)
        Private Const BracketMissThreshold As Integer = 3

        Private _freeRideActive As Boolean = False
        Private _lastPositionClosedAt As DateTimeOffset = DateTimeOffset.MinValue

        ' ── Engine state ─────────────────────────────────────────────────────────
        Private _running As Boolean = False
        Private _disposed As Boolean = False
        Private _timer As System.Threading.Timer
        Private _cts As CancellationTokenSource
        Private _callbackRunning As Integer = 0
        Private ReadOnly _zombieOrders As New List(Of Long)
        Private _brokerType As BrokerType = BrokerType.TopStepX
        Private _effectiveContractId As String = String.Empty

        ' ── Events ───────────────────────────────────────────────────────────────
        Public Event LogMessage As EventHandler(Of String) Implements IPumpNDumpExecutionEngine.LogMessage
        Public Event ExecutionStopped As EventHandler(Of String) Implements IPumpNDumpExecutionEngine.ExecutionStopped
        Public Event TradeOpened As EventHandler(Of TradeOpenedEventArgs) Implements IPumpNDumpExecutionEngine.TradeOpened
        Public Event TradeClosed As EventHandler(Of TradeClosedEventArgs) Implements IPumpNDumpExecutionEngine.TradeClosed
        Public Event PositionChanged As EventHandler(Of SniperPositionEventArgs) Implements IPumpNDumpExecutionEngine.PositionChanged

        Public Sub New(ingestionService As IBarIngestionService,
                       orderService As IOrderService,
                       logger As ILogger(Of PumpNDumpExecutionEngine),
                       diagLogger As DiagnosticLogger)
            _ingestionService = ingestionService
            _orderService = orderService
            _logger = logger
            _diagLogger = diagLogger
        End Sub

        ' ── Properties ───────────────────────────────────────────────────────────

        Public ReadOnly Property IsRunning As Boolean Implements IPumpNDumpExecutionEngine.IsRunning
            Get
                Return _running
            End Get
        End Property

        Public ReadOnly Property CurrentQty As Integer Implements IPumpNDumpExecutionEngine.CurrentQty
            Get
                Return _currentQty
            End Get
        End Property

        Public ReadOnly Property AverageEntry As Decimal Implements IPumpNDumpExecutionEngine.AverageEntry
            Get
                Return _averageEntry
            End Get
        End Property

        Public ReadOnly Property FreeRideActive As Boolean Implements IPumpNDumpExecutionEngine.FreeRideActive
            Get
                Return _freeRideActive
            End Get
        End Property

        Public ReadOnly Property UnrealisedPnl As Decimal Implements IPumpNDumpExecutionEngine.UnrealisedPnl
            Get
                If _currentQty = 0 OrElse _averageEntry = 0D OrElse _lastClose = 0D OrElse _tickSize = 0D Then Return 0D
                Dim priceMove = If(_tradeSide = OrderSide.Buy,
                                   _lastClose - _averageEntry,
                                   _averageEntry - _lastClose)
                Return priceMove / _tickSize * _tickValue * _currentQty
            End Get
        End Property

        ' ── Public API ──────────────────────────────────────────────────────────

        Public Sub Start(contractId As String,
                         accountId As Long,
                         takeProfitTicks As Integer,
                         stopLossTicks As Integer,
                         freeRidePnlThreshold As Decimal,
                         scaleInTicks As Integer,
                         maxRiskHeatTicks As Integer,
                         targetTotalSize As Integer,
                         momentumFadeAtrFraction As Double,
                         tightenTicksPerBar As Integer,
                         durationHours As Double,
                         tickSize As Decimal,
                         tickValue As Decimal,
                         brokerType As BrokerType,
                         Optional tradingStartHourUtc As Integer = 6,
                         Optional tradingEndHourUtc As Integer = 21) Implements IPumpNDumpExecutionEngine.Start
            If _running Then Return

            _contractId = contractId
            _accountId = accountId
            ' Honour caller-provided ticks; fall back to $100/$1000 formula only when <= 0
            Dim tv = If(tickValue > 0, tickValue, 1.25D)
            _stopLossTicks  = If(stopLossTicks  > 0, stopLossTicks,  Math.Max(1, CInt(Math.Ceiling(100D  / tv))))
            _takeProfitTicks = If(takeProfitTicks > 0, takeProfitTicks, Math.Max(1, CInt(Math.Ceiling(1000D / tv))))
            _freeRidePnlThreshold = If(freeRidePnlThreshold > 0, freeRidePnlThreshold, 50D)
            _scaleInTicks = Math.Max(1, scaleInTicks)
            _maxRiskHeatTicks = If(maxRiskHeatTicks > 0, maxRiskHeatTicks, Integer.MaxValue)
            _targetTotalSize = Math.Max(1, targetTotalSize)
            _momentumFadeAtrFraction = If(momentumFadeAtrFraction > 0, momentumFadeAtrFraction, 0.5)
            _tightenTicksPerBar = Math.Max(1, tightenTicksPerBar)
            _tickSize = If(tickSize > 0, tickSize, 0.25D)
            _tickValue = If(tickValue > 0, tickValue, 1.25D)
            _expiresAt = DateTimeOffset.UtcNow.AddHours(If(durationHours > 0, durationHours, 2.0))
            _tradingStartHour = tradingStartHourUtc
            _tradingEndHour = tradingEndHourUtc

            ' ── Broker resolution
            _brokerType = brokerType
            Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
            If brokerType = BrokerType.TopStepX AndAlso fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId) Then
                _effectiveContractId = fav.PxContractId
                If fav.PxTickSize > 0 Then _tickSize = fav.PxTickSize
                If fav.PxTickValue > 0 Then
                    _tickValue = fav.PxTickValue
                    If stopLossTicks  <= 0 Then _stopLossTicks  = Math.Max(1, CInt(Math.Ceiling(100D  / _tickValue)))
                    If takeProfitTicks <= 0 Then _takeProfitTicks = Math.Max(1, CInt(Math.Ceiling(1000D / _tickValue)))
                End If
            Else
                _effectiveContractId = contractId
            End If

            ' Reset position state
            _currentQty = 0
            _entryPrices.Clear()
            _averageEntry = 0D
            _lastEntryPrice = 0D
            _brackets.Clear()
            _freeRideActive = False
            _lastClose = 0D

            _running = True
            _cts = New CancellationTokenSource()
            Interlocked.Exchange(_callbackRunning, 0)

            AddHandler _orderService.OrderFilled, AddressOf OnOrderFilled

            _diagLogger.StartSession(_contractId, "Pump-n-Dump")
            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                .EventType = "ENGINE_START",
                .Symbol    = _contractId,
                .Action    = "START",
                .Why       = $"TP={_takeProfitTicks}t SL={_stopLossTicks}t FreeRide=${_freeRidePnlThreshold:F0} " &
                             $"ScaleIn={_scaleInTicks}t MaxHeat={_maxRiskHeatTicks}t MaxQty={_targetTotalSize} " &
                             $"Fade<{_momentumFadeAtrFraction:F2}xATR Tighten={_tightenTicksPerBar}t " &
                             $"Expires={_expiresAt:HH:mm}UTC tick={_tickSize} val={_tickValue}"
            })

            Log($"🚀 Pump-n-Dump started — {_contractId} | 3-Bar Price Action | 1-min bars")
            If Not String.Equals(_effectiveContractId, _contractId, StringComparison.OrdinalIgnoreCase) Then
                Log($"   → PX contract: {_effectiveContractId} | tick size={_tickSize} value=${_tickValue}/tick")
            End If
            Log($"   TP: {_takeProfitTicks}t  SL: {_stopLossTicks}t  FreeRide: ${_freeRidePnlThreshold:F0}")
            Log($"   ScaleIn: {_scaleInTicks}t  MaxHeat: {_maxRiskHeatTicks}t  MaxQty: {_targetTotalSize}")
            Log($"   MomentumFade: <{_momentumFadeAtrFraction:F2}×ATR  TightenPerPoll: {_tightenTicksPerBar}t")
            Log($"   Expires: {_expiresAt:HH:mm} UTC")
            Log("Watching for 3 consecutive bars in same direction...")

            _timer = New System.Threading.Timer(AddressOf TimerCallback, Nothing,
                                                TimeSpan.Zero, TimeSpan.FromSeconds(15))
        End Sub

        Public Async Function StopAsync(Optional reason As String = "Stopped by user") As Task Implements IPumpNDumpExecutionEngine.StopAsync
            If Not _running Then Return
            _running = False

            RemoveHandler _orderService.OrderFilled, AddressOf OnOrderFilled

            _cts?.Cancel()
            _timer?.Change(Timeout.Infinite, 0)

            Log($"■ Pump-n-Dump stopping — {reason}, flattening positions...")

            If _currentQty > 0 Then
                Dim cleanupCts As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Try
                    For Each b In _brackets
                        If b.TpOrderId.HasValue Then
                            Try : Await _orderService.CancelOrderAsync(b.TpOrderId.Value) : Catch : End Try
                        End If
                        If b.SlOrderId.HasValue Then
                            Try : Await _orderService.CancelOrderAsync(b.SlOrderId.Value) : Catch : End Try
                        End If
                    Next
                    Await EmergencyCloseAsync(cleanupCts.Token)
                Catch ex As Exception
                    Log($"⚠️  Error during flattening: {ex.Message}")
                End Try
            End If

            Log($"■ Pump-n-Dump stopped — {reason}")
            _diagLogger.CloseSession()
            RaiseEvent ExecutionStopped(Me, reason)
        End Function

        ' ── Event Handlers ───────────────────────────────────────────────────────

        Private Sub OnOrderFilled(sender As Object, e As OrderFilledEventArgs)
            If Not _running Then Return
            Dim filledId = e.Order.ExternalOrderId
            If Not filledId.HasValue Then Return

            Dim matchingBracket = _brackets.FirstOrDefault(
                Function(b) (b.TpOrderId.HasValue AndAlso b.TpOrderId.Value = filledId.Value) OrElse
                            (b.SlOrderId.HasValue AndAlso b.SlOrderId.Value = filledId.Value))

            If matchingBracket IsNot Nothing Then
                Dim isTp = matchingBracket.TpOrderId.HasValue AndAlso matchingBracket.TpOrderId.Value = filledId.Value
                Log($"⚡ Event: {If(isTp, "TP", "SL")} filled ({e.Order.Quantity} @ {e.Order.FillPrice}) — cancelling other leg.")

                Task.Run(Async Function()
                             If isTp AndAlso matchingBracket.SlOrderId.HasValue Then
                                 Try
                                     Await _orderService.CancelOrderAsync(matchingBracket.SlOrderId.Value)
                                 Catch
                                     SyncLock _zombieOrders
                                         If Not _zombieOrders.Contains(matchingBracket.SlOrderId.Value) Then
                                             _zombieOrders.Add(matchingBracket.SlOrderId.Value)
                                         End If
                                     End SyncLock
                                 End Try
                             ElseIf Not isTp AndAlso matchingBracket.TpOrderId.HasValue Then
                                 Try
                                     Await _orderService.CancelOrderAsync(matchingBracket.TpOrderId.Value)
                                 Catch
                                     SyncLock _zombieOrders
                                         If Not _zombieOrders.Contains(matchingBracket.TpOrderId.Value) Then
                                             _zombieOrders.Add(matchingBracket.TpOrderId.Value)
                                         End If
                                     End SyncLock
                                 End Try
                             End If
                         End Function)
            End If
        End Sub

        ' ── Timer callback ───────────────────────────────────────────────────────

        Private Sub TimerCallback(state As Object)
            If Not _running Then Return
            If Interlocked.CompareExchange(_callbackRunning, 1, 0) <> 0 Then
                Log("⏭  Previous check still running — skipping this poll")
                Return
            End If

            Task.Run(Async Function() As Task
                         Try
                             Await DoCheckAsync()
                         Catch ex As OperationCanceledException
                             ' Normal: Stop() called while API request in flight
                         Catch ex As Exception
                             _logger.LogError(ex, "PumpNDumpExecutionEngine unhandled error")
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
                Await StopAsync("Session expired")
                Return
            End If

            Dim remaining = _expiresAt - DateTimeOffset.UtcNow
            Dim remStr = $"{CInt(remaining.TotalHours)}h {remaining.Minutes}m remaining"

            ' ── Fetch 1-min bars ─────────────────────────────────────────────────
            Await _ingestionService.IngestAsync(_contractId, BarTimeframe.OneMinute, 25, ct)
            Dim bars = Await _ingestionService.GetBarsForMLAsync(_contractId, BarTimeframe.OneMinute, 25, ct)

            If bars Is Nothing OrElse bars.Count < 20 Then
                Dim cnt = If(bars Is Nothing, 0, bars.Count)
                Log($"Waiting for 1-min bars — have {cnt}/20 needed ({remStr})")
                Return
            End If

            Dim lastBar = bars.Last()
            _lastClose = lastBar.Close

            ' ── ATR for momentum-fade detection ──────────────────────────────────
            Dim highVals = bars.Select(Function(b) b.High).ToList()
            Dim lowVals = bars.Select(Function(b) b.Low).ToList()
            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim atrVals = TechnicalIndicators.ATR(highVals, lowVals, closes, 14)
            Dim currentAtr = TechnicalIndicators.LastValid(atrVals)

            ' ── Check if open positions have closed ───────────────────────────────
            If _currentQty > 0 Then
                Dim liveOrders = Await _orderService.GetLiveWorkingOrdersAsync(_accountId, _contractId, ct)

                ' Zombie order cleanup
                If _zombieOrders.Count > 0 Then
                    Dim zombiesToKill As New List(Of Long)
                    SyncLock _zombieOrders
                        zombiesToKill = _zombieOrders.ToList()
                    End SyncLock
                    Dim stillLive = liveOrders.Where(Function(o) o.ExternalOrderId.HasValue AndAlso
                                                                  zombiesToKill.Contains(o.ExternalOrderId.Value)).ToList()
                    If stillLive.Count > 0 Then
                        Log($"🧟 Found {stillLive.Count} zombie orders — forcing cancellation...")
                        For Each z In stillLive
                            Try : Await _orderService.CancelOrderAsync(z.ExternalOrderId.Value) : Catch : End Try
                        Next
                    End If
                    SyncLock _zombieOrders
                        For Each z In zombiesToKill
                            If Not liveOrders.Any(Function(o) o.ExternalOrderId = z) Then _zombieOrders.Remove(z)
                        Next
                    End SyncLock
                End If

                Dim bracketsToRemove As New List(Of BracketPair)
                Dim totalClosedPnl As Decimal = 0D
                Dim primaryExitReason As String = "Closed"

                For Each bracket In _brackets.ToList()
                    Dim tpMissing = bracket.TpOrderId.HasValue AndAlso
                                   Not liveOrders.Any(Function(o) o.ExternalOrderId = bracket.TpOrderId.Value)
                    Dim slMissing = bracket.SlOrderId.HasValue AndAlso
                                   Not liveOrders.Any(Function(o) o.ExternalOrderId = bracket.SlOrderId.Value)

                    If tpMissing OrElse slMissing Then
                        bracket.MissCount += 1
                        If bracket.MissCount < BracketMissThreshold Then
                            Log($"⚠️  Bracket miss {bracket.MissCount}/{BracketMissThreshold} — order(s) not visible; will retry.")
                            Continue For
                        End If

                        ' Cancel the orphaned surviving leg
                        If bracket.TpOrderId.HasValue AndAlso Not tpMissing Then
                            Try
                                Await _orderService.CancelOrderAsync(bracket.TpOrderId.Value)
                            Catch
                                SyncLock _zombieOrders
                                    If Not _zombieOrders.Contains(bracket.TpOrderId.Value) Then
                                        _zombieOrders.Add(bracket.TpOrderId.Value)
                                    End If
                                End SyncLock
                            End Try
                        End If
                        If bracket.SlOrderId.HasValue AndAlso Not slMissing Then
                            Try
                                Await _orderService.CancelOrderAsync(bracket.SlOrderId.Value)
                            Catch
                                SyncLock _zombieOrders
                                    If Not _zombieOrders.Contains(bracket.SlOrderId.Value) Then
                                        _zombieOrders.Add(bracket.SlOrderId.Value)
                                    End If
                                End SyncLock
                            End Try
                        End If

                        ' Determine exit reason and P&L
                        Dim bracketPnl As Decimal = 0D
                        Dim exitReason As String = "Closed"

                        If tpMissing Then
                            exitReason = "TP"
                            Try
                                Dim tpFill = Await _orderService.TryGetOrderFillPriceAsync(bracket.TpOrderId.Value, _accountId)
                                If tpFill.HasValue Then
                                    Dim move = If(_tradeSide = OrderSide.Buy, tpFill.Value - _averageEntry, _averageEntry - tpFill.Value)
                                    bracketPnl = move / _tickSize * _tickValue * bracket.Qty
                                End If
                            Catch : End Try
                        ElseIf slMissing Then
                            exitReason = "SL"
                            Dim slPrice = If(_tradeSide = OrderSide.Buy,
                                             _averageEntry - _stopLossTicks * _tickSize,
                                             _averageEntry + _stopLossTicks * _tickSize)
                            Try
                                Dim slFill = Await _orderService.TryGetOrderFillPriceAsync(bracket.SlOrderId.Value, _accountId)
                                If slFill.HasValue Then slPrice = slFill.Value
                            Catch : End Try
                            Dim move = If(_tradeSide = OrderSide.Buy, slPrice - _averageEntry, _averageEntry - slPrice)
                            bracketPnl = move / _tickSize * _tickValue * bracket.Qty
                        End If

                        totalClosedPnl += bracketPnl
                        primaryExitReason = exitReason
                        bracketsToRemove.Add(bracket)
                    Else
                        bracket.MissCount = 0
                    End If
                Next

                If bracketsToRemove.Count > 0 Then
                    For Each b In bracketsToRemove
                        _brackets.Remove(b)
                        _currentQty -= b.Qty
                    Next
                    Log($"✓ Bracket(s) closed ({primaryExitReason}) | P&L ≈ ${totalClosedPnl:N0}")
                    _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                        .EventType = "CLOSED",
                        .Symbol    = _contractId,
                        .Action    = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                        .Why       = $"exit={primaryExitReason} pnl=${totalClosedPnl:N0} avgEntry={_averageEntry:F4} lastClose={_lastClose:F4}",
                        .Outcome   = New DiagOutcome With {
                            .Status = primaryExitReason,
                            .PlUsd  = totalClosedPnl
                        }
                    })
                    RaiseEvent TradeClosed(Me, New TradeClosedEventArgs(primaryExitReason, totalClosedPnl))

                    If _currentQty <= 0 Then
                        _currentQty = 0
                        _entryPrices.Clear()
                        _averageEntry = 0D
                        _lastEntryPrice = 0D
                        _brackets.Clear()
                        _freeRideActive = False
                        _lastPositionClosedAt = DateTimeOffset.UtcNow
                        RaisePositionChanged()
                        Return
                    Else
                        RaisePositionChanged()
                    End If
                End If

                ' ── Position snapshot ────────────────────────────────────────────
                Dim snapSlPrice = If(_brackets.Count > 0, _brackets(0).CurrentSlPrice, 0D)
                Dim snapTpPrice = If(_brackets.Count > 0, _brackets(0).CurrentTpPrice, 0D)
                _diagLogger.WritePositionSnapshot(
                    _contractId,
                    If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                    _averageEntry, _lastClose, "bar_close",
                    UnrealisedPnl, snapSlPrice, snapTpPrice,
                    Nothing, _stopLossTicks, _takeProfitTicks)

                ' ── Free-ride: trigger once, then trail every poll ────────────────
                If Not _freeRideActive Then
                    Dim pnl = UnrealisedPnl
                    If pnl >= _freeRidePnlThreshold Then
                        Log($"🔒 Free-ride triggered — P&L ${pnl:F2} ≥ threshold ${_freeRidePnlThreshold:F0}. Moving SLs to breakeven...")
                        Await ApplyFreeRideAsync(ct)
                    End If
                Else
                    ' Ratchet SLs behind price on every poll — closes trade when trend reverses
                    Await TrailStopsAsync(_lastClose, ct)
                End If

                ' ── Scale-in check ────────────────────────────────────────────────
                If _freeRideActive Then
                    Log("⏸ Scale-in suppressed — free-ride is active.")
                ElseIf _currentQty < _targetTotalSize Then
                    Dim scaleDistance = _scaleInTicks * _tickSize
                    Dim priceMovedEnough As Boolean
                    If _tradeSide = OrderSide.Buy Then
                        priceMovedEnough = _lastClose >= _lastEntryPrice + scaleDistance
                    Else
                        priceMovedEnough = _lastClose <= _lastEntryPrice - scaleDistance
                    End If

                    If priceMovedEnough Then
                        Log($"📈 Scale-in trigger: price moved ≥ {_scaleInTicks}t from last entry. Scaling in...")
                        Await ScaleInAsync(_lastClose, ct)
                    Else
                        Dim pnl = UnrealisedPnl
                        Log($"Position open: {_currentQty}/{_targetTotalSize} contracts | AvgEntry={_averageEntry:F2} | P&L≈${pnl:F2} | {remStr}")
                    End If
                Else
                    Log($"Position at max ({_targetTotalSize}/{_targetTotalSize}) | AvgEntry={_averageEntry:F2} | P&L≈${UnrealisedPnl:F2} | {remStr}")
                End If

                ' ── Momentum-fade TP tightening ───────────────────────────────────
                If currentAtr > 0 Then
                    Dim last3Bars = bars.TakeLast(4).Take(3).ToList()  ' 3 completed bars before current
                    Dim avgRange = last3Bars.Average(Function(b) b.High - b.Low)
                    If CDbl(avgRange) < currentAtr * _momentumFadeAtrFraction Then
                        Log($"📉 Momentum fading — avg bar range {avgRange:F2} < {_momentumFadeAtrFraction:F2}×ATR({currentAtr:F2}). Tightening TPs by {_tightenTicksPerBar}t...")
                        Await TightenTakeProfitsAsync(_lastClose, ct)
                    End If
                End If

                Return
            End If

            ' ── FLAT: look for 3-bar entry signal ────────────────────────────────
            ' Yahoo Finance historical endpoint returns only closed bars — no forming bar is included.
            ' Use the 3 most recent bars directly.

            ' ── Stale-bar guard ──────────────────────────────────────────────────
            Dim barAgeMins = (DateTimeOffset.UtcNow - lastBar.Timestamp).TotalMinutes
            If barAgeMins > 5.0 Then
                Log($"⏸  Stale bar ({barAgeMins:F0} min old) — entry suppressed")
                Return
            End If

            ' ── Trading hours guard ──────────────────────────────────────────────
            Dim utcHour = DateTimeOffset.UtcNow.Hour
            If _tradingEndHour > 0 AndAlso (utcHour < _tradingStartHour OrElse utcHour >= _tradingEndHour) Then
                Log($"⏸  Outside trading hours (UTC {utcHour:00}:xx, window={_tradingStartHour:00}–{_tradingEndHour:00}h) — entry suppressed")
                Return
            End If

            Const ReEntryCooldownSecs As Integer = 30
            If (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds < ReEntryCooldownSecs Then
                Log($"⏸  Re-entry cooldown ({ReEntryCooldownSecs}s) — skipping entry signal")
                _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                    .EventType       = "REJECT",
                    .Symbol          = _contractId,
                    .Action          = "NONE",
                    .RejectionReason = $"re-entry cooldown ({ReEntryCooldownSecs}s)",
                    .MetricsAtEntry  = New DiagMetricsAtEntry With {.PriceEntry = _lastClose}
                })
                Return
            End If
            Dim completedBars = bars.TakeLast(3).ToList()

            Dim allGreen = completedBars.All(Function(b) b.Close > b.Open)
            Dim allRed = completedBars.All(Function(b) b.Close < b.Open)

            If allGreen Then
                Dim b0 = completedBars(0)
                Dim b1 = completedBars(1)
                Dim b2 = completedBars(2)
                Log($"🟢 PUMP SIGNAL: 3 consecutive green bars")
                Log($"   {b0.Open:F2}→{b0.Close:F2}  {b1.Open:F2}→{b1.Close:F2}  {b2.Open:F2}→{b2.Close:F2}")
                _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                    .EventType      = "SIGNAL",
                    .Symbol         = _contractId,
                    .Action         = "BUY",
                    .Why            = $"3 green bars {b0.Open:F4}→{b0.Close:F4} {b1.Open:F4}→{b1.Close:F4} {b2.Open:F4}→{b2.Close:F4}",
                    .MetricsAtEntry = New DiagMetricsAtEntry With {.PriceEntry = _lastClose, .Atr10 = CDec(currentAtr)}
                })
                _tradeSide = OrderSide.Buy
                Await PlaceInitialEntryAsync(OrderSide.Buy, _lastClose, ct)
            ElseIf allRed Then
                Dim b0 = completedBars(0)
                Dim b1 = completedBars(1)
                Dim b2 = completedBars(2)
                Log($"🔴 DUMP SIGNAL: 3 consecutive red bars")
                Log($"   {b0.Open:F2}→{b0.Close:F2}  {b1.Open:F2}→{b1.Close:F2}  {b2.Open:F2}→{b2.Close:F2}")
                _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                    .EventType      = "SIGNAL",
                    .Symbol         = _contractId,
                    .Action         = "SELL",
                    .Why            = $"3 red bars {b0.Open:F4}→{b0.Close:F4} {b1.Open:F4}→{b1.Close:F4} {b2.Open:F4}→{b2.Close:F4}",
                    .MetricsAtEntry = New DiagMetricsAtEntry With {.PriceEntry = _lastClose, .Atr10 = CDec(currentAtr)}
                })
                _tradeSide = OrderSide.Sell
                Await PlaceInitialEntryAsync(OrderSide.Sell, _lastClose, ct)
            Else
                Dim last = completedBars.Last()
                Dim dirs = String.Join(" ", completedBars.Select(Function(b) If(b.Close > b.Open, "🟢", "🔴")))
                Log($"Watching — {dirs} | Close={_lastClose:F2} | {remStr}")
            End If
        End Function

        ' ── Order placement ─────────────────────────────────────────────────────

        Private Async Function PlaceInitialEntryAsync(side As OrderSide,
                                                       entryPrice As Decimal,
                                                       ct As CancellationToken) As Task
            Dim entryOrder As New Order With {
                .AccountId = _accountId,
                .Broker = _brokerType,
                .ContractId = _effectiveContractId,
                .Side = side,
                .OrderType = OrderType.Market,
                .Quantity = 1,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = "Pump-n-Dump: 3-bar initial entry"
            }

            Dim entryFailed = False
            Try
                Dim placed = Await _orderService.PlaceOrderAsync(entryOrder)
                If placed.Status = OrderStatus.Rejected Then
                    Dim reason = If(String.IsNullOrWhiteSpace(placed.Notes), "unknown reason", placed.Notes)
                    Log($"⚠️  Entry order rejected: {reason}")
                    entryFailed = True
                Else
                    Log($"Entry {side} order placed — Market qty=1")
                End If
            Catch ex As Exception
                Log($"⚠️  Entry order failed: {ex.Message}")
                entryFailed = True
            End Try

            If entryFailed Then Return

            _currentQty = 1
            _entryPrices.Clear()
            _entryPrices.Add(entryPrice)
            _averageEntry = entryPrice
            _lastEntryPrice = entryPrice

            ' BUG-19: correct average entry from broker-confirmed fill price
            Await CorrectEntryFromFillAsync(ct)

            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                .EventType      = "ENTRY",
                .Symbol         = _contractId,
                .Action         = If(side = OrderSide.Buy, "BUY", "SELL"),
                .Why            = $"initial entry qty=1 estPrice={entryPrice:F4} avgEntry={_averageEntry:F4}",
                .MetricsAtEntry = New DiagMetricsAtEntry With {.PriceEntry = entryPrice}
            })

            RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(side, _contractId, 100,
                                                                DateTimeOffset.UtcNow,
                                                                entryOrder.ExternalOrderId))
            RaisePositionChanged()
            Await PlaceBracketAsync(side, entryPrice, 1, ct)
        End Function

        Private Async Function ScaleInAsync(currentPrice As Decimal, ct As CancellationToken) As Task
            Dim remaining = _targetTotalSize - _currentQty
            If remaining <= 0 Then Return

            ' Heat check
            Dim slOffset = _stopLossTicks * _tickSize
            Dim newLotSl = If(_tradeSide = OrderSide.Buy, currentPrice - slOffset, currentPrice + slOffset)
            Dim newLotDist = Math.Abs(currentPrice - newLotSl)
            Dim newLotHeat = (newLotDist / _tickSize)
            If (CalculateCurrentHeat() + newLotHeat) > _maxRiskHeatTicks Then
                Log($"🛑 Scale-in blocked by Risk Heat: {CalculateCurrentHeat():F0} + {newLotHeat:F0} > {_maxRiskHeatTicks}")
                _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                    .EventType       = "REJECT",
                    .Symbol          = _contractId,
                    .Action          = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                    .RejectionReason = $"heat cap: current={CalculateCurrentHeat():F0} + new={newLotHeat:F0} > max={_maxRiskHeatTicks}",
                    .MetricsAtEntry  = New DiagMetricsAtEntry With {.PriceEntry = currentPrice}
                })
                _lastEntryPrice = currentPrice
                Return
            End If

            Dim entryOrder As New Order With {
                .AccountId = _accountId,
                .Broker = _brokerType,
                .ContractId = _effectiveContractId,
                .Side = _tradeSide,
                .OrderType = OrderType.Market,
                .Quantity = 1,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"Pump-n-Dump: scale-in #{_currentQty + 1}"
            }

            Dim entryFailed = False
            Try
                Dim placed = Await _orderService.PlaceOrderAsync(entryOrder)
                If placed.Status = OrderStatus.Rejected Then
                    Dim reason = If(String.IsNullOrWhiteSpace(placed.Notes), "unknown reason", placed.Notes)
                    Log($"⚠️  Scale-in order rejected: {reason}")
                    entryFailed = True
                End If
            Catch ex As Exception
                Log($"⚠️  Scale-in entry failed: {ex.Message}")
                entryFailed = True
            End Try

            If entryFailed Then Return

            _currentQty += 1
            _entryPrices.Add(currentPrice)
            _averageEntry = _entryPrices.Average()
            _lastEntryPrice = currentPrice

            ' BUG-19: correct average entry from broker-confirmed fill price
            Await CorrectEntryFromFillAsync(ct)

            Log($"✅ Scale-in @ {currentPrice:F2} | New AvgEntry={_averageEntry:F2} | Qty={_currentQty}/{_targetTotalSize}")
            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                .EventType      = "SCALE_IN",
                .Symbol         = _contractId,
                .Action         = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                .Why            = $"qty={_currentQty}/{_targetTotalSize} avgEntry={_averageEntry:F4} heat={CalculateCurrentHeat():F0}t",
                .MetricsAtEntry = New DiagMetricsAtEntry With {.PriceEntry = currentPrice}
            })
            RaisePositionChanged()
            Await PlaceBracketAsync(_tradeSide, currentPrice, 1, ct)
        End Function

        Private Async Function PlaceBracketAsync(side As OrderSide,
                                                  entryPrice As Decimal,
                                                  qty As Integer,
                                                  ct As CancellationToken) As Task
            Dim tick = _tickSize
            Dim exitSide = If(side = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)
            Dim pair As New BracketPair With {
                .Qty = qty,
                .EntryPrice = entryPrice
            }

            ' ── Take Profit ──────────────────────────────────────────────────────
            If _takeProfitTicks > 0 Then
                Dim tpPrice = If(side = OrderSide.Buy,
                                 entryPrice + _takeProfitTicks * tick,
                                 entryPrice - _takeProfitTicks * tick)
                Dim tpOrder As New Order With {
                    .AccountId = _accountId,
                    .Broker = _brokerType,
                    .ContractId = _effectiveContractId,
                    .Side = exitSide,
                    .OrderType = OrderType.Limit,
                    .Quantity = qty,
                    .LimitPrice = tpPrice,
                    .Status = OrderStatus.Pending,
                    .PlacedAt = DateTimeOffset.UtcNow,
                    .Notes = $"Pump-n-Dump TP qty={qty}"
                }
                Try
                    Dim placed = Await _orderService.PlaceOrderAsync(tpOrder)
                    pair.TpOrderId = placed.ExternalOrderId
                    pair.CurrentTpPrice = tpPrice
                    Log($"Take Profit Limit qty={qty} @ {tpPrice:F2} (+{_takeProfitTicks}t)")
                Catch ex As Exception
                    Log($"⚠️  TP order failed: {ex.Message}")
                End Try
            End If

            ' ── Stop Loss ────────────────────────────────────────────────────────
            If _stopLossTicks > 0 Then
                Dim slPrice = If(side = OrderSide.Buy,
                                 entryPrice - _stopLossTicks * tick,
                                 entryPrice + _stopLossTicks * tick)
                pair.CurrentSlPrice = slPrice

                Dim slOrder As New Order With {
                    .AccountId = _accountId,
                    .Broker = _brokerType,
                    .ContractId = _effectiveContractId,
                    .Side = exitSide,
                    .OrderType = OrderType.StopOrder,
                    .Quantity = qty,
                    .StopPrice = slPrice,
                    .Status = OrderStatus.Pending,
                    .PlacedAt = DateTimeOffset.UtcNow,
                    .Notes = $"Pump-n-Dump SL qty={qty}"
                }

                Dim slRejected = False
                Try
                    Dim placed = Await _orderService.PlaceOrderAsync(slOrder)
                    pair.SlOrderId = placed.ExternalOrderId
                    If placed.Status = OrderStatus.Rejected Then
                        Log($"🚨 SL order rejected by API")
                        slRejected = True
                    Else
                        Log($"Stop Loss StopLimit qty={qty} @ {slPrice:F2} (-{_stopLossTicks}t)")
                    End If
                Catch ex As Exception
                    Log($"🚨 SL order exception: {ex.Message}")
                    slRejected = True
                End Try

                If slRejected Then
                    Log($"🚨 Position UNPROTECTED — emergency closing!")
                    If pair.TpOrderId.HasValue Then
                        Try : Await _orderService.CancelOrderAsync(pair.TpOrderId.Value) : Catch : End Try
                    End If
                    Await EmergencyCloseAsync(ct)
                    Return
                End If
            End If

            _brackets.Add(pair)
            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                .EventType = "BRACKET_PLACED",
                .Symbol    = _contractId,
                .Action    = If(side = OrderSide.Buy, "BUY", "SELL"),
                .Why       = $"qty={qty} tp={pair.CurrentTpPrice:F4}(+{_takeProfitTicks}t) sl={pair.CurrentSlPrice:F4}(-{_stopLossTicks}t)",
                .Settings  = New DiagSettings With {
                    .TpPrice = pair.CurrentTpPrice,
                    .SlPrice = pair.CurrentSlPrice
                }
            })
        End Function

        Private Async Function ApplyFreeRideAsync(ct As CancellationToken) As Task
            Dim exitSide = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)
            Dim bePrice = _averageEntry  ' breakeven = average entry
            Dim anyUnprotected As Boolean = False

            For Each b In _brackets
                If Not b.SlOrderId.HasValue Then
                    anyUnprotected = True
                    Continue For
                End If

                ' Only move SL if it's below/above breakeven (would improve protection)
                Dim alreadyAtBe As Boolean
                If _tradeSide = OrderSide.Buy Then
                    alreadyAtBe = b.CurrentSlPrice >= bePrice
                Else
                    alreadyAtBe = b.CurrentSlPrice <= bePrice
                End If
                If alreadyAtBe Then Continue For

                Dim cancelOk = True
                Try
                    Await _orderService.CancelOrderAsync(b.SlOrderId.Value)
                Catch ex As Exception
                    Log($"⚠️  Free-ride: could not cancel old SL: {ex.Message}")
                    cancelOk = False
                End Try

                If cancelOk Then
                    Dim newSl As New Order With {
                        .AccountId = _accountId,
                        .Broker = _brokerType,
                        .ContractId = _effectiveContractId,
                        .Side = exitSide,
                        .OrderType = OrderType.StopOrder,
                        .Quantity = b.Qty,
                        .StopPrice = bePrice,
                        .Status = OrderStatus.Pending,
                        .PlacedAt = DateTimeOffset.UtcNow,
                        .Notes = $"Pump-n-Dump free-ride SL @ {bePrice:F2}"
                    }
                    Try
                        Dim placed = Await _orderService.PlaceOrderAsync(newSl)
                        If placed.Status = OrderStatus.Rejected Then
                            Log($"🚨 Free-ride SL rejected by API — bracket unprotected")
                            b.SlOrderId = Nothing     ' stale ID cleared; cancel already went through
                            anyUnprotected = True
                        Else
                            b.SlOrderId = placed.ExternalOrderId
                            b.CurrentSlPrice = bePrice
                        End If
                    Catch ex As Exception
                        Log($"⚠️  Free-ride SL replacement failed: {ex.Message}")
                        b.SlOrderId = Nothing         ' cancel succeeded but place failed — clear stale ID
                        anyUnprotected = True
                    End Try
                End If
            Next

            If anyUnprotected Then
                Log("🚨 Bracket unprotected after free-ride SL failure — emergency closing!")
                Await EmergencyCloseAsync(CancellationToken.None)
                Return
            End If

            _freeRideActive = True
            Log($"🔒 Free-ride active — all SLs at breakeven {bePrice:F2}")
            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                .EventType      = "FREE_RIDE",
                .Symbol         = _contractId,
                .Action         = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                .Why            = $"avgEntry={_averageEntry:F4} bePrice={bePrice:F4} pnl=${UnrealisedPnl:F2}",
                .MetricsAtEntry = New DiagMetricsAtEntry With {.PriceEntry = _lastClose}
            })
            RaisePositionChanged()
        End Function

        ''' <summary>
        ''' Ratchet each bracket's SL <see cref="_stopLossTicks"/> ticks behind current price.
        ''' Runs every poll once free-ride is active. SL only ever moves in the profit direction.
        ''' If a cancel/replace cycle leaves a bracket unprotected, triggers emergency close.
        ''' </summary>
        Private Async Function TrailStopsAsync(currentPrice As Decimal, ct As CancellationToken) As Task
            Dim tick = _tickSize
            Dim trailDistance = _stopLossTicks * tick
            Dim exitSide = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)
            Dim anyMoved As Boolean = False
            Dim anyUnprotected As Boolean = False

            For Each b In _brackets
                ' A bracket with no SL ID has already lost its stop — flag immediately
                If Not b.SlOrderId.HasValue Then
                    anyUnprotected = True
                    Continue For
                End If

                ' Compute candidate stop and enforce the ratchet (never move SL back)
                Dim candidateSl As Decimal
                If _tradeSide = OrderSide.Buy Then
                    candidateSl = currentPrice - trailDistance
                    If candidateSl <= b.CurrentSlPrice Then Continue For
                Else
                    candidateSl = currentPrice + trailDistance
                    If candidateSl >= b.CurrentSlPrice Then Continue For
                End If

                Dim cancelOk = True
                Try
                    Await _orderService.CancelOrderAsync(b.SlOrderId.Value)
                Catch ex As Exception
                    Log($"⚠️  Trail SL: could not cancel old SL: {ex.Message}")
                    cancelOk = False
                End Try

                If cancelOk Then
                    Dim newSl As New Order With {
                        .AccountId = _accountId,
                        .Broker = _brokerType,
                        .ContractId = _effectiveContractId,
                        .Side = exitSide,
                        .OrderType = OrderType.StopOrder,
                        .Quantity = b.Qty,
                        .StopPrice = candidateSl,
                        .Status = OrderStatus.Pending,
                        .PlacedAt = DateTimeOffset.UtcNow,
                        .Notes = $"Pump-n-Dump trail SL @ {candidateSl:F2}"
                    }
                    Try
                        Dim placed = Await _orderService.PlaceOrderAsync(newSl)
                        If placed.Status = OrderStatus.Rejected Then
                            Log($"🚨 Trail SL rejected by API — bracket unprotected")
                            b.SlOrderId = Nothing
                            anyUnprotected = True
                        Else
                            Log($"🔒 Trail SL {b.CurrentSlPrice:F2} → {candidateSl:F2}")
                            Dim oldSl = b.CurrentSlPrice
                            b.SlOrderId = placed.ExternalOrderId
                            b.CurrentSlPrice = candidateSl
                            anyMoved = True
                            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                                .EventType = "TRAIL_SL",
                                .Symbol    = _contractId,
                                .Action    = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                                .Why       = $"sl {oldSl:F4} → {candidateSl:F4} price={currentPrice:F4} dist={_stopLossTicks}t",
                                .Settings  = New DiagSettings With {.SlPrice = candidateSl}
                            })
                        End If
                    Catch ex As Exception
                        Log($"⚠️  Trail SL replacement failed: {ex.Message}")
                        b.SlOrderId = Nothing         ' cancel succeeded but place failed — clear stale ID
                        anyUnprotected = True
                    End Try
                End If
            Next

            If anyUnprotected Then
                Log("🚨 Bracket unprotected after trail SL failure — emergency closing!")
                Await EmergencyCloseAsync(CancellationToken.None)
            ElseIf anyMoved Then
                RaisePositionChanged()
            End If
        End Function

        Private Async Function TightenTakeProfitsAsync(currentPrice As Decimal, ct As CancellationToken) As Task
            Dim tick = _tickSize
            Dim exitSide = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)
            Dim minSafeDistance = 3 * tick

            For Each b In _brackets
                If Not b.TpOrderId.HasValue OrElse b.CurrentTpPrice = 0D Then Continue For

                Dim tightenAmount = _tightenTicksPerBar * tick
                Dim newTpPrice As Decimal

                If _tradeSide = OrderSide.Buy Then
                    newTpPrice = b.CurrentTpPrice - tightenAmount
                    ' Don't tighten closer than minSafeDistance above current price
                    If newTpPrice < currentPrice + minSafeDistance Then
                        newTpPrice = currentPrice + minSafeDistance
                    End If
                    ' Don't tighten past existing TP (no loosening)
                    If newTpPrice >= b.CurrentTpPrice Then Continue For
                    ' Don't tighten to or below the current SL (inversion guard)
                    If b.CurrentSlPrice > 0D Then
                        If newTpPrice <= b.CurrentSlPrice + minSafeDistance Then
                            Log($"⚠️  TP tighten skipped — would invert bracket (SL={b.CurrentSlPrice:F2} TP={newTpPrice:F2})")
                            Continue For
                        End If
                    End If
                Else
                    newTpPrice = b.CurrentTpPrice + tightenAmount
                    If newTpPrice > currentPrice - minSafeDistance Then
                        newTpPrice = currentPrice - minSafeDistance
                    End If
                    If newTpPrice <= b.CurrentTpPrice Then Continue For
                    ' Don't tighten to or above the current SL (inversion guard)
                    If b.CurrentSlPrice > 0D Then
                        If newTpPrice >= b.CurrentSlPrice - minSafeDistance Then
                            Log($"⚠️  TP tighten skipped — would invert bracket (SL={b.CurrentSlPrice:F2} TP={newTpPrice:F2})")
                            Continue For
                        End If
                    End If
                End If

                ' Cancel old TP and replace
                Dim cancelOk = True
                Try
                    Await _orderService.CancelOrderAsync(b.TpOrderId.Value)
                Catch ex As Exception
                    Log($"⚠️  TP tighten: could not cancel old TP: {ex.Message}")
                    cancelOk = False
                End Try

                If cancelOk Then
                    Dim newTp As New Order With {
                        .AccountId = _accountId,
                        .Broker = _brokerType,
                        .ContractId = _effectiveContractId,
                        .Side = exitSide,
                        .OrderType = OrderType.Limit,
                        .Quantity = b.Qty,
                        .LimitPrice = newTpPrice,
                        .Status = OrderStatus.Pending,
                        .PlacedAt = DateTimeOffset.UtcNow,
                        .Notes = $"Pump-n-Dump TP tightened → {newTpPrice:F2}"
                    }
                    Try
                        Dim placed = Await _orderService.PlaceOrderAsync(newTp)
                        Dim oldTp = b.CurrentTpPrice
                        Log($"📉 TP tightened: {b.CurrentTpPrice:F2} → {newTpPrice:F2} (-{_tightenTicksPerBar}t)")
                        b.TpOrderId = placed.ExternalOrderId
                        b.CurrentTpPrice = newTpPrice
                        _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                            .EventType = "TP_TIGHTEN",
                            .Symbol    = _contractId,
                            .Action    = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                            .Why       = $"tp {oldTp:F4} → {newTpPrice:F4} (-{_tightenTicksPerBar}t) price={currentPrice:F4}",
                            .Settings  = New DiagSettings With {.TpPrice = newTpPrice, .SlPrice = b.CurrentSlPrice}
                        })
                    Catch ex As Exception
                        Log($"⚠️  TP replacement failed: {ex.Message}")
                    End Try
                End If
            Next
        End Function

        ' ── Helpers ─────────────────────────────────────────────────────────────

        ''' <summary>BUG-19: Poll broker up to 3×750ms to get the confirmed fill price and
        ''' correct _averageEntry / _lastEntryPrice when the drift exceeds one tick.</summary>
        Private Async Function CorrectEntryFromFillAsync(ct As CancellationToken) As Task
            Const MaxAttempts As Integer = 3
            Const DelayMs As Integer = 750
            For attempt = 1 To MaxAttempts
                Try
                    Await Task.Delay(DelayMs, ct)
                    Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(_accountId, _contractId, Nothing, ct)
                    If snapshot IsNot Nothing AndAlso snapshot.OpenRate > 0D Then
                        Dim drift = Math.Abs(snapshot.OpenRate - _lastEntryPrice)
                        If drift > _tickSize Then
                            Dim before = _averageEntry
                            ' Replace the last entry price estimate with the confirmed fill
                            If _entryPrices.Count > 0 Then
                                _entryPrices(_entryPrices.Count - 1) = snapshot.OpenRate
                            End If
                            _lastEntryPrice = snapshot.OpenRate
                            _averageEntry = _entryPrices.Average()
                            Log($"📌 Fill corrected {before:F2} → {snapshot.OpenRate:F2} (Δ={drift:F2})")
                        End If
                        Return
                    End If
                Catch ex As OperationCanceledException
                    Return
                Catch
                    ' Ignore and retry
                End Try
            Next
        End Function

        Private Function CalculateCurrentHeat() As Decimal
            Dim total As Decimal = 0D
            For Each b In _brackets
                If b.CurrentSlPrice = 0D Then Continue For
                Dim dist = If(_tradeSide = OrderSide.Buy,
                              b.EntryPrice - b.CurrentSlPrice,
                              b.CurrentSlPrice - b.EntryPrice)
                Dim ticks = dist / _tickSize
                If ticks > 0 Then total += ticks * b.Qty
            Next
            Return total
        End Function

        Private Async Function EmergencyCloseAsync(ct As CancellationToken) As Task
            Dim exitSide = If(_tradeSide = OrderSide.Buy, OrderSide.Sell, OrderSide.Buy)

            ' BUG-18: use broker-confirmed qty to avoid over/under-close on rejected fills
            Dim closeQty As Integer = _currentQty
            Try
                Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(_accountId, _contractId, Nothing, ct)
                If snapshot IsNot Nothing AndAlso snapshot.Units > 0 Then
                    Dim brokerQty = CInt(snapshot.Units)
                    If brokerQty <> _currentQty Then
                        Log($"⚠️  Emergency close qty mismatch: internal={_currentQty}, broker={brokerQty} — using broker qty")
                    End If
                    closeQty = brokerQty
                End If
            Catch ex As Exception
                Log($"⚠️  Could not fetch broker position snapshot ({ex.Message}) — falling back to internal qty {_currentQty}")
            End Try

            Dim closeOrder As New Order With {
                .AccountId = _accountId,
                .Broker = _brokerType,
                .ContractId = _effectiveContractId,
                .Side = exitSide,
                .OrderType = OrderType.Market,
                .Quantity = closeQty,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = "Pump-n-Dump: emergency close"
            }

            Try
                Await _orderService.PlaceOrderAsync(closeOrder)
                Log("🔴 Emergency close order placed")
            Catch ex As Exception
                Log($"🚨 CRITICAL: Emergency close ALSO failed: {ex.Message}")
            End Try

            Dim estimatedPnl As Decimal = 0D
            If _lastClose > 0D AndAlso _averageEntry > 0D Then
                Dim move = If(_tradeSide = OrderSide.Buy,
                              _lastClose - _averageEntry,
                              _averageEntry - _lastClose)
                estimatedPnl = move / _tickSize * _tickValue * _currentQty
            End If

            Log($"✓ Position closed (Emergency) — {_currentQty} contracts | P&L ≈ ${estimatedPnl:N0}")
            _diagLogger.WriteEntry(New DiagnosticLogEntry With {
                .EventType = "EMERGENCY_CLOSE",
                .Symbol    = _contractId,
                .Action    = If(_tradeSide = OrderSide.Buy, "BUY", "SELL"),
                .Why       = $"qty={_currentQty} avgEntry={_averageEntry:F4} lastClose={_lastClose:F4} estPnl=${estimatedPnl:N0}",
                .Outcome   = New DiagOutcome With {
                    .Status = "EMERGENCY_CLOSE",
                    .PlUsd  = estimatedPnl
                }
            })
            RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("Emergency Close", estimatedPnl))

            _currentQty = 0
            _entryPrices.Clear()
            _averageEntry = 0D
            _lastEntryPrice = 0D
            _brackets.Clear()
            _freeRideActive = False
            RaisePositionChanged()
        End Function

        Private Sub RaisePositionChanged()
            RaiseEvent PositionChanged(Me, New SniperPositionEventArgs(
                _currentQty, _averageEntry, _freeRideActive, CalculateCurrentHeat()))
        End Sub

        Private Sub Log(message As String)
            Dim ts = $"{DateTime.Now:HH:mm:ss}  {message}"
            _logger.LogInformation("[PumpNDumpEngine] {Msg}", message)
            RaiseEvent LogMessage(Me, ts)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                Task.Run(Function() StopAsync("Engine disposed"))
                _timer?.Dispose()
                _disposed = True
                GC.SuppressFinalize(Me)
            End If
        End Sub

    End Class

End Namespace
