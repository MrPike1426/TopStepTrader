Imports System.Linq
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' ARCH-19: strategy-agnostic entry execution pipeline extracted from
    ''' <c>SuperTrendPlusViewModel.FireEntryAsync</c>. See
    ''' <see cref="IEntryExecutionService"/> for the contract.
    '''
    ''' Singleton lifetime: the AI veto suppression dictionary is per-contract and
    ''' should survive a single strategy ViewModel's lifetime so a second strategy
    ''' joining the same instrument inherits the in-progress block window.
    ''' </summary>
    Public Class EntryExecutionService
        Implements IEntryExecutionService

        Private Const AiSuppressionMinutes As Integer = 15
        Private Const AiCheckTimeoutSeconds As Integer = 8
        Private Const BarsToFetch As Integer = 60

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _barService As IBarIngestionService
        Private ReadOnly _contractResolver As IContractResolutionService
        Private ReadOnly _claudeService As IClaudeReviewService
        Private ReadOnly _tradeRecordService As ITradeRecordService
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _debugCapture As IDebugTradeCaptureService
        Private ReadOnly _snapshotEnricher As TradeSetupSnapshotEnricher
        Private ReadOnly _marketHub As MarketHubClient
        Private ReadOnly _logger As ILogger(Of EntryExecutionService)

        ''' <summary>
        ''' Per-contract AI veto suppression. Was <c>SuperTrendPlusViewModel._aiSuppression</c>;
        ''' moved here so it survives across strategy ViewModels (singleton service).
        ''' </summary>
        Private ReadOnly _aiSuppression As New Dictionary(Of String, DateTimeOffset)(
            StringComparer.OrdinalIgnoreCase)

        Public Sub New(orderService As IOrderService,
                       barService As IBarIngestionService,
                       contractResolver As IContractResolutionService,
                       tradeRecordService As ITradeRecordService,
                       personaService As IPersonaService,
                       logger As ILogger(Of EntryExecutionService),
                       Optional claudeService As IClaudeReviewService = Nothing,
                       Optional debugCapture As IDebugTradeCaptureService = Nothing,
                       Optional snapshotEnricher As TradeSetupSnapshotEnricher = Nothing,
                       Optional marketHub As MarketHubClient = Nothing)
            _orderService = orderService
            _barService = barService
            _contractResolver = contractResolver
            _tradeRecordService = tradeRecordService
            _personaService = personaService
            _claudeService = claudeService
            _debugCapture = debugCapture
            _snapshotEnricher = snapshotEnricher
            _marketHub = marketHub
            _logger = logger
        End Sub

        Public Function IsAiSuppressed(contractSymbol As String) As Boolean _
            Implements IEntryExecutionService.IsAiSuppressed
            If String.IsNullOrEmpty(contractSymbol) Then Return False
            SyncLock _aiSuppression
                Dim until As DateTimeOffset
                If _aiSuppression.TryGetValue(contractSymbol, until) AndAlso
                   DateTimeOffset.UtcNow < until Then
                    Return True
                End If
            End SyncLock
            Return False
        End Function

        Public Async Function PlaceAsync(request As EntryExecutionRequest,
                                         ct As CancellationToken) _
            As Task(Of EntryExecutionResult) Implements IEntryExecutionService.PlaceAsync

            Dim slot As PositionSlot = request.Slot
            Dim contractId As String = request.ContractSymbol
            Dim side As String = If(request.Candidate.Side = OrderSide.Buy, "Buy", "Sell")
            Dim stLine As Decimal = request.StopReferencePrice
            Dim lastClose As Decimal = request.LastClose
            Dim barTime As DateTimeOffset = request.BarTime

            _logger.LogInformation("Entry [Slot {Idx}] {Side} {Contract} — resolving account...",
                                   slot.SlotIndex, side, contractId)

            ' 1. Resolve / validate the trading account
            If request.AccountId = 0 Then
                _logger.LogWarning("Entry [Slot {Idx}] BLOCKED — accountId=0.", slot.SlotIndex)
                ReleaseSlot(request)
                Return Failure("AccountId=0")
            End If
            slot.AccountId = request.AccountId

            ' 2. Live-price guard: abort if the live price has already crossed the
            ' suggested stop before the market order can fill (e.g. 09:30 ET gap open).
            Try
                Dim guardBars = Await _barService.GetLiveBarsAsync(contractId, BarTimeframe.FifteenSecond, 3)
                If guardBars IsNot Nothing AndAlso guardBars.Count > 0 Then
                    Dim livePrice = CDec(guardBars(guardBars.Count - 1).Close)
                    Dim isSell = String.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase)
                    If (isSell AndAlso livePrice > stLine) OrElse (Not isSell AndAlso livePrice < stLine) Then
                        _logger.LogWarning("Entry [{Contract}] aborted — live price {Live:F2} crossed stop line {St:F2} before fill",
                                           contractId, livePrice, stLine)
                        ReleaseSlot(request)
                        Return Failure("Live-price guard crossed stop")
                    End If
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "Entry [{Contract}] live-price guard fetch failed — proceeding", contractId)
            End Try

            If ct.IsCancellationRequested Then
                ReleaseSlot(request)
                Return Failure("Cancelled")
            End If

            ' 3. Initial-stop tick computation (PxMinStopDollars + PhasedTrail clamps — BUG-87)
            Dim oSide As OrderSide = If(side = "Buy", OrderSide.Buy, OrderSide.Sell)
            Dim fc As FavouriteContract = FavouriteContracts.TryGetBySymbolResolved(contractId, _contractResolver)
            Dim stopTicks As Integer? = Nothing
            If fc IsNot Nothing AndAlso fc.PxTickSize > 0D Then
                Dim rawDist As Decimal = Math.Abs(lastClose - stLine)
                Dim rawTicks As Integer = CInt(Math.Round(rawDist / fc.PxTickSize))
                Dim minTicks As Integer = 1
                If fc.PxMinStopDollars > 0D AndAlso fc.PxTickValue > 0D Then
                    minTicks = CInt(Math.Ceiling(fc.PxMinStopDollars / fc.PxTickValue))
                End If
                Dim initialStopTicks = Math.Max(rawTicks, minTicks)
                If fc.PhasedTrailMinInitialStopTicks > 0 AndAlso initialStopTicks < fc.PhasedTrailMinInitialStopTicks Then
                    _logger.LogInformation(
                        "Entry [{Contract}] initial SL clamped UP to floor: {Old}t → {New}t (fav floor)",
                        contractId, initialStopTicks, fc.PhasedTrailMinInitialStopTicks)
                    initialStopTicks = fc.PhasedTrailMinInitialStopTicks
                End If
                If fc.PhasedTrailMaxInitialStopTicks > 0 AndAlso initialStopTicks > fc.PhasedTrailMaxInitialStopTicks Then
                    _logger.LogInformation(
                        "Entry [{Contract}] initial SL clamped DOWN to cap: {Old}t → {New}t (fav cap)",
                        contractId, initialStopTicks, fc.PhasedTrailMaxInitialStopTicks)
                    initialStopTicks = fc.PhasedTrailMaxInitialStopTicks
                End If

                stopTicks = initialStopTicks
            End If
            _logger.LogInformation("Entry bracket for {Contract}: SL={SL} ticks (flip-only; no hard TP) lastClose={Close}, stLine={St}",
                                   contractId, If(stopTicks.HasValue, stopTicks.Value.ToString(), "none"),
                                   lastClose, stLine)

            ' 4. AI pre-trade veto (gated by request.IsAiEnabled + per-contract suppression)
            Dim capturedAiResult As String = Nothing
            Dim capturedAiReason As String = Nothing
            If request.IsAiEnabled AndAlso _claudeService IsNot Nothing Then
                Dim isSuppressed As Boolean = False
                SyncLock _aiSuppression
                    Dim suppressedUntil As DateTimeOffset
                    If _aiSuppression.TryGetValue(contractId, suppressedUntil) AndAlso
                       DateTimeOffset.UtcNow < suppressedUntil Then
                        isSuppressed = True
                        _logger.LogInformation("Entry AI check suppressed for {Contract} until {Until:HH:mm:ss} UTC",
                                               contractId, suppressedUntil.UtcDateTime)
                    End If
                End SyncLock

                If isSuppressed Then
                    ReleaseSlot(request)
                    Return Failure("AI suppression window active")
                End If

                Try
                    Dim barsForAi As IList(Of MarketBar) = Nothing
                    Try
                        Dim tfMins As Integer = request.TimeframeMinutes
                        Dim barsNeeded As Integer = Math.Max(30, CInt(Math.Ceiling(240.0 / Math.Max(1, tfMins))))
                        barsForAi = Await _barService.GetLiveBarsAsync(contractId, request.TimeframeForBars, barsNeeded)
                    Catch
                    End Try

                    Dim exitDesc As String = $"{request.StrategyDisplayName} flip-only exit — no hard TP bracket. " &
                                             $"SL placed at SuperTrend line ({If(stopTicks.HasValue, $"{stopTicks.Value} ticks", "TBD")} from entry at {stLine:F2}). " &
                                             $"Persona: {request.Persona} (RR target {request.PersonaRrRatio:F2}R, MinADX {request.PersonaMinAdx:F0}). " &
                                             "Position managed via phased stop ratcheting and 9-signal degradation monitor."
                    If Not String.IsNullOrEmpty(request.ExitStrategyDescription) Then
                        exitDesc = request.ExitStrategyDescription
                    End If
                    Dim ctx As New PreTradeContext With {
                        .ContractId = contractId,
                        .ContractDescription = contractId,
                        .Side = side,
                        .Price = lastClose,
                        .AdxValue = 0F,
                        .TpMultiple = 0D,
                        .UtcNow = DateTimeOffset.UtcNow,
                        .StrategyName = request.StrategyDisplayName,
                        .ExitStrategyDescription = exitDesc,
                        .RecentBars = If(barsForAi IsNot Nothing,
                                         CType(barsForAi.ToList(), IReadOnlyList(Of MarketBar)),
                                         Nothing)
                    }
                    Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        cts.CancelAfter(TimeSpan.FromSeconds(AiCheckTimeoutSeconds))
                        Dim aiResult = Await _claudeService.PreTradeCheckAsync(ctx, cts.Token)
                        If Not aiResult.Proceed Then
                            Dim fullReason As String = If(aiResult.Reasoning, String.Empty)
                            _logger.LogInformation("Entry AI VETO [{Contract}]: {Reason}", contractId, fullReason)
                            SyncLock _aiSuppression
                                _aiSuppression(contractId) = DateTimeOffset.UtcNow.AddMinutes(AiSuppressionMinutes)
                            End SyncLock
                            request.OnAiLogEntry?.Invoke(contractId, $"VETO — {fullReason}")
                            request.OnWatchlistAiStatus?.Invoke(contractId, $"🤖 AI: {fullReason}")
                            ReleaseSlot(request)
                            Return Failure($"AI veto: {fullReason}")
                        End If
                        request.OnWatchlistAiStatus?.Invoke(contractId, "🤖 AI Checked ✓")
                        request.OnAiLogEntry?.Invoke(contractId, "Pre-trade check PASSED ✓")
                        capturedAiResult = "PASSED"
                        capturedAiReason = If(aiResult.Reasoning.Length > 500,
                                              aiResult.Reasoning.Substring(0, 497) & "...",
                                              aiResult.Reasoning)
                    End Using
                Catch ex As Exception
                    _logger.LogWarning(ex, "Entry AI check error for {Contract} — proceeding anyway", contractId)
                End Try
            End If

            If ct.IsCancellationRequested Then
                _logger.LogWarning("Entry [{Contract}] BLOCKED — cancelled before order placement.", contractId)
                ReleaseSlot(request)
                Return Failure("Cancelled")
            End If

            ' 5. Place the bracket order
            Dim order As New Order With {
                .AccountId = slot.AccountId,
                .ContractId = contractId,
                .Side = oSide,
                .Quantity = slot.Contracts,
                .OrderType = OrderType.Market,
                .InitialStopTicks = stopTicks,
                .InitialTakeProfitTicks = Nothing
            }
            _logger.LogDebug("Entry {Contract} slot={Slot} stopTicks={Stop} side={Side}",
                             contractId, slot.SlotIndex, stopTicks, oSide)

            Dim placed As Order = Nothing
            Try
                placed = Await _orderService.PlaceOrderAsync(order)
            Catch ex As Exception
                _logger.LogWarning(ex, "Entry PlaceOrderAsync failed for {Contract}", contractId)
            End Try

            Dim isAccepted = placed IsNot Nothing AndAlso
                             (placed.Status = OrderStatus.Working OrElse placed.Status = OrderStatus.Filled)
            If Not isAccepted Then
                _logger.LogWarning("Entry order not accepted for {Contract}: status={Status}", contractId, placed?.Status)
                ReleaseSlot(request)
                Return Failure("Order not accepted")
            End If

            ' Order accepted — clear the in-flight flag so normal monitoring takes over
            slot.IsEntryInFlight = False
            slot.StopPrice = stLine
            slot.EntryTime = DateTime.Now
            slot.EntryBarTime = barTime

            ' FEAT-52: subscribe this slot's PX contract to the MarketHub quote stream so
            ' OnMarketQuoteReceived (VM-side) starts populating sub-second P&L. Fire-and-forget.
            If _marketHub IsNot Nothing Then
                Dim fcSub = FavouriteContracts.TryGetBySymbolResolved(contractId, _contractResolver)
                Dim pxIdSub As String = If(fcSub IsNot Nothing, fcSub.PxContractId, Nothing)
                If Not String.IsNullOrEmpty(pxIdSub) Then
#Disable Warning BC42358
                    Task.Run(Async Function() As Task
                                 Try
                                     Await _marketHub.SubscribeContractAsync(pxIdSub)
                                 Catch ex As Exception
                                     _logger.LogDebug(ex, "Entry MarketHub subscribe failed for {Id}", pxIdSub)
                                 End Try
                             End Function)
#Enable Warning BC42358
                End If
            End If

            ' 7. Begin push-driven live price + P&L tracking (VM-side callback because
            ' the per-slot tick handler captures VM-private state).
            request.OnSlotEntered?.Invoke(slot)

            ' Debug capture (FEAT-39) — fire-and-forget; opens trade header + initial actions
            If request.DebugCaptureEnabled AndAlso _debugCapture IsNot Nothing Then
                Dim newTradeId = Guid.NewGuid().ToString("D")
                slot.DebugTradeId = newTradeId
                Dim header As New DebugTradeRecord With {
                    .TradeId = newTradeId,
                    .SlotIndex = slot.SlotIndex,
                    .Persona = request.Persona,
                    .Instrument = contractId,
                    .TimeFrame = request.TimeframeLabel,
                    .EntryMode = request.EntryModeLabel,
                    .Direction = If(side = "Buy", "Long", "Short"),
                    .EntryPrice = lastClose,
                    .EntryTime = DateTime.UtcNow.ToString("O"),
                    .InitialSL = stLine,
                    .InitialTP = 0D,
                    .ContractCount = slot.Contracts,
                    .SuperTrendConfigJson = request.StrategyConfigJson,
                    .AiCheckResult = capturedAiResult,
                    .AiCheckReason = capturedAiReason,
                    .CreatedAt = DateTime.UtcNow.ToString("O"),
                    .AccountId = slot.AccountId
                }
                _debugCapture.BeginTrade(header)
                _debugCapture.RecordAction(New DebugTradeAction With {
                    .TradeId = newTradeId,
                    .TimestampUtc = DateTime.UtcNow.ToString("O"),
                    .ActionType = "OrderPlaced",
                    .Price = lastClose,
                    .Quantity = slot.Contracts,
                    .OrderId = placed.ExternalOrderId,
                    .NewValue = stLine,
                    .Reason = If(String.Equals(request.EntryModeLabel, "Preemptive", StringComparison.OrdinalIgnoreCase),
                                 "Preemptive entry", "Bar-close entry"),
                    .Source = "Local"
                })
                _debugCapture.RecordAction(New DebugTradeAction With {
                    .TradeId = newTradeId,
                    .TimestampUtc = DateTime.UtcNow.ToString("O"),
                    .ActionType = "StopLossPlaced",
                    .NewValue = stLine,
                    .Reason = "Initial SuperTrend stop",
                    .Source = "Local"
                })
            End If
            slot.MissCount = 0
            slot.PositionId = placed.ExternalPositionId
            slot.EntryOrderId = placed.ExternalOrderId
            slot.EntryPrice = 0D
            slot.TakeProfitPrice = 0D
            slot.StopPhase = StopPhase.Initial
            slot.InitialRisk = 0D
            slot.EntryAtr = 0D

            ' 6. Persistence — LiveTradeRecord + TradeOutcome + TradeSetupSnapshot.
            ' Fire-and-forget so the entry path stays prompt; failures are logged.
#Disable Warning BC42358
            Task.Run(Async Function()
                         Await PersistTradeRecordsAsync(request, placed, capturedAiResult, capturedAiReason, side, contractId, stLine, lastClose)
                     End Function)
#Enable Warning BC42358

            ' Capture entry ATR from the bars that were used to fire the entry
            Try
                Dim entryBars = Await _barService.GetLiveBarsAsync(contractId, request.TimeframeForBars, BarsToFetch)
                If entryBars IsNot Nothing AndAlso entryBars.Count >= 14 Then
                    Dim eHighs = entryBars.Select(Function(b) b.High).ToList()
                    Dim eLows = entryBars.Select(Function(b) b.Low).ToList()
                    Dim eCloses = entryBars.Select(Function(b) b.Close).ToList()
                    Dim atr14 = TechnicalIndicators.ATR(eHighs, eLows, eCloses, period:=14)
                    Dim eN = entryBars.Count - 1
                    If atr14 IsNot Nothing AndAlso atr14.Length > eN AndAlso Not Single.IsNaN(atr14(eN)) Then
                        slot.EntryAtr = CDec(atr14(eN))
                    End If
                End If
            Catch
            End Try

            Return New EntryExecutionResult With {
                .Success = True,
                .PlacedPositionId = placed.ExternalPositionId,
                .InitialStopTicks = stopTicks
            }
        End Function

        Private Async Function PersistTradeRecordsAsync(request As EntryExecutionRequest,
                                                        placed As Order,
                                                        capturedAiResult As String,
                                                        capturedAiReason As String,
                                                        side As String,
                                                        contractId As String,
                                                        stLine As Decimal,
                                                        lastClose As Decimal) As Task
            Dim slot As PositionSlot = request.Slot
            Try
                Dim fcRec = FavouriteContracts.TryGetBySymbolResolved(contractId, _contractResolver)
                Dim persona = _personaService.GetProfile(request.Persona)
                Dim commission = 0.5D * slot.Contracts
                Dim fees = If(fcRec IsNot Nothing, fcRec.RoundTripFee * slot.Contracts, 0.8D * slot.Contracts)
                Dim displaySymbol = If(fcRec IsNot Nothing, "/" & fcRec.Name, contractId)
                Dim rec As New LiveTradeRecord With {
                    .EntryOrderId = If(slot.EntryOrderId.HasValue, slot.EntryOrderId.Value, 0),
                    .ContractId = contractId,
                    .Symbol = displaySymbol,
                    .Direction = If(side = "Buy", "Long", "Short"),
                    .Sizes = slot.Contracts,
                    .MaxScaleIns = If(persona IsNot Nothing, persona.MaxScaleIns, 1),
                    .StrategyName = request.StrategyName,
                    .Persona = request.Persona,
                    .Timeframe = request.TimeframeLabel,
                    .EntryTime = DateTimeOffset.UtcNow,
                    .EntryPrice = 0D,
                    .CommissionUsd = commission,
                    .FeesUsd = fees,
                    .IsOpen = True
                }
                Dim newRecId = Await _tradeRecordService.OpenTradeAsync(rec)
                slot.TradeRecordId = newRecId

                ' FEAT-57: Signal + open TradeOutcome row.
                Try
                    Dim tfMins As Integer = request.TimeframeMinutes
                    Dim sigType As SignalType =
                        If(side = "Buy", SignalType.Buy, SignalType.Sell)
                    Dim sigConfidence As Single = slot.EntryAdx

                    Dim signal As New TradeSignal With {
                        .ContractId = contractId,
                        .GeneratedAt = DateTimeOffset.UtcNow,
                        .SignalType = sigType,
                        .Confidence = sigConfidence,
                        .ModelVersion = request.ModelVersion,
                        .SuggestedEntryPrice = lastClose,
                        .SuggestedStopLoss = stLine,
                        .SuggestedTakeProfit = Nothing
                    }
                    Dim sigId = Await _tradeRecordService.SaveSignalAsync(signal)
                    If sigId > 0 Then
                        Dim outcome As New TradeOutcome With {
                            .ContractId = contractId,
                            .Timeframe = tfMins,
                            .SignalType = If(side = "Buy", "Buy", "Sell"),
                            .SignalConfidence = sigConfidence,
                            .ModelVersion = request.ModelVersion,
                            .EntryTime = DateTimeOffset.UtcNow,
                            .EntryPrice = lastClose
                        }
                        slot.TradeOutcomeId = Await _tradeRecordService.OpenOutcomeAsync(sigId, newRecId, outcome)

                        ' FEAT-58: indicator + context snapshot linked to the outcome
                        If slot.TradeOutcomeId > 0 Then
                            Try
                                Dim entrySession As String = SessionWindowResolver.Resolve(DateTime.UtcNow)
                                slot.EntrySessionWindow = entrySession

                                Dim snapBars = Await _barService.GetLiveBarsAsync(contractId, request.TimeframeForBars, BarsToFetch)
                                Dim adxAtEntry As Single = 0F
                                Dim plusDiAtEntry As Single = 0F
                                Dim minusDiAtEntry As Single = 0F
                                Dim rsiAtEntry As Single = 0F
                                Dim atrAtEntry As Decimal = 0D
                                Dim openPx As Decimal = 0D
                                Dim highPx As Decimal = 0D
                                Dim lowPx As Decimal = 0D
                                Dim closePx As Decimal = lastClose
                                Dim volPx As Long = 0L
                                If snapBars IsNot Nothing AndAlso snapBars.Count >= 14 Then
                                    Dim eHighs = snapBars.Select(Function(b) b.High).ToList()
                                    Dim eLows = snapBars.Select(Function(b) b.Low).ToList()
                                    Dim eCloses = snapBars.Select(Function(b) b.Close).ToList()
                                    Dim dmi = TechnicalIndicators.DMI(eHighs, eLows, eCloses, period:=14)
                                    Dim atr = TechnicalIndicators.ATR(eHighs, eLows, eCloses, period:=14)
                                    Dim rsi = TechnicalIndicators.RSI(eCloses, 14)
                                    Dim eN = snapBars.Count - 1
                                    If Not Single.IsNaN(dmi.ADX(eN)) Then adxAtEntry = dmi.ADX(eN)
                                    If Not Single.IsNaN(dmi.PlusDI(eN)) Then plusDiAtEntry = dmi.PlusDI(eN)
                                    If Not Single.IsNaN(dmi.MinusDI(eN)) Then minusDiAtEntry = dmi.MinusDI(eN)
                                    If rsi IsNot Nothing AndAlso rsi.Length > eN AndAlso Not Single.IsNaN(rsi(eN)) Then rsiAtEntry = rsi(eN)
                                    If atr IsNot Nothing AndAlso atr.Length > eN AndAlso Not Single.IsNaN(atr(eN)) Then atrAtEntry = CDec(atr(eN))
                                    Dim entryBar = snapBars(eN)
                                    openPx = entryBar.Open
                                    highPx = entryBar.High
                                    lowPx = entryBar.Low
                                    closePx = entryBar.Close
                                    volPx = CLng(entryBar.Volume)
                                End If

                                Dim band As Integer = If(request.BandForAdx IsNot Nothing, request.BandForAdx(adxAtEntry), 0)
                                Dim longCount As Integer = If(side = "Buy", band, 0)
                                Dim shortCount As Integer = If(side = "Buy", 0, band)
                                Dim slMult As Single = If(persona IsNot Nothing, CSng(persona.SlMultipleOfN), 0F)
                                Dim tpMult As Single = If(persona IsNot Nothing, CSng(persona.TpMultipleOfN), 0F)
                                Dim nowUtc As DateTime = DateTime.UtcNow

                                Dim snapshotModel As New TradeSetupSnapshot With {
                                    .TradeOutcomeId = slot.TradeOutcomeId,
                                    .CapturedAt = DateTimeOffset.UtcNow,
                                    .PlusDI = plusDiAtEntry,
                                    .MinusDI = minusDiAtEntry,
                                    .AdxValue = adxAtEntry,
                                    .Rsi14 = rsiAtEntry,
                                    .AtrValue = atrAtEntry,
                                    .LongCount = longCount,
                                    .ShortCount = shortCount,
                                    .TotalConditions = 3,
                                    .UpPct = If(side = "Buy" AndAlso band > 0, CInt(band / 3.0 * 100), 0),
                                    .DownPct = If(side <> "Buy" AndAlso band > 0, CInt(band / 3.0 * 100), 0),
                                    .SignalBarOpen = openPx,
                                    .SignalBarHigh = highPx,
                                    .SignalBarLow = lowPx,
                                    .SignalBarClose = closePx,
                                    .SignalBarVolume = volPx,
                                    .SessionWindow = entrySession,
                                    .DayOfWeek = CInt(nowUtc.DayOfWeek),
                                    .HourOfDay = nowUtc.Hour,
                                    .StrategyName = request.StrategyName,
                                    .PersonaName = request.Persona,
                                    .SlMultiple = slMult,
                                    .TpMultiple = tpMult,
                                    .TimeframeMinutes = tfMins
                                }
                                If _snapshotEnricher IsNot Nothing AndAlso snapBars IsNot Nothing Then
                                    _snapshotEnricher.PopulateAdditionalIndicators(snapshotModel, snapBars)
                                End If
                                Await _tradeRecordService.SaveSetupSnapshotAsync(slot.TradeOutcomeId, snapshotModel)
                            Catch exSnap As Exception
                                _logger.LogWarning(exSnap, "Entry [Slot {Idx}] failed to save setup snapshot for outcome {Id}",
                                                   slot.SlotIndex, slot.TradeOutcomeId)
                            End Try
                        End If
                    End If
                Catch exOutcome As Exception
                    _logger.LogWarning(exOutcome, "Entry [Slot {Idx}] failed to open TradeOutcome for {Contract}",
                                       slot.SlotIndex, contractId)
                End Try
            Catch ex As Exception
                _logger.LogWarning(ex, "Entry [Slot {Idx}] failed to open trade record for {Contract}",
                                   slot.SlotIndex, contractId)
            End Try
        End Function

        Private Sub ReleaseSlot(request As EntryExecutionRequest)
            request.OnReleaseSlot?.Invoke(request.Slot.SlotIndex)
        End Sub

        Private Shared Function Failure(reason As String) As EntryExecutionResult
            Return New EntryExecutionResult With {
                .Success = False,
                .AbortReason = reason
            }
        End Function

    End Class

End Namespace
