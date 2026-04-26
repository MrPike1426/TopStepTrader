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
    ''' CryptoJoe-specific execution engine — identical to StrategyExecutionEngine except:
    '''   1. BUY orders only: any SELL signal is suppressed before entering the placement
    '''      pipeline.  No SELL order is ever submitted to the broker API.
    '''   2. Confidence override: all placed trades report 100 % confidence regardless of
    '''      the raw EMA/RSI score, because CryptoJoe trades on any bullish signal.
    '''
    ''' StrategyExecutionEngine.vb is unchanged; this class is wired exclusively through
    ''' CryptoJoeViewModel and its own DI registration.
    ''' Register as Transient — one instance per strategy session.
    ''' </summary>
    Public Class CryptoStrategyExecutionEngine
        Implements IDisposable

        ' ── Dependencies ──────────────────────────────────────────────────────────
        Private ReadOnly _ingestionService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _logger As ILogger(Of CryptoStrategyExecutionEngine)

        ' ── State ─────────────────────────────────────────────────────────────────
        Private _strategy As StrategyDefinition
        Private _timer As System.Threading.Timer
        Private _cts As CancellationTokenSource
        Private _callbackRunning As Integer = 0
        Private _positionOpen As Boolean = False
        Private _disposed As Boolean = False
        Private _running As Boolean = False
        Private _lastCheckedBarCount As Integer = 0
        Private _openPositionId As Long? = Nothing

        ' ── Events
        ''' <summary>Raised on the thread-pool whenever a log line is produced.</summary>
        Public Event LogMessage As EventHandler(Of String)
        ''' <summary>Raised when the engine stops (expired, stopped, or errored).</summary>
        Public Event ExecutionStopped As EventHandler(Of String)
        ''' <summary>Raised when an entry order is placed (trade opened).</summary>
        Public Event TradeOpened As EventHandler(Of TradeOpenedEventArgs)
        ''' <summary>Raised when the bracket position closes (TP or SL filled).</summary>
        Public Event TradeClosed As EventHandler(Of TradeClosedEventArgs)
        ''' <summary>Raised each bar-check cycle with the latest bar close price, for live P&amp;L updates.</summary>
        Public Event BarPriceUpdated As EventHandler(Of Decimal)
        ''' <summary>Raised every 15-second tick while a position is open with API-authoritative P&amp;L and positionId.</summary>
        Public Event PositionSynced As EventHandler(Of PositionSyncedEventArgs)
        ''' <summary>Raised after every bar check with the live EMA/RSI confidence score (0–100), even when no signal fires.</summary>
        Public Event ConfidenceUpdated As EventHandler(Of ConfidenceUpdatedEventArgs)

        ' ── Market-open guard ─────────────────────────────────────────────────────
        Public Property IsOrderingAllowed As Func(Of Boolean) = Function() True

        ' ── Trade-tracking state ──────────────────────────────────────────────────
        Private _lastEntryPrice As Decimal = 0D
        Private _lastEntrySide As OrderSide = OrderSide.Buy
        Private _lastConfidencePct As Integer = 0
        Private _lastTpPrice As Decimal = 0D
        Private _lastSlPrice As Decimal = 0D
        Private _lastTpExternalId As Long? = Nothing
        Private _pendingConfidencePct As Integer = 0
        Private _lastFinalAmount As Decimal = 0D
        Private _positionOpenedAt As DateTimeOffset = DateTimeOffset.MinValue
        Private _lastPositionClosedAt As DateTimeOffset = DateTimeOffset.MinValue
        Private Const ReEntryCooldownSeconds As Integer = 60

        ' ── Stepped trailing SL constants ─────────────────────────────────────────
        Private ReadOnly TrailTriggerPct As Decimal = 2.0D   ' trail arms when profit ≥ 2.0 %
        Private ReadOnly TrailStepPct As Decimal = 0.5D       ' ratchet step = 0.5 % per level
        Private ReadOnly TrailSlOffset As Decimal = 1.5D      ' SL = steppedProfit − 1.5 %

        Private _lastApiPnl As Decimal = 0D
        Private _mcCloudSlPrice As Decimal? = Nothing
        Private _lultTriggerExtreme As Decimal? = Nothing
        Private Const ReversalConfirmBars As Integer = 2
        Private _currentTrendSide As OrderSide?
        Private _reversalCandidateSide As OrderSide?
        Private _reversalConfirmCount As Integer = 0
        Private _lastBarTimestamp As DateTimeOffset = DateTimeOffset.MinValue
        Private _trailLastSteps As Integer = -1  ' −1 = trail not yet armed; advances each ratchet

        ' ── Confidence-driven scale-in state ──────────────────────────────────────
        Private Const ScaleInRequiredTicks As Integer = 3
        ''' <summary>Maximum additional positions. Reads MaxScaleIns from the active risk profile; falls back to 3.</summary>
        Private ReadOnly Property MaxScaleInTrades As Integer
            Get
                Return If(_strategy IsNot Nothing, _strategy.MaxScaleIns, 3)
            End Get
        End Property
        Private Const ExtremeConfidenceHighThreshold As Integer = 85
        Private Const ExtremeConfidenceLowThreshold As Integer = 25
        Private Const NeutralConfidenceLow As Integer = 40
        Private Const NeutralConfidenceHigh As Integer = 60
        Private _extremeConfidenceDurationCount As Integer = 0
        Private _scaleInTradeCount As Integer = 0
        Private _openTradeCount As Integer = 0
        Private _currentAtrValue As Decimal = 0D
        Private _currentEma21 As Decimal = 0D
        Const ScaleInPullbackTolerance As Double = 0.001

        ' Scale-in thresholds
        Private Const ScaleInBullThreshold As Integer = 85
        Private Const ScaleInBearThreshold As Integer = 25

        ' Running sum of DollarPerPoint across ALL open positions (initial + scale-ins).
        Private _totalDollarPerPoint As Decimal = 0D


        Public Sub New(ingestionService As IBarIngestionService,
                       orderService As IOrderService,
                       logger As ILogger(Of CryptoStrategyExecutionEngine))
            _ingestionService = ingestionService
            _orderService = orderService
            _logger = logger
        End Sub

        ' ── Public API ────────────────────────────────────────────────────────────

        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _running
            End Get
        End Property

        Public Sub Start(strategy As StrategyDefinition)
            If _running Then Return
            _strategy = strategy
            _strategy.ExpiresAt = DateTimeOffset.UtcNow.AddHours(strategy.DurationHours)
            _positionOpen = False
            _openPositionId = Nothing
            _openTradeCount = 0
            _positionOpenedAt = DateTimeOffset.MinValue
            _lastPositionClosedAt = DateTimeOffset.MinValue
            _lastApiPnl = 0D
            _mcCloudSlPrice = Nothing
            _lultTriggerExtreme = Nothing
            _currentTrendSide = Nothing
            _reversalCandidateSide = Nothing
            _reversalConfirmCount = 0
            _lastBarTimestamp = DateTimeOffset.MinValue
            _extremeConfidenceDurationCount = 0
            _scaleInTradeCount = 0
            _currentAtrValue = 0D
            _currentEma21 = 0D
            ResetTrailState()
            _running = True
            _lastCheckedBarCount = 0
            _cts = New CancellationTokenSource()
            Interlocked.Exchange(_callbackRunning, 0)

            Log($"[CRYPTO BUY-ONLY] Strategy started — {strategy.ContractId} | {strategy.Name} | BUY orders only")
            Log($"Duration: {strategy.DurationHours}hrs | Expires: {strategy.ExpiresAt:HH:mm} UTC")
            Log($"Checking bars every 15 seconds...")

            Task.Run(Async Function() As Task
                         Try
                             Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                                 strategy.AccountId, strategy.ContractId, Nothing, _cts.Token)
                             If snapshot IsNot Nothing Then
                                 _positionOpen = True
                                 _openPositionId = snapshot.PositionId
                                 _positionOpenedAt = DateTimeOffset.UtcNow.AddSeconds(-61)
                                 If snapshot.OpenRate > 0D Then
                                     _lastEntryPrice = snapshot.OpenRate
                                     _lastEntrySide = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)
                                 End If
                                 _currentTrendSide = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)
                                 _lastFinalAmount = snapshot.Amount
                                 _openTradeCount = snapshot.PositionCount
                                 Dim startupSide = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)
                                 RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(startupSide, strategy.ContractId, 100,
                                         snapshot.OpenedAtUtc, Nothing, snapshot.PositionId,
                                         snapshot.OpenedAtUtc, snapshot.Amount, snapshot.OpenRate))
                                 Log($"⚠️  Existing {snapshot.PositionCount} position(s) detected on startup (positionId={snapshot.PositionId}, entry={snapshot.OpenRate:F4}, units={snapshot.Units:F3}) — monitoring without placing new entry. Stepped trail active from ≥2% profit.")
                             Else
                                 Log($"✓ No existing positions for {strategy.ContractId} — ready to trade.")
                             End If
                         Catch ex As Exception
                             Log($"⚠️  Startup position check failed: {ex.Message} — assuming no open positions.")
                         End Try
                     End Function)

            ' 3-second initial delay gives the startup position-check Task.Run time to complete
            ' before the first bar-check tick fires, eliminating a race where _positionOpen
            ' could still be False when the timer's first callback runs.
            _timer = New System.Threading.Timer(AddressOf TimerCallback, Nothing,
                                                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15))
        End Sub

        Public Sub [Stop](Optional reason As String = "Stopped by user")
            If Not _running Then Return
            _running = False
            _cts?.Cancel()
            _timer?.Change(Timeout.Infinite, 0)
            If _positionOpen Then
                Log($"⚠️  POSITIONS STILL OPEN — {_strategy?.ContractId} has active positions. " &
                    $"Monitor manually or restart the engine to resume automated management.")
            End If
            Log($"■ Strategy stopped — {reason}")
            RaiseEvent ExecutionStopped(Me, reason)
        End Sub

        ' ── Timer callback ────────────────────────────────────────────────────────

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
                         Catch ex As Exception
                             _logger.LogError(ex, "CryptoStrategyExecutionEngine unhandled error")
                             Log($"⚠️  Error during bar check: {ex.Message}")
                         Finally
                             Interlocked.Exchange(_callbackRunning, 0)
                         End Try
                     End Function)
        End Sub

        Private Async Function DoCheckAsync() As Task
            If Not _running Then Return
            Dim ct = If(_cts IsNot Nothing, _cts.Token, CancellationToken.None)

            If DateTimeOffset.UtcNow > _strategy.ExpiresAt Then
                [Stop]("Strategy duration expired")
                Return
            End If

            Dim remaining = _strategy.ExpiresAt - DateTimeOffset.UtcNow
            Dim remStr = $"{CInt(remaining.TotalHours)}h {remaining.Minutes}m remaining"

            Dim timeframe = CType(If(_strategy.TimeframeMinutes = 1, BarTimeframe.OneMinute,
                                  If(_strategy.TimeframeMinutes = 5, BarTimeframe.FiveMinute,
                                  If(_strategy.TimeframeMinutes = 15, BarTimeframe.FifteenMinute,
                                  If(_strategy.TimeframeMinutes = 60, BarTimeframe.OneHour,
                                     BarTimeframe.FiveMinute)))), BarTimeframe)

            Dim minBars = _strategy.IndicatorPeriod + 5
            Dim fetchCount = Math.Max(minBars + 15, 70)

            Await _ingestionService.IngestAsync(_strategy.ContractId, timeframe, fetchCount, ct)

            Dim bars = Await _ingestionService.GetBarsForMLAsync(_strategy.ContractId, timeframe, fetchCount, ct)

            If bars Is Nothing OrElse bars.Count < minBars Then
                Dim barCount = If(bars Is Nothing, 0, bars.Count)
                If barCount = 0 Then
                    Log($"No bars returned for '{_strategy.ContractId}' — market may be closed or outside trading hours. Retrying… ({remStr})")
                Else
                    Log($"Waiting for bars — have {barCount}/{minBars} needed ({remStr})")
                End If
                Return
            End If

            If bars.Count > _lastCheckedBarCount Then
                _lastCheckedBarCount = bars.Count
                Dim tfMin = _strategy.TimeframeMinutes
                Dim gapThresholdMin = tfMin * 2.5
                Log($"📊 Bar window: {bars.First().Timestamp:yyyy-MM-dd HH:mm} UTC → {bars.Last().Timestamp:yyyy-MM-dd HH:mm} UTC ({bars.Count} bars, {tfMin}-min tf)")
                Dim gapCount As Integer = 0
                For i = 1 To bars.Count - 1
                    Dim gapMin = (bars(i).Timestamp - bars(i - 1).Timestamp).TotalMinutes
                    If gapMin > gapThresholdMin Then
                        Log($"⚠️  Gap at bar {i}: {bars(i - 1).Timestamp:HH:mm} UTC → {bars(i).Timestamp:HH:mm} UTC ({gapMin:F0} min, expected {tfMin} min)")
                        gapCount += 1
                    End If
                Next
                If gapCount = 0 Then
                    Log($"✓ Bar series contiguous — {bars.Count} bars, no gaps > {CInt(gapThresholdMin)} min")
                Else
                    Log($"⚠️  {gapCount} gap(s) detected — EMA/RSI warmup spans a market closure; indicators may be unreliable across the gap")
                End If
            End If

            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim opens = bars.Select(Function(b) b.Open).ToList()
            Dim highs = bars.Select(Function(b) b.High).ToList()
            Dim lows = bars.Select(Function(b) b.Low).ToList()
            Dim volumes = bars.Select(Function(b) b.Volume).ToList()

            Dim lastBar = bars.Last()
            RaiseEvent BarPriceUpdated(Me, CDec(lastBar.Close))
            Dim side As OrderSide? = Nothing
            Dim rawUpPct As Integer = 0
            Dim rawDownPct As Integer = 0

            Dim periodTicks = TimeSpan.FromMinutes(_strategy.TimeframeMinutes).Ticks
            Dim barPeriodStart = New DateTimeOffset(
                lastBar.Timestamp.Ticks - (lastBar.Timestamp.Ticks Mod periodTicks),
                lastBar.Timestamp.Offset)
            Dim isNewBar = (barPeriodStart > _lastBarTimestamp)
            If isNewBar Then _lastBarTimestamp = barPeriodStart

            Dim barAgeMins = (DateTimeOffset.UtcNow - lastBar.Timestamp).TotalMinutes
            Dim barIsStale = barAgeMins > _strategy.TimeframeMinutes * 3.0

            If Not barIsStale Then

                Select Case _strategy.Condition
                    Case StrategyConditionType.FullCandleOutsideBands,
                     StrategyConditionType.CloseOutsideBands
                        Dim bands = TechnicalIndicators.BollingerBands(closes,
                                                                   _strategy.IndicatorPeriod,
                                                                   _strategy.IndicatorMultiplier)
                        Dim upper = CDec(TechnicalIndicators.LastValid(bands.Upper))
                        Dim lower = CDec(TechnicalIndicators.LastValid(bands.Lower))
                        Dim middle = CDec(TechnicalIndicators.LastValid(bands.Middle))

                        If _strategy.Condition = StrategyConditionType.FullCandleOutsideBands Then
                            If _strategy.GoLongWhenBelowBands AndAlso lastBar.High < lower Then
                                Log($"✅ Full candle below lower band! High={lastBar.High:F2} < Lower={lower:F2}")
                                side = OrderSide.Buy
                            ElseIf _strategy.GoShortWhenAboveBands AndAlso lastBar.Low > upper Then
                                Log($"✅ Full candle above upper band! Low={lastBar.Low:F2} > Upper={upper:F2}")
                                side = OrderSide.Sell
                            Else
                                Log($"Bar checked — Close={lastBar.Close:F2} | BB [{lower:F2} — {middle:F2} — {upper:F2}] | no signal ({remStr})")
                            End If
                        Else
                            If _strategy.GoLongWhenBelowBands AndAlso lastBar.Close < lower Then
                                Log($"✅ Close below lower band! Close={lastBar.Close:F2} < Lower={lower:F2}")
                                side = OrderSide.Buy
                            ElseIf _strategy.GoShortWhenAboveBands AndAlso lastBar.Close > upper Then
                                Log($"✅ Close above upper band! Close={lastBar.Close:F2} > Upper={upper:F2}")
                                side = OrderSide.Sell
                            Else
                                Log($"Bar checked — Close={lastBar.Close:F2} | BB [{lower:F2}—{upper:F2}] | no signal ({remStr})")
                            End If
                        End If

                    Case StrategyConditionType.RSIOversold, StrategyConditionType.RSIOverbought
                        Dim rsi = TechnicalIndicators.RSI(closes, _strategy.IndicatorPeriod)
                        Dim rsiVal = TechnicalIndicators.LastValid(rsi)

                        If _strategy.GoLongWhenBelowBands AndAlso rsiVal < 30 Then
                            Log($"✅ RSI oversold! RSI={rsiVal:F1} < 30")
                            side = OrderSide.Buy
                        ElseIf _strategy.GoShortWhenAboveBands AndAlso rsiVal > 70 Then
                            Log($"✅ RSI overbought! RSI={rsiVal:F1} > 70")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — RSI={rsiVal:F1} | no signal ({remStr})")
                        End If

                    Case StrategyConditionType.EMACrossAbove, StrategyConditionType.EMACrossBelow
                        Dim fastEma = TechnicalIndicators.EMA(closes, _strategy.IndicatorPeriod)
                        Dim slowEma = TechnicalIndicators.EMA(closes, _strategy.SecondaryPeriod)
                        Dim fastNow = TechnicalIndicators.LastValid(fastEma)
                        Dim fastPrev = TechnicalIndicators.PreviousValid(fastEma)
                        Dim slowNow = TechnicalIndicators.LastValid(slowEma)
                        Dim slowPrev = TechnicalIndicators.PreviousValid(slowEma)

                        Dim crossedAbove = fastPrev < slowPrev AndAlso fastNow > slowNow
                        Dim crossedBelow = fastPrev > slowPrev AndAlso fastNow < slowNow

                        If _strategy.GoLongWhenBelowBands AndAlso crossedAbove Then
                            Log($"✅ EMA{_strategy.IndicatorPeriod} crossed above EMA{_strategy.SecondaryPeriod}!")
                            side = OrderSide.Buy
                        ElseIf _strategy.GoShortWhenAboveBands AndAlso crossedBelow Then
                            Log($"✅ EMA{_strategy.IndicatorPeriod} crossed below EMA{_strategy.SecondaryPeriod}!")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — EMA{_strategy.IndicatorPeriod}={fastNow:F2} EMA{_strategy.SecondaryPeriod}={slowNow:F2} | no signal ({remStr})")
                        End If

                    Case StrategyConditionType.EmaRsiWeightedScore
                        Dim ema21Vals = TechnicalIndicators.EMA(closes, 21)
                        Dim ema50Vals = TechnicalIndicators.EMA(closes, 50)
                        Dim rsi14Vals = TechnicalIndicators.RSI(closes, 14)

                        Dim ema21Now = TechnicalIndicators.LastValid(ema21Vals)
                        Dim ema21Prev = TechnicalIndicators.PreviousValid(ema21Vals)
                        Dim ema50Now = TechnicalIndicators.LastValid(ema50Vals)
                        Dim rsiVal = TechnicalIndicators.LastValid(rsi14Vals)
                        Dim lastClose = CDec(lastBar.Close)
                        _currentEma21 = CDec(ema21Now)

                        Dim bullScore As Double = 0

                        If ema21Now > ema50Now Then bullScore += 25
                        If lastClose > CDec(ema21Now) Then bullScore += 20
                        If lastClose > CDec(ema50Now) Then bullScore += 15

                        Dim rsiScore As Double
                        If rsiVal >= 50 AndAlso rsiVal < 70 Then
                            rsiScore = 20
                        Else
                            rsiScore = 0
                        End If
                        bullScore += rsiScore
                        bullScore = Math.Max(0.0, Math.Min(100.0, bullScore))

                        If ema21Now > ema21Prev Then bullScore += 10

                        Dim lastThree = bars.Skip(bars.Count - 3).ToList()
                        Dim bullCandles = lastThree.Where(Function(b) b.Close >= b.Open).Count()
                        If bullCandles >= 2 Then bullScore += 10

                        Dim upPct As Double = bullScore
                        Dim downPct As Double = 100 - bullScore
                        Dim minPct As Integer = _strategy.MinConfidencePct

                        Dim dmiResult = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim adxNow = TechnicalIndicators.LastValid(dmiResult.ADX)
                        Dim adxGatePassed = (adxNow >= _strategy.AdxThreshold)

                        Dim atrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(atrVals))

                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(CInt(upPct), CInt(downPct), adxGatePassed, CSng(adxNow), lastClose))

                        If Not adxGatePassed Then
                            Log($"Bar checked — ADX={adxNow:F1} < 20 (ranging market) — signal suppressed | EMA/RSI: UP={upPct:F0}% DOWN={downPct:F0}% | ATR={_currentAtrValue:F4} | {remStr}")
                        ElseIf upPct >= minPct Then
                            _pendingConfidencePct = CInt(upPct)
                            Log($"✅ EMA/RSI weighted: UP={upPct:F0}% ≥ {minPct}% — LONG signal! Close={lastClose:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2} RSI={rsiVal:F1} ADX={adxNow:F1}")
                            side = OrderSide.Buy
                        ElseIf downPct >= minPct Then
                            _pendingConfidencePct = CInt(downPct)
                            Log($"✅ EMA/RSI weighted: DOWN={downPct:F0}% ≥ {minPct}% — SHORT signal! Close={lastClose:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2} RSI={rsiVal:F1} ADX={adxNow:F1}")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — EMA/RSI: UP={upPct:F0}% DOWN={downPct:F0}% | no signal (need ≥{minPct}%) | EMA21={ema21Now:F2} EMA50={ema50Now:F2} RSI={rsiVal:F1} ADX={adxNow:F1} | {remStr}")
                        End If

                    Case StrategyConditionType.MultiConfluence
                        Dim mcResult = MultiConfluenceStrategy.Evaluate(highs, lows, closes, volumes)
                        _currentAtrValue = mcResult.AtrValue
                        _mcCloudSlPrice = Nothing

                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(mcResult.BullScore, mcResult.BearScore, adxValue:=mcResult.AdxValue, lastClose:=CDec(lastBar.Close)))

                        If mcResult.Side.HasValue Then
                            Dim mcSide = mcResult.Side.Value
                            _pendingConfidencePct = 100
                            _mcCloudSlPrice = mcResult.CloudEdgeSl
                            If mcSide = OrderSide.Buy Then
                                Log($"✅ Multi-Confluence LONG — all 7 conditions met! {mcResult.StatusLine} | {remStr}")
                            Else
                                Log($"✅ Multi-Confluence SHORT — all 7 conditions met! {mcResult.StatusLine} | {remStr}")
                            End If
                            side = mcSide
                        Else
                            Log($"Bar checked — Multi-Confluence: {mcResult.StatusLine} | {remStr}")
                        End If

                    Case StrategyConditionType.LultDivergence
                        _lultTriggerExtreme = Nothing
                        Dim lultOpens = bars.Select(Function(b) b.Open).ToList()
                        Dim lultAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(lultAtrVals))
                        Dim lultResult = LultDivergenceStrategy.Evaluate(highs, lows, closes, lultOpens)
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(lultResult.BullScore, lultResult.BearScore, lastClose:=CDec(lastBar.Close)))
                        If Not lultResult.IsInTradingWindow Then
                            Log($"Bar checked — LULT (OUT of EST window): {lultResult.StatusLine} | {remStr}")
                        ElseIf lultResult.Side.HasValue Then
                            Dim lultSide = lultResult.Side.Value
                            _pendingConfidencePct = 100
                            Dim tickBuf = If(_currentAtrValue > 0D,
                                         Math.Max(_currentAtrValue * 0.25D, 0.75D), 0.75D)
                            _lultTriggerExtreme = If(lultSide = OrderSide.Buy,
                                                  lultResult.TriggerWaveExtreme - tickBuf,
                                                  lultResult.TriggerWaveExtreme + tickBuf)
                            Dim partialMsg = If(lultResult.PartialTpSwingLevel.HasValue,
                                            $" | Partial TP swing={lultResult.PartialTpSwingLevel.Value:F4}",
                                            String.Empty)
                            If lultSide = OrderSide.Buy Then
                                Log($"✅ LULT LONG — 6/6 steps confirmed! {lultResult.StatusLine} | " &
                                $"AnchorWT1={lultResult.AnchorWt1:F1} TriggerWT1={lultResult.TriggerWt1:F1} " &
                                $"TriggerLow={lultResult.TriggerWaveExtreme:F4} SL≈{_lultTriggerExtreme:F4}{partialMsg} | {remStr}")
                            Else
                                Log($"✅ LULT SHORT — 6/6 steps confirmed! {lultResult.StatusLine} | " &
                                $"AnchorWT1={lultResult.AnchorWt1:F1} TriggerWT1={lultResult.TriggerWt1:F1} " &
                                $"TriggerHigh={lultResult.TriggerWaveExtreme:F4} SL≈{_lultTriggerExtreme:F4}{partialMsg} | {remStr}")
                            End If
                            side = lultSide
                        Else
                            Log($"Bar checked — LULT: {lultResult.StatusLine} | {remStr}")
                        End If

                    Case StrategyConditionType.VidyaCross
                        ' ── VIDYA Cross (crypto) ─────────────────────────────────────────────
                        ' Long  when close crosses ABOVE the VIDYA line AND 6-bar ΔVol ≥ +20%
                        ' Short when close crosses BELOW the VIDYA line AND 6-bar ΔVol ≤ −20%
                        ' Confidence = |ΔVol| × 100  (30% delta → confidence 30, 75% → 75)
                        Dim vidyaLength = If(_strategy.IndicatorPeriod > 0, _strategy.IndicatorPeriod, 14)
                        Dim cmoLen = If(_strategy.SecondaryPeriod > 0, _strategy.SecondaryPeriod, 9)
                        Dim vidyaArr = TechnicalIndicators.VIDYA(closes, vidyaLength, cmoLen)
                        Dim cmoArr = TechnicalIndicators.CMO(closes, cmoLen)   ' retained for diagnostics
                        Dim deltaArr = TechnicalIndicators.DeltaVolume(closes, opens, volumes)

                        Dim vidyaNow = CDec(TechnicalIndicators.LastValid(vidyaArr))
                        Dim vidyaPrev = CDec(TechnicalIndicators.PreviousValid(vidyaArr))
                        Dim closeNow = CDec(lastBar.Close)
                        Dim closePrev = If(bars.Count >= 2, CDec(bars(bars.Count - 2).Close), closeNow)
                        Dim cmoNow = CDbl(TechnicalIndicators.LastValid(cmoArr))
                        Dim deltaNow = CDbl(TechnicalIndicators.LastValid(deltaArr))
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(TechnicalIndicators.ATR(highs, lows, closes, 14)))

                        Const VolThreshold As Double = 0.20   ' ±20% delta required to fire
                        Dim vidyaConfidence = CInt(Math.Min(100, Math.Abs(deltaNow) * 100))
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(
                            If(deltaNow >= VolThreshold, vidyaConfidence, 0),
                            If(deltaNow <= -VolThreshold, vidyaConfidence, 0),
                            adxValue:=CSng(deltaNow),
                            lastClose:=closeNow))

                        If vidyaNow = 0 OrElse vidyaPrev = 0 Then
                            Log($"Bar checked — VIDYA: insufficient data | {remStr}")
                        ElseIf closeNow > vidyaNow AndAlso closePrev <= vidyaPrev Then
                            ' Price crossed above VIDYA — require ≥ +20% volume delta
                            If deltaNow >= VolThreshold Then
                                _pendingConfidencePct = vidyaConfidence
                                Log($"✅ VIDYA CROSS LONG! Close={closeNow:F4} > VIDYA={vidyaNow:F4} " &
                                    $"| ΔVol={deltaNow:P0}✓ (conf={vidyaConfidence}) | CMO={cmoNow:F3} | {remStr}")
                                side = OrderSide.Buy
                            Else
                                Log($"⚠ VIDYA cross long filtered — ΔVol={deltaNow:P0} < +20% threshold " &
                                    $"| Close={closeNow:F4} VIDYA={vidyaNow:F4} | {remStr}")
                            End If
                        ElseIf closeNow < vidyaNow AndAlso closePrev >= vidyaPrev Then
                            ' Price crossed below VIDYA — require ≤ −20% volume delta (suppressed by BUY-only constraint below)
                            If deltaNow <= -VolThreshold Then
                                _pendingConfidencePct = vidyaConfidence
                                Log($"✅ VIDYA CROSS SHORT! Close={closeNow:F4} < VIDYA={vidyaNow:F4} " &
                                    $"| ΔVol={deltaNow:P0}✓ (conf={vidyaConfidence}) | CMO={cmoNow:F3} | {remStr}")
                                side = OrderSide.Sell
                            Else
                                Log($"⚠ VIDYA cross short filtered — ΔVol={deltaNow:P0} > −20% threshold " &
                                    $"| Close={closeNow:F4} VIDYA={vidyaNow:F4} | {remStr}")
                            End If
                        Else
                            Log($"Bar checked — VIDYA={vidyaNow:F4} Close={closeNow:F4} ΔVol={deltaNow:P0} CMO={cmoNow:F3} | {remStr}")
                        End If

                    Case StrategyConditionType.DoubleBubbleButt
                        ' ── Double Bubble Butt ──────────────────────────────────────────────
                        ' Two BB sets over SMA(20): inner ±1.0 SD, outer ±2.0 SD.
                        ' Long  when close enters Buy Zone  (close > upper inner 1.0 SD band).
                        ' Short when close enters Sell Zone (close < lower inner 1.0 SD band).
                        Dim dbbInner = TechnicalIndicators.BollingerBands(closes, 20, 1.0)
                        Dim dbbInnerUp  = CDec(TechnicalIndicators.LastValid(dbbInner.Upper))
                        Dim dbbInnerLow = CDec(TechnicalIndicators.LastValid(dbbInner.Lower))
                        Dim dbbOuter = TechnicalIndicators.BollingerBands(closes, 20, 2.0)
                        Dim dbbOuterUp  = CDec(TechnicalIndicators.LastValid(dbbOuter.Upper))
                        Dim dbbOuterLow = CDec(TechnicalIndicators.LastValid(dbbOuter.Lower))
                        Dim dbbAtrVals  = TechnicalIndicators.ATR(highs, lows, closes, 20)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(dbbAtrVals))

                        Dim dbbClose = CDec(lastBar.Close)
                        If dbbInnerUp = 0D OrElse dbbInnerLow = 0D Then
                            Log($"Bar checked — DBB: bands not ready | {remStr}")
                        ElseIf dbbClose > dbbInnerUp Then
                            Log($"✅ DBB LONG! Close={dbbClose:F4} > upper 1-SD={dbbInnerUp:F4} (Buy Zone) | outer=[{dbbOuterLow:F4}–{dbbOuterUp:F4}] | ATR={_currentAtrValue:F4} | {remStr}")
                            side = OrderSide.Buy
                        ElseIf dbbClose < dbbInnerLow Then
                            Log($"✅ DBB SHORT! Close={dbbClose:F4} < lower 1-SD={dbbInnerLow:F4} (Sell Zone) | outer=[{dbbOuterLow:F4}–{dbbOuterUp:F4}] | ATR={_currentAtrValue:F4} | {remStr}")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — DBB: Neutral Zone | Close={dbbClose:F4} inner=[{dbbInnerLow:F4}–{dbbInnerUp:F4}] | {remStr}")
                        End If

                    Case Else
                        Log($"Condition '{_strategy.Condition}' not yet implemented")

                End Select

                ' ── CryptoJoe BUY-only constraint ─────────────────────────────────────────
                ' CryptoJoe is BUY-only.  Clear any SELL signal before it can
                ' enter the reversal-confirmation or order-placement pipeline.
                ' Applies uniformly to all strategy condition types above.
                If side.HasValue AndAlso side.Value = OrderSide.Sell Then
                    Log($"⛔ [CRYPTO BUY-ONLY] SELL signal suppressed for {_strategy.ContractId} — CryptoJoe is BUY-only.")
                    side = Nothing
                End If

                If side.HasValue Then
                    If _currentTrendSide Is Nothing Then
                        _currentTrendSide = side
                        _reversalCandidateSide = Nothing
                        _reversalConfirmCount = 0
                    ElseIf side.Value = _currentTrendSide.Value Then
                        If _reversalCandidateSide.HasValue Then
                            Log($"↩  Reversal candidate cleared — {side.Value} signal confirms existing trend")
                        End If
                        _reversalCandidateSide = Nothing
                        _reversalConfirmCount = 0
                    Else
                        If isNewBar Then
                            If Not _reversalCandidateSide.HasValue OrElse _reversalCandidateSide.Value <> side.Value Then
                                _reversalCandidateSide = side
                                _reversalConfirmCount = 1
                            Else
                                _reversalConfirmCount += 1
                            End If
                            Log($"↔  Reversal candidate: was {_currentTrendSide.Value}, now {side.Value} " &
                            $"({_reversalConfirmCount}/{ReversalConfirmBars} confirmations)")
                        End If

                        If _reversalConfirmCount >= ReversalConfirmBars Then
                            Await DoReversalFlushAsync(side.Value, CDec(lastBar.Close), ct)
                        End If
                    End If
                End If

            End If ' Not barIsStale

            If _positionOpen Then
                Dim secondsSinceEntry = (DateTimeOffset.UtcNow - _positionOpenedAt).TotalSeconds
                If secondsSinceEntry < 60 Then
                    Log($"⏳ Sync skipped ({CInt(secondsSinceEntry)}s since entry — waiting 60 s for portfolio to reflect new position)")
                Else
                    Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                        _strategy.AccountId, _strategy.ContractId, Nothing, ct)
                    If snapshot IsNot Nothing Then
                        If Not _openPositionId.HasValue Then
                            _openPositionId = snapshot.PositionId
                            Log($"🔗 positionId resolved: {snapshot.PositionId}")
                        End If
                        ' ProjectX portfolio API does not return a pnL field — calculate from
                        ' current bar close price across all aggregated position units.
                        Dim calculatedPnl = If(snapshot.Units > 0D,
                            Math.Round((CDec(lastBar.Close) - snapshot.OpenRate) * snapshot.Units *
                                       If(snapshot.IsBuy, 1D, -1D), 2), 0D)
                        _lastApiPnl = calculatedPnl
                        RaiseEvent PositionSynced(Me, New PositionSyncedEventArgs(
                            snapshot.PositionId, calculatedPnl, snapshot.OpenedAtUtc))
                    Else
                        Dim closedCount = Math.Max(1, _openTradeCount)
                        Log($"⚠️  API reconciliation: no open positions for {_strategy.ContractId} — " &
                            $"force-closing {closedCount} UI trade row(s) (SL/TP/external close). " &
                            $"Final P&L={If(_lastApiPnl >= 0, "+", "")}${_lastApiPnl:F2}. Ready for next signal.")
                        _positionOpen = False
                        _openPositionId = Nothing
                        Dim closePnl = _lastApiPnl
                        For i As Integer = 1 To closedCount
                            RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("SL/TP", closePnl))
                            closePnl = 0D
                        Next
                        _openTradeCount = 0
                        _lastApiPnl = 0D
                        _lastPositionClosedAt = DateTimeOffset.UtcNow  ' start re-entry cooldown
                        ResetTrailState()
                    End If
                End If
            End If

            ' ── Stepped trailing bracket — engine-tracked free-ride SL/TP ────────────
            ' Runs after the broker reconciliation so a just-detected broker close does
            ' not trigger a double-flatten.  Returns True when it closes the position.
            If _positionOpen AndAlso _lastEntryPrice > 0D Then
                Dim trailClosed = Await ApplySteppedTrailAsync(CDec(lastBar.Close), ct)
                If trailClosed Then Return
            End If

            If barIsStale Then
                Log($"⏸  Market closed — last bar is {CInt(barAgeMins)} min old (limit: {_strategy.TimeframeMinutes * 3} min) — monitoring positions only. ({remStr})")
            ElseIf _strategy.Condition = StrategyConditionType.EmaRsiWeightedScore Then
                Await EvaluateConfidenceActionsAsync(rawUpPct, rawDownPct, side, CDec(lastBar.Close), isNewBar, ct)
            Else
                If side.HasValue Then
                    If _positionOpen Then
                        Log($"⛔ Signal ({side.Value}) blocked — position already open (positionId={If(_openPositionId.HasValue, _openPositionId.Value.ToString(), "pending")}). Waiting for close before next entry.")
                    ElseIf (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds < ReEntryCooldownSeconds Then
                        Dim cooldownLeft = CInt(ReEntryCooldownSeconds - (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds)
                        Log($"⏸  Re-entry cooldown — {cooldownLeft}s remaining after last close | signal: {side.Value}")
                    ElseIf Not IsOrderingAllowed.Invoke() Then
                        Log($"⏸  {_strategy.ContractId} market CLOSED — monitoring only (no orders) | signal: {side.Value}")
                    Else
                        _positionOpen = True
                        Dim slArg As Decimal? = If(_strategy.Condition = StrategyConditionType.LultDivergence,
                                                   _lultTriggerExtreme, _mcCloudSlPrice)
                        Await PlaceBracketOrdersAsync(side.Value, lastBar.Close, slArg)
                    End If
                End If
            End If
        End Function

        Private Async Function PlaceBracketOrdersAsync(side As OrderSide, lastClose As Decimal,
                                                         Optional cloudSlPrice As Decimal? = Nothing) As Task
            ' ── CryptoJoe BUY-only defense-in-depth guard ─────────────────────────────
            ' The primary filter runs in DoCheckAsync, but this guard ensures no SELL order
            ' reaches the broker even if PlaceBracketOrdersAsync is reached by any other path.
            If side = OrderSide.Sell Then
                Log($"⛔ [CRYPTO BUY-ONLY] SELL bracket order blocked (defense-in-depth) for '{_strategy.ContractId}' — CryptoJoe is BUY-only.")
                _positionOpen = False
                Return
            End If

            ' ── CryptoJoe confidence override ─────────────────────────────────────────
            ' All CryptoJoe trades record 100 % confidence regardless of the raw EMA/RSI
            ' score so the trade history and UI always show the intended certainty level.
            _pendingConfidencePct = 100

            Dim instrId As Integer = 0
            Dim fav = TopStepTrader.Core.Trading.FavouriteContracts.TryGetBySymbol(_strategy.ContractId)
            If fav IsNot Nothing Then
                instrId = fav.InstrumentId
            ElseIf Not Integer.TryParse(_strategy.ContractId, instrId) Then
                Log($"⚠️  Cannot resolve instrumentId for '{_strategy.ContractId}' — order aborted. " &
                    $"Add contract to Core.Trading.FavouriteContracts.")
                _positionOpen = False
                Return
            End If

            Dim priceUsed = lastClose

            Dim minNotional = If(fav IsNot Nothing, fav.MinNotionalUsd, 1000D)
            Dim finalAmount = minNotional
            Dim clamped = False

            ' ── DollarPerPoint for TopStepX futures = tickValue / tickSize × contracts ──
            Dim tickSzCj = If(_strategy.TickSize > 0D, _strategy.TickSize, If(fav IsNot Nothing, fav.PxTickSize, 0.01D))
            Dim tickValCj = If(_strategy.TickValue > 0D, _strategy.TickValue, If(fav IsNot Nothing, fav.PxTickValue, 1D))
            Dim contractsCj = CDec(If(_strategy.Contracts > 0, _strategy.Contracts, 1))
            Dim dollarPerPoint = If(tickSzCj > 0D, Math.Round((tickValCj / tickSzCj) * contractsCj, 4), 0D)
            Dim sideStr = If(side = OrderSide.Buy, "BUY", "SELL")
            _totalDollarPerPoint = dollarPerPoint

            ' Simplified SL: 5% below for BUY
            Dim slPriceVal As Decimal? = Math.Round(priceUsed * 0.95, 4)
            Dim tpPriceVal As Decimal? = Nothing  ' No TP

            Log($"📋 ORDER | instrId={instrId} side={side} | final=${finalAmount:F2}")
            Log($"📋 Simplified SL | priceUsed={priceUsed:F4} SL=5% below → {slPriceVal.Value:F4}")

            Dim entryOrder As New Order With {
                .AccountId = _strategy.AccountId,
                .ContractId = _strategy.ContractId,
                .InstrumentId = instrId,
                .Side = side,
                .OrderType = OrderType.Market,
                .Amount = finalAmount,
                .StopLossRate = slPriceVal,
                .TakeProfitRate = tpPriceVal,
                .IsTslEnabled = False,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"AI Strategy: {_strategy.Name}"
            }

            Try
                Await _orderService.PlaceOrderAsync(entryOrder)
                If entryOrder.Status = OrderStatus.Rejected Then
                    Log($"⚠️  Entry order rejected by API — no position opened.")
                    _positionOpen = False
                    Return
                End If
                Log($"✅ Entry {side} placed — instrId={instrId} amount=${finalAmount:F2} " &
                    $"SL={If(slPriceVal.HasValue, slPriceVal.Value.ToString("F4"), "none")}")
            Catch ex As Exception
                Log($"⚠️  Entry order failed: {ex.Message}")
                _positionOpen = False
                Return
            End Try

            ' New position — reset stepped trailing state so the fresh entry is tracked cleanly.
            ResetTrailState()

            _lastEntryPrice = priceUsed
            _lastEntrySide = side
            _lastConfidencePct = _pendingConfidencePct
            _lastTpExternalId = entryOrder.ExternalOrderId
            _lastTpPrice = If(tpPriceVal.HasValue, tpPriceVal.Value, 0D)
            _lastSlPrice = If(slPriceVal.HasValue, slPriceVal.Value, 0D)
            _lastFinalAmount = finalAmount
            _openPositionId = entryOrder.ExternalPositionId
            _positionOpenedAt = DateTimeOffset.UtcNow
            _openTradeCount += 1

            RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(side, _strategy.ContractId,
                                                                _lastConfidencePct,
                                                                _positionOpenedAt,
                                                                entryOrder.ExternalOrderId,
                                                                entryOrder.ExternalPositionId,
                                                                _positionOpenedAt,
                                                                finalAmount,
                                                                priceUsed))
            Log($"Position open — positionId={_openPositionId}. Monitoring for SL/TP hit every 15 s.")
        End Function

        ' ── Helpers ───────────────────────────────────────────────────────────────

        Private Async Function DoReversalFlushAsync(newSide As OrderSide,
                                                     lastClose As Decimal,
                                                     ct As CancellationToken) As Task
            Dim prevSide = If(_currentTrendSide.HasValue, _currentTrendSide.Value.ToString(), "None")
            Log($"🔄 REVERSAL CONFIRMED — was {prevSide}, flipping to {newSide}. " &
                $"Closing/cancelling all {_strategy.ContractId} positions...")

            Dim ok = Await _orderService.FlattenContractAsync(_strategy.AccountId, _strategy.ContractId, ct)
            If ok Then
                Log($"✅ Flatten complete — {_strategy.ContractId} closed. Waiting for next {newSide} signal...")
            Else
                Log($"⚠️  Flatten partially failed for {_strategy.ContractId} — check positions manually. Waiting for next {newSide} signal...")
            End If

            Dim reversalClosedCount = If(_positionOpen, Math.Max(1, _openTradeCount), 0)
            If reversalClosedCount > 0 Then
                Dim closePnl As Decimal = _lastApiPnl
                If closePnl = 0D AndAlso _lastEntryPrice > 0D AndAlso _totalDollarPerPoint > 0D Then
                    Dim priceMove = If(_lastEntrySide = OrderSide.Buy,
                                       lastClose - _lastEntryPrice,
                                       _lastEntryPrice - lastClose)
                    ' TopStepX futures P&L = priceMove × DollarPerPoint
                    closePnl = Math.Round(priceMove * _totalDollarPerPoint, 2)
                End If
                If reversalClosedCount > 1 Then
                    Log($"🔄 Closing {reversalClosedCount} stale UI trade row(s) during reversal flush for {_strategy.ContractId}")
                End If
                For i As Integer = 1 To reversalClosedCount
                    RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("Reversal", closePnl))
                    closePnl = 0D
                Next
            End If

            _positionOpen = False
            _openPositionId = Nothing
            _openTradeCount = 0
            _positionOpenedAt = DateTimeOffset.MinValue
            _lastPositionClosedAt = DateTimeOffset.UtcNow  ' start re-entry cooldown
            _lastApiPnl = 0D
            _currentTrendSide = newSide
            _reversalCandidateSide = Nothing
            _reversalConfirmCount = 0
            _extremeConfidenceDurationCount = 0
            _scaleInTradeCount = 0
            ResetTrailState()
        End Function

        ' ── Confidence-driven scale-in / neutral-exit (EMA/RSI strategy) ───────

        Private Async Function EvaluateConfidenceActionsAsync(
                rawUpPct As Integer,
                rawDownPct As Integer,
                side As OrderSide?,
                lastClose As Decimal,
                isNewBar As Boolean,
                ct As CancellationToken) As Task

            If rawUpPct >= NeutralConfidenceLow AndAlso rawUpPct <= NeutralConfidenceHigh Then
                _extremeConfidenceDurationCount = 0
                If _positionOpen Then
                    Log($"🔔 NEUTRAL CONFIDENCE — UP={rawUpPct}% DOWN={rawDownPct}% " &
                        $"(band: {NeutralConfidenceLow}–{NeutralConfidenceHigh}%) — flattening all positions immediately...")
                    Await DoNeutralFlattenAsync(ct)
                Else
                    Log($"Confidence neutral — UP={rawUpPct}% DOWN={rawDownPct}% | no open positions at broker — confidence exit skipped")
                End If
                Return
            End If

            If Not _positionOpen Then
                If side.HasValue Then
                    If Not IsOrderingAllowed.Invoke() Then
                        Log($"⏸  {_strategy.ContractId} market CLOSED — monitoring only (no orders) | UP={rawUpPct}% DOWN={rawDownPct}% signal={side.Value}")
                    ElseIf (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds < ReEntryCooldownSeconds Then
                        Dim cooldownLeft = CInt(ReEntryCooldownSeconds - (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds)
                        Log($"⏸  Re-entry cooldown — {cooldownLeft}s remaining after last close | UP={rawUpPct}% DOWN={rawDownPct}%")
                    Else
                        Log($"🎯 INITIAL TRADE — {side.Value} | Confidence: UP={rawUpPct}% DOWN={rawDownPct}%")
                        _positionOpen = True
                        Await PlaceBracketOrdersAsync(side.Value, lastClose)
                        _extremeConfidenceDurationCount = 0
                    End If
                End If
                Return
            End If

            Dim isExtremeBull = rawUpPct > ScaleInBullThreshold
            Dim isExtremeBear = rawUpPct < ScaleInBearThreshold

            If Not isExtremeBull AndAlso Not isExtremeBear Then
                If _extremeConfidenceDurationCount > 0 Then
                    Log($"Scale-in paused — UP={rawUpPct}% DOWN={rawDownPct}% " &
                        $"(need >{ScaleInBullThreshold}% or <{ScaleInBearThreshold}%) | timer reset")
                End If
                _extremeConfidenceDurationCount = 0
                Return
            End If

            Dim extremeSide As OrderSide = If(isExtremeBull, OrderSide.Buy, OrderSide.Sell)

            ' ── CryptoJoe BUY-only: suppress SELL scale-ins ───────────────────────────
            ' A bear extreme (low upPct) would normally trigger a SELL scale-in; suppress it.
            If extremeSide = OrderSide.Sell Then
                Log($"⛔ [CRYPTO BUY-ONLY] SELL scale-in suppressed | UP={rawUpPct}% DOWN={rawDownPct}% — CryptoJoe is BUY-only.")
                _extremeConfidenceDurationCount = 0
                Return
            End If

            If _currentTrendSide.HasValue AndAlso _currentTrendSide.Value <> extremeSide Then
                If _extremeConfidenceDurationCount > 0 OrElse _scaleInTradeCount > 0 Then
                    Log($"Scale-in direction mismatch — extreme={extremeSide} but trend={_currentTrendSide.Value} | counters reset")
                End If
                _extremeConfidenceDurationCount = 0
                _scaleInTradeCount = 0
                Return
            End If

            If isNewBar Then
                _extremeConfidenceDurationCount += 1
                Log($"⏱  Extreme confidence bar {_extremeConfidenceDurationCount}/{ScaleInRequiredTicks} — " &
                    $"UP={rawUpPct}% DOWN={rawDownPct}% | {extremeSide} | pullback to EMA21 ✓ | " &
                    $"scale-in {_scaleInTradeCount}/{MaxScaleInTrades}")
            Else
                Log($"⏱  Extreme confidence (same bar, tick skipped) — " &
                    $"UP={rawUpPct}% DOWN={rawDownPct}% | {extremeSide} | " &
                    $"bar count {_extremeConfidenceDurationCount}/{ScaleInRequiredTicks}")
            End If

            If _scaleInTradeCount >= MaxScaleInTrades Then
                Log($"Scale-in cap reached ({MaxScaleInTrades}/{MaxScaleInTrades}) — holding position, no further scale-in trades")
                Return
            End If

            If _extremeConfidenceDurationCount < ScaleInRequiredTicks Then Return

            _extremeConfidenceDurationCount = 0
            _scaleInTradeCount += 1
            If Not IsOrderingAllowed.Invoke() Then
                Log($"⏸  {_strategy.ContractId} market CLOSED — scale-in {_scaleInTradeCount}/{MaxScaleInTrades} suppressed | UP={rawUpPct}% DOWN={rawDownPct}%")
                Return
            End If
            Log($"📈 SCALE-IN {_scaleInTradeCount}/{MaxScaleInTrades} — Adding {extremeSide} position | " &
                $"Confidence: UP={rawUpPct}% DOWN={rawDownPct}%")
            Await PlaceScaleInOrderAsync(extremeSide, lastClose, _scaleInTradeCount)
        End Function

        Private Async Function PlaceScaleInOrderAsync(side As OrderSide,
                                                       lastClose As Decimal,
                                                       scaleIndex As Integer) As Task
            ' ── CryptoJoe BUY-only defense-in-depth guard ─────────────────────────────
            If side = OrderSide.Sell Then
                Log($"⛔ [CRYPTO BUY-ONLY] SELL scale-in order blocked (defense-in-depth) for '{_strategy.ContractId}'.")
                Return
            End If

            ' ── CryptoJoe confidence override ─────────────────────────────────────────
            _pendingConfidencePct = 100

            Dim instrId As Integer = 0
            Dim fav = TopStepTrader.Core.Trading.FavouriteContracts.TryGetBySymbol(_strategy.ContractId)
            If fav IsNot Nothing Then
                instrId = fav.InstrumentId
            ElseIf Not Integer.TryParse(_strategy.ContractId, instrId) Then
                Log($"⚠️  Cannot resolve instrumentId for '{_strategy.ContractId}' — scale-in {scaleIndex}/{MaxScaleInTrades} aborted.")
                Return
            End If

            Dim priceUsed = lastClose

            ' Reuse the current bracket's SL/TP prices for the scale-in order.
            ' The bracket continues to track the whole position as one unit.
            Dim slPriceVal As Decimal? = _lastSlPrice
            Dim tpPriceVal As Decimal? = Nothing

            Dim minNotional = If(fav IsNot Nothing, fav.MinNotionalUsd, 1000D)
            Dim finalAmount = minNotional

            Log($"📋 SCALE-IN ORDER {scaleIndex}/{MaxScaleInTrades} | instrId={instrId} side={side} | amount=${finalAmount:F2}")
            Dim slTpSourceSi = If(_currentAtrValue > 0D, $"ATR={_currentAtrValue:F4} (1.5× / 3.0×)", "pct-based")
            Log($"📋 priceUsed={priceUsed:F4} | {slTpSourceSi} | " &
                $"SL={If(slPriceVal.HasValue, slPriceVal.Value.ToString("F4"), "none")} | " &
                $"TP={If(tpPriceVal.HasValue, tpPriceVal.Value.ToString("F4"), "none")}")

            Dim entryOrder As New Order With {
                .AccountId = _strategy.AccountId,
                .ContractId = _strategy.ContractId,
                .InstrumentId = instrId,
                .Side = side,
                .OrderType = OrderType.Market,
                .Amount = finalAmount,
                .StopLossRate = slPriceVal,
                .TakeProfitRate = tpPriceVal,
                .IsTslEnabled = False,   ' Turtle bracket engine owns all SL movement — no continuous broker trail
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"AI Scale-In {scaleIndex}/{MaxScaleInTrades}: {_strategy.Name}"
            }

            Try
                Await _orderService.PlaceOrderAsync(entryOrder)
                If entryOrder.Status = OrderStatus.Rejected Then
                    Log($"⚠️  Scale-in {scaleIndex}/{MaxScaleInTrades} rejected by API — order not placed.")
                    Return
                End If
                Log($"✅ Scale-in {scaleIndex}/{MaxScaleInTrades} {side} placed — instrId={instrId} amount=${finalAmount:F2} " &
                    $"SL={If(slPriceVal.HasValue, slPriceVal.Value.ToString("F4"), "none")}")
            Catch ex As Exception
                Log($"⚠️  Scale-in {scaleIndex}/{MaxScaleInTrades} order failed: {ex.Message}")
                Return
            End Try

            ' ── Rescale the Turtle bracket to include this position's units ─────────
            ' DollarPerPoint grows with each scale-in; rescaling ensures SL/TP thresholds
            ' reflect the combined portfolio P&L, not just the initial single position.
            If _positionOpen AndAlso priceUsed > 0D Then
                Dim scaleTickSz = If(_strategy.TickSize > 0D, _strategy.TickSize, If(fav IsNot Nothing, fav.PxTickSize, 0.01D))
                Dim scaleTickVal = If(_strategy.TickValue > 0D, _strategy.TickValue, If(fav IsNot Nothing, fav.PxTickValue, 1D))
                Dim scaleContracts = CDec(If(_strategy.Contracts > 0, _strategy.Contracts, 1))
                Dim newPositionDpp = If(scaleTickSz > 0D, Math.Round((scaleTickVal / scaleTickSz) * scaleContracts, 4), 0D)
                _totalDollarPerPoint += newPositionDpp
                Log($"Scale-in {scaleIndex} added {newPositionDpp:F4} DPP " &
                    $"→ total DPP={_totalDollarPerPoint:F4}")
            End If

            RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(
                side, _strategy.ContractId, _pendingConfidencePct,
                DateTimeOffset.UtcNow,
                entryOrder.ExternalOrderId,
                entryOrder.ExternalPositionId,
                DateTimeOffset.UtcNow,
                finalAmount,
                priceUsed))
            _openTradeCount += 1
        End Function

        Private Async Function DoNeutralFlattenAsync(ct As CancellationToken) As Task
            Log($"🔴 NEUTRAL EXIT — Closing ALL positions for {_strategy.ContractId} via API flatten...")
            Dim ok = Await _orderService.FlattenContractAsync(_strategy.AccountId, _strategy.ContractId, ct)
            If ok Then
                Log($"✅ Neutral flatten complete — {_strategy.ContractId} fully closed. " &
                    $"Confidence returned to neutral; re-entry requires a new extreme signal.")
            Else
                Log($"⚠️  Neutral flatten partially failed for {_strategy.ContractId} — check positions manually.")
            End If

            Dim closedCount = If(_positionOpen, Math.Max(1, _openTradeCount), 0)
            If closedCount > 0 Then
                If closedCount > 1 Then
                    Log($"⚠️  Closing {closedCount} UI trade row(s) for {_strategy.ContractId} — all positions flattened")
                End If
                Dim closePnl = _lastApiPnl
                For i As Integer = 1 To closedCount
                    RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("Neutral", closePnl))
                    closePnl = 0D
                Next
            End If

            _positionOpen = False
            _openPositionId = Nothing
            _openTradeCount = 0
            _positionOpenedAt = DateTimeOffset.MinValue
            _lastPositionClosedAt = DateTimeOffset.UtcNow  ' start re-entry cooldown
            _lastApiPnl = 0D
            _extremeConfidenceDurationCount = 0
            _scaleInTradeCount = 0
            _currentTrendSide = Nothing
            _reversalCandidateSide = Nothing
            _reversalConfirmCount = 0
            ResetTrailState()
        End Function

        ' ── Turtle bracket monitoring ─────────────────────────────────────────────

        ''' <summary>
        ''' Stepped trailing SL — arms when profit reaches 2.0 %, then ratchets the broker SL
        ''' upward in 0.5 % steps.  SL only ever tightens (never loosens).  TP is not managed.
        ''' Algorithm (mirrors TrailingBracketTests):
        '''   steps         = floor((profitPct − 2.0) / 0.5)
        '''   steppedProfit = 2.0 + steps × 0.5
        '''   slPct         = steppedProfit − 1.5
        '''   slPrice Long  = entry × (1 + slPct / 100)
        '''   slPrice Short = entry × (1 − slPct / 100)
        ''' Pushes each new SL level to the broker via EditPositionSlTpAsync.
        ''' Returns False always — the broker SL and the reconciliation loop handle the close.
        ''' </summary>
        Private Async Function ApplySteppedTrailAsync(currentPrice As Decimal,
                                                      ct As CancellationToken) As Task(Of Boolean)
            If _lastEntryPrice <= 0D Then Return False

            ' ── Compute current profit % ───────────────────────────────────────────
            ' Long: profit rises as price rises above entry.
            ' Short: profit rises as price falls below entry.
            Dim profitPct As Decimal
            If _lastEntrySide = OrderSide.Buy Then
                profitPct = (currentPrice - _lastEntryPrice) / _lastEntryPrice * 100D
            Else
                profitPct = (_lastEntryPrice - currentPrice) / _lastEntryPrice * 100D
            End If

            ' ── Trail only activates at ≥ 2.0 % profit ────────────────────────────
            If profitPct < TrailTriggerPct Then Return False

            ' ── Ratchet step = floor((profit − 2.0) / 0.5) ────────────────────────
            ' "Never loosen" guard: steps only ever increases so SL cannot move back.
            Dim steps = CInt(Math.Floor(CDbl(profitPct - TrailTriggerPct) / CDbl(TrailStepPct)))
            If steps <= _trailLastSteps Then Return False   ' same step — SL already current

            ' ── Compute new SL price ───────────────────────────────────────────────
            Dim steppedProfit = TrailTriggerPct + steps * TrailStepPct
            Dim slPct = steppedProfit - TrailSlOffset        ' profit % at which SL sits
            Dim newSlPrice As Decimal
            If _lastEntrySide = OrderSide.Buy Then
                newSlPrice = Math.Round(_lastEntryPrice * (1D + slPct / 100D), 4)
            Else
                newSlPrice = Math.Round(_lastEntryPrice * (1D - slPct / 100D), 4)
            End If

            ' ── Update state ───────────────────────────────────────────────────────
            Dim isFirstArm = (_trailLastSteps < 0)
            _trailLastSteps = steps
            _lastSlPrice = newSlPrice

            If isFirstArm Then
                Log($"🔔 Trail ARMED — profit={profitPct:F2}% ≥ {TrailTriggerPct}% | " &
                    $"SL → +{slPct:F2}% = {newSlPrice:F4}  (position is free-ride)")
            Else
                Log($"⬆  Trail RATCHET #{steps} — profit={profitPct:F2}% | " &
                    $"steppedProfit={steppedProfit:F1}% | SL → +{slPct:F2}% = {newSlPrice:F4}")
            End If

            ' ── Compute trailing TP candidate: TpN × ATR ahead of current price ─────
            ' Ratchets only away from entry — never tightens back toward price.
            Dim newTpPrice As Decimal? = Nothing
            If _currentAtrValue > 0D AndAlso _lastTpPrice > 0D Then
                Dim tpN = If(_strategy.TpMultipleOfN > 0D, _strategy.TpMultipleOfN, 2.0D)
                Dim rawTp = If(_lastEntrySide = OrderSide.Buy,
                               currentPrice + tpN * _currentAtrValue,
                               currentPrice - tpN * _currentAtrValue)
                Dim tpShouldMove = If(_lastEntrySide = OrderSide.Buy,
                                      rawTp > _lastTpPrice,
                                      rawTp < _lastTpPrice)
                If tpShouldMove Then
                    newTpPrice = Math.Round(rawTp, 4)
                    Log($"🎯 ATR trail TP [{_lastEntrySide}]: {_lastTpPrice:F4} → {newTpPrice.Value:F4}  ATR={_currentAtrValue:F4} × {tpN:F2}N")
                End If
            End If

            ' ── Push updated SL (and TP if advancing) ──────────────────────────────
            If _openPositionId.HasValue Then
                Dim ok = Await _orderService.EditPositionSlTpAsync(
                    _openPositionId.Value, slRate:=newSlPrice, tpRate:=newTpPrice, cancel:=ct)
                If ok Then
                    If newTpPrice.HasValue Then _lastTpPrice = newTpPrice.Value
                    Log($"✅ Trail pushed — positionId={_openPositionId.Value} SL={newSlPrice:F4}" &
                        If(newTpPrice.HasValue, $"  TP={newTpPrice.Value:F4}", ""))
                Else
                    Log($"⚠️  Trail push failed — tracking locally; broker may lag (positionId={_openPositionId.Value})")
                End If
            Else
                Log($"⚠️  positionId unknown — trail SL {newSlPrice:F4} tracked locally only")
            End If

            Return False  ' broker SL and reconciliation loop handle the position close
        End Function

        ''' <summary>Resets stepped-trail state. Called on position open, close, reversal, and flatten.</summary>
        Private Sub ResetTrailState()
            _totalDollarPerPoint = 0D
            _trailLastSteps = -1
        End Sub

        Private Sub Log(message As String)
            Dim timestamped = $"{DateTime.Now:HH:mm:ss}  {message}"
            _logger.LogInformation("[CryptoStrategyEngine] {Msg}", message)
            RaiseEvent LogMessage(Me, timestamped)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                [Stop]("Engine disposed")
                _timer?.Dispose()
                _cts?.Dispose()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
